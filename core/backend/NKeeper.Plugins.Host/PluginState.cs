using NKeeper.Abstractions;
using NKeeper.Plugins.Packaging;

namespace NKeeper.Plugins.Host;

/// <summary>What core remembers about a plugin it has seen (docs/plugin-security.md §8): the pin and the enabled state.</summary>
public sealed record PluginRecord(
    string Id,
    string Version,
    string PackageSha256,
    string KeyId,
    bool BuiltIn,
    bool Enabled,
    string? EnabledBy,
    DateTimeOffset? EnabledAtUtc,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>Where plugin records are kept: the core database in the server, memory in tests.</summary>
public interface IPluginStateStore
{
    Task<IReadOnlyDictionary<string, PluginRecord>> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Inserts or replaces the record for <see cref="PluginRecord.Id"/>.</summary>
    Task SaveAsync(PluginRecord record, CancellationToken cancellationToken);
}

/// <summary>Where the host records plugin audit events (§9): <c>plugin.discovered</c>, <c>plugin.refused</c>….</summary>
public interface IPluginAuditSink
{
    Task WriteAsync(string action, string target, string detail, CancellationToken cancellationToken);
}

/// <summary>Where a plugin stands after startup.</summary>
public enum PluginState
{
    /// <summary>Verified, enabled, and its code is loaded.</summary>
    Loaded,

    /// <summary>Verified, but not enabled.</summary>
    Disabled,

    /// <summary>A newer version signed by a different key: waits for an administrator to approve the signer.</summary>
    AwaitingSignerApproval,

    /// <summary>Refused: see <see cref="PluginStatus.ReasonCode"/>.</summary>
    Refused,

    /// <summary>Recorded before, but its package is no longer installed. Its data is kept.</summary>
    Missing,
}

/// <summary>One plugin's (or one package file's) outcome, as the admin UI and <c>GET /api/plugins</c> show it.</summary>
/// <param name="Id">The plugin ID; for a package refused before its identity was known, its file name.</param>
public sealed record PluginStatus(
    string Id,
    string? Version,
    PluginState State,
    string? ReasonCode,
    string Message,
    string? PackageSha256,
    string? KeyId,
    bool BuiltIn,
    string? FileName);

/// <summary>A plugin whose verified code is loaded.</summary>
public sealed record LoadedPlugin(PluginManifest Manifest, IPlugin Instance, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Files);

/// <summary>Raised when the server must not start (<see cref="PluginHostOptions.RequireAllEnabledLoaded"/>, or a plugin failing after its endpoints began mapping).</summary>
public sealed class PluginHostException : Exception
{
    public PluginHostException()
    {
    }

    public PluginHostException(string message)
        : base(message)
    {
    }

    public PluginHostException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
