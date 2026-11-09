namespace NKeeper.Plugins.Host;

/// <summary>Plugin host settings, from the <c>Plugins</c> configuration section.</summary>
public sealed class PluginHostOptions
{
    public const string SectionName = "Plugins";

    /// <summary>Where installed packages live (docs/plugin-security.md §4).</summary>
    public string Directory { get; set; } = "/usr/lib/nkeeper/plugins";

    /// <summary>Where additional trusted keys and the local revocation list live.</summary>
    public string TrustDirectory { get; set; } = "/etc/nkeeper/plugin-trust";

    /// <summary>Development only: a directory of development keys. Ignored in any other environment.</summary>
    public string? DevelopmentTrustDirectory { get; set; }

    /// <summary>
    /// Development only: the owner the location checks expect instead of root, so tests can run
    /// unprivileged (§5.7). Ignored in any other environment.
    /// </summary>
    public uint? DevelopmentOwnerUid { get; set; }

    /// <summary>Refuse to start if any enabled plugin is refused (§9).</summary>
    public bool RequireAllEnabledLoaded { get; set; }
}
