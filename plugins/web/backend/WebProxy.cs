using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;
using NKeeper.Abstractions;
using Yarp.ReverseProxy.Forwarder;

namespace NKeeper.Plugins.Web;

/// <summary>
/// Serves web sessions on their own origins (<c>nkw-&lt;id&gt;.&lt;web domain&gt;</c>, docs/architecture.md §7.18,
/// §7.19): core sends every request on such a host here, and it's forwarded to the session's target through
/// core's connection, never to NKeeper's own API. A request must carry the session's cookie (set by a one-time
/// launch link), and core ends the session when the user's access changes. Ported from 1.x.
/// </summary>
public sealed class WebOriginHandler(WebSessionStore sessions, IHttpForwarder forwarder, TimeProvider timeProvider) : ISessionOriginHandler
{
    public const string Prefix = "nkw";
    public const string LaunchPath = "/nkeeper/launch";
    public const string CookieName = "__nkw_session";
    private const long MaxRequestBodyBytes = 1L << 30;
    private static readonly ForwarderRequestConfig RequestConfig = new() { ActivityTimeout = TimeSpan.FromMinutes(10) };

    public async Task HandleAsync(HttpContext context, string label)
    {
        var session = sessions.Find(label);
        if (session is null)
        {
            await WritePageAsync(context, StatusCodes.Status410Gone, "This web session has ended", "Open the host again from NKeeper to start a new session.");
            return;
        }

        var now = timeProvider.GetUtcNow();
        if (context.Request.Path.Equals(LaunchPath, StringComparison.Ordinal))
        {
            await LaunchAsync(context, session, now);
            return;
        }

        if (!session.HasSecret(context.Request.Cookies[CookieName]))
        {
            await WritePageAsync(context, StatusCodes.Status403Forbidden, "Open this page from NKeeper", "This web session belongs to someone else, or this browser hasn't opened it from NKeeper.");
            return;
        }

        // Core ends the session when the user's access changes (§7.17).
        if (session.Registration.Ended.IsCancellationRequested)
        {
            sessions.End(session.Id);
            await WritePageAsync(context, StatusCodes.Status403Forbidden, "Access to this host has ended", "Your account or your access to this host changed. Open it again from NKeeper if you still have access.");
            return;
        }

        session.LastSeenUtc = now;
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
        {
            bodySize.MaxRequestBodySize = MaxRequestBodyBytes;
        }

        var proxyOrigin = $"{context.Request.Scheme}://{context.Request.Host}";
        var target = session.Connection.BaseAddress;
        var error = await forwarder.SendAsync(context, target.GetLeftPart(UriPartial.Authority), session.Connection.Client, RequestConfig, new WebProxyTransformer(target, proxyOrigin, session.EmbedderOrigin));
        if (error != ForwarderError.None && !context.Response.HasStarted)
        {
            var failure = context.GetForwarderErrorFeature()?.Exception;
            await WritePageAsync(
                context,
                StatusCodes.Status502BadGateway,
                $"Couldn't reach {target.Authority}",
                failure is HttpRequestException { InnerException: System.Security.Authentication.AuthenticationException }
                    ? "The server's certificate isn't the one trusted in NKeeper any more, so the connection was refused."
                    : "NKeeper couldn't get a response from the host. Check that its web interface is running.");
        }
    }

    /// <summary>Exchanges the one-time launch token for the session cookie, then opens the site (at its start path).</summary>
    private static async Task LaunchAsync(HttpContext context, WebSession session, DateTimeOffset now)
    {
        if (!session.TryRedeemLaunchToken(context.Request.Query["t"].ToString(), now))
        {
            // Already used (e.g. the link was opened twice): fine if this browser has the cookie.
            if (session.HasSecret(context.Request.Cookies[CookieName]))
            {
                context.Response.Redirect("/");
                return;
            }

            await WritePageAsync(context, StatusCodes.Status403Forbidden, "This link has already been used", "Open the host again from NKeeper.");
            return;
        }

        // The session is shown in a frame inside NKeeper, a different site: the cookie must be
        // SameSite=None (hence Secure; browsers accept that on https and on *.localhost) and
        // Partitioned, so it lives only in NKeeper's frame and survives third-party cookie blocking.
        var options = new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.None,
            Path = "/",
            IsEssential = true,
        };
        options.Extensions.Add("Partitioned");
        context.Response.Cookies.Append(CookieName, session.Secret, options);
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Redirect(session.TakeStartPath());
    }

    private static async Task WritePageAsync(HttpContext context, int status, string title, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'";
        await context.Response.WriteAsync(
            $$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>{{WebUtility.HtmlEncode(title)}} · NKeeper</title>
            <style>body{margin:0;min-height:100vh;display:grid;place-items:center;background:#0b1220;color:#e5ebf5;font:16px/1.5 system-ui,sans-serif}
            main{max-width:30rem;padding:2rem;background:#111a2e;border:1px solid #233150;border-radius:12px}h1{margin:0 0 .5rem;font-size:1.25rem}p{margin:0;color:#9aa8c1}</style>
            </head><body><main><h1>{{WebUtility.HtmlEncode(title)}}</h1><p>{{WebUtility.HtmlEncode(message)}}</p></main></body></html>
            """);
    }

}

/// <summary>
/// Makes the target believe it is being browsed directly (Host, Origin and Referer point at it)
/// and keeps the browser on the session origin (redirects and cookies are rewritten).
/// NKeeper's own session cookie never reaches the target.
/// </summary>
internal sealed partial class WebProxyTransformer(Uri target, string proxyOrigin, string? embedderOrigin) : HttpTransformer
{
    private readonly string _targetOrigin = target.GetLeftPart(UriPartial.Authority);

    public override async ValueTask TransformRequestAsync(HttpContext httpContext, HttpRequestMessage proxyRequest, string destinationPrefix, CancellationToken cancellationToken)
    {
        await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);

        // The request URI already points at the target, which then becomes the Host header.
        proxyRequest.Headers.Host = null;

        if (proxyRequest.Headers.TryGetValues("Cookie", out var cookies))
        {
            var kept = cookies
                .SelectMany(header => header.Split(';'))
                .Select(cookie => cookie.Trim())
                .Where(cookie => cookie.Length > 0 && !cookie.StartsWith(WebOriginHandler.CookieName + "=", StringComparison.Ordinal))
                .ToList();
            proxyRequest.Headers.Remove("Cookie");
            if (kept.Count > 0)
            {
                proxyRequest.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", kept));
            }
        }

        RewriteOrigin(proxyRequest, "Origin");
        RewriteOrigin(proxyRequest, "Referer");
    }

    public override async ValueTask<bool> TransformResponseAsync(HttpContext httpContext, HttpResponseMessage? proxyResponse, CancellationToken cancellationToken)
    {
        var result = await base.TransformResponseAsync(httpContext, proxyResponse, cancellationToken);
        var headers = httpContext.Response.Headers;

        // Absolute redirects back to the target stay on the session origin.
        var location = headers.Location.ToString();
        if (location.StartsWith(_targetOrigin, StringComparison.OrdinalIgnoreCase)
            && (location.Length == _targetOrigin.Length || location[_targetOrigin.Length] is '/' or '?' or '#'))
        {
            headers.Location = proxyOrigin + location[_targetOrigin.Length..];
        }

        // Cookies belong to the session origin, inside NKeeper's frame: drop Domain (it names the
        // target) and make them usable in a cross-site frame (SameSite=None; Secure; Partitioned).
        if (headers.SetCookie.Count > 0)
        {
            headers.SetCookie = new StringValues([.. headers.SetCookie.Select(cookie => ForFrame(cookie!))]);
        }

        // Only NKeeper may frame the session: replace the target's own framing rules.
        headers.Remove("X-Frame-Options");
        if (headers.ContentSecurityPolicy.Count > 0)
        {
            headers.ContentSecurityPolicy = new StringValues([.. headers.ContentSecurityPolicy
                .Select(policy => FrameAncestors().Replace(policy!, string.Empty).Trim().Trim(';').Trim())
                .Where(policy => policy.Length > 0)]);
        }

        headers.Append("Content-Security-Policy", $"frame-ancestors {embedderOrigin ?? "'self'"}");

        // Don't let the target pin HSTS or alternative services onto NKeeper's session host.
        headers.Remove("Strict-Transport-Security");
        headers.Remove("Alt-Svc");
        return result;
    }

    private void RewriteOrigin(HttpRequestMessage request, string header)
    {
        if (request.Headers.TryGetValues(header, out var values) && values.FirstOrDefault() is { } value
            && value.StartsWith(proxyOrigin, StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Remove(header);
            request.Headers.TryAddWithoutValidation(header, _targetOrigin + value[proxyOrigin.Length..]);
        }
    }

    private static string ForFrame(string cookie)
    {
        var rewritten = CrossSiteAttributes().Replace(cookie, string.Empty);
        return rewritten + "; Secure; SameSite=None; Partitioned";
    }

    // Domain (names the target), and attributes re-added in a cross-site-frame-friendly form.
    [GeneratedRegex(@";\s*(domain=[^;]*|samesite=[^;]*|secure|partitioned)(?=\s*(;|$))", RegexOptions.IgnoreCase)]
    private static partial Regex CrossSiteAttributes();

    [GeneratedRegex(@"(^|;)\s*frame-ancestors[^;]*", RegexOptions.IgnoreCase)]
    private static partial Regex FrameAncestors();
}
