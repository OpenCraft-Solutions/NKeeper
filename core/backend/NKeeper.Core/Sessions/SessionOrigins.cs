using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Abstractions;

namespace NKeeper.Core.Sessions;

/// <summary>
/// Session origins (docs/architecture.md §7.18): requests for <c>&lt;prefix&gt;-&lt;label&gt;.&lt;web domain&gt;</c>
/// go straight to the plugin that claimed the prefix, before any of NKeeper's own pipeline.
/// </summary>
public sealed partial class SessionOriginRouter : ISessionOrigins
{
    public const string BaseDomainSetting = "Origins:BaseDomain";

    private readonly Dictionary<string, Type> _handlers;

    /// <param name="baseDomain">The web domain, or null for none (origins off).</param>
    /// <param name="origins">The prefixes the loaded plugins claimed, with their handlers.</param>
    public SessionOriginRouter(string? baseDomain, IEnumerable<SessionOrigin> origins)
    {
        if (!string.IsNullOrWhiteSpace(baseDomain))
        {
            baseDomain = baseDomain.Trim().TrimEnd('.').ToLowerInvariant();
            if (!DomainPattern().IsMatch(baseDomain))
            {
                throw new InvalidOperationException($"{BaseDomainSetting} '{baseDomain}' isn't a DNS name such as web.example.com.");
            }

            BaseDomain = baseDomain;
        }

        _handlers = origins.ToDictionary(o => o.Prefix, o => o.Handler, StringComparer.Ordinal);
    }

    /// <summary>The web domain, lower-case; null when origins are off.</summary>
    public string? BaseDomain { get; }

    public Uri? UrlFor(string prefix, string label) =>
        BaseDomain is null || !_handlers.ContainsKey(prefix) || !LabelPattern().IsMatch(label)
            ? null
            : new Uri($"https://{prefix}-{label}.{BaseDomain}/");

    /// <summary>The handler and label for a request's host, or null when it isn't a session origin.</summary>
    public (Type Handler, string Label)? Match(HostString host)
    {
        if (BaseDomain is null)
        {
            return null;
        }

        var name = host.Host.ToLowerInvariant();
        var suffix = "." + BaseDomain;
        if (!name.EndsWith(suffix, StringComparison.Ordinal))
        {
            return null;
        }

        // Exactly one label in front of the web domain: <prefix>-<label>.
        var first = name[..^suffix.Length];
        var dash = first.IndexOf('-', StringComparison.Ordinal);
        if (dash <= 0 || first.Contains('.', StringComparison.Ordinal))
        {
            return null;
        }

        var label = first[(dash + 1)..];
        return _handlers.TryGetValue(first[..dash], out var handler) && LabelPattern().IsMatch(label) ? (handler, label) : null;
    }

    [GeneratedRegex(@"^(?=.{1,200}\z)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z][a-z0-9-]{0,62}\z")]
    private static partial Regex DomainPattern();

    [GeneratedRegex(@"^[a-z0-9]{1,63}\z")]
    private static partial Regex LabelPattern();
}

public static class SessionOriginServices
{
    /// <summary>
    /// First in the pipeline (after forwarded headers): a session origin's request goes to its plugin's handler and
    /// nowhere else, so it never reaches NKeeper's sign-in, antiforgery, API, shell or security headers.
    /// </summary>
    public static void UseSessionOrigins(this WebApplication app)
    {
        var router = app.Services.GetRequiredService<SessionOriginRouter>();
        if (router.BaseDomain is null)
        {
            return;
        }

        app.Use(async (context, next) =>
        {
            if (router.Match(context.Request.Host) is not var (handlerType, label))
            {
                await next(context);
                return;
            }

            var handler = (ISessionOriginHandler)context.RequestServices.GetRequiredService(handlerType);
            await handler.HandleAsync(context, label);
        });
    }
}
