using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NKeeper.Testing;

namespace NKeeper.Server.Tests;

/// <summary>The plugins page's side of the catalog (docs/architecture.md §9.1): for administrators, and installing nothing itself.</summary>
public sealed class PluginCatalogTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string BobPassword = "Bob-Passw0rd-123";

    [Fact]
    public async Task Only_administrators_see_the_catalog_and_a_server_without_one_says_its_off()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        using var admin = await server.SignedInClientAsync();
        await server.CreateUserAsync("bob", BobPassword);
        using var bob = server.Client();
        (await bob.LoginAsync("bob", BobPassword)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync("/api/plugins/catalog", TestContext.Current.CancellationToken)).StatusCode);
        var catalog = await admin.GetFromJsonAsync<JsonElement>("/api/plugins/catalog", TestContext.Current.CancellationToken);
        Assert.False(catalog.GetProperty("enabled").GetBoolean());
        Assert.False(catalog.GetProperty("canRequest").GetBoolean());
        using var request = await admin.SendWithTokenAsync(HttpMethod.Post, "/api/plugins/catalog/requests", new { id = "kvm" });
        Assert.Equal(HttpStatusCode.Conflict, request.StatusCode);
    }

    [Fact]
    public async Task A_request_needs_something_the_last_refresh_offered()
    {
        var requests = Directory.CreateTempSubdirectory("nkeeper-requests-").FullName;
        try
        {
            await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
            server.Settings["Plugins:Catalog:Url"] = "https://catalog.invalid/catalog.json";
            server.Settings["Plugins:Catalog:RequestDirectory"] = requests;
            using var admin = await server.SignedInClientAsync();

            var catalog = await admin.GetFromJsonAsync<JsonElement>("/api/plugins/catalog", TestContext.Current.CancellationToken);
            Assert.True(catalog.GetProperty("enabled").GetBoolean());
            Assert.True(catalog.GetProperty("canRequest").GetBoolean());
            using var request = await admin.SendWithTokenAsync(HttpMethod.Post, "/api/plugins/catalog/requests", new { id = "kvm" });

            Assert.Equal(HttpStatusCode.NotFound, request.StatusCode);
            Assert.Empty(Directory.GetFiles(requests));
        }
        finally
        {
            Directory.Delete(requests, recursive: true);
        }
    }
}
