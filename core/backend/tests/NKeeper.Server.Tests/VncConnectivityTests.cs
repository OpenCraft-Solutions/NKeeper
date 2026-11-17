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
/// docs/architecture.md §7.20: VNC desktops core opens for a plugin (through the test plugin's <c>/desktop</c>
/// endpoint), through a real guacd to a real VNC server (TigerVNC). Ported from 1.x's <c>VncSessionTests</c>.
/// </summary>
public sealed class VncConnectivityTests(PostgresFixture postgres, VncServerFixture vnc) : IClassFixture<PostgresFixture>, IClassFixture<VncServerFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_password_only_credential_opens_the_desktop_without_a_trust_prompt()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, new { password = VncServerFixture.Password });

        var result = await DesktopAsync(admin, host, new { serviceId = ServiceId(host), credentialId = CredentialId(host), protocol = "vnc" });

        var opcodes = result.GetProperty("opcodes").EnumerateArray().Select(o => o.GetString()).ToList();
        Assert.Equal("ready", opcodes[0]);
        Assert.Contains("img", opcodes);
        Assert.DoesNotContain("error", opcodes);

        // No server identity to check, so nothing was presented or trusted.
        var reloaded = await admin.GetFromJsonAsync<JsonElement>(new Uri($"/api/hosts/{Id(host)}", UriKind.Relative), Token);
        Assert.Empty(reloaded.GetProperty("trustedKeys").EnumerateArray());
        var used = Assert.Single(await AuditAsync(server, AuditActions.CredentialUse));
        Assert.Contains($"vnc to service {ServiceId(host)}, plugin hello, credential {CredentialId(host)}", used.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Closing_the_desktop_closes_its_vnc_connection()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, new { password = VncServerFixture.Password });
        var before = await WaitForConnectionsAsync(0);

        await DesktopAsync(admin, host, new { serviceId = ServiceId(host), credentialId = CredentialId(host), protocol = "vnc" });

        // The probe disposed the desktop when it returned.
        Assert.Equal(before, await WaitForConnectionsAsync(before));
    }

    [Fact]
    public async Task A_wrong_password_is_reported_by_guacd()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, new { password = "not-the-password" });

        var result = await DesktopAsync(admin, host, new { serviceId = ServiceId(host), credentialId = CredentialId(host), protocol = "vnc" });

        Assert.Contains("error", result.GetProperty("opcodes").EnumerateArray().Select(o => o.GetString()));
    }

    [Fact]
    public async Task A_credential_without_a_password_is_refused_before_connecting()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, new { username = "someone" });

        var failed = await DesktopAsync(admin, host, new { serviceId = ServiceId(host), credentialId = CredentialId(host), protocol = "vnc" }, HttpStatusCode.BadGateway);

        Assert.Equal("NoSecret", failed.GetProperty("reason").GetString());
        Assert.Empty(await AuditAsync(server, AuditActions.CredentialUse));
    }

    [Fact]
    public async Task Vnc_takes_no_rdp_security_setting()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, new { password = VncServerFixture.Password });

        using var response = await admin.SendWithTokenAsync(HttpMethod.Post, $"/api/p/hello/hosts/{Id(host)}/desktop", new { serviceId = ServiceId(host), credentialId = CredentialId(host), protocol = "vnc", security = "tls" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- Helpers ----

    private static Guid Id(JsonElement host) => host.GetProperty("id").GetGuid();

    private static Guid ServiceId(JsonElement host) => host.GetProperty("services")[0].GetProperty("id").GetGuid();

    private static Guid CredentialId(JsonElement host) => host.GetProperty("credentials")[0].GetProperty("id").GetGuid();

    private async Task<int> WaitForConnectionsAsync(int expected)
    {
        var last = await vnc.OpenConnectionsAsync();
        for (var attempt = 0; attempt < 75 && last != expected; attempt++)
        {
            await Task.Delay(200, Token);
            last = await vnc.OpenConnectionsAsync();
        }

        return last;
    }

    private async Task<JsonElement> CreateHostAsync(HttpClient admin, object credential)
    {
        using var response = await admin.SendWithTokenAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = $"vnc-{Guid.NewGuid():N}",
            ipAddress = VncServerFixture.Address,
            services = new[] { new { type = "hello", port = vnc.Port, settings = new { greeting = "a" } } },
            credentials = new[] { credential },
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Token));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    /// <summary>Calls the test plugin's desktop endpoint, expecting <paramref name="status"/>.</summary>
    private static async Task<JsonElement> DesktopAsync(HttpClient client, JsonElement host, object probe, HttpStatusCode status = HttpStatusCode.OK)
    {
        using var response = await client.SendWithTokenAsync(HttpMethod.Post, $"/api/p/hello/hosts/{Id(host)}/desktop", probe);
        var body = await response.Content.ReadAsStringAsync(Token);
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    private static async Task<List<AuditEvent>> AuditAsync(ServerScenario server, string action)
    {
        using var scope = server.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CoreDbContext>().AuditEvents.AsNoTracking().Where(e => e.Action == action).ToListAsync(Token);
    }

    private async Task<ServerScenario> ServerAsync()
    {
        var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        server.Settings["Guacd:Address"] = vnc.GuacdAddress;
        server.Install(TestPlugins.Package(server.DevelopmentKey));
        return server;
    }
}
