using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NKeeper.Testing;

namespace NKeeper.Server.Tests;

/// <summary>
/// docs/architecture.md §7.11: the real <c>ssh</c> plugin, packaged from its build output and loaded like any
/// plugin, opening terminals on a real OpenSSH server through core's stream sessions.
/// </summary>
public sealed class SshPluginTests(PostgresFixture postgres, SshServerFixture ssh) : IClassFixture<PostgresFixture>, IClassFixture<SshServerFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_plugin_contributes_its_service_and_session_types()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var plugins = await admin.GetFromJsonAsync<JsonElement>(new Uri("/api/plugins", UriKind.Relative), Token);
        var plugin = plugins.EnumerateArray().Single(p => p.GetProperty("id").GetString() == "ssh");
        Assert.Equal("loaded", plugin.GetProperty("state").GetString());
        var serviceType = Assert.Single(plugin.GetProperty("serviceTypes").EnumerateArray());
        Assert.Equal(("SSH", 22, "tcp"), (serviceType.GetProperty("label").GetString(), serviceType.GetProperty("defaultPort").GetInt32(), serviceType.GetProperty("health").GetString()));
        var action = Assert.Single(plugin.GetProperty("hostActions").EnumerateArray());
        Assert.Equal(("ssh.terminal", "Terminal", "ssh", "required"), (
            action.GetProperty("id").GetString(),
            action.GetProperty("label").GetString(),
            action.GetProperty("sessionType").GetString(),
            action.GetProperty("credential").GetString()));
        var sessionType = Assert.Single(plugin.GetProperty("sessionTypes").EnumerateArray());
        Assert.Equal(("ssh", "Terminal"), (sessionType.GetProperty("id").GetString(), sessionType.GetProperty("label").GetString()));
    }

    [Fact]
    public async Task A_terminal_opens_at_the_size_asked_resizes_and_ends_when_the_shell_exits()
    {
        await using var server = await ServerAsync();
        var client = server.Client();
        using var login = await client.LoginAsync(ServerScenario.AdminUserName, ServerScenario.AdminPassword);
        login.EnsureSuccessStatusCode();
        var cookie = string.Join("; ", login.SetCookies().Select(c => c.Split(';')[0]));
        using var created = await client.SendWithTokenAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = "ssh",
            ipAddress = SshServerFixture.Address,
            services = new[] { new { type = "ssh", port = ssh.Port, settings = new { } } },
            credentials = new[] { new { username = SshServerFixture.UserName, privateKey = ssh.PrivateKey } },
        });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Token));
        var host = await created.Content.ReadFromJsonAsync<JsonElement>(Token);

        await using var terminal = await SessionClient.ConnectAsync(server, cookie, "ssh");
        await terminal.SendAsync(new
        {
            type = "start",
            hostId = host.GetProperty("id").GetGuid(),
            serviceId = host.GetProperty("services")[0].GetProperty("id").GetGuid(),
            credentialId = host.GetProperty("credentials")[0].GetProperty("id").GetGuid(),
            options = new { columns = 100, rows = 30 },
        });
        await terminal.ExpectAsync("hostKey");
        await terminal.SendAsync(new { type = "hostKeyDecision", trust = true });
        await terminal.ExpectAsync("connected");

        await terminal.SendBinaryAsync(Encoding.UTF8.GetBytes("echo size-$(stty size | tr ' ' x) $TERM\n"));
        await terminal.ReadUntilAsync("size-30x100 xterm-256color");

        // A resize with dimensions of the wrong kind falls back, rather than ending the terminal.
        await terminal.SendAsync(new { type = "resize", columns = "120", rows = true });
        await terminal.SendAsync(new { type = "resize", columns = 120, rows = 40 });
        await terminal.SendBinaryAsync(Encoding.UTF8.GetBytes("echo size-$(stty size | tr ' ' x)\n"));
        await terminal.ReadUntilAsync("size-40x120");

        await terminal.SendBinaryAsync(Encoding.UTF8.GetBytes("exit\n"));
        Assert.Equal("closed", (await terminal.ExpectAsync("closed")).GetProperty("reason").GetString());
    }

    private async Task<ServerScenario> ServerAsync()
    {
        var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        server.Install(TestPlugins.SshPackage(server.DevelopmentKey));
        return server;
    }
}
