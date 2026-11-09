using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace NKeeper.Server;

/// <summary>Where the built shell is, from the <c>Frontend</c> configuration section.</summary>
public sealed class FrontendOptions
{
    public const string SectionName = "Frontend";

    /// <summary>The shell's build output (<c>core/frontend/app/dist</c>); null for an API-only server.</summary>
    public string? Root { get; set; }
}

/// <summary>
/// The shell as the server serves it (docs/plugin-security.md §7.2, §7.3): its index.html, checked
/// at startup, and the Content Security Policy that allows exactly its one import map.
/// </summary>
public sealed partial class ShellDocument
{
    private ShellDocument(string root, byte[] index, string contentSecurityPolicy)
    {
        Root = root;
        Index = index;
        ContentSecurityPolicy = contentSecurityPolicy;
    }

    public string Root { get; }

    public byte[] Index { get; }

    public string ContentSecurityPolicy { get; }

    /// <summary>
    /// Reads index.html. Refuses (throws) if it has any inline script other than at most one import
    /// map, so a build mistake can't quietly widen the policy.
    /// </summary>
    public static ShellDocument Load(string root)
    {
        var path = Path.Combine(root, "index.html");
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Frontend:Root is set, but '{path}' doesn't exist. Build the frontend first (npm run build).");
        }

        var index = File.ReadAllBytes(path);
        var html = Encoding.UTF8.GetString(index);
        var importMaps = new List<string>();
        foreach (Match script in ScriptPattern().Matches(html))
        {
            var attributes = script.Groups["attributes"].Value;
            if (SrcAttributePattern().IsMatch(attributes))
            {
                if (script.Groups["body"].Value.Trim().Length > 0)
                {
                    throw new InvalidOperationException("index.html has a script with both src and inline content.");
                }

                continue;
            }

            if (!ImportMapTypePattern().IsMatch(attributes))
            {
                throw new InvalidOperationException("index.html has an inline script other than the import map; the Content Security Policy allows none.");
            }

            importMaps.Add(script.Groups["body"].Value);
        }

        if (importMaps.Count > 1)
        {
            throw new InvalidOperationException("index.html has more than one import map.");
        }

        var scriptSource = importMaps.Count == 1
            ? $"'self' 'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(importMaps[0])))}'"
            : "'self'";
        var policy = string.Join("; ",
            "default-src 'self'",
            $"script-src {scriptSource}",
            "style-src 'self'",
            "img-src 'self' data:",
            "connect-src 'self'",
            "font-src 'self'",
            "object-src 'none'",
            "base-uri 'none'",
            "form-action 'self'",
            "frame-ancestors 'none'");
        return new ShellDocument(Path.GetFullPath(root), index, policy);
    }

    [GeneratedRegex(@"<script\b(?<attributes>[^>]*)>(?<body>[\s\S]*?)</script\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScriptPattern();

    [GeneratedRegex(@"\bsrc\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SrcAttributePattern();

    [GeneratedRegex(@"\btype\s*=\s*[""']?importmap[""']?(\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImportMapTypePattern();
}

internal static class FrontendHosting
{
    private static readonly string[] HashedFolders = ["/assets/", "/runtime/"];

    /// <summary>Headers on every response: no MIME sniffing, no referrer.</summary>
    public static void UseSecurityHeaders(this WebApplication app) =>
        app.Use(async (context, next) =>
        {
            context.Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            await next(context);
        });

    /// <summary>Serves the shell's files, and index.html (with its policy) for every other page route.</summary>
    public static void UseShell(this WebApplication app, ShellDocument shell)
    {
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(shell.Root),
            ServeUnknownFileTypes = false,
            OnPrepareResponse = context =>
            {
                var path = context.Context.Request.Path.Value ?? string.Empty;
                if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                {
                    // index.html requested by name gets the same policy as every other page.
                    context.Context.Response.Headers[HeaderNames.ContentSecurityPolicy] = shell.ContentSecurityPolicy;
                }

                context.Context.Response.Headers[HeaderNames.CacheControl] = HashedFolders.Any(f => path.StartsWith(f, StringComparison.Ordinal))
                    ? "public, max-age=31536000, immutable"
                    : "no-cache";
            },
        });
    }

    /// <summary>The single-page app: any GET that isn't the API, a plugin file or health gets index.html.</summary>
    public static void MapShellFallback(this WebApplication app, ShellDocument shell) =>
        app.MapFallback(context =>
        {
            var path = context.Request.Path;
            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)
                || path.StartsWithSegments("/api") || path.StartsWithSegments("/plugins") || path.StartsWithSegments("/health"))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }

            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers[HeaderNames.ContentSecurityPolicy] = shell.ContentSecurityPolicy;
            context.Response.Headers[HeaderNames.CacheControl] = "no-cache";
            return context.Response.Body.WriteAsync(shell.Index).AsTask();
        });
}
