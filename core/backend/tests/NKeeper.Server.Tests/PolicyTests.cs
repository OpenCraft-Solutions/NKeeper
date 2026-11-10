using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Testing;

namespace NKeeper.Server.Tests;

/// <summary>
/// docs/architecture.md §6.4, and the start of the authorization sweep (docs/v2-plan.md §4.4): every
/// mapped endpoint, core's and plugins' alike, is called the ways that must be refused.
/// </summary>
public sealed partial class PolicyTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly string[] UnsafeMethods = [HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch, HttpMethods.Delete];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_endpoint_that_isnt_anonymous_refuses_a_caller_who_isnt_signed_in()
    {
        await using var server = await ServerWithHelloAsync();
        using var anonymous = server.Client(handleCookies: false);
        var checkedPaths = new List<string>();

        foreach (var (path, method, _) in Calls(server).Where(c => !c.Anonymous))
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative));
            using var response = await anonymous.SendAsync(request, Token);
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{method} {path} answered {(int)response.StatusCode} without a sign-in.");
            checkedPaths.Add(path);
        }

        Assert.Contains("/api/plugins", checkedPaths);
        Assert.Contains("/api/auth/me", checkedPaths);
        Assert.Contains("/api/p/hello/ping", checkedPaths);
        Assert.Contains("/api/p/hello/echo", checkedPaths);
        Assert.Contains("/api/p/hello/raw", checkedPaths);
        Assert.Contains(checkedPaths, p => p.StartsWith("/plugins/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_unsafe_api_request_without_the_antiforgery_token_is_refused_even_when_signed_in()
    {
        await using var server = await ServerWithHelloAsync();
        using var client = await server.SignedInClientAsync();
        var checkedPaths = new List<string>();

        foreach (var (path, method, _) in Calls(server).Where(c => UnsafeMethods.Contains(c.Method) && c.Path.StartsWith("/api/", StringComparison.Ordinal)))
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative));
            using var response = await client.SendAsync(request, Token);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{method} {path} answered {(int)response.StatusCode} without a token.");
            Assert.Equal("csrf_token_invalid", (await response.ProblemAsync()).GetProperty("code").GetString());
            checkedPaths.Add($"{method} {path}");
        }

        Assert.Contains("POST /api/auth/login", checkedPaths);
        Assert.Contains("POST /api/p/hello/echo", checkedPaths);
        Assert.Contains("POST /api/p/hello/raw", checkedPaths);
    }

    [Fact]
    public async Task A_plugin_endpoint_answers_a_signed_in_caller_who_sends_the_token()
    {
        await using var server = await ServerWithHelloAsync();
        using var client = await server.SignedInClientAsync();

        using var echo = await client.SendWithTokenAsync(HttpMethod.Post, "/api/p/hello/echo");
        using var raw = await client.SendWithTokenAsync(HttpMethod.Post, "/api/p/hello/raw");

        Assert.Equal(HttpStatusCode.OK, echo.StatusCode);
        Assert.Equal("raw", await raw.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task The_anonymous_endpoints_are_exactly_the_documented_ones()
    {
        await using var server = await ServerWithHelloAsync();
        server.Client().Dispose();

        var anonymous = Calls(server).Where(c => c.Anonymous).Select(c => $"{c.Method} {c.Path}").Distinct().Order(StringComparer.Ordinal);

        Assert.Equal(["GET /api/auth/antiforgery", "GET /health/live", "POST /api/auth/login", "POST /api/auth/logout"], anonymous);
    }

    private async Task<ServerScenario> ServerWithHelloAsync()
    {
        var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        server.Install(TestPlugins.Package(server.DevelopmentKey, frontend: new Dictionary<string, byte[]>
        {
            [TestPlugins.FrontendEntry] = System.Text.Encoding.UTF8.GetBytes(TestPlugins.HelloFrontend()),
        }));
        return server;
    }

    /// <summary>
    /// Every mapped route as a concrete call: parameters filled in, and each of its methods, or GET and
    /// POST for one that takes any method. The shell's fallback route is left out: it serves pages.
    /// </summary>
    private static List<(string Path, string Method, bool Anonymous)> Calls(ServerScenario server)
    {
        server.Client().Dispose();
        var calls = new List<(string, string, bool)>();
        foreach (var endpoint in server.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var pattern = endpoint.RoutePattern.RawText ?? string.Empty;
            if (pattern.Contains("{*path:nonfile}", StringComparison.Ordinal))
            {
                continue;
            }

            var path = "/" + RouteParameter().Replace(pattern.TrimStart('/'), m => m.Value.Contains("**", StringComparison.Ordinal) ? "x.js" : "x");
            var anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [HttpMethods.Get, HttpMethods.Post];
            calls.AddRange(methods.Select(m => (path, m, anonymous)));
        }

        return calls;
    }

    [GeneratedRegex(@"\{[^}]+\}")]
    private static partial Regex RouteParameter();
}
