using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Core.Audit;
using NKeeper.Core.Persistence;
using NKeeper.Testing;

namespace NKeeper.Server.Tests;

/// <summary>
/// docs/architecture.md §8: behind nginx, the server takes the browser's address and scheme from the
/// forwarded headers, but only when enabled, and only from a proxy on the loopback address.
/// </summary>
public sealed class ForwardedHeadersTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true, "127.0.0.1", "203.0.113.9")]
    [InlineData(true, "::1", "203.0.113.9")]
    [InlineData(true, "192.0.2.1", "192.0.2.1")]
    [InlineData(false, "127.0.0.1", "127.0.0.1")]
    public async Task The_client_address_comes_from_the_local_proxy_only(bool enabled, string peer, string recorded)
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        server.Settings["ForwardedHeaders:Enabled"] = enabled ? "true" : "false";
        server.TestServices = services => services.AddSingleton<IStartupFilter>(new PeerAddress(IPAddress.Parse(peer)));
        var client = server.Client();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.9");

        using var login = await client.LoginAsync(ServerScenario.AdminUserName, ServerScenario.AdminPassword);
        login.EnsureSuccessStatusCode();

        using var scope = server.Services.CreateScope();
        var signIn = await scope.ServiceProvider.GetRequiredService<CoreDbContext>().AuditEvents.AsNoTracking().SingleAsync(e => e.Action == AuditActions.SignIn, Token);
        Assert.Equal(recorded, signIn.ClientAddress);
    }

    [Theory]
    [InlineData(true, "127.0.0.1", true)]
    [InlineData(true, "192.0.2.1", false)]
    [InlineData(false, "127.0.0.1", false)]
    public async Task The_scheme_comes_from_the_local_proxy_only(bool enabled, string peer, bool https)
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        server.Settings["ForwardedHeaders:Enabled"] = enabled ? "true" : "false";
        server.TestServices = services => services.AddSingleton<IStartupFilter>(new PeerAddress(IPAddress.Parse(peer)));
        var client = server.Client();
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");

        // The antiforgery cookie is Secure exactly when the request is HTTPS.
        using var response = await client.GetAsync(new Uri("/api/auth/antiforgery", UriKind.Relative), Token);
        response.EnsureSuccessStatusCode();
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("NKeeper.Antiforgery=", StringComparison.Ordinal));
        Assert.Equal(https, cookie.Contains("; secure", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Makes every request come from <paramref name="address"/>, ahead of the server's own pipeline.</summary>
    private sealed class PeerAddress(IPAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = address;
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
