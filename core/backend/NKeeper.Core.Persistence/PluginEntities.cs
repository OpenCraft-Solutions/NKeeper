namespace NKeeper.Core.Persistence;

/// <summary>
/// An administrator's binding of a plugin's integration to a host, service and credential (docs/architecture.md §7.39,
/// SDK 1.13). Deleting the host, service or credential deletes it.
/// </summary>
public sealed class PluginIntegrationBinding
{
    public string PluginId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public Guid HostId { get; set; }

    public Guid ServiceId { get; set; }

    public Guid CredentialId { get; set; }

    public Guid? SetByUserId { get; set; }

    public DateTimeOffset SetAtUtc { get; set; }
}

/// <summary>A plugin's settings document and its schema version (§7.39).</summary>
/// <summary>
/// The instance's lock after an export to move it (docs/architecture.md §7.45, from 1.x): while the row exists, hosts and
/// users can't be changed here, so nothing is changed that the new server would never get. Sessions still work.
/// </summary>
public sealed class InstanceLockRow
{
    /// <summary>Always 1: there's one lock.</summary>
    public int Id { get; set; } = 1;

    public DateTimeOffset LockedAtUtc { get; set; }

    public Guid? LockedByUserId { get; set; }

    public string LockedBy { get; set; } = string.Empty;
}

public sealed class PluginSettingsRow
{
    public string PluginId { get; set; } = string.Empty;

    public int Version { get; set; }

    /// <summary>The document, as JSON (<c>jsonb</c>).</summary>
    public string Data { get; set; } = "{}";

    public Guid? UpdatedByUserId { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>A plugin's document for one host (§7.39), deleted with the host.</summary>
public sealed class HostDataRow
{
    public string PluginId { get; set; } = string.Empty;

    public Guid HostId { get; set; }

    public int Version { get; set; }

    /// <summary>The document, as JSON (<c>jsonb</c>).</summary>
    public string Data { get; set; } = "{}";

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
