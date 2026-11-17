using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Core.Persistence;
using NKeeper.Testing;

namespace NKeeper.Server.Tests;

/// <summary>docs/architecture.md §7.12.1: plugin audit events, through the test plugin's <c>/audit</c> endpoint.</summary>
public sealed class PluginAuditTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_plugin_records_only_events_named_after_itself()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        server.Install(TestPlugins.Package(server.DevelopmentKey));
        using var admin = await server.SignedInClientAsync();
        using var created = await admin.SendWithTokenAsync(HttpMethod.Post, "/api/hosts", new { name = "probe", ipAddress = "192.0.2.10" });
        var hostId = (await created.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("id").GetGuid();

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
}
