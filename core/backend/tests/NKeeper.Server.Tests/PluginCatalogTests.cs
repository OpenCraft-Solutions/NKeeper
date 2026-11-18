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
        using var request = await admin.SendWithTokenAsync(HttpMethod.Post, "/api/plugins/restart");
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

    [Fact]
    public async Task Disabling_a_plugin_is_recorded_for_the_next_restart_and_enabling_it_again_too()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        server.Install(TestPlugins.Package(server.DevelopmentKey));
        using var admin = await server.SignedInClientAsync();

        using var disabled = await admin.SendWithTokenAsync(HttpMethod.Post, "/api/plugins/hello/disable");
        var body = await disabled.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        Assert.Equal(["hello"], body.GetProperty("changed").EnumerateArray().Select(e => e.GetString()));
        Assert.False(body.GetProperty("catalog").GetProperty("switches").GetProperty("hello").GetBoolean());
        using var enabled = await admin.SendWithTokenAsync(HttpMethod.Post, "/api/plugins/hello/enable");
        Assert.True((await enabled.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("catalog").GetProperty("switches").GetProperty("hello").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.SendWithTokenAsync(HttpMethod.Post, "/api/plugins/nothing/disable")).StatusCode);
    }

    [Fact]
    public async Task An_upload_that_verifies_is_left_for_roots_helper_and_trusting_keys_needs_roots_permission()
    {
        var requests = Directory.CreateTempSubdirectory("nkeeper-requests-").FullName;
        try
        {
            await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
            server.Settings["Plugins:Catalog:RequestDirectory"] = requests;
            using var admin = await server.SignedInClientAsync();
            var package = TestPlugins.Package(server.DevelopmentKey, id: "uploaded");

            using var upload = await Upload(admin, package.FileName, package.Content);
            using var junk = await Upload(admin, "junk-1.0.0.nkp", [1, 2, 3]);
            using var trust = await admin.SendWithTokenAsync(HttpMethod.Post, "/api/plugins/trust", new { keyFile = "{}", password = ServerScenario.AdminPassword });

            Assert.Equal(HttpStatusCode.Accepted, upload.StatusCode);
            Assert.Equal(["uploaded.json", "uploaded.nkp"], Directory.GetFiles(requests).Select(Path.GetFileName).Order(StringComparer.Ordinal));
            Assert.Equal(package.Content, await File.ReadAllBytesAsync(Path.Combine(requests, "uploaded.nkp"), TestContext.Current.CancellationToken));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, junk.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, trust.StatusCode);
        }
        finally
        {
            Directory.Delete(requests, recursive: true);
        }
    }

    private static async Task<HttpResponseMessage> Upload(HttpClient client, string name, byte[] content)
    {
        var (header, token) = await client.AntiforgeryAsync();
        var form = new MultipartFormDataContent { { new ByteArrayContent(content), "package", name } };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/plugins/upload") { Content = form };
        request.Headers.Add(header, token);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
