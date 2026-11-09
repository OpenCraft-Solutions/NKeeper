using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NKeeper.Core.Persistence;
using NKeeper.Plugins.Host;
using NKeeper.Testing;

namespace NKeeper.Server.Tests;

/// <summary>Phase 0's exit criteria, end to end: a real server, a real database, signed plugin packages.</summary>
public sealed class PluginStartupTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [Fact]
    public async Task The_server_verifies_loads_and_serves_a_signed_plugin()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        server.Install(TestPlugins.Package(server.DevelopmentKey));
        using var client = server.Client();

        var plugins = await client.GetFromJsonAsync<List<PluginStatusResponse>>("/api/plugins", Json, TestContext.Current.CancellationToken);
        var ping = await client.GetStringAsync(new Uri("/api/p/hello/ping", UriKind.Relative), TestContext.Current.CancellationToken);

        var hello = Assert.Single(plugins!);
        Assert.Equal(("hello", PluginState.Loaded, true), (hello.Id, hello.State, hello.BuiltIn));
        Assert.Equal(server.DevelopmentKey.KeyId, hello.KeyId);
        Assert.Equal("pong from hello 1.0.0", ping);
    }

    [Fact]
    public async Task Plugin_records_and_audit_events_are_kept_in_the_core_schema()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        server.Install(TestPlugins.Package(server.DevelopmentKey));
        using var client = server.Client();

        await using var db = new CoreDbContext(CoreDbContext.CreateOptions(server.ConnectionString));
        var record = await db.InstalledPlugins.SingleAsync(TestContext.Current.CancellationToken);
        var actions = await db.AuditEvents.OrderBy(a => a.Id).Select(a => a.Action).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(("hello", "1.0.0", true, true), (record.Id, record.Version, record.BuiltIn, record.Enabled));
        Assert.Equal(["plugin.discovered", "plugin.loaded"], actions);
    }

    [Fact]
    public async Task A_tampered_package_is_listed_as_refused_and_its_endpoints_dont_exist()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        var path = server.Install(TestPlugins.Package(server.DevelopmentKey));
        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        bytes[^30] ^= 0xFF;
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        using var client = server.Client();

        var plugins = await client.GetFromJsonAsync<List<PluginStatusResponse>>("/api/plugins", Json, TestContext.Current.CancellationToken);
        using var ping = await client.GetAsync(new Uri("/api/p/hello/ping", UriKind.Relative), TestContext.Current.CancellationToken);

        var status = Assert.Single(plugins!);
        Assert.Equal(PluginState.Refused, status.State);
        Assert.NotNull(status.ReasonCode);
        Assert.Equal(HttpStatusCode.NotFound, ping.StatusCode);
    }

    [Fact]
    public async Task The_unauthenticated_plugin_list_carries_no_messages()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        File.WriteAllBytes(Path.Combine(server.PluginDirectory, "stray-1.0.0.nkp"), [1, 2, 3]);
        using var client = server.Client();

        var json = await client.GetStringAsync(new Uri("/api/plugins", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.DoesNotContain("message", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(server.PluginDirectory.Replace("\\", "\\\\", StringComparison.Ordinal), json, StringComparison.Ordinal);
        Assert.Contains("\"refused\"", json, StringComparison.Ordinal);
    }
}
