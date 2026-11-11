using Microsoft.Net.Http.Headers;
using NKeeper.Abstractions;
using NKeeper.Plugins.Host;
using NKeeper.Plugins.Packaging;

namespace NKeeper.Server;

/// <summary>
/// One plugin in <c>GET /api/plugins</c>, which any signed-in user may read. So it carries no free-text
/// messages, which can name server paths; administrators see those in the logs and audit trail.
/// </summary>
/// <param name="FrontendEntry">The URL of the frontend entry module, for a loaded plugin that has a frontend.</param>
/// <param name="FrontendEntrySha256">The entry module's SHA-256, for checking it if browsers gain module integrity for import().</param>
/// <param name="ServiceTypes">For a loaded plugin, the service types it contributed (docs/architecture.md §7.4).</param>
/// <param name="SessionTypes">For a loaded plugin, the session types it contributed (§7.10).</param>
/// <param name="HostActions">For a loaded plugin, the host actions it contributed (§7.12).</param>
public sealed record PluginStatusResponse(
    string Id,
    string? Version,
    PluginState State,
    string? ReasonCode,
    bool BuiltIn,
    string? PackageSha256,
    string? KeyId,
    string? FrontendEntry = null,
    string? FrontendEntrySha256 = null,
    IReadOnlyList<ServiceTypeResponse>? ServiceTypes = null,
    IReadOnlyList<SessionTypeResponse>? SessionTypes = null,
    IReadOnlyList<HostActionResponse>? HostActions = null);

/// <summary>A Connect menu entry: the session type it opens, and whether it needs a credential.</summary>
public sealed record HostActionResponse(string Id, string Label, string SessionType, HostActionCredential Credential);

/// <summary>A session type: what it is called, and the service types it opens on.</summary>
public sealed record SessionTypeResponse(string Id, string Label, IReadOnlyList<string> ServiceTypes, bool SupportsJumpHost);

/// <summary>A service type, with the schema the shell draws a service's settings form from.</summary>
/// <param name="Health">The kind of its health probe (§7.13), e.g. <c>tcp</c>; null when it has none.</param>
public sealed record ServiceTypeResponse(string Id, string Label, int? DefaultPort, int SettingsVersion, IReadOnlyList<ServiceSettingResponse> Settings, string? Health)
{
    public static ServiceTypeResponse From(ServiceType type) =>
        new(type.Id, type.Label, type.DefaultPort, type.SettingsVersion, [.. type.Settings.Select(ServiceSettingResponse.From)], type.Health?.Kind);
}

/// <param name="Choices">For a choice, its values; otherwise null.</param>
public sealed record ServiceSettingResponse(
    string Key,
    string Label,
    ServiceSettingKind Kind,
    bool Required,
    int? MaxLength,
    long? Minimum,
    long? Maximum,
    IReadOnlyList<string>? Choices)
{
    public static ServiceSettingResponse From(ServiceSetting setting) =>
        new(
            setting.Key,
            setting.Label,
            setting.Kind,
            setting.Required,
            setting.MaxLength,
            setting.Minimum,
            setting.Maximum,
            setting.Kind == ServiceSettingKind.Choice ? setting.Choices : null);
}

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

    /// <summary>A package path as a URL path: each segment escaped (a valid name may hold '#' or '?'), the slashes kept.</summary>
    private static string EscapePath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    public static void MapPluginStatus(this IEndpointRouteBuilder api) =>
        api.MapGet("/plugins", (PluginHostResult plugins) => plugins.Statuses
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
                    entry is null || s.PackageSha256 is null ? null : FrontendBase(loaded!, s.PackageSha256) + EscapePath(entry[PackageFormat.FrontendFolder.Length..]),
                    entry is null ? null : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(loaded!.Files[entry].Span)),
                    loaded is null ? null : [.. plugins.ServiceTypes.Where(t => t.PluginId == s.Id).Select(t => ServiceTypeResponse.From(t.Type))],
                    loaded is null ? null : [.. plugins.SessionTypes.Where(t => t.PluginId == s.Id).Select(t => new SessionTypeResponse(t.Type.Id, t.Type.Label, t.Type.ServiceTypes, t.Type.SupportsJumpHost))],
                    loaded is null ? null : [.. plugins.HostActions.Where(a => a.PluginId == s.Id).Select(a => new HostActionResponse(a.Action.Id, a.Action.Label, a.Action.SessionType, a.Action.Credential))]);
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

            context.Response.Headers[HeaderNames.CacheControl] = "private, max-age=31536000, immutable";
            return Results.Bytes(bytes, contentType);
        });
}
