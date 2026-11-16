using System.Net.Http.Json;
using System.Text.Json;
using NKeeper.Testing;

namespace NKeeper.Smoke.Tests;

public sealed class SmokePluginTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task The_built_package_loads_into_the_test_server_and_serves_its_endpoint()
    {
        var options = new TestServerOptions { ConnectionString = await postgres.CreateDatabaseAsync() };
        foreach (var package in DevelopmentKeys.BuiltPackages())
        {
            options.PluginPackages.Add(package);
        }

        Assert.NotEmpty(options.PluginPackages);
        await using var server = await TestServer.StartAsync(options, TestContext.Current.CancellationToken);
        using var client = await server.SignedInClientAsync(TestContext.Current.CancellationToken);

        var plugins = await client.Http.GetFromJsonAsync<JsonElement>("/api/plugins", TestContext.Current.CancellationToken);

        var plugin = Assert.Single(plugins.EnumerateArray());
        Assert.True(plugin.GetProperty("state").GetString() == "loaded", $"{plugin}{Environment.NewLine}{server.Log}");
        var ping = await client.Http.GetStringAsync(new Uri("/api/p/com.example.smoke/ping", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.EndsWith("/index.js", plugin.GetProperty("frontendEntry").GetString());
        Assert.Equal("pong from com.example.smoke 1.0.0", ping);
    }

    [Fact]
    public void The_package_carries_the_xrdp_image_beside_the_test_assemblies()
    {
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Rdp", "xrdp", "Dockerfile")));
    }
}
