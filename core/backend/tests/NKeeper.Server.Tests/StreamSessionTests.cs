using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Core.Audit;
using NKeeper.Core.Persistence;
using NKeeper.Core.Sessions;
using NKeeper.Testing;

namespace NKeeper.Server.Tests;

/// <summary>
/// docs/architecture.md §7.10: stream sessions through core's WebSocket endpoint, with the test plugin's session
/// type (an echo, or a shell over SSH), and the registry that ends them when access changes.
/// </summary>
public sealed class StreamSessionTests(PostgresFixture postgres, SshServerFixture ssh) : IClassFixture<PostgresFixture>, IClassFixture<SshServerFixture>
{
    private const string BobPassword = "Bob-Passw0rd-123";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_session_opens_carries_the_plugins_frames_and_is_audited()
    {
        await using var server = await ServerAsync();
        var (admin, cookie) = await SignInAsync(server, ServerScenario.AdminUserName, ServerScenario.AdminPassword);
        var host = await CreateHostAsync(admin);

        await using (var session = await SessionClient.ConnectAsync(server, cookie))
        {
            await session.SendAsync(new { type = "start", hostId = Id(host), serviceId = ServiceId(host) });
            Assert.Equal("connecting", (await session.ExpectAsync("status")).GetProperty("status").GetString());
            await session.ExpectAsync("connected");
            Assert.Single(server.Services.GetRequiredService<SessionRegistry>().Open);

            await session.SendAsync(new { type = "hello" });
            var echo = await session.ExpectAsync("echo");
            Assert.Equal("ssh", echo.GetProperty("host").GetString());

            await session.SendBinaryAsync([1, 2, 3]);
            Assert.Equal([1, 2, 3], await session.ReceiveBinaryAsync());
        }

        // The browser closed it: the registry lets go, and the audit has both ends.
        await WaitForAsync(() => server.Services.GetRequiredService<SessionRegistry>().Open.Count == 0);
        var opened = Assert.Single(await AuditAsync(server, AuditActions.SessionOpen));
        Assert.Contains($"on service {ServiceId(host)}, plugin hello, no credential", opened.Detail, StringComparison.Ordinal);
        await WaitForAsync(async () => (await AuditAsync(server, AuditActions.SessionClose)).Count == 1);
        Assert.Contains(": closed, after", Assert.Single(await AuditAsync(server, AuditActions.SessionClose)).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Requests_that_arent_the_users_own_browser_are_refused_before_upgrading()
    {
        await using var server = await ServerAsync();
        var (_, cookie) = await SignInAsync(server, ServerScenario.AdminUserName, ServerScenario.AdminPassword);

        // Another site's page, no sign-in, and an unknown session type.
        await AssertRefusedAsync(server, cookie, "http://evil.example", "hello", HttpStatusCode.Forbidden);
        await AssertRefusedAsync(server, null, "http://localhost", "hello", HttpStatusCode.Unauthorized);
        await AssertRefusedAsync(server, cookie, "http://localhost", "nope", HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_bad_start_is_refused_with_a_code()
    {
        await using var server = await ServerAsync();
        var (admin, cookie) = await SignInAsync(server, ServerScenario.AdminUserName, ServerScenario.AdminPassword);
        var host = await CreateHostAsync(admin);

        async Task<string?> CodeAsync(object start)
        {
            await using var session = await SessionClient.ConnectAsync(server, cookie);
            await session.SendAsync(start);
            return (await session.ExpectAsync("error")).GetProperty("code").GetString();
        }

        Assert.Equal("bad_request", await CodeAsync(new { type = "hello" }));
        Assert.Equal("bad_request", await CodeAsync(new { type = "start", hostId = Id(host) }));
        Assert.Equal("host_not_found", await CodeAsync(new { type = "start", hostId = Guid.NewGuid(), serviceId = ServiceId(host) }));
        Assert.Equal("wrong_service", await CodeAsync(new { type = "start", hostId = Id(host), serviceId = Guid.NewGuid() }));
        Assert.Equal("credential_denied", await CodeAsync(new { type = "start", hostId = Id(host), serviceId = ServiceId(host), promptReference = "made-up" }));
        Assert.Equal("failed", await CodeAsync(new { type = "start", hostId = Id(host), serviceId = ServiceId(host), options = new { fail = true } }));
        Assert.Empty(server.Services.GetRequiredService<SessionRegistry>().Open);
        Assert.Empty(await AuditAsync(server, AuditActions.SessionOpen));

        // A host the user can't see is refused just like a missing one.
        await server.CreateUserAsync("bob", BobPassword);
        var (_, bob) = await SignInAsync(server, "bob", BobPassword);
        await using var hidden = await SessionClient.ConnectAsync(server, bob);
        await hidden.SendAsync(new { type = "start", hostId = Id(host), serviceId = ServiceId(host) });
        Assert.Equal("host_not_found", (await hidden.ExpectAsync("error")).GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_unknown_key_is_asked_about_in_band_and_the_shell_runs_once_it_is_trusted()
    {
        await using var server = await ServerAsync();
        var (admin, cookie) = await SignInAsync(server, ServerScenario.AdminUserName, ServerScenario.AdminPassword);
        var host = await CreateHostAsync(admin, port: ssh.Port, credential: new { username = SshServerFixture.UserName, password = SshServerFixture.Password });
        var start = new { type = "start", hostId = Id(host), serviceId = ServiceId(host), credentialId = CredentialId(host) };

        // Refused: nothing is trusted, and the session doesn't open.
        await using (var refused = await SessionClient.ConnectAsync(server, cookie))
        {
            await refused.SendAsync(start);
            await refused.ExpectAsync("hostKey");
            await refused.SendAsync(new { type = "hostKeyDecision", trust = false });
            Assert.Equal("host_key_rejected", (await refused.ExpectAsync("error")).GetProperty("code").GetString());
        }

        Assert.Empty(await AuditAsync(server, AuditActions.TrustAdd));

        await using var session = await SessionClient.ConnectAsync(server, cookie);
        await session.SendAsync(start);
        var key = await session.ExpectAsync("hostKey");
        Assert.Equal((Id(host), ServiceId(host)), (key.GetProperty("hostId").GetGuid(), key.GetProperty("serviceId").GetGuid()));
        await session.SendAsync(new { type = "hostKeyDecision", trust = true });
        await session.ExpectAsync("connected");
        Assert.Single(await AuditAsync(server, AuditActions.TrustAdd));

        await session.SendBinaryAsync(Encoding.UTF8.GetBytes("echo in-band-$((6*7))\n"));
        await session.ReadUntilAsync("in-band-42");
        Assert.Contains($"credential {CredentialId(host)}", Assert.Single(await AuditAsync(server, AuditActions.SessionOpen)).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sessions_end_when_access_is_taken_away()
    {
        await using var server = await ServerAsync();
        var (admin, _) = await SignInAsync(server, ServerScenario.AdminUserName, ServerScenario.AdminPassword);
        var host = await CreateHostAsync(admin);
        var bobId = await server.CreateUserAsync("bob", BobPassword);
        async Task GrantAsync(object[] grants) =>
            (await admin.SendWithTokenAsync(HttpMethod.Put, $"/api/users/{bobId}/grants", grants)).EnsureSuccessStatusCode();
        await GrantAsync([new { hostId = Id(host), level = "use" }]);
        var (bobClient, bob) = await SignInAsync(server, "bob", BobPassword);

        // Grants replaced.
        await using (var session = await OpenEchoAsync(server, bob, host))
        {
            await GrantAsync([]);
            Assert.Equal("access_revoked", (await session.ExpectAsync("closed")).GetProperty("reason").GetString());
        }

        // The host's tags edited, so a group grant no longer covers it.
        await GrantAsync([new { tagName = "team", tagValue = "blue", level = "use" }]);
        host = await UpdateTagsAsync(admin, host, [new { name = "team", value = "blue" }]);
        await using (var session = await OpenEchoAsync(server, bob, host))
        {
            await UpdateTagsAsync(admin, host, []);
            Assert.Equal("access_revoked", (await session.ExpectAsync("closed")).GetProperty("reason").GetString());
        }

        // Signing out.
        await GrantAsync([new { hostId = Id(host), level = "use" }]);
        await using (var session = await OpenEchoAsync(server, bob, host))
        {
            (await bobClient.LogoutAsync()).EnsureSuccessStatusCode();
            Assert.Equal("access_revoked", (await session.ExpectAsync("closed")).GetProperty("reason").GetString());
        }

        // Disabled by an administrator.
        var (_, again) = await SignInAsync(server, "bob", BobPassword);
        await using (var session = await OpenEchoAsync(server, again, host))
        {
            (await admin.SendWithTokenAsync(HttpMethod.Put, $"/api/users/{bobId}", new { email = "bob@example.com", role = "user", isDisabled = true })).EnsureSuccessStatusCode();
            Assert.Equal("access_revoked", (await session.ExpectAsync("closed")).GetProperty("reason").GetString());
        }

        await WaitForAsync(async () => (await AuditAsync(server, AuditActions.SessionClose)).Count == 4);
        Assert.All(await AuditAsync(server, AuditActions.SessionClose), e => Assert.Contains(": access_revoked,", e.Detail, StringComparison.Ordinal));
        Assert.Empty(server.Services.GetRequiredService<SessionRegistry>().Open);
    }

    [Fact]
    public async Task A_user_has_at_most_twenty_sessions()
    {
        await using var server = await ServerAsync();
        var (admin, cookie) = await SignInAsync(server, ServerScenario.AdminUserName, ServerScenario.AdminPassword);
        var host = await CreateHostAsync(admin);
        var open = new List<SessionClient>();
        try
        {
            for (var i = 0; i < SessionRegistry.MaxPerUser; i++)
            {
                open.Add(await OpenEchoAsync(server, cookie, host));
            }

            await using var extra = await SessionClient.ConnectAsync(server, cookie);
            Assert.Equal("too_many_sessions", (await extra.ExpectAsync("error")).GetProperty("code").GetString());
        }
        finally
        {
            foreach (var session in open)
            {
                await session.DisposeAsync();
            }
        }

        // Places are given back as sessions close.
        await WaitForAsync(() => server.Services.GetRequiredService<SessionRegistry>().Open.Count == 0);
        await using var later = await OpenEchoAsync(server, cookie, host);
    }

    [Fact]
    public async Task Plugin_status_lists_session_types()
    {
        await using var server = await ServerAsync();
        var (admin, _) = await SignInAsync(server, ServerScenario.AdminUserName, ServerScenario.AdminPassword);
        var plugins = await admin.GetFromJsonAsync<JsonElement>(new Uri("/api/plugins", UriKind.Relative), Token);
        var hello = plugins.EnumerateArray().Single(p => p.GetProperty("id").GetString() == "hello");
        var type = Assert.Single(hello.GetProperty("sessionTypes").EnumerateArray());
        Assert.Equal(("hello", "Hello session", "hello", true), (
            type.GetProperty("id").GetString(),
            type.GetProperty("label").GetString(),
            type.GetProperty("serviceTypes")[0].GetString(),
            type.GetProperty("supportsJumpHost").GetBoolean()));
    }

    // ---- Helpers ----

    private static async Task<SessionClient> OpenEchoAsync(ServerScenario server, string cookie, JsonElement host)
    {
        var session = await SessionClient.ConnectAsync(server, cookie);
        await session.SendAsync(new { type = "start", hostId = Id(host), serviceId = ServiceId(host) });
        await session.ExpectAsync("connected");
        return session;
    }

    private static async Task AssertRefusedAsync(ServerScenario server, string? cookie, string origin, string type, HttpStatusCode status)
    {
        var client = server.WebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers.Origin = origin;
            if (cookie is not null)
            {
                request.Headers.Cookie = cookie;
            }
        };
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(new Uri($"ws://localhost/api/sessions/{type}"), Token));
        Assert.Contains(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), refused.Message, StringComparison.Ordinal);
    }

    private static async Task<(HttpClient Client, string Cookie)> SignInAsync(ServerScenario server, string userName, string password)
    {
        var client = server.Client();
        using var response = await client.LoginAsync(userName, password);
        response.EnsureSuccessStatusCode();
        return (client, string.Join("; ", response.SetCookies().Select(c => c.Split(';')[0])));
    }

    private static async Task<JsonElement> CreateHostAsync(HttpClient admin, int port = 7, object? credential = null)
    {
        using var response = await admin.SendWithTokenAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = "ssh",
            ipAddress = SshServerFixture.Address,
            services = new[] { new { type = "hello", port, settings = new { greeting = "a" } } },
            credentials = credential is null ? Array.Empty<object>() : [credential],
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Token));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    private static async Task<JsonElement> UpdateTagsAsync(HttpClient admin, JsonElement host, object[] tags)
    {
        var current = await admin.GetFromJsonAsync<JsonElement>(new Uri($"/api/hosts/{Id(host)}", UriKind.Relative), Token);
        using var response = await admin.SendWithTokenAsync(HttpMethod.Put, $"/api/hosts/{Id(host)}", new
        {
            name = "ssh",
            ipAddress = SshServerFixture.Address,
            tags,
            services = current.GetProperty("services").EnumerateArray().Select(s => new { id = s.GetProperty("id").GetGuid(), type = "hello", port = s.GetProperty("port").GetInt32(), settings = s.GetProperty("settings") }),
            version = current.GetProperty("version").GetUInt32(),
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Token));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    private static Guid Id(JsonElement host) => host.GetProperty("id").GetGuid();

    private static Guid ServiceId(JsonElement host) => host.GetProperty("services")[0].GetProperty("id").GetGuid();

    private static Guid CredentialId(JsonElement host) => host.GetProperty("credentials")[0].GetProperty("id").GetGuid();

    private static async Task<List<AuditEvent>> AuditAsync(ServerScenario server, string action)
    {
        using var scope = server.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CoreDbContext>().AuditEvents.AsNoTracking().Where(e => e.Action == action).ToListAsync(Token);
    }

    private static Task WaitForAsync(Func<bool> condition) => WaitForAsync(() => Task.FromResult(condition()));

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 100 && !await condition(); i++)
        {
            await Task.Delay(100, Token);
        }

        Assert.True(await condition(), "Timed out waiting.");
    }

    private async Task<ServerScenario> ServerAsync()
    {
        var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        server.Install(TestPlugins.Package(server.DevelopmentKey));
        return server;
    }
}
