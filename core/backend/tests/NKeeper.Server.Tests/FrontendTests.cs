using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NKeeper.Testing;

namespace NKeeper.Server.Tests;

/// <summary>docs/plugin-security.md §7.2 and §7.3: the shell's policy, and plugin frontend files from verified bytes.</summary>
public sealed class FrontendTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string ImportMap = """{"imports":{"react":"/runtime/react-1.js","@nkeeper/sdk":"/runtime/sdk-1.js"}}""";

    private static readonly string Index = $"""
        <!doctype html>
        <html><head>
        <script type="importmap">{ImportMap}</script>
        <script type="module" crossorigin src="/assets/app-1a2b3c.js"></script>
        </head><body><div id="root"></div></body></html>
        """;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- The shell ----

    [Theory]
    [InlineData("/")]
    [InlineData("/hosts/some/deep/route")]
    [InlineData("/index.html")]
    public async Task Pages_carry_a_policy_that_allows_exactly_the_import_map(string path)
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        server.CreateShell(Index);
        using var client = server.Client();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Token);

        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(ImportMap)));
        var policy = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"script-src 'self' 'sha256-{hash}'", policy, StringComparison.Ordinal);
        Assert.Contains("object-src 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe", policy, StringComparison.Ordinal);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("type=\"importmap\"", await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hashed_assets_are_cached_forever_and_pages_never()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        server.CreateShell(Index);
        using var client = server.Client();

        using var asset = await client.GetAsync(new Uri("/assets/app-1a2b3c.js", UriKind.Relative), Token);
        using var page = await client.GetAsync(new Uri("/", UriKind.Relative), Token);

        Assert.Equal("public, max-age=31536000, immutable", asset.Headers.CacheControl?.ToString());
        Assert.Equal("no-cache", page.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData("/api/does-not-exist")]
    [InlineData("/plugins/nothing/1.0.0/aaaaaaaaaaaa/index.js")]
    public async Task Api_and_plugin_paths_never_fall_back_to_the_shell(string path)
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        server.CreateShell(Index);
        using var client = await server.SignedInClientAsync();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<script type=\"importmap\">{\"imports\":{}}</script>")]
    [InlineData("<script type=\"text/javascript\">window.x = 1</script>")]
    [InlineData("<script src=\"/assets/app-1a2b3c.js\">inline too</script>")]
    [InlineData("<script type=\"text/javascript\" data-type=\"importmap\">window.x = 1</script>")]
    [InlineData("<script data-src=\"x\" type=\"module\">window.x = 1</script>")]
    [InlineData("<script type=\"importmap-x\">window.x = 1</script>")]
    [InlineData("<script type=\"module\" type=\"importmap\">window.x = 1</script>")]
    public async Task The_server_refuses_to_start_with_an_extra_inline_script(string extra)
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        server.CreateShell(Index.Replace("</head>", extra + "</head>", StringComparison.Ordinal));

        var error = Record.Exception(() => server.Client());

        Assert.NotNull(error);
        Assert.Contains("index.html", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_api_only_server_serves_no_pages()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        using var client = await server.SignedInClientAsync();

        using var response = await client.GetAsync(new Uri("/", UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    // ---- Plugin frontend files ----

    [Fact]
    public async Task A_loaded_plugins_entry_module_is_served_from_its_verified_bytes()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        var frontend = Encoding.UTF8.GetBytes(TestPlugins.HelloFrontend());
        server.Install(TestPlugins.Package(server.DevelopmentKey, frontend: new Dictionary<string, byte[]> { [TestPlugins.FrontendEntry] = frontend }));
        using var client = await server.SignedInClientAsync();

        var hello = Assert.Single((await client.GetFromJsonAsync<List<PluginStatusResponse>>("/api/plugins", Json, Token))!);
        using var response = await client.GetAsync(new Uri(hello.FrontendEntry!, UriKind.Relative), Token);

        Assert.Equal($"/plugins/hello/1.0.0/{hello.PackageSha256![..12]}/index.js", hello.FrontendEntry);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(frontend)), hello.FrontendEntrySha256);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal((true, false, TimeSpan.FromDays(365)), (response.Headers.CacheControl!.Private, response.Headers.CacheControl.Public, response.Headers.CacheControl.MaxAge!.Value));
        Assert.Contains("immutable", response.Headers.CacheControl.Extensions.Select(e => e.Name));
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(frontend, await response.Content.ReadAsByteArrayAsync(Token));
    }

    [Fact]
    public async Task An_entry_name_with_url_reserved_characters_is_published_escaped()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        var frontend = Encoding.UTF8.GetBytes(TestPlugins.HelloFrontend());
        server.Install(TestPlugins.Package(
            server.DevelopmentKey,
            frontendEntry: "frontend/app #1.js",
            frontend: new Dictionary<string, byte[]> { ["frontend/app #1.js"] = frontend }));
        using var client = await server.SignedInClientAsync();

        var hello = Assert.Single((await client.GetFromJsonAsync<List<PluginStatusResponse>>("/api/plugins", Json, Token))!);
        using var response = await client.GetAsync(new Uri(hello.FrontendEntry!, UriKind.Relative), Token);

        Assert.EndsWith("/app%20%231.js", hello.FrontendEntry, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(frontend, await response.Content.ReadAsByteArrayAsync(Token));
    }

    [Theory]
    [InlineData("/plugins/hello/1.0.0/{tag}/missing.js")]
    [InlineData("/plugins/hello/1.0.0/{tag}/notes.txt")]
    [InlineData("/plugins/hello/1.0.0/000000000000/index.js")]
    [InlineData("/plugins/hello/9.9.9/{tag}/index.js")]
    [InlineData("/plugins/other/1.0.0/{tag}/index.js")]
    [InlineData("/plugins/hello/1.0.0/{tag}/..%2Fbackend%2FNKeeper.TestPlugins.Hello.dll")]
    [InlineData("/plugins/hello/1.0.0/{tag}/%2E%2E/nkeeper-plugin.json")]
    public async Task Anything_but_an_allowed_listed_frontend_file_is_not_found(string url)
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        var package = TestPlugins.Package(server.DevelopmentKey, frontend: new Dictionary<string, byte[]>
        {
            [TestPlugins.FrontendEntry] = Encoding.UTF8.GetBytes(TestPlugins.HelloFrontend()),
            ["frontend/notes.txt"] = "not an allowed type"u8.ToArray(),
        });
        server.Install(package);
        using var client = await server.SignedInClientAsync();
        var tag = (await client.GetFromJsonAsync<List<PluginStatusResponse>>("/api/plugins", Json, Token))!.Single().PackageSha256![..12];

        using var response = await client.GetAsync(new Uri(url.Replace("{tag}", tag, StringComparison.Ordinal), UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_plugin_that_isnt_loaded_serves_no_files()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        server.Install(TestPlugins.Package(server.DevelopmentKey, builtIn: false, frontend: new Dictionary<string, byte[]>
        {
            [TestPlugins.FrontendEntry] = Encoding.UTF8.GetBytes(TestPlugins.HelloFrontend()),
        }));
        using var client = await server.SignedInClientAsync();

        var hello = Assert.Single((await client.GetFromJsonAsync<List<PluginStatusResponse>>("/api/plugins", Json, Token))!);
        using var response = await client.GetAsync(new Uri($"/plugins/hello/1.0.0/{hello.PackageSha256![..12]}/index.js", UriKind.Relative), Token);

        Assert.Null(hello.FrontendEntry);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
