using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Core.Audit;
using NKeeper.Core.Credentials;
using NKeeper.Core.Persistence;
using NKeeper.Testing;

namespace NKeeper.Server.Tests;

/// <summary>docs/architecture.md §7.46: combining duplicate hosts into one.</summary>
public sealed class HostMergeTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string BobPassword = "Bob-Passw0rd-123";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Combining_keeps_one_host_with_what_the_others_had_and_their_grants_and_secrets()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        server.Install(TestPlugins.Package(server.DevelopmentKey));
        using var admin = await server.SignedInClientAsync();
        var keep = await CreateHostAsync(admin, "web01", hostname: null, mac: null, "root", "root-pass", tag: null);
        var other = await CreateHostAsync(admin, "web01-old", hostname: "web01.lab.example", mac: "AA:BB:CC:DD:EE:01", "deploy", "deploy-pass", tag: "env");
        var bobId = await server.CreateUserAsync("bob", BobPassword);
        (await admin.SendWithTokenAsync(HttpMethod.Put, $"/api/users/{bobId}/grants", new[] { new { hostId = Id(other), level = "manage" }, new { hostId = Id(keep), level = "use" } })).EnsureSuccessStatusCode();

        using var merged = await admin.SendWithTokenAsync(HttpMethod.Post, $"/api/hosts/{Id(keep)}/merge", new { sourceIds = new[] { Id(other) } });
        var host = await merged.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.True(merged.IsSuccessStatusCode, host.GetRawText());

        // Its own details stay; the other's fill what it lacked.
        Assert.Equal(("web01", "web01.lab.example", "AA:BB:CC:DD:EE:01"), (host.GetProperty("name").GetString(), host.GetProperty("hostname").GetString(), host.GetProperty("macAddress").GetString()));
        Assert.Equal(["env"], host.GetProperty("tags").EnumerateArray().Select(t => t.GetProperty("name").GetString()));
        // The same service once; both accounts.
        Assert.Single(host.GetProperty("services").EnumerateArray());
        Assert.Equal(["root", "deploy"], host.GetProperty("credentials").EnumerateArray().Select(c => c.GetProperty("username").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/hosts/{Id(other)}", Token)).StatusCode);

        using var scope = server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreDbContext>();
        // The copied secret decrypts for its new credential: each is bound to its own.
        var deploy = await db.HostCredentials.AsNoTracking().SingleAsync(c => c.HostId == Id(keep) && c.Username == "deploy", Token);
        using (var secret = scope.ServiceProvider.GetRequiredService<CredentialProtector>().Unprotect(deploy.Id, deploy.ProtectedPassword!))
        {
            Assert.Equal("deploy-pass", secret.Reveal());
        }

        // One grant for Bob, at the higher level.
        var grant = await db.HostAccessGrants.AsNoTracking().SingleAsync(g => g.UserId == bobId, Token);
        Assert.Equal((Id(keep), HostAccessLevel.Manage), (grant.HostId, grant.Level));
        Assert.Single(await db.AuditEvents.AsNoTracking().Where(e => e.Action == AuditActions.HostMerge).ToListAsync(Token));
    }

    [Fact]
    public async Task Combining_needs_manage_on_every_host_and_changes_nothing_otherwise()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        server.Install(TestPlugins.Package(server.DevelopmentKey));
        using var admin = await server.SignedInClientAsync();
        var keep = await CreateHostAsync(admin, "web01", null, null, "root", "root-pass", null);
        var other = await CreateHostAsync(admin, "web01-old", null, null, "deploy", "deploy-pass", null);
        var bobId = await server.CreateUserAsync("bob", BobPassword);
        (await admin.SendWithTokenAsync(HttpMethod.Put, $"/api/users/{bobId}/grants", new[] { new { hostId = Id(keep), level = "manage" }, new { hostId = Id(other), level = "use" } })).EnsureSuccessStatusCode();
        using var bob = server.Client();
        (await bob.LoginAsync("bob", BobPassword)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Forbidden, (await bob.SendWithTokenAsync(HttpMethod.Post, $"/api/hosts/{Id(keep)}/merge", new { sourceIds = new[] { Id(other) } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/hosts/{Id(other)}", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendWithTokenAsync(HttpMethod.Post, $"/api/hosts/{Id(keep)}/merge", new { sourceIds = new[] { Id(keep) } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.SendWithTokenAsync(HttpMethod.Post, $"/api/hosts/{Id(keep)}/merge", new { sourceIds = new[] { Guid.NewGuid() } })).StatusCode);
    }

    private static async Task<JsonElement> CreateHostAsync(HttpClient admin, string name, string? hostname, string? mac, string username, string password, string? tag)
    {
        using var response = await admin.SendWithTokenAsync(HttpMethod.Post, "/api/hosts", new
        {
            name,
            hostname,
            ipAddress = "192.0.2.10",
            macAddress = mac,
            tags = tag is null ? Array.Empty<object>() : [new { name = tag, value = "prod" }],
            services = new[] { new { type = "hello", port = 22, settings = new { greeting = "a" } } },
            credentials = new[] { new { username, password } },
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Token));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    private static Guid Id(JsonElement host) => host.GetProperty("id").GetGuid();
}
