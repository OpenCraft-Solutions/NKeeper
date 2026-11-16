using NKeeper.Plugins.Packaging;
using NKeeper.Testing;

namespace NKeeper.Server.Tests;

/// <summary>The test server NKeeper.Testing ships to plugin repositories (docs/architecture.md §9), started the way they start it.</summary>
public sealed class TestServerTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task A_plugin_package_and_the_development_key_it_is_signed_with_load_into_the_test_server()
    {
        using var key = PluginSigningKey.Generate();
        var directory = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "test-server-inputs", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var package = TestPlugins.Package(key).WriteTo(directory);
            var trustFile = Path.Combine(directory, "dev.trust.json");
            await File.WriteAllBytesAsync(trustFile, TrustedKey.Development(key.PublicKey.Span).ToKeyFile(), TestContext.Current.CancellationToken);
            var options = new TestServerOptions { ConnectionString = await postgres.CreateDatabaseAsync(), DevelopmentTrustFiles = [trustFile] };
            options.PluginPackages.Add(package);

            await using var server = await TestServer.StartAsync(options, TestContext.Current.CancellationToken);
            using var client = await server.SignedInClientAsync(TestContext.Current.CancellationToken);
            var ping = await client.Http.GetStringAsync(new Uri("/api/p/hello/ping", UriKind.Relative), TestContext.Current.CancellationToken);
            using var echo = await client.SendAsync(HttpMethod.Post, "/api/p/hello/echo", cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal("pong from hello 1.0.0", ping);
            Assert.Equal("echo", await echo.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Without_a_development_key_to_trust_the_test_server_refuses_to_start()
    {
        var options = new TestServerOptions { ConnectionString = "Host=127.0.0.1;Database=none", DevelopmentTrustFiles = [] };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TestServer.StartAsync(options, TestContext.Current.CancellationToken));

        Assert.Contains("development trust file", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
