using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NKeeper.Testing;

namespace NKeeper.Server.Tests;

/// <summary>docs/architecture.md §7.49: the host a virtual machine runs on. Ported from 1.x's inventory tests.</summary>
public sealed class HypervisorLinkTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string BobPassword = "Bob-Passw0rd-123";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_virtual_machine_runs_on_another_host_never_in_a_loop_and_forgets_it_when_that_host_goes()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        using var admin = await server.SignedInClientAsync();
        var kvm = await CreateAsync(admin, new { name = "kvm01", ipAddress = "192.0.2.1", isVirtualMachine = false });
        var nested = await CreateAsync(admin, new { name = "nested", ipAddress = "192.0.2.2", isVirtualMachine = true, hypervisorId = Id(kvm) });
        var guest = await CreateAsync(admin, new { name = "guest", ipAddress = "192.0.2.3", isVirtualMachine = true, hypervisorId = Id(nested) });

        Assert.Equal(Id(kvm), nested.GetProperty("hypervisorId").GetGuid());
        Assert.Equal(Id(nested), guest.GetProperty("hypervisorId").GetGuid());

        // Not itself, not something running on it, not a host that isn't there.
        Assert.Equal("hypervisorId", await RejectedFieldAsync(admin, nested, new { name = "nested", ipAddress = "192.0.2.2", isVirtualMachine = true, hypervisorId = Id(nested) }));
        Assert.Equal("hypervisorId", await RejectedFieldAsync(admin, nested, new { name = "nested", ipAddress = "192.0.2.2", isVirtualMachine = true, hypervisorId = Id(guest) }));
        Assert.Equal("hypervisorId", await RejectedFieldAsync(admin, nested, new { name = "nested", ipAddress = "192.0.2.2", isVirtualMachine = true, hypervisorId = Guid.NewGuid() }));

        // A host that isn't a VM runs on nothing.
        using (var physical = await admin.SendWithTokenAsync(HttpMethod.Post, "/api/hosts", new { name = "box", ipAddress = "192.0.2.4", isVirtualMachine = false, hypervisorId = Id(kvm) }))
        {
            Assert.Equal(JsonValueKind.Null, (await physical.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("hypervisorId").ValueKind);
        }

        // Deleting the hypervisor leaves its VM with its host unknown.
        (await admin.SendWithTokenAsync(HttpMethod.Delete, $"/api/hosts/{Id(nested)}")).EnsureSuccessStatusCode();
        var after = await admin.GetFromJsonAsync<JsonElement>($"/api/hosts/{Id(guest)}", Token);
        Assert.Equal(JsonValueKind.Null, after.GetProperty("hypervisorId").ValueKind);
    }

    [Fact]
    public async Task An_edit_by_someone_who_cant_see_the_hypervisor_keeps_the_link_but_cant_make_one()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        using var admin = await server.SignedInClientAsync();
        var kvm = await CreateAsync(admin, new { name = "kvm01", ipAddress = "192.0.2.1" });
        var other = await CreateAsync(admin, new { name = "kvm02", ipAddress = "192.0.2.5" });
        var guest = await CreateAsync(admin, new { name = "guest", ipAddress = "192.0.2.3", isVirtualMachine = true, hypervisorId = Id(kvm) });
        var bobId = await server.CreateUserAsync("bob", BobPassword);
        (await admin.SendWithTokenAsync(HttpMethod.Put, $"/api/users/{bobId}/grants", new[] { new { hostId = Id(guest), level = "manage" } })).EnsureSuccessStatusCode();
        using var bob = server.Client();
        (await bob.LoginAsync("bob", BobPassword)).EnsureSuccessStatusCode();

        using var kept = await bob.SendWithTokenAsync(HttpMethod.Put, $"/api/hosts/{Id(guest)}", new
        {
            name = "guest-renamed",
            ipAddress = "192.0.2.3",
            isVirtualMachine = true,
            hypervisorId = Id(kvm),
            version = guest.GetProperty("version").GetUInt32(),
        });
        var renamed = await kept.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.True(kept.IsSuccessStatusCode, renamed.GetRawText());
        Assert.Equal(Id(kvm), renamed.GetProperty("hypervisorId").GetGuid());

        Assert.Equal("hypervisorId", await RejectedFieldAsync(bob, renamed, new { name = "guest-renamed", ipAddress = "192.0.2.3", isVirtualMachine = true, hypervisorId = Id(other) }));
    }

    [Fact]
    public async Task Combining_hosts_moves_their_guests_to_the_kept_one()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        using var admin = await server.SignedInClientAsync();
        var keep = await CreateAsync(admin, new { name = "kvm01", ipAddress = "192.0.2.1" });
        var old = await CreateAsync(admin, new { name = "kvm01-old", ipAddress = "192.0.2.1" });
        var guest = await CreateAsync(admin, new { name = "guest", ipAddress = "192.0.2.3", isVirtualMachine = true, hypervisorId = Id(old) });

        (await admin.SendWithTokenAsync(HttpMethod.Post, $"/api/hosts/{Id(keep)}/merge", new { sourceIds = new[] { Id(old) } })).EnsureSuccessStatusCode();

        var after = await admin.GetFromJsonAsync<JsonElement>($"/api/hosts/{Id(guest)}", Token);
        Assert.Equal(Id(keep), after.GetProperty("hypervisorId").GetGuid());

        // Combining a VM into the host it runs on leaves that host running on nothing.
        (await admin.SendWithTokenAsync(HttpMethod.Post, $"/api/hosts/{Id(keep)}/merge", new { sourceIds = new[] { Id(guest) } })).EnsureSuccessStatusCode();
        var combined = await admin.GetFromJsonAsync<JsonElement>($"/api/hosts/{Id(keep)}", Token);
        Assert.Equal(JsonValueKind.Null, combined.GetProperty("hypervisorId").ValueKind);
    }

    private static async Task<JsonElement> CreateAsync(HttpClient admin, object request)
    {
        using var response = await admin.SendWithTokenAsync(HttpMethod.Post, "/api/hosts", request);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Token));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    /// <summary>The field a rejected edit names.</summary>
    private static async Task<string> RejectedFieldAsync(HttpClient client, JsonElement host, object fields)
    {
        var body = JsonSerializer.SerializeToNode(fields)!.AsObject();
        body["version"] = host.GetProperty("version").GetUInt32();
        using var response = await client.SendWithTokenAsync(HttpMethod.Put, $"/api/hosts/{Id(host)}", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        return Assert.Single(problem.GetProperty("errors").EnumerateObject()).Name;
    }

    private static Guid Id(JsonElement host) => host.GetProperty("id").GetGuid();
}
