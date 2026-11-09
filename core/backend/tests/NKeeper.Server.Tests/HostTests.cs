using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NKeeper.Core.Audit;
using NKeeper.Core.Persistence;
using NKeeper.Testing;

namespace NKeeper.Server.Tests;

/// <summary>docs/architecture.md §7: hosts, tags, grants and host access. Ported from 1.x inventory and access tests.</summary>
public sealed class HostTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string BobPassword = "Bob-Passw0rd-123";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- Hosts ----

    [Fact]
    public async Task An_administrator_creates_reads_updates_and_deletes_a_host()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        using var admin = await server.SignedInClientAsync();

        var created = await CreateHostAsync(admin, "web01", new { name = "env", value = "prod" }, new { name = "role" });
        var listed = await admin.GetFromJsonAsync<List<Host>>("/api/hosts", Json, Token);
        using var update = await admin.SendWithTokenAsync(HttpMethod.Put, $"/api/hosts/{created.Id}", new { name = "web01-renamed", hostname = "web01.example.com", tags = Array.Empty<object>(), version = created.Version });
        var updated = (await update.Content.ReadFromJsonAsync<Host>(Json, Token))!;
        using var delete = await admin.SendWithTokenAsync(HttpMethod.Delete, $"/api/hosts/{created.Id}");
        using var gone = await admin.GetAsync(new Uri($"/api/hosts/{created.Id}", UriKind.Relative), Token);

        Assert.Equal(("web01", "192.0.2.10", true), (created.Name, created.IpAddress, created.CanManage));
        Assert.Equal([new Tag("env", "prod"), new Tag("role", null)], created.Tags);
        Assert.Equal(created.Id, Assert.Single(listed!).Id);
        Assert.Equal(("web01-renamed", 0), (updated.Name, updated.Tags.Count));
        Assert.NotEqual(created.Version, updated.Version);
        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NotFound), (delete.StatusCode, gone.StatusCode));
        await using var db = new CoreDbContext(CoreDbContext.CreateOptions(server.ConnectionString));
        var actions = await db.AuditEvents.Where(a => a.Action.StartsWith("host.")).OrderBy(a => a.Id).Select(a => a.Action).ToListAsync(Token);
        Assert.Equal([AuditActions.HostCreate, AuditActions.HostUpdate, AuditActions.HostDelete], actions);
    }

    [Fact]
    public async Task Addresses_are_normalized_and_duplicate_tags_dropped()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        using var admin = await server.SignedInClientAsync();

        using var response = await admin.SendWithTokenAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = "  db01  ",
            ipAddress = " 2001:DB8:0:0::0001 ",
            tags = new object[] { new { name = "Env", value = "Prod" }, new { name = "env", value = "prod" }, new { name = "env", value = "test" } },
        });
        var host = (await response.Content.ReadFromJsonAsync<Host>(Json, Token))!;

        Assert.Equal(("db01", "2001:db8::1"), (host.Name, host.IpAddress));
        Assert.Equal([new Tag("Env", "Prod"), new Tag("env", "test")], host.Tags);
    }

    [Theory]
    [InlineData("""{"hostname":"web01.example.com"}""", "name")]
    [InlineData("""{"name":"web01"}""", "hostname")]
    [InlineData("""{"name":"web01","hostname":"not a hostname"}""", "hostname")]
    [InlineData("""{"name":"web01","ipAddress":"10.1"}""", "ipAddress")]
    [InlineData("""{"name":"web01","ipAddress":"192.0.2.300"}""", "ipAddress")]
    [InlineData("""{"name":"web01","ipAddress":"192.0.2.10","tags":[{"name":"env:prod"}]}""", "tags[0].name")]
    [InlineData("""{"name":"web01","ipAddress":"192.0.2.10","tags":[{"name":" "}]}""", "tags[0].name")]
    public async Task An_invalid_host_reports_the_field(string body, string field)
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        using var admin = await server.SignedInClientAsync();

        using var response = await SendRawAsync(admin, HttpMethod.Post, "/api/hosts", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, (await response.ProblemAsync()).GetProperty("errors").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task An_edit_based_on_an_old_version_is_refused()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, "web01");
        (await admin.SendWithTokenAsync(HttpMethod.Put, $"/api/hosts/{host.Id}", new { name = "first", ipAddress = "192.0.2.10", version = host.Version })).EnsureSuccessStatusCode();

        using var stale = await admin.SendWithTokenAsync(HttpMethod.Put, $"/api/hosts/{host.Id}", new { name = "second", ipAddress = "192.0.2.10", version = host.Version });
        using var unversioned = await admin.SendWithTokenAsync(HttpMethod.Put, $"/api/hosts/{host.Id}", new { name = "third", ipAddress = "192.0.2.10" });

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("version_conflict", (await stale.ProblemAsync()).GetProperty("code").GetString());
        Assert.True((await unversioned.ProblemAsync()).GetProperty("errors").TryGetProperty("version", out _));
        Assert.Equal("first", (await admin.GetFromJsonAsync<Host>($"/api/hosts/{host.Id}", Json, Token))!.Name);
    }

    [Fact]
    public async Task Only_administrators_create_hosts()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        await server.CreateUserAsync("bob", BobPassword);
        using var bob = await SignInAsync(server, "bob");

        using var response = await bob.SendWithTokenAsync(HttpMethod.Post, "/api/hosts", new { name = "web01", ipAddress = "192.0.2.10" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- Access ----

    [Fact]
    public async Task A_user_without_grants_sees_no_hosts_and_hidden_ones_answer_404()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, "web01");
        await server.CreateUserAsync("bob", BobPassword);
        using var bob = await SignInAsync(server, "bob");

        var listed = await bob.GetFromJsonAsync<List<Host>>("/api/hosts", Json, Token);
        using var get = await bob.GetAsync(new Uri($"/api/hosts/{host.Id}", UriKind.Relative), Token);
        using var update = await bob.SendWithTokenAsync(HttpMethod.Put, $"/api/hosts/{host.Id}", new { name = "mine", ipAddress = "192.0.2.10", version = host.Version });
        using var delete = await bob.SendWithTokenAsync(HttpMethod.Delete, $"/api/hosts/{host.Id}");
        using var missing = await bob.GetAsync(new Uri($"/api/hosts/{Guid.NewGuid()}", UriKind.Relative), Token);

        Assert.Empty(listed!);
        Assert.Equal(
            (HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.NotFound),
            (get.StatusCode, update.StatusCode, delete.StatusCode, missing.StatusCode));
    }

    [Fact]
    public async Task Use_lets_a_user_see_a_host_and_Manage_lets_them_change_it()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, "web01");
        var bobId = await server.CreateUserAsync("bob", BobPassword);
        await GrantAsync(admin, bobId, new { hostId = host.Id, level = "use" });
        using var bob = await SignInAsync(server, "bob");

        var seen = await bob.GetFromJsonAsync<Host>($"/api/hosts/{host.Id}", Json, Token);
        using var denied = await bob.SendWithTokenAsync(HttpMethod.Put, $"/api/hosts/{host.Id}", new { name = "mine", ipAddress = "192.0.2.10", version = host.Version });
        using var deniedDelete = await bob.SendWithTokenAsync(HttpMethod.Delete, $"/api/hosts/{host.Id}");
        await GrantAsync(admin, bobId, new { hostId = host.Id, level = "manage" });
        using var allowed = await bob.SendWithTokenAsync(HttpMethod.Put, $"/api/hosts/{host.Id}", new { name = "mine", ipAddress = "192.0.2.10", version = host.Version });

        Assert.False(seen!.CanManage);
        Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.Forbidden), (denied.StatusCode, deniedDelete.StatusCode));
        Assert.Equal("host_manage_denied", (await denied.ProblemAsync()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.True((await allowed.Content.ReadFromJsonAsync<Host>(Json, Token))!.CanManage);
    }

    [Fact]
    public async Task Group_grants_follow_tags_without_case_and_a_name_alone_matches_any_value()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        using var admin = await server.SignedInClientAsync();
        var prod = await CreateHostAsync(admin, "prod01", new { name = "Env", value = "PROD" });
        var test = await CreateHostAsync(admin, "test01", new { name = "env", value = "test" });
        var other = await CreateHostAsync(admin, "other01");
        var bobId = await server.CreateUserAsync("bob", BobPassword);
        var aliceId = await server.CreateUserAsync("alice", BobPassword);
        await GrantAsync(admin, bobId, new { tagName = "env", tagValue = "prod", level = "use" });
        await GrantAsync(admin, aliceId, new { tagName = "ENV", level = "use" });
        using var bob = await SignInAsync(server, "bob");
        using var alice = await SignInAsync(server, "alice");

        var bobBefore = await NamesAsync(bob);
        (await admin.SendWithTokenAsync(HttpMethod.Put, $"/api/hosts/{other.Id}", new { name = "other01", ipAddress = "192.0.2.10", tags = new[] { new { name = "env", value = "Prod" } }, version = other.Version })).EnsureSuccessStatusCode();
        var bobAfter = await NamesAsync(bob);

        Assert.Equal(["prod01"], bobBefore);
        Assert.Equal(["other01", "prod01"], bobAfter);
        Assert.Equal(["other01", "prod01", "test01"], await NamesAsync(alice));
        _ = test;
    }

    [Fact]
    public async Task Retagging_a_host_out_of_ones_own_grants_hides_it()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, "web01", new { name = "team", value = "a" });
        var bobId = await server.CreateUserAsync("bob", BobPassword);
        await GrantAsync(admin, bobId, new { tagName = "team", tagValue = "a", level = "manage" });
        using var bob = await SignInAsync(server, "bob");

        using var retag = await bob.SendWithTokenAsync(HttpMethod.Put, $"/api/hosts/{host.Id}", new { name = "web01", ipAddress = "192.0.2.10", tags = new[] { new { name = "team", value = "b" } }, version = host.Version });

        Assert.Equal(HttpStatusCode.NotFound, retag.StatusCode);
        Assert.Empty(await NamesAsync(bob));
    }

    // ---- Grants ----

    [Fact]
    public async Task Grants_round_trip_with_host_names_and_duplicates_collapse_to_the_higher_level()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, "db01");
        var bobId = await server.CreateUserAsync("bob", BobPassword);

        var grants = await GrantAsync(
            admin,
            bobId,
            new { hostId = host.Id, level = "use" },
            new { hostId = host.Id, level = "manage" },
            new { tagName = "env", tagValue = "prod", level = "use" },
            new { tagName = "ENV", tagValue = "PROD", level = "use" });
        var read = await admin.GetFromJsonAsync<List<Grant>>($"/api/users/{bobId}/grants", Json, Token);

        Assert.Equal(2, grants.Count);
        Assert.Contains(grants, g => g.HostId == host.Id && g.HostName == "db01" && g.Level == "manage");
        Assert.Contains(grants, g => g.TagName == "env" && g.TagValue == "prod" && g.Level == "use");
        Assert.Equal(grants.Select(g => g.Id).Order(), read!.Select(g => g.Id).Order());
    }

    [Theory]
    [InlineData("""[{"level":"use"}]""")]
    [InlineData("""[{"hostId":"00000000-0000-0000-0000-000000000001","tagName":"env","level":"use"}]""")]
    [InlineData("""[{"hostId":"00000000-0000-0000-0000-000000000001","level":"use"}]""")]
    [InlineData("""[{"tagName":"env"}]""")]
    public async Task Invalid_grants_are_refused(string body)
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        using var admin = await server.SignedInClientAsync();
        var bobId = await server.CreateUserAsync("bob", BobPassword);

        using var response = await SendRawAsync(admin, HttpMethod.Put, $"/api/users/{bobId}/grants", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Only_administrators_set_grants_and_every_change_is_audited()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        using var admin = await server.SignedInClientAsync();
        var bobId = await server.CreateUserAsync("bob", BobPassword);
        using var bob = await SignInAsync(server, "bob");

        using var own = await bob.SendWithTokenAsync(HttpMethod.Put, $"/api/users/{bobId}/grants", new[] { new { tagName = "env", level = "manage" } });
        await GrantAsync(admin, bobId, new { tagName = "env", level = "use" });

        Assert.Equal(HttpStatusCode.Forbidden, own.StatusCode);
        await using var db = new CoreDbContext(CoreDbContext.CreateOptions(server.ConnectionString));
        Assert.Equal(1, await db.AuditEvents.CountAsync(a => a.Action == AuditActions.UserGrantsReplace && a.Target == bobId.ToString(), Token));
    }

    [Fact]
    public async Task Deleting_a_host_deletes_its_host_grants_but_not_group_grants()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, "web01");
        var bobId = await server.CreateUserAsync("bob", BobPassword);
        await GrantAsync(admin, bobId, new { hostId = host.Id, level = "use" }, new { tagName = "env", level = "use" });

        (await admin.SendWithTokenAsync(HttpMethod.Delete, $"/api/hosts/{host.Id}")).EnsureSuccessStatusCode();
        var left = await admin.GetFromJsonAsync<List<Grant>>($"/api/users/{bobId}/grants", Json, Token);

        Assert.Equal("env", Assert.Single(left!).TagName);
    }

    private static async Task<Host> CreateHostAsync(HttpClient admin, string name, params object[] tags)
    {
        using var response = await admin.SendWithTokenAsync(HttpMethod.Post, "/api/hosts", new { name, ipAddress = "192.0.2.10", tags });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Host>(Json, Token))!;
    }

    private static async Task<List<Grant>> GrantAsync(HttpClient admin, Guid userId, params object[] grants)
    {
        using var response = await admin.SendWithTokenAsync(HttpMethod.Put, $"/api/users/{userId}/grants", grants);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<Grant>>(Json, Token))!;
    }

    private static async Task<List<string>> NamesAsync(HttpClient client) =>
        [.. (await client.GetFromJsonAsync<List<Host>>("/api/hosts", Json, Token))!.Select(h => h.Name)];

    private static async Task<HttpClient> SignInAsync(ServerScenario server, string userName)
    {
        var client = server.Client();
        using var response = await client.LoginAsync(userName, BobPassword);
        response.EnsureSuccessStatusCode();
        return client;
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

    private sealed record Tag(string Name, string? Value);

    private sealed record Host(Guid Id, string Name, string? Hostname, string? IpAddress, List<Tag> Tags, uint Version, bool CanManage);

    private sealed record Grant(Guid Id, Guid? HostId, string? HostName, string? TagName, string? TagValue, string Level);
}
