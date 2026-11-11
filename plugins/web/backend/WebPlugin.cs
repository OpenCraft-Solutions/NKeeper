using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Abstractions;

namespace NKeeper.Plugins.Web;

/// <summary>
/// Web interfaces through NKeeper, in a tab (docs/architecture.md §7.19): <c>http</c> and <c>https</c> services,
/// opened as stateful sessions (§7.17) on origins of their own (§7.18), proxied through core's connection (§7.16).
/// </summary>
public sealed class WebPlugin : IPlugin
{
    public const string SessionTypeId = "web";

    public void Contribute(IPluginContributions contributions, PluginContext context)
    {
        contributions.AddServiceType(new ServiceType("http", "HTTP") { DefaultPort = 80, Health = HealthProbe.Tcp });
        contributions.AddServiceType(new ServiceType("https", "HTTPS") { DefaultPort = 443, Health = HealthProbe.Tls });
        contributions.AddSessionType(new SessionType(SessionTypeId, "Web page", null) { ServiceTypes = ["http", "https"] });
        contributions.AddHostAction(new HostAction("web.open", "Web page", SessionTypeId) { Credential = HostActionCredential.None });
        contributions.AddSessionOrigin(new SessionOrigin(WebOriginHandler.Prefix, typeof(WebOriginHandler)));
    }

    public void ConfigureServices(IServiceCollection services, PluginContext context)
    {
        services.AddHttpForwarder();
        services.AddSingleton<WebSessionStore>();
        services.AddSingleton<WebOriginHandler>();
        services.AddHostedService<WebSessionSweeper>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints, PluginContext context)
    {
        var use = endpoints.MapHostGroup(HostPermission.Use);
        use.MapPost("/sessions", OpenAsync);
        use.MapPost("/sessions/{id}/touch", (string id, HttpContext http, WebSessionStore store) =>
            store.TouchOwned(id, UserId(http)) ? Results.NoContent() : Results.NotFound());
        use.MapDelete("/sessions/{id}", (string id, HttpContext http, WebSessionStore store) =>
            store.EndOwned(id, UserId(http)) ? Results.NoContent() : Results.NotFound());
    }

    private static async Task<IResult> OpenAsync(
        HttpContext http,
        OpenRequest? request,
        IConnectivity connectivity,
        IPluginSessions sessions,
        ISessionOrigins origins,
        WebSessionStore store)
    {
        var host = http.GetHostContext();
        var service = host.Services.FirstOrDefault(s => s.Id == request?.ServiceId && s.Type is "http" or "https");
        if (service is null)
        {
            return Problem(StatusCodes.Status400BadRequest, "bad_request", "This host has no such web service. Reload the host and try again.");
        }

        if (origins.UrlFor(WebOriginHandler.Prefix, "probe") is null)
        {
            return Problem(StatusCodes.Status503ServiceUnavailable, "web_domain_missing", "Web pages need a web domain, which this server wasn't installed with (setup.sh --web-domain).");
        }

        IHttpConnection connection;
        try
        {
            connection = await connectivity.OpenHttpAsync(host, service.Id, tls: service.Type == "https", http.RequestAborted);
        }
        catch (ConnectException ex)
        {
            return ex.Presented is { } certificate
                ? Problem(StatusCodes.Status409Conflict, "host_key_unknown", ex.Message, certificate)
                : Problem(StatusCodes.Status502BadGateway, Code(ex.Reason), ex.Message);
        }

        IPluginSession registration;
        try
        {
            registration = await sessions.OpenAsync(host, service.Id, SessionTypeId, http.RequestAborted);
        }
        catch (SessionLimitException ex)
        {
            await connection.DisposeAsync();
            return Problem(StatusCodes.Status409Conflict, "too_many_sessions", ex.Message);
        }

        // NKeeper's own origin, the only one that may frame the session.
        var embedder = $"{http.Request.Scheme}://{http.Request.Host}";
        var session = store.Add(UserId(http), host.Id, connection, registration, embedder);
        var launch = new Uri(origins.UrlFor(WebOriginHandler.Prefix, session.Id)!, $"{WebOriginHandler.LaunchPath.TrimStart('/')}?t={session.LaunchToken}");
        return Results.Ok(new OpenResponse(session.Id, launch.ToString()));
    }

    private static Guid UserId(HttpContext http) =>
        Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;

    private static IResult Problem(int status, string code, string detail, PresentedIdentity? certificate = null)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = code };
        if (certificate is not null)
        {
            extensions["presented"] = certificate;
        }

        return Results.Problem(statusCode: status, detail: detail, extensions: extensions);
    }

    private static string Code(ConnectFailure reason) => reason switch
    {
        ConnectFailure.HostKeyChanged => "host_key_changed",
        ConnectFailure.TimedOut => "timeout",
        ConnectFailure.JumpHostUnavailable => "jump_host_unavailable",
        _ => "unreachable",
    };

    private sealed record OpenRequest(Guid ServiceId);

    private sealed record OpenResponse(string Id, string Url);
}
