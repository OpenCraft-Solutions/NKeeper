using System.Net;
using NKeeper.Testing;

namespace NKeeper.Server.Tests;

public sealed class HealthTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Liveness_answers_healthy()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        using var client = server.Client();

        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
