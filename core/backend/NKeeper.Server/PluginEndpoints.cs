using NKeeper.Plugins.Host;

namespace NKeeper.Server;

/// <summary>
/// One plugin in <c>GET /api/plugins</c>. Until identity arrives (Phase 1) the endpoint is unauthenticated,
/// so it carries no free-text messages, which can name server paths; administrators see those in the
/// logs and audit trail meanwhile.
/// </summary>
public sealed record PluginStatusResponse(
    string Id,
    string? Version,
    PluginState State,
    string? ReasonCode,
    bool BuiltIn,
    string? PackageSha256,
    string? KeyId);

internal static class PluginEndpoints
{
    public static void MapPluginStatus(this WebApplication app) =>
        app.MapGet("/api/plugins", (PluginHostResult plugins) => plugins.Statuses
            .Select(s => new PluginStatusResponse(s.Id, s.Version, s.State, s.ReasonCode, s.BuiltIn, s.PackageSha256, s.KeyId))
            .ToList());
}
