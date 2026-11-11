using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Core.Persistence;
using NKeeper.Testing;

namespace NKeeper.Server.Tests;

/// <summary>
/// docs/architecture.md §7.12 (run actions), §7.12.1 (plugin audit events) and §7.15: the wake-on-lan plugin,
/// packaged from its build output and loaded like any plugin, waking a host through its run action.
/// </summary>
public sealed class WakeOnLanTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_plugin_contributes_its_service_type_and_run_action()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var plugins = await admin.GetFromJsonAsync<JsonElement>(new Uri("/api/plugins", UriKind.Relative), Token);
        var plugin = plugins.EnumerateArray().Single(p => p.GetProperty("id").GetString() == "wake-on-lan");
        Assert.Equal("loaded", plugin.GetProperty("state").GetString());
        var serviceType = Assert.Single(plugin.GetProperty("serviceTypes").EnumerateArray());
        Assert.Equal(("wake-on-lan", 9, "macAddress"), (serviceType.GetProperty("id").GetString(), serviceType.GetProperty("defaultPort").GetInt32(), serviceType.GetProperty("settings")[0].GetProperty("key").GetString()));
        var action = Assert.Single(plugin.GetProperty("hostActions").EnumerateArray());
        Assert.Equal(("wake-on-lan.wake", "wake", JsonValueKind.Null, "none"), (
            action.GetProperty("id").GetString(),
            action.GetProperty("endpoint").GetString(),
            action.GetProperty("sessionType").ValueKind,
            action.GetProperty("credential").GetString()));
        Assert.Equal(["wake-on-lan"], action.GetProperty("serviceTypes").EnumerateArray().Select(t => t.GetString()));
    }

    [Fact]
    public async Task Waking_a_host_sends_the_magic_packet_and_is_audited()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
        var host = await CreateHostAsync(admin, port, "00:1A:2b:3c:4d:5e");

        using var woken = await admin.SendWithTokenAsync(HttpMethod.Post, $"/api/p/wake-on-lan/hosts/{Id(host)}/wake", new { serviceId = ServiceId(host) });
        Assert.True(woken.StatusCode == HttpStatusCode.OK, await woken.Content.ReadAsStringAsync(Token));
        Assert.StartsWith("Sent a Wake-on-LAN packet to 00:1a:2b:3c:4d:5e.", (await woken.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("message").GetString(), StringComparison.Ordinal);

        // The packet sent to the address itself: six 0xFF bytes, then the MAC address 16 times.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var packet = (await listener.ReceiveAsync(timeout.Token)).Buffer;
        Assert.Equal(102, packet.Length);
        Assert.All(packet[..6], b => Assert.Equal(0xFF, b));
        Assert.All(packet[6..].Chunk(6), mac => Assert.Equal(new byte[] { 0x00, 0x1a, 0x2b, 0x3c, 0x4d, 0x5e }, mac));

        using var scope = server.Services.CreateScope();
        var sent = await scope.ServiceProvider.GetRequiredService<CoreDbContext>().AuditEvents.AsNoTracking().SingleAsync(e => e.Action == "wake-on-lan.send", Token);
        Assert.Equal((Id(host).ToString(), ServerScenario.AdminUserName), (sent.Target, sent.Actor));
        Assert.Contains($"00:1a:2b:3c:4d:5e to ", sent.Detail, StringComparison.Ordinal);
        Assert.Contains($"127.0.0.1:{port}", sent.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bad_MAC_address_or_another_service_is_refused()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, 9, "not-a-mac");

        using var badMac = await admin.SendWithTokenAsync(HttpMethod.Post, $"/api/p/wake-on-lan/hosts/{Id(host)}/wake", new { serviceId = ServiceId(host) });
        Assert.Equal(HttpStatusCode.BadRequest, badMac.StatusCode);
        Assert.Contains("isn’t a MAC address", await badMac.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);

        using var otherService = await admin.SendWithTokenAsync(HttpMethod.Post, $"/api/p/wake-on-lan/hosts/{Id(host)}/wake", new { serviceId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, otherService.StatusCode);
    }

    [Fact]
    public async Task A_plugin_records_only_events_named_after_itself()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        server.Install(TestPlugins.Package(server.DevelopmentKey));
        using var admin = await server.SignedInClientAsync();
        using var created = await admin.SendWithTokenAsync(HttpMethod.Post, "/api/hosts", new { name = "probe", ipAddress = "192.0.2.10" });
        var hostId = Id(await created.Content.ReadFromJsonAsync<JsonElement>(Token));

        async Task<HttpStatusCode> RecordAsync(string action)
        {
            using var response = await admin.SendWithTokenAsync(HttpMethod.Post, $"/api/p/hello/hosts/{hostId}/audit", new { action });
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await RecordAsync("hello.probe"));
        Assert.Equal(HttpStatusCode.BadRequest, await RecordAsync("host.update"));
        Assert.Equal(HttpStatusCode.BadRequest, await RecordAsync("wake-on-lan.send"));
        Assert.Equal(HttpStatusCode.BadRequest, await RecordAsync("hello.Probe Now"));
        Assert.Equal(HttpStatusCode.BadRequest, await RecordAsync("hello."));

        using var scope = server.Services.CreateScope();
        var events = await scope.ServiceProvider.GetRequiredService<CoreDbContext>().AuditEvents.AsNoTracking().Where(e => e.Action.StartsWith("hello.") || e.Action == "wake-on-lan.send").ToListAsync(Token);
        Assert.Equal("hello.probe", Assert.Single(events).Action);
    }

    private static async Task<JsonElement> CreateHostAsync(HttpClient admin, int port, string mac)
    {
        using var response = await admin.SendWithTokenAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = "sleeper",
            ipAddress = "127.0.0.1",
            services = new[] { new { type = "wake-on-lan", port, settings = new { macAddress = mac } } },
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Token));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    private static Guid Id(JsonElement host) => host.GetProperty("id").GetGuid();

    private static Guid ServiceId(JsonElement host) => host.GetProperty("services")[0].GetProperty("id").GetGuid();

    private async Task<ServerScenario> ServerAsync()
    {
        var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        server.Install(TestPlugins.WakeOnLanPackage(server.DevelopmentKey));
        return server;
    }
}
