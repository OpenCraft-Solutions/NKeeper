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
/// docs/architecture.md §7.20, §7.21: the desktop plugin, opening an RDP session through a real guacd to a real xrdp
/// server, with the server's certificate trusted in-band and guacd's instructions relayed to the browser.
/// </summary>
public sealed class DesktopTests(PostgresFixture postgres, RdpServerFixture rdp) : IClassFixture<PostgresFixture>, IClassFixture<RdpServerFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_desktop_opens_once_its_certificate_is_trusted_and_relays_guacd()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        server.Settings["Guacd:Address"] = rdp.GuacdAddress;
        server.Install(TestPlugins.DesktopPackage(server.DevelopmentKey));
        var client = server.Client();
        using var login = await client.LoginAsync(ServerScenario.AdminUserName, ServerScenario.AdminPassword);
        login.EnsureSuccessStatusCode();
        var cookie = string.Join("; ", login.SetCookies().Select(c => c.Split(';')[0]));
        using var created = await client.SendWithTokenAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = "xrdp",
            ipAddress = RdpServerFixture.Address,
            services = new[] { new { type = "rdp", port = rdp.Port, settings = new { security = "tls" } } },
            credentials = new[] { new { username = RdpServerFixture.UserName, password = RdpServerFixture.Password } },
        });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Token));
        var host = await created.Content.ReadFromJsonAsync<JsonElement>(Token);

        await using var desktop = await SessionClient.ConnectAsync(server, cookie, "rdp");
        await desktop.SendAsync(new
        {
            type = "start",
            hostId = host.GetProperty("id").GetGuid(),
            serviceId = host.GetProperty("services")[0].GetProperty("id").GetGuid(),
            credentialId = host.GetProperty("credentials")[0].GetProperty("id").GetGuid(),
            options = new { width = 1024, height = 768, dpi = 96 },
        });

        // The server's certificate, in-band: a certificate this time, with its subject.
        var prompt = await desktop.ExpectAsync("hostKey");
        Assert.Equal(("tls-certificate", "x509"), (prompt.GetProperty("kind").GetString(), prompt.GetProperty("algorithm").GetString()));
        Assert.False(string.IsNullOrEmpty(prompt.GetProperty("subject").GetString()));
        await desktop.SendAsync(new { type = "hostKeyDecision", trust = true });
        await desktop.ExpectAsync("connected");

        // guacd's "ready" first, then the remote desktop's drawing.
        await desktop.ReadTextUntilAsync("5.ready,");
        await desktop.SendTextAsync("4.size,4.1024,3.768,2.96;");
        await desktop.ReadTextUntilAsync(".img,");

        using var scope = server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreDbContext>();
        var key = await db.HostTrustedKeys.AsNoTracking().SingleAsync(Token);
        Assert.Equal(TrustedKeyKind.TlsCertificate, key.Kind);
        Assert.Contains(await db.AuditEvents.AsNoTracking().Where(e => e.Action == AuditActions.CredentialUse).Select(e => e.Detail).ToListAsync(Token), d => d.StartsWith("rdp to service ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_guacd_the_desktop_says_the_gateway_is_unavailable()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        server.Settings["Guacd:Address"] = "127.0.0.1:1";
        server.Install(TestPlugins.DesktopPackage(server.DevelopmentKey));
        var client = server.Client();
        using var login = await client.LoginAsync(ServerScenario.AdminUserName, ServerScenario.AdminPassword);
        var cookie = string.Join("; ", login.SetCookies().Select(c => c.Split(';')[0]));
        using var created = await client.SendWithTokenAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = "xrdp",
            ipAddress = RdpServerFixture.Address,
            services = new[] { new { type = "rdp", port = rdp.Port, settings = new { security = "tls" } } },
            credentials = new[] { new { username = RdpServerFixture.UserName, password = RdpServerFixture.Password } },
        });
        var host = await created.Content.ReadFromJsonAsync<JsonElement>(Token);
        var start = new
        {
            type = "start",
            hostId = host.GetProperty("id").GetGuid(),
            serviceId = host.GetProperty("services")[0].GetProperty("id").GetGuid(),
            credentialId = host.GetProperty("credentials")[0].GetProperty("id").GetGuid(),
        };

        // Trusting the certificate comes first; then guacd is found missing.
        await using var desktop = await SessionClient.ConnectAsync(server, cookie, "rdp");
        await desktop.SendAsync(start);
        await desktop.ExpectAsync("hostKey");
        await desktop.SendAsync(new { type = "hostKeyDecision", trust = true });
        Assert.Equal("gateway_unavailable", (await desktop.ExpectAsync("error")).GetProperty("code").GetString());
    }
}
