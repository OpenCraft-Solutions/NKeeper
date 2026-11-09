using Microsoft.Net.Http.Headers;
using NKeeper.Plugins.Host;
using NKeeper.Plugins.Packaging;

namespace NKeeper.Server;

/// <summary>
/// One plugin in <c>GET /api/plugins</c>. Until identity arrives (Phase 1) the endpoint is unauthenticated,
/// so it carries no free-text messages, which can name server paths; administrators see those in the
/// logs and audit trail meanwhile.
/// </summary>
/// <param name="FrontendEntry">The URL of the frontend entry module, for a loaded plugin that has a frontend.</param>
/// <param name="FrontendEntrySha256">The entry module's SHA-256, for checking it if browsers gain module integrity for import().</param>
public sealed record PluginStatusResponse(
    string Id,
    string? Version,
    PluginState State,
    string? ReasonCode,
    bool BuiltIn,
    string? PackageSha256,
    string? KeyId,
    string? FrontendEntry = null,
    string? FrontendEntrySha256 = null);

internal static class PluginEndpoints
{
    /// <summary>File types a plugin's frontend may serve (docs/plugin-security.md §7.2), with their content types.</summary>
    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".map"] = "application/json; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".ico"] = "image/x-icon",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
    };

    /// <summary>The URL prefix of a loaded plugin's frontend files: <c>/plugins/&lt;id&gt;/&lt;version&gt;/&lt;hash tag&gt;/</c>.</summary>
    public static string FrontendBase(LoadedPlugin plugin, string packageSha256) =>
        $"/plugins/{plugin.Manifest.Id}/{plugin.Manifest.Version.Text}/{packageSha256[..12]}/";

    public static void MapPluginStatus(this WebApplication app) =>
        app.MapGet("/api/plugins", (PluginHostResult plugins) => plugins.Statuses
            .Select(s =>
            {
                var loaded = s.State == PluginState.Loaded ? plugins.Loaded.FirstOrDefault(p => p.Manifest.Id == s.Id) : null;
                var entry = loaded?.Manifest.Frontend?.Entry;
                return new PluginStatusResponse(
                    s.Id,
                    s.Version,
                    s.State,
                    s.ReasonCode,
                    s.BuiltIn,
                    s.PackageSha256,
                    s.KeyId,
                    entry is null || s.PackageSha256 is null ? null : FrontendBase(loaded!, s.PackageSha256) + entry[PackageFormat.FrontendFolder.Length..],
                    entry is null ? null : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(loaded!.Files[entry].Span)));
            })
            .ToList());

    /// <summary>
    /// A loaded plugin's frontend files, from its verified in-memory copy (§7.2): only under
    /// <c>frontend/</c>, only allow-listed types, at a URL tagged with the package hash.
    /// </summary>
    public static void MapPluginFrontends(this WebApplication app) =>
        app.MapGet("/plugins/{id}/{version}/{tag}/{**path}", (string id, string version, string tag, string? path, PluginHostResult plugins, HttpContext context) =>
        {
            var status = plugins.Statuses.FirstOrDefault(s => s.Id == id && s.State == PluginState.Loaded);
            var plugin = plugins.Loaded.FirstOrDefault(p => p.Manifest.Id == id);
            var file = PackageFormat.FrontendFolder + (path ?? string.Empty);
            if (status?.PackageSha256 is not { } sha256
                || plugin is null
                || plugin.Manifest.Version.Text != version
                || !string.Equals(tag, sha256[..12], StringComparison.Ordinal)
                || !PackagePath.IsValid(file)
                || !ContentTypes.TryGetValue(Path.GetExtension(file), out var contentType)
                || !plugin.Files.TryGetValue(file, out var bytes))
            {
                return Results.NotFound();
            }

            context.Response.Headers[HeaderNames.CacheControl] = "public, max-age=31536000, immutable";
            return Results.Bytes(bytes, contentType);
        });
}
