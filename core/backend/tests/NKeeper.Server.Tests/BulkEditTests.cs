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

/// <summary>docs/architecture.md §7.50: the same changes to several hosts. Ported from 1.x's bulk edit tests.</summary>
public sealed class BulkEditTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string BobPassword = "Bob-Passw0rd-123";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Tags_services_credentials_and_where_they_run_change_on_every_host_keeping_the_rest()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        server.Install(TestPlugins.Package(server.DevelopmentKey));
        using var admin = await server.SignedInClientAsync();
        var kvm = await CreateAsync(admin, "kvm01", [], []);
        var web = await CreateAsync(admin, "web01", [new { name = "env", value = "test" }, new { name = "role", value = "web" }], [new { username = "root", password = "root-pass" }]);
        var db = await CreateAsync(admin, "db01", [new { name = "env", value = "prod" }], [new { username = "old", password = "old-pass" }]);

        using var response = await admin.SendWithTokenAsync(HttpMethod.Post, "/api/hosts/bulk", new
        {
            hostIds = new[] { Id(web), Id(db) },
            removeTags = new[] { new { name = "role", value = (string?)null } },
            setTags = new[] { new { name = "env", value = "staging" } },
            addTags = new[] { new { name = "team", value = "ops" } },
            hypervisor = new { hypervisorId = Id(kvm) },
            addServices = new[] { new { type = "hello", port = 2222, settings = new { greeting = "b" } } },
            removeCredentials = new[] { new { username = "OLD" } },
            addCredentials = new[] { new { username = "deploy", password = "deploy-pass" } },
        });
        var hosts = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.True(response.IsSuccessStatusCode, hosts.GetRawText());

        var byName = hosts.EnumerateArray().ToDictionary(h => h.GetProperty("name").GetString()!);
        Assert.Equal(["db01", "web01"], byName.Keys.Order());
        foreach (var host in byName.Values)
        {
            Assert.Equal(["env=staging", "team=ops"], host.GetProperty("tags").EnumerateArray().Select(t => $"{t.GetProperty("name").GetString()}={t.GetProperty("value").GetString()}"));
            Assert.True(host.GetProperty("isVirtualMachine").GetBoolean());
            Assert.Equal(Id(kvm), host.GetProperty("hypervisorId").GetGuid());
            Assert.Equal([22, 2222], host.GetProperty("services").EnumerateArray().Select(s => s.GetProperty("port").GetInt32()));
        }

        Assert.Equal(["root", "deploy"], byName["web01"].GetProperty("credentials").EnumerateArray().Select(c => c.GetProperty("username").GetString()));
        Assert.Equal(["deploy"], byName["db01"].GetProperty("credentials").EnumerateArray().Select(c => c.GetProperty("username").GetString()));

        // The kept credential keeps its secret; the new one has its own.
        using var scope = server.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<CoreDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<CredentialProtector>();
        foreach (var (username, password) in new[] { ("root", "root-pass"), ("deploy", "deploy-pass") })
        {
            var credential = await context.HostCredentials.AsNoTracking().SingleAsync(c => c.HostId == Id(web) && c.Username == username, Token);
            using var secret = protector.Unprotect(credential.Id, credential.ProtectedPassword!);
            Assert.Equal(password, secret.Reveal());
        }

        Assert.Equal(2, await context.AuditEvents.CountAsync(e => e.Action == AuditActions.HostUpdate, Token));

        // The same changes again change nothing.
        using var again = await admin.SendWithTokenAsync(HttpMethod.Post, "/api/hosts/bulk", new { hostIds = new[] { Id(web), Id(db) }, addTags = new[] { new { name = "team", value = "ops" } } });
        again.EnsureSuccessStatusCode();
        Assert.Equal(2, await context.AuditEvents.CountAsync(e => e.Action == AuditActions.HostUpdate, Token));
    }

    [Fact]
    public async Task One_host_that_cant_take_the_changes_stops_them_all_and_every_host_needs_manage()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        server.Install(TestPlugins.Package(server.DevelopmentKey));
        using var admin = await server.SignedInClientAsync();
        var kvm = await CreateAsync(admin, "kvm01", [], []);
        var guest = await CreateAsync(admin, "guest", [], []);
        var other = await CreateAsync(admin, "other", [], []);

        // kvm01 can't run on itself, so guest doesn't either.
        using var loop = await admin.SendWithTokenAsync(HttpMethod.Post, "/api/hosts/bulk", new { hostIds = new[] { Id(kvm), Id(guest) }, hypervisor = new { hypervisorId = Id(kvm) } });
        Assert.Equal(HttpStatusCode.BadRequest, loop.StatusCode);
        Assert.Contains("kvm01", await loop.Content.ReadAsStringAsync(Token));
        var after = await admin.GetFromJsonAsync<JsonElement>($"/api/hosts/{Id(guest)}", Token);
        Assert.False(after.GetProperty("isVirtualMachine").GetBoolean());

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendWithTokenAsync(HttpMethod.Post, "/api/hosts/bulk", new { hostIds = new[] { Id(guest) } })).StatusCode);

        var bobId = await server.CreateUserAsync("bob", BobPassword);
        (await admin.SendWithTokenAsync(HttpMethod.Put, $"/api/users/{bobId}/grants", new[] { new { hostId = Id(guest), level = "manage" }, new { hostId = Id(other), level = "use" } })).EnsureSuccessStatusCode();
        using var bob = server.Client();
        (await bob.LoginAsync("bob", BobPassword)).EnsureSuccessStatusCode();
        var tag = new[] { new { name = "team", value = "ops" } };
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.SendWithTokenAsync(HttpMethod.Post, "/api/hosts/bulk", new { hostIds = new[] { Id(guest), Id(other) }, addTags = tag })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.SendWithTokenAsync(HttpMethod.Post, "/api/hosts/bulk", new { hostIds = new[] { Id(guest), Id(kvm) }, addTags = tag })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bob.SendWithTokenAsync(HttpMethod.Post, "/api/hosts/bulk", new { hostIds = new[] { Id(guest) }, addTags = tag })).StatusCode);
    }

    private static async Task<JsonElement> CreateAsync(HttpClient admin, string name, object[] tags, object[] credentials)
    {
        using var response = await admin.SendWithTokenAsync(HttpMethod.Post, "/api/hosts", new
        {
            name,
            ipAddress = "192.0.2.10",
            tags,
            services = new[] { new { type = "hello", port = 22, settings = new { greeting = "a" } } },
            credentials,
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Token));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    private static Guid Id(JsonElement host) => host.GetProperty("id").GetGuid();
}
