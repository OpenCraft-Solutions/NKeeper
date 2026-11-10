using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Core.Persistence;
using NKeeper.Testing;

namespace NKeeper.Server.Tests;

/// <summary>docs/architecture.md §7.4: services on hosts, of the types plugins contribute. The test plugin contributes <c>hello</c>.</summary>
public sealed class ServiceTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_host_keeps_its_services_in_order_with_their_settings_checked_and_tidied()
    {
        await using var server = await ServerWithHelloAsync();
        using var admin = await server.SignedInClientAsync();

        var host = await CreateHostAsync(admin, """
            [
              { "type": "hello", "label": " Main ", "settings": { "greeting": "  hi  ", "repeat": 3, "loud": true, "language": "fr" } },
              { "type": "hello", "port": 7007, "address": "2001:DB8::1", "settings": { "greeting": "yo", "repeat": null, "language": null } }
            ]
            """);

        var services = host.GetProperty("services").EnumerateArray().ToList();
        Assert.Equal(2, services.Count);
        Assert.Equal("Main", services[0].GetProperty("label").GetString());
        AssertJson("""{"greeting":"hi","repeat":3,"loud":true,"language":"fr"}""", services[0].GetProperty("settings"));
        Assert.True(services[0].GetProperty("available").GetBoolean());
        Assert.Equal(JsonValueKind.Null, services[0].GetProperty("port").ValueKind);
        Assert.Equal(7007, services[1].GetProperty("port").GetInt32());
        Assert.Equal("2001:db8::1", services[1].GetProperty("address").GetString());
        AssertJson("""{"greeting":"yo"}""", services[1].GetProperty("settings"));
    }

    [Theory]
    [InlineData("""[{ "type": "nope", "settings": {} }]""", "services[0].type")]
    [InlineData("""[{ "settings": {} }]""", "services[0].type")]
    [InlineData("""[null]""", "services[0].type")]
    [InlineData("""[{ "type": "hello" }]""", "services[0].settings.greeting")]
    [InlineData("""[{ "type": "hello", "settings": { "greeting": "   " } }]""", "services[0].settings.greeting")]
    [InlineData("""[{ "type": "hello", "settings": [] }]""", "services[0].settings")]
    [InlineData("""[{ "type": "hello", "settings": { "greeting": "hi", "extra": 1 } }]""", "services[0].settings.extra")]
    [InlineData("""[{ "type": "hello", "settings": { "greeting": "this greeting is far too long" } }]""", "services[0].settings.greeting")]
    [InlineData("""[{ "type": "hello", "settings": { "greeting": 5 } }]""", "services[0].settings.greeting")]
    [InlineData("""[{ "type": "hello", "settings": { "greeting": "hi", "repeat": 6 } }]""", "services[0].settings.repeat")]
    [InlineData("""[{ "type": "hello", "settings": { "greeting": "hi", "repeat": 1.5 } }]""", "services[0].settings.repeat")]
    [InlineData("""[{ "type": "hello", "settings": { "greeting": "hi", "loud": "yes" } }]""", "services[0].settings.loud")]
    [InlineData("""[{ "type": "hello", "settings": { "greeting": "hi", "language": "de" } }]""", "services[0].settings.language")]
    [InlineData("""[{ "type": "hello", "port": 0, "settings": { "greeting": "hi" } }]""", "services[0].port")]
    [InlineData("""[{ "type": "hello", "port": 65536, "settings": { "greeting": "hi" } }]""", "services[0].port")]
    [InlineData("""[{ "type": "hello", "address": "not an address", "settings": { "greeting": "hi" } }]""", "services[0].address")]
    [InlineData("""[{ "id": "00000000-0000-0000-0000-000000000001", "type": "hello", "settings": { "greeting": "hi" } }]""", "services[0].id")]
    public async Task An_invalid_service_reports_the_field(string services, string field)
    {
        await using var server = await ServerWithHelloAsync();
        using var admin = await server.SignedInClientAsync();

        using var response = await SendRawAsync(admin, HttpMethod.Post, "/api/hosts", HostBody(services));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, (await response.ProblemAsync()).GetProperty("errors").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task A_host_has_at_most_50_services()
    {
        await using var server = await ServerWithHelloAsync();
        using var admin = await server.SignedInClientAsync();
        var one = """{ "type": "hello", "settings": { "greeting": "hi" } }""";

        using var fifty = await SendRawAsync(admin, HttpMethod.Post, "/api/hosts", HostBody($"[{string.Join(',', Enumerable.Repeat(one, 50))}]"));
        using var more = await SendRawAsync(admin, HttpMethod.Post, "/api/hosts", HostBody($"[{string.Join(',', Enumerable.Repeat(one, 51))}]", name: "web02"));

        Assert.Equal(HttpStatusCode.Created, fifty.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, more.StatusCode);
    }

    [Fact]
    public async Task An_edit_keeps_the_ids_of_services_it_sends_back_and_removes_the_rest()
    {
        await using var server = await ServerWithHelloAsync();
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, """
            [{ "type": "hello", "label": "a", "settings": { "greeting": "a" } }, { "type": "hello", "label": "b", "settings": { "greeting": "b" } }]
            """);
        var ids = host.GetProperty("services").EnumerateArray().Select(s => s.GetProperty("id").GetGuid()).ToList();

        using var updated = await SendRawAsync(admin, HttpMethod.Put, $"/api/hosts/{Id(host)}", HostBody(
            $$"""[{ "type": "hello", "label": "new", "settings": { "greeting": "n" } }, { "id": "{{ids[0]}}", "type": "hello", "label": "a2", "settings": { "greeting": "a2" } }]""",
            version: host.GetProperty("version").GetUInt32()));

        updated.EnsureSuccessStatusCode();
        var services = (await updated.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("services").EnumerateArray().ToList();
        Assert.Equal(["new", "a2"], services.Select(s => s.GetProperty("label").GetString()));
        Assert.Equal(ids[0], services[1].GetProperty("id").GetGuid());
        Assert.DoesNotContain(services, s => s.GetProperty("id").GetGuid() == ids[1]);
    }

    [Fact]
    public async Task Settings_stored_at_an_older_version_are_upgraded_on_read_without_being_saved()
    {
        await using var server = await ServerWithHelloAsync();
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, "[]");
        var serviceId = await AddStoredServiceAsync(server, Id(host), "hello", """{"text":"old"}""", settingsVersion: 1);

        var read = await admin.GetFromJsonAsync<JsonElement>($"/api/hosts/{Id(host)}", Token);
        var scoped = await admin.GetFromJsonAsync<JsonElement>($"/api/p/hello/hosts/{Id(host)}/services", Token);

        AssertJson("""{"greeting":"old"}""", read.GetProperty("services")[0].GetProperty("settings"));
        AssertJson("""{"greeting":"old"}""", scoped[0].GetProperty("settings"));
        var stored = await StoredServiceAsync(server, serviceId);
        Assert.Equal((1, "old"), (stored.SettingsVersion, JsonDocument.Parse(stored.Settings).RootElement.GetProperty("text").GetString()));
    }

    [Fact]
    public async Task A_service_whose_type_is_unavailable_is_kept_as_it_is_but_cant_be_changed_or_added()
    {
        await using var server = await ServerWithHelloAsync();
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, "[]");
        var serviceId = await AddStoredServiceAsync(server, Id(host), "gone", """{"anything":[1,2]}""", settingsVersion: 3, label: "Old");

        var read = await admin.GetFromJsonAsync<JsonElement>($"/api/hosts/{Id(host)}", Token);
        var service = read.GetProperty("services")[0];
        Assert.False(service.GetProperty("available").GetBoolean());
        AssertJson("""{"anything":[1,2]}""", service.GetProperty("settings"));

        var asItIs = $$"""[{ "id": "{{serviceId}}", "type": "gone", "label": "Old", "settings": {"anything":[1,2]} }]""";
        using var kept = await SendRawAsync(admin, HttpMethod.Put, $"/api/hosts/{Id(host)}", HostBody(asItIs, version: read.GetProperty("version").GetUInt32()));
        kept.EnsureSuccessStatusCode();
        var version = (await kept.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("version").GetUInt32();

        var changed = $$"""[{ "id": "{{serviceId}}", "type": "gone", "label": "New", "settings": {"anything":[1,2]} }]""";
        using var refused = await SendRawAsync(admin, HttpMethod.Put, $"/api/hosts/{Id(host)}", HostBody(changed, version: version));
        using var added = await SendRawAsync(admin, HttpMethod.Post, "/api/hosts", HostBody("""[{ "type": "gone", "settings": {} }]""", name: "web02"));
        using var removed = await SendRawAsync(admin, HttpMethod.Put, $"/api/hosts/{Id(host)}", HostBody("[]", version: version));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, added.StatusCode);
        removed.EnsureSuccessStatusCode();
        Assert.Empty((await removed.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("services").EnumerateArray());
    }

    [Fact]
    public async Task A_host_scoped_endpoint_sees_the_services_with_their_defaults_applied()
    {
        await using var server = await ServerWithHelloAsync();
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, """
            [{ "type": "hello", "settings": { "greeting": "a" } }, { "type": "hello", "port": 9, "address": "bmc.example.com", "settings": { "greeting": "b" } }]
            """);
        await AddStoredServiceAsync(server, Id(host), "gone", "{}", settingsVersion: 1, position: 2);

        var services = await admin.GetFromJsonAsync<JsonElement>($"/api/p/hello/hosts/{Id(host)}/services", Token);

        Assert.Equal(
            [("hello", 7, "192.0.2.10", true), ("hello", 9, "bmc.example.com", true), ("gone", 0, "192.0.2.10", false)],
            services.EnumerateArray().Select(s => (
                s.GetProperty("type").GetString(),
                s.GetProperty("port").ValueKind == JsonValueKind.Null ? 0 : s.GetProperty("port").GetInt32(),
                s.GetProperty("address").GetString(),
                s.GetProperty("available").GetBoolean())));
    }

    [Fact]
    public async Task The_plugin_list_carries_each_plugins_service_types_and_their_schemas()
    {
        await using var server = await ServerWithHelloAsync();
        using var admin = await server.SignedInClientAsync();

        var plugins = await admin.GetFromJsonAsync<JsonElement>("/api/plugins", Token);

        var type = Assert.Single(plugins.EnumerateArray().Single(p => p.GetProperty("id").GetString() == "hello").GetProperty("serviceTypes").EnumerateArray());
        Assert.Equal(("hello", "Hello", 7, 2), (type.GetProperty("id").GetString(), type.GetProperty("label").GetString(), type.GetProperty("defaultPort").GetInt32(), type.GetProperty("settingsVersion").GetInt32()));
        var settings = type.GetProperty("settings").EnumerateArray().ToList();
        Assert.Equal(["greeting:text", "repeat:number", "loud:switch", "language:choice"], settings.Select(s => $"{s.GetProperty("key").GetString()}:{s.GetProperty("kind").GetString()}"));
        Assert.True(settings[0].GetProperty("required").GetBoolean());
        Assert.Equal(20, settings[0].GetProperty("maxLength").GetInt32());
        Assert.Equal((1, 5), (settings[1].GetProperty("minimum").GetInt64(), settings[1].GetProperty("maximum").GetInt64()));
        Assert.Equal(["en", "fr"], settings[3].GetProperty("choices").EnumerateArray().Select(c => c.GetString()));
    }

    private static Guid Id(JsonElement host) => host.GetProperty("id").GetGuid();

    /// <summary>Compares JSON by value: PostgreSQL's <c>jsonb</c> doesn't keep the order of an object's keys.</summary>
    private static void AssertJson(string expected, JsonElement actual) =>
        Assert.True(
            System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(expected), System.Text.Json.Nodes.JsonNode.Parse(actual.GetRawText())),
            $"Expected {expected}, got {actual.GetRawText()}");

    private static string HostBody(string services, string name = "web01", uint? version = null) =>
        $$"""{ "name": "{{name}}", "ipAddress": "192.0.2.10", "services": {{services}}{{(version is { } v ? $", \"version\": {v}" : "")}} }""";

    private static async Task<JsonElement> CreateHostAsync(HttpClient admin, string services)
    {
        using var response = await SendRawAsync(admin, HttpMethod.Post, "/api/hosts", HostBody(services));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Token));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    /// <summary>Writes a service straight to the database, as an older version of a type, or a type since removed, would have.</summary>
    private static async Task<Guid> AddStoredServiceAsync(ServerScenario server, Guid hostId, string type, string settings, int settingsVersion, string? label = null, int position = 0)
    {
        using var scope = server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreDbContext>();
        var service = new HostService { Id = Guid.NewGuid(), HostId = hostId, Position = position, Type = type, Label = label, Settings = settings, SettingsVersion = settingsVersion };
        db.HostServices.Add(service);
        await db.SaveChangesAsync(Token);
        return service.Id;
    }

    private static async Task<HostService> StoredServiceAsync(ServerScenario server, Guid id)
    {
        using var scope = server.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CoreDbContext>().HostServices.AsNoTracking().SingleAsync(s => s.Id == id, Token);
    }

    private static async Task<HttpResponseMessage> SendRawAsync(HttpClient client, HttpMethod method, string path, string body)
    {
        var (header, token) = await client.AntiforgeryAsync();
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative))
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(header, token);
        return await client.SendAsync(request, Token);
    }

    private async Task<ServerScenario> ServerWithHelloAsync()
    {
        var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        server.Install(TestPlugins.Package(server.DevelopmentKey));
        return server;
    }
}
