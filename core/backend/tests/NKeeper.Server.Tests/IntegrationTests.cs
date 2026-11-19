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
/// docs/architecture.md §7.39, SDK 1.13: integrations bound by an administrator, used only by users of their host,
/// with core's bearer sign-in; plugin settings, host data and administrators' endpoints, through the test plugin.
/// </summary>
public sealed class IntegrationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string BobPassword = "Bob-Passw0rd-123";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_bound_integration_signs_in_with_the_token_only_for_users_of_its_host()
    {
        await using var outside = new HttpsSignInTests.FakeBmc();
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var system = await CreateHostAsync(admin, "zabbix", outside.Port, "s3cret-token");
        await TrustAsync(admin, system);

        Assert.Equal("NotBound", (await IntegrationAsync(admin, HttpStatusCode.Conflict)).GetProperty("reason").GetString());
        using (var bound = await admin.SendWithTokenAsync(HttpMethod.Put, "/api/plugins/hello/integrations/server", Binding(system)))
        {
            Assert.Equal(HttpStatusCode.NoContent, bound.StatusCode);
        }

        var listed = Assert.Single((await admin.GetFromJsonAsync<JsonElement>("/api/plugins/integrations", Token)).EnumerateArray(), i => i.GetProperty("pluginId").GetString() == "hello");
        Assert.Equal("zabbix", listed.GetProperty("binding").GetProperty("hostName").GetString());

        // Core's bearer sign-in, from the credential's password, which the plugin never sees.
        var answer = await IntegrationAsync(admin);
        Assert.Equal(("zabbix", "GET / Bearer s3cret-token"), (answer.GetProperty("host").GetString(), answer.GetProperty("body").GetString()));

        // Only users of the integration's host (the owner's decision): Bob can't until he's granted it.
        var bobId = await server.CreateUserAsync("bob", BobPassword);
        using var bob = server.Client();
        (await bob.LoginAsync("bob", BobPassword)).EnsureSuccessStatusCode();
        Assert.Equal("NoAccess", (await IntegrationAsync(bob, HttpStatusCode.Conflict)).GetProperty("reason").GetString());
        using (var granted = await admin.SendWithTokenAsync(HttpMethod.Put, $"/api/users/{bobId}/grants", new[] { new { hostId = Id(system), level = "use" } }))
        {
            Assert.Equal(HttpStatusCode.OK, granted.StatusCode);
        }

        Assert.Equal("GET / Bearer s3cret-token", (await IntegrationAsync(bob)).GetProperty("body").GetString());
        // Bound twice (once to trust the certificate), cleared once.
        Assert.Equal(2, (await AuditAsync(server, AuditActions.IntegrationSet)).Count);
        Assert.Single(await AuditAsync(server, AuditActions.IntegrationCleared));
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.SendWithTokenAsync(HttpMethod.Delete, "/api/plugins/hello/integrations/server")).StatusCode);
    }

    [Fact]
    public async Task A_binding_must_fit_the_integration_and_goes_with_its_credential()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var system = await CreateHostAsync(admin, "zabbix", 443, "token");

        using (var wrongType = await admin.SendWithTokenAsync(HttpMethod.Put, "/api/plugins/hello/integrations/server", new { hostId = Id(system), serviceId = ServiceId(system, 1), credentialId = CredentialId(system) }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrongType.StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await admin.SendWithTokenAsync(HttpMethod.Put, "/api/plugins/hello/integrations/other", Binding(system))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendWithTokenAsync(HttpMethod.Put, "/api/plugins/hello/integrations/server", Binding(system))).StatusCode);

        // Deleting the credential deletes the binding.
        using (var edited = await admin.SendWithTokenAsync(HttpMethod.Put, $"/api/hosts/{Id(system)}", new
        {
            name = "zabbix",
            ipAddress = "127.0.0.1",
            version = system.GetProperty("version").GetInt32(),
            services = system.GetProperty("services").EnumerateArray().Select(s => new { id = s.GetProperty("id").GetGuid(), type = s.GetProperty("type").GetString(), port = s.GetProperty("port").GetInt32(), settings = s.GetProperty("settings") }).ToArray(),
            credentials = Array.Empty<object>(),
        }))
        {
            Assert.True(edited.IsSuccessStatusCode, await edited.Content.ReadAsStringAsync(Token));
        }

        Assert.Equal("NotBound", (await IntegrationAsync(admin, HttpStatusCode.Conflict)).GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Settings_are_the_plugins_own_for_administrators_and_host_data_is_kept_per_host()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, "web01", 443, "x");

        Assert.Equal(HttpStatusCode.NoContent, (await admin.GetAsync("/api/p/hello/admin/settings", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendWithTokenAsync(HttpMethod.Put, "/api/p/hello/admin/settings", new { version = 2, data = new { refresh = 5 } })).StatusCode);
        var settings = await admin.GetFromJsonAsync<JsonElement>("/api/p/hello/admin/settings", Token);
        Assert.Equal((2, 5), (settings.GetProperty("version").GetInt32(), settings.GetProperty("data").GetProperty("refresh").GetInt32()));
        Assert.Single(await AuditAsync(server, AuditActions.PluginSettingsChanged));

        await server.CreateUserAsync("bob", BobPassword);
        using var bob = server.Client();
        (await bob.LoginAsync("bob", BobPassword)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync("/api/p/hello/admin/settings", Token)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendWithTokenAsync(HttpMethod.Put, $"/api/p/hello/hosts/{Id(host)}/data", new { linked = 10084 })).StatusCode);
        var data = await admin.GetFromJsonAsync<JsonElement>($"/api/p/hello/hosts/{Id(host)}/data", Token);
        Assert.Equal(10084, data.GetProperty("data").GetProperty("linked").GetInt32());

        // Deleted with its host.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendWithTokenAsync(HttpMethod.Delete, $"/api/hosts/{Id(host)}")).StatusCode);
        using var scope = server.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<CoreDbContext>().HostData.ToListAsync(Token));
    }

    private async Task<ServerScenario> ServerAsync()
    {
        var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        server.Install(TestPlugins.Package(server.DevelopmentKey));
        // Another service type, which the integration doesn't take.
        server.Install(TestPlugins.Package(server.DevelopmentKey, id: "hello-other"));
        return server;
    }

    /// <summary>A host with a hello service (the integration's type) and another service, and a credential for the first only.</summary>
    private static async Task<JsonElement> CreateHostAsync(HttpClient admin, string name, int port, string token)
    {
        using var response = await admin.SendWithTokenAsync(HttpMethod.Post, "/api/hosts", new
        {
            name,
            ipAddress = "127.0.0.1",
            services = new object[] { new { type = "hello", port, settings = new { greeting = "a" } }, new { type = "hello-other", port = 22, settings = new { greeting = "a" } } },
            credentials = new[] { new { username = "api", password = token, serviceIndexes = new[] { 0 } } },
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Token));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    private static async Task TrustAsync(HttpClient admin, JsonElement host)
    {
        var unknown = await IntegrationAsync(admin, HttpStatusCode.Conflict);
        Assert.Equal("NotBound", unknown.GetProperty("reason").GetString());

        // Bind first so the plugin can show the certificate, then trust what it presented.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendWithTokenAsync(HttpMethod.Put, "/api/plugins/hello/integrations/server", Binding(host))).StatusCode);
        var presented = (await IntegrationAsync(admin, HttpStatusCode.BadGateway)).GetProperty("presented");
        using var trusted = await admin.SendWithTokenAsync(
            HttpMethod.Post,
            $"/api/hosts/{Id(host)}/services/{ServiceId(host)}/trusted-keys",
            new { algorithm = presented.GetProperty("algorithm").GetString(), fingerprint = presented.GetProperty("fingerprint").GetString() });
        Assert.True(trusted.IsSuccessStatusCode, await trusted.Content.ReadAsStringAsync(Token));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendWithTokenAsync(HttpMethod.Delete, "/api/plugins/hello/integrations/server")).StatusCode);
    }

    private static async Task<JsonElement> IntegrationAsync(HttpClient client, HttpStatusCode status = HttpStatusCode.OK)
    {
        using var response = await client.SendWithTokenAsync(HttpMethod.Post, "/api/p/hello/integration", new { serviceId = Guid.Empty });
        var body = await response.Content.ReadAsStringAsync(Token);
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    private static object Binding(JsonElement host) => new { hostId = Id(host), serviceId = ServiceId(host), credentialId = CredentialId(host) };

    private static async Task<List<AuditEvent>> AuditAsync(ServerScenario server, string action)
    {
        using var scope = server.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CoreDbContext>().AuditEvents.AsNoTracking().Where(e => e.Action == action).ToListAsync(Token);
    }

    private static Guid Id(JsonElement host) => host.GetProperty("id").GetGuid();

    private static Guid ServiceId(JsonElement host, int index = 0) => host.GetProperty("services")[index].GetProperty("id").GetGuid();

    private static Guid CredentialId(JsonElement host) => host.GetProperty("credentials")[0].GetProperty("id").GetGuid();
}
