using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Core.Audit;
using NKeeper.Core.Persistence;
using NKeeper.Testing;

namespace NKeeper.Server.Tests;

/// <summary>
/// docs/architecture.md §7.7–§7.9 against a real OpenSSH server: the trust store, SSH connections core opens for a
/// plugin (through the test plugin's <c>/ssh</c> endpoint), and jump hosts. Ported from 1.x's terminal and jump host tests.
/// </summary>
public sealed class SshConnectivityTests(PostgresFixture postgres, SshServerFixture ssh) : IClassFixture<PostgresFixture>, IClassFixture<SshServerFixture>
{
    private const string BobPassword = "Bob-Passw0rd-123";
    private const string WrongPassword = "Wr0ng-secret-PASSWORD";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- The trust store (§7.7) ----

    [Fact]
    public async Task An_unknown_key_is_presented_then_trusted_through_core_and_the_command_runs()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, Credential(password: SshServerFixture.Password));
        var probe = Probe(host, command: "echo hi-$((6*7))");

        var unknown = await SshAsync(admin, host, probe, HttpStatusCode.BadGateway);
        Assert.Equal("HostKeyUnknown", unknown.GetProperty("reason").GetString());
        var key = unknown.GetProperty("presentedKey");
        Assert.Equal((Id(host), ServiceId(host)), (key.GetProperty("hostId").GetGuid(), key.GetProperty("serviceId").GetGuid()));
        Assert.Contains(key.GetProperty("fingerprint").GetString()!, unknown.GetProperty("message").GetString(), StringComparison.Ordinal);

        await TrustAsync(admin, key, HttpStatusCode.NoContent);
        var ran = await SshAsync(admin, host, probe);
        Assert.Equal((0, "hi-42\n"), (ran.GetProperty("exitStatus").GetInt32(), ran.GetProperty("output").GetString()));

        var trusted = Assert.Single((await GetHostAsync(admin, host)).GetProperty("trustedKeys").EnumerateArray());
        Assert.Equal(
            ("ssh-host-key", JsonValueKind.Null, ssh.Port, key.GetProperty("algorithm").GetString(), key.GetProperty("fingerprint").GetString()),
            (trusted.GetProperty("kind").GetString(), trusted.GetProperty("address").ValueKind, trusted.GetProperty("port").GetInt32(),
                trusted.GetProperty("algorithm").GetString(), trusted.GetProperty("fingerprint").GetString()));

        // Each presentation can be trusted once.
        await TrustAsync(admin, key, HttpStatusCode.BadRequest);

        var added = Assert.Single(await AuditAsync(server, AuditActions.TrustAdd));
        Assert.Equal(Id(host).ToString(), added.Target);
        var used = Assert.Single(await AuditAsync(server, AuditActions.CredentialUse));
        Assert.Contains($"credential {CredentialId(host, 0)}", used.Detail, StringComparison.Ordinal);
        Assert.Contains("plugin hello", used.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_a_key_core_presented_to_the_same_session_can_be_trusted()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, Credential(password: SshServerFixture.Password));
        var key = (await SshAsync(admin, host, Probe(host), HttpStatusCode.BadGateway)).GetProperty("presentedKey");

        // A fingerprint no server presented.
        var forged = new { algorithm = key.GetProperty("algorithm").GetString(), fingerprint = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" };
        using (var response = await admin.SendWithTokenAsync(HttpMethod.Post, $"/api/hosts/{Id(host)}/services/{ServiceId(host)}/trusted-keys", forged))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("trust_not_presented", (await response.ProblemAsync()).GetProperty("code").GetString());
        }

        // Another user's session, with Use on the host, wasn't shown this key.
        var bobId = await server.CreateUserAsync("bob", BobPassword);
        (await admin.SendWithTokenAsync(HttpMethod.Put, $"/api/users/{bobId}/grants", new[] { new { hostId = Id(host), level = "use" } })).EnsureSuccessStatusCode();
        using var bob = await SignInAsync(server, "bob");
        await TrustAsync(bob, key, HttpStatusCode.BadRequest);

        // Nor is it trusted after signing out and in again.
        (await admin.LogoutAsync()).EnsureSuccessStatusCode();
        using var again = await server.SignedInClientAsync();
        await TrustAsync(again, key, HttpStatusCode.BadRequest);

        // Someone who can't see the host gets 404.
        await server.CreateUserAsync("eve", BobPassword);
        using var eve = await SignInAsync(server, "eve");
        await TrustAsync(eve, key, HttpStatusCode.NotFound);
        Assert.Empty(await AuditAsync(server, AuditActions.TrustAdd));
    }

    [Fact]
    public async Task A_changed_key_is_refused_and_audited_until_a_manager_removes_the_old_one()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var host = await CreateTrustedHostAsync(admin, Credential(password: SshServerFixture.Password));
        await using (var scope = server.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<CoreDbContext>().HostTrustedKeys
                .ExecuteUpdateAsync(k => k.SetProperty(x => x.Fingerprint, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"), Token);
        }

        var changed = await SshAsync(admin, host, Probe(host), HttpStatusCode.BadGateway);
        Assert.Equal(("HostKeyChanged", JsonValueKind.Null), (changed.GetProperty("reason").GetString(), changed.GetProperty("presentedKey").ValueKind));
        var mismatch = Assert.Single(await AuditAsync(server, AuditActions.TrustMismatch));
        Assert.Contains("trusted SHA256:AAAA", mismatch.Detail, StringComparison.Ordinal);

        // A Use-only user can't remove it.
        var keyId = (await GetHostAsync(admin, host)).GetProperty("trustedKeys")[0].GetProperty("id").GetGuid();
        var bobId = await server.CreateUserAsync("bob", BobPassword);
        (await admin.SendWithTokenAsync(HttpMethod.Put, $"/api/users/{bobId}/grants", new[] { new { hostId = Id(host), level = "use" } })).EnsureSuccessStatusCode();
        using var bob = await SignInAsync(server, "bob");
        using (var denied = await bob.SendWithTokenAsync(HttpMethod.Delete, $"/api/hosts/{Id(host)}/trusted-keys/{keyId}"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        using (var removed = await admin.SendWithTokenAsync(HttpMethod.Delete, $"/api/hosts/{Id(host)}/trusted-keys/{keyId}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        Assert.Single(await AuditAsync(server, AuditActions.TrustRemove));
        Assert.Equal("HostKeyUnknown", (await SshAsync(admin, host, Probe(host), HttpStatusCode.BadGateway)).GetProperty("reason").GetString());
    }

    // ---- Signing in (§7.9) ----

    [Fact]
    public async Task Keys_passphrases_and_typed_credentials_sign_in()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var host = await CreateTrustedHostAsync(
            admin,
            Credential(password: SshServerFixture.Password),
            Credential(privateKey: ssh.PrivateKey),
            Credential(privateKey: ssh.EncryptedPrivateKey, password: SshServerFixture.Passphrase));

        foreach (var index in new[] { 1, 2 })
        {
            var ran = await SshAsync(admin, host, Probe(host, index, command: "whoami"));
            Assert.Equal($"{SshServerFixture.UserName}\n", ran.GetProperty("output").GetString());
        }

        using var prompt = await admin.SendWithTokenAsync(
            HttpMethod.Post,
            $"/api/hosts/{Id(host)}/services/{ServiceId(host)}/credential-prompt",
            new { username = SshServerFixture.UserName, password = SshServerFixture.Password });
        var reference = (await prompt.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("reference").GetString();
        var typed = await SshAsync(admin, host, new { serviceId = ServiceId(host), promptReference = reference, command = "echo typed" });
        Assert.Equal("typed\n", typed.GetProperty("output").GetString());
        Assert.Contains(await AuditAsync(server, AuditActions.CredentialUse), e => e.Detail.Contains("a typed credential", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Failures_say_why_and_never_carry_a_secret()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var host = await CreateTrustedHostAsync(
            admin,
            Credential(password: SshServerFixture.Password),
            Credential(password: WrongPassword),
            Credential(),
            Credential(privateKey: "-----BEGIN OPENSSH PRIVATE KEY-----\nbm90LWEta2V5\n-----END OPENSSH PRIVATE KEY-----"),
            Credential(privateKey: ssh.EncryptedPrivateKey, password: WrongPassword));

        var bodies = new List<string>();
        foreach (var (index, reason) in new[] { (1, "AuthenticationFailed"), (2, "NoSecret"), (3, "InvalidPrivateKey"), (4, "InvalidPrivateKey") })
        {
            var failed = await SshAsync(admin, host, Probe(host, index), HttpStatusCode.BadGateway);
            Assert.Equal(reason, failed.GetProperty("reason").GetString());
            bodies.Add(failed.GetRawText());
        }

        // Nothing listens on port 1.
        var closed = await CreateHostAsync(admin, Credential(password: SshServerFixture.Password), port: 1);
        Assert.Equal("Unreachable", (await SshAsync(admin, closed, Probe(closed), HttpStatusCode.BadGateway)).GetProperty("reason").GetString());

        var audit = string.Join('\n', (await AuditAsync(server, null)).Select(e => $"{e.Action} {e.Target} {e.Detail}"));
        foreach (var secret in new[] { SshServerFixture.Password, WrongPassword, SshServerFixture.Passphrase, "bm90LWEta2V5" })
        {
            Assert.DoesNotContain(secret, string.Join('\n', bodies), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, audit, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_shell_runs_on_a_terminal_that_can_be_resized()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var host = await CreateTrustedHostAsync(admin, Credential(password: SshServerFixture.Password));

        var shell = await SshAsync(admin, host, new { serviceId = ServiceId(host), credentialId = CredentialId(host, 0), shell = true, command = "echo $TERM-$((6*7)) $(stty size)", expect = "40 120" });
        var output = shell.GetProperty("output").GetString();
        Assert.Contains("xterm-256color-42", output, StringComparison.Ordinal);
        Assert.Contains("40 120", output, StringComparison.Ordinal);
    }

    // ---- Jump hosts (§7.8) ----

    [Fact]
    public async Task A_host_behind_a_jump_host_is_reached_through_it_and_both_keys_are_checked()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var jump = await CreateHostAsync(admin, Credential(password: SshServerFixture.Password), name: "bastion");
        var inner = await CreateHostAsync(
            admin,
            Credential(privateKey: ssh.PrivateKey),
            name: "inner",
            address: SshServerFixture.InnerAddress,
            port: SshServerFixture.InnerPort,
            jump: JumpTo(jump));
        Assert.Equal(Id(jump), inner.GetProperty("jump").GetProperty("hostId").GetGuid());
        var probe = Probe(inner, command: "echo through-the-jump");

        // First the jump host's key is asked about, for the jump host's service.
        var jumpKey = (await SshAsync(admin, inner, probe, HttpStatusCode.BadGateway)).GetProperty("presentedKey");
        Assert.Equal((Id(jump), ServiceId(jump)), (jumpKey.GetProperty("hostId").GetGuid(), jumpKey.GetProperty("serviceId").GetGuid()));
        await TrustAsync(admin, jumpKey, HttpStatusCode.NoContent);

        // Then the target's own, for the target: the jump host can't vouch for it.
        var innerKey = (await SshAsync(admin, inner, probe, HttpStatusCode.BadGateway)).GetProperty("presentedKey");
        Assert.Equal((Id(inner), ServiceId(inner)), (innerKey.GetProperty("hostId").GetGuid(), innerKey.GetProperty("serviceId").GetGuid()));
        await TrustAsync(admin, innerKey, HttpStatusCode.NoContent);

        Assert.Equal("through-the-jump\n", (await SshAsync(admin, inner, probe)).GetProperty("output").GetString());
        Assert.Equal(SshServerFixture.InnerPort, (await GetHostAsync(admin, inner)).GetProperty("trustedKeys")[0].GetProperty("port").GetInt32());
        var used = Assert.Single(await AuditAsync(server, AuditActions.CredentialUse));
        Assert.Contains($"through jump host {Id(jump)} with credential {CredentialId(jump, 0)}", used.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Using_a_jump_host_needs_access_to_it_too()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var jump = await CreateTrustedHostAsync(admin, Credential(password: SshServerFixture.Password));
        var inner = await CreateHostAsync(admin, Credential(password: SshServerFixture.Password), name: "inner", jump: JumpTo(jump));
        var bobId = await server.CreateUserAsync("bob", BobPassword);
        (await admin.SendWithTokenAsync(HttpMethod.Put, $"/api/users/{bobId}/grants", new[] { new { hostId = Id(inner), level = "use" } })).EnsureSuccessStatusCode();
        using var bob = await SignInAsync(server, "bob");

        var refused = await SshAsync(bob, inner, Probe(inner), HttpStatusCode.BadGateway);
        Assert.Equal("JumpHostUnavailable", refused.GetProperty("reason").GetString());
        Assert.Empty(await AuditAsync(server, AuditActions.CredentialUse));
    }

    [Fact]
    public async Task A_jump_host_is_checked_when_it_is_set()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var jump = await CreateHostAsync(admin, Credential(password: SshServerFixture.Password), name: "bastion");
        var scoped = await CreateHostAsync(admin, $$"""{ "username": "x", "password": "y", "serviceIndexes": [] }""", name: "scoped");
        var behind = await CreateHostAsync(admin, Credential(password: SshServerFixture.Password), name: "behind", jump: JumpTo(jump));

        async Task RefusedAsync(object jumpTo, string field, JsonElement? host = null)
        {
            using var response = await admin.SendWithTokenAsync(
                host is null ? HttpMethod.Post : HttpMethod.Put,
                host is { } existing ? $"/api/hosts/{Id(existing)}" : "/api/hosts",
                new
                {
                    name = "new",
                    ipAddress = "192.0.2.20",
                    services = host is { } h ? (object)ServicesOf(h) : new[] { new { type = "hello", settings = new { greeting = "a" } } },
                    version = host?.GetProperty("version").GetUInt32(),
                    jump = jumpTo,
                });
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Token));
            Assert.True((await response.ProblemAsync()).GetProperty("errors").TryGetProperty(field, out _), field);
        }

        await RefusedAsync(new { hostId = Guid.NewGuid(), serviceId = ServiceId(jump), credentialId = CredentialId(jump, 0) }, "jump.hostId");
        await RefusedAsync(new { hostId = Id(jump), serviceId = ServiceId(jump), credentialId = CredentialId(jump, 0) }, "jump.hostId", jump);
        await RefusedAsync(new { hostId = Id(behind), serviceId = ServiceId(behind), credentialId = CredentialId(behind, 0) }, "jump.hostId");
        await RefusedAsync(new { hostId = Id(scoped), serviceId = ServiceId(scoped), credentialId = CredentialId(jump, 0) }, "jump.credentialId");
        await RefusedAsync(new { hostId = Id(scoped), serviceId = ServiceId(scoped), credentialId = CredentialId(scoped, 0) }, "jump.credentialId");
        await RefusedAsync(new { hostId = Id(jump), serviceId = ServiceId(scoped), credentialId = CredentialId(jump, 0) }, "jump.serviceId");

        // The jump host can't be given one of its own while another host goes through it.
        await RefusedAsync(new { hostId = Id(scoped), serviceId = ServiceId(scoped), credentialId = CredentialId(scoped, 0) }, "jump.hostId", jump);

        // A host the editor can't see is refused like a missing one.
        var bobId = await server.CreateUserAsync("bob", BobPassword);
        (await admin.SendWithTokenAsync(HttpMethod.Put, $"/api/users/{bobId}/grants", new[] { new { hostId = Id(behind), level = "manage" } })).EnsureSuccessStatusCode();
        using var bob = await SignInAsync(server, "bob");
        using (var hidden = await bob.SendWithTokenAsync(HttpMethod.Put, $"/api/hosts/{Id(behind)}", new
        {
            name = "behind",
            ipAddress = "127.0.0.1",
            services = ServicesOf(behind),
            version = behind.GetProperty("version").GetUInt32(),
            jump = new { hostId = Id(scoped), serviceId = ServiceId(scoped), credentialId = CredentialId(scoped, 0) },
        }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, hidden.StatusCode);
        }

        // But an edit that keeps the jump host as it is goes through.
        using (var kept = await bob.SendWithTokenAsync(HttpMethod.Put, $"/api/hosts/{Id(behind)}", new
        {
            name = "renamed",
            ipAddress = "127.0.0.1",
            services = ServicesOf(behind),
            version = behind.GetProperty("version").GetUInt32(),
            jump = JumpTo(jump),
        }))
        {
            Assert.True(kept.IsSuccessStatusCode, await kept.Content.ReadAsStringAsync(Token));
        }
    }

    [Fact]
    public async Task Deleting_the_jump_host_clears_it()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var jump = await CreateHostAsync(admin, Credential(password: SshServerFixture.Password), name: "bastion");
        var inner = await CreateHostAsync(admin, Credential(password: SshServerFixture.Password), name: "inner", jump: JumpTo(jump));

        (await admin.SendWithTokenAsync(HttpMethod.Delete, $"/api/hosts/{Id(jump)}")).EnsureSuccessStatusCode();
        Assert.Equal(JsonValueKind.Null, (await GetHostAsync(admin, inner)).GetProperty("jump").ValueKind);
    }

    // ---- Helpers ----

    private static string Credential(string? password = null, string? privateKey = null) =>
        JsonSerializer.Serialize(new { username = SshServerFixture.UserName, password, privateKey });

    private static object JumpTo(JsonElement jump) => new { hostId = Id(jump), serviceId = ServiceId(jump), credentialId = CredentialId(jump, 0) };

    private static object Probe(JsonElement host, int credential = 0, string? command = null) =>
        new { serviceId = ServiceId(host), credentialId = CredentialId(host, credential), command };

    private static Guid Id(JsonElement host) => host.GetProperty("id").GetGuid();

    private static Guid ServiceId(JsonElement host) => host.GetProperty("services")[0].GetProperty("id").GetGuid();

    private static Guid CredentialId(JsonElement host, int index) => host.GetProperty("credentials")[index].GetProperty("id").GetGuid();

    private static object[] ServicesOf(JsonElement host) =>
        [.. host.GetProperty("services").EnumerateArray().Select(s => (object)new { id = s.GetProperty("id").GetGuid(), type = "hello", port = s.GetProperty("port").GetInt32(), settings = s.GetProperty("settings") })];

    private Task<JsonElement> CreateHostAsync(HttpClient admin, string credential, string name = "ssh", string? address = null, int? port = null, object? jump = null) =>
        CreateHostAsync(admin, [credential], name, address, port, jump);

    private async Task<JsonElement> CreateHostAsync(HttpClient admin, string[] credentials, string name = "ssh", string? address = null, int? port = null, object? jump = null)
    {
        var body = $$"""
            { "name": "{{name}}", "ipAddress": "{{address ?? SshServerFixture.Address}}",
              "services": [{ "type": "hello", "port": {{port ?? ssh.Port}}, "settings": { "greeting": "a" } }],
              "credentials": [{{string.Join(", ", credentials)}}],
              "jump": {{JsonSerializer.Serialize(jump, JsonSerializerOptions.Web)}} }
            """;
        var (header, token) = await admin.AntiforgeryAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/hosts", UriKind.Relative))
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(header, token);
        using var response = await admin.SendAsync(request, Token);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Token));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    /// <summary>A host whose key is already trusted, so a test can go straight to what it's about.</summary>
    private async Task<JsonElement> CreateTrustedHostAsync(HttpClient admin, params string[] credentials)
    {
        var host = await CreateHostAsync(admin, credentials);
        var unknown = await SshAsync(admin, host, Probe(host), HttpStatusCode.BadGateway);
        await TrustAsync(admin, unknown.GetProperty("presentedKey"), HttpStatusCode.NoContent);
        return await GetHostAsync(admin, host);
    }

    private static async Task<JsonElement> GetHostAsync(HttpClient client, JsonElement host) =>
        await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/hosts/{Id(host)}", UriKind.Relative), Token);

    /// <summary>Calls the test plugin's SSH endpoint, expecting <paramref name="status"/>.</summary>
    private static async Task<JsonElement> SshAsync(HttpClient client, JsonElement host, object probe, HttpStatusCode status = HttpStatusCode.OK)
    {
        using var response = await client.SendWithTokenAsync(HttpMethod.Post, $"/api/p/hello/hosts/{Id(host)}/ssh", probe);
        var body = await response.Content.ReadAsStringAsync(Token);
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    private static async Task TrustAsync(HttpClient client, JsonElement key, HttpStatusCode status)
    {
        using var response = await client.SendWithTokenAsync(
            HttpMethod.Post,
            $"/api/hosts/{key.GetProperty("hostId").GetGuid()}/services/{key.GetProperty("serviceId").GetGuid()}/trusted-keys",
            new { algorithm = key.GetProperty("algorithm").GetString(), fingerprint = key.GetProperty("fingerprint").GetString() });
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(Token)}");
    }

    private static async Task<List<AuditEvent>> AuditAsync(ServerScenario server, string? action)
    {
        using var scope = server.Services.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<CoreDbContext>().AuditEvents.AsNoTracking();
        return await (action is null ? events : events.Where(e => e.Action == action)).ToListAsync(Token);
    }

    private static async Task<HttpClient> SignInAsync(ServerScenario server, string userName)
    {
        var client = server.Client();
        using var response = await client.LoginAsync(userName, BobPassword);
        response.EnsureSuccessStatusCode();
        return client;
    }

    private async Task<ServerScenario> ServerAsync()
    {
        var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        server.Install(TestPlugins.Package(server.DevelopmentKey));
        return server;
    }
}
