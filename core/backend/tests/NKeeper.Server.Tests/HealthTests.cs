using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using NKeeper.Testing;

namespace NKeeper.Server.Tests;

/// <summary>docs/architecture.md §7.13: core runs the service types' TCP probes, and shows each user only their hosts' results.</summary>
public sealed class HealthTests(PostgresFixture postgres, SshServerFixture ssh) : IClassFixture<PostgresFixture>, IClassFixture<SshServerFixture>
{
    private const string BobPassword = "Bob-password-123!";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_host_is_checked_on_demand_and_only_its_users_see_the_results()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var closedPort = ClosedPort();
        var host = await CreateHostAsync(admin, "checked", services:
        [
            new { type = "hello", port = ssh.Port, address = (string?)null, settings = new { greeting = "a" } },
            new { type = "hello", port = closedPort, address = (string?)"127.0.0.1", settings = new { greeting = "a" } },
            new { type = "hello", port = ssh.Port, address = (string?)null, settings = new { greeting = "b" } },
        ]);
        var services = host.GetProperty("services").EnumerateArray().Select(s => s.GetProperty("id").GetGuid()).ToList();

        using var checkedNow = await admin.SendWithTokenAsync(HttpMethod.Post, $"/api/hosts/{Id(host)}/health");
        Assert.Equal(HttpStatusCode.OK, checkedNow.StatusCode);
        var results = (await checkedNow.Content.ReadFromJsonAsync<JsonElement>(Token)).EnumerateArray().ToDictionary(r => r.GetProperty("serviceId").GetGuid());
        Assert.Equal("up", results[services[0]].GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, results[services[0]].GetProperty("reason").ValueKind);
        Assert.Equal(("down", "refused"), (results[services[1]].GetProperty("state").GetString(), results[services[1]].GetProperty("reason").GetString()));

        // The same address and port is checked once.
        Assert.Equal(results[services[0]].GetProperty("checkedAtUtc").GetDateTimeOffset(), results[services[2]].GetProperty("checkedAtUtc").GetDateTimeOffset());

        var all = await admin.GetFromJsonAsync<JsonElement>(new Uri("/api/health", UriKind.Relative), Token);
        Assert.Equal(services.Order(), all.EnumerateArray().Select(r => r.GetProperty("serviceId").GetGuid()).Order());

        // Someone who can't see the host can't check it, or see its results.
        await server.CreateUserAsync("bob", BobPassword);
        var bob = server.Client();
        (await bob.LoginAsync("bob", BobPassword)).EnsureSuccessStatusCode();
        using var refused = await bob.SendWithTokenAsync(HttpMethod.Post, $"/api/hosts/{Id(host)}/health");
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Empty((await bob.GetFromJsonAsync<JsonElement>(new Uri("/api/health", UriKind.Relative), Token)).EnumerateArray());
    }

    [Fact]
    public async Task A_host_behind_a_jump_host_is_not_checked()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var jumpHost = await CreateHostAsync(
            admin,
            "jump",
            services: [new { type = "hello", port = ssh.Port, address = (string?)null, settings = new { greeting = "a" } }],
            credentials: [new { username = "jump", password = "jump-password" }]);
        var host = await CreateHostAsync(
            admin,
            "behind",
            services: [new { type = "hello", port = ssh.Port, address = (string?)null, settings = new { greeting = "a" } }],
            jump: new
            {
                hostId = Id(jumpHost),
                serviceId = jumpHost.GetProperty("services")[0].GetProperty("id").GetGuid(),
                credentialId = jumpHost.GetProperty("credentials")[0].GetProperty("id").GetGuid(),
            });

        using var checkedNow = await admin.SendWithTokenAsync(HttpMethod.Post, $"/api/hosts/{Id(host)}/health");
        Assert.Equal(HttpStatusCode.OK, checkedNow.StatusCode);
        Assert.Empty((await checkedNow.Content.ReadFromJsonAsync<JsonElement>(Token)).EnumerateArray());
    }

    /// <summary>A local port nothing listens on: one the system just gave out and took back.</summary>
    private static int ClosedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<JsonElement> CreateHostAsync(HttpClient admin, string name, object[] services, object[]? credentials = null, object? jump = null)
    {
        using var response = await admin.SendWithTokenAsync(HttpMethod.Post, "/api/hosts", new
        {
            name,
            ipAddress = SshServerFixture.Address,
            services,
            credentials = credentials ?? [],
            jump,
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Token));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    private static Guid Id(JsonElement host) => host.GetProperty("id").GetGuid();

    private async Task<ServerScenario> ServerAsync()
    {
        var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        server.Install(TestPlugins.Package(server.DevelopmentKey));
        return server;
    }
}
