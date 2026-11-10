namespace NKeeper.Plugins.Host;

/// <summary>
/// Refusals by the plugin host (docs/plugin-security.md §5.6, §5.7, §8), in addition to the
/// verifier's <see cref="Packaging.ReasonCodes"/>.
/// </summary>
public static class HostReasonCodes
{
    /// <summary>Outside Development, on an OS other than Linux: no plugin loads.</summary>
    public const string UnsupportedPlatform = "unsupported_platform";

    /// <summary>The plugin or trust directory, an ancestor, or a file in them failed the ownership and mode checks: no plugin loads.</summary>
    public const string UnsafeLocation = "unsafe_location";

    /// <summary>A trust key file or the revocation list can't be used: no plugin loads.</summary>
    public const string BadTrustConfig = "bad_trust_config";

    /// <summary>Two packages carry the same plugin ID: neither loads.</summary>
    public const string DuplicateId = "duplicate_id";

    /// <summary>The verified code couldn't be loaded or configured.</summary>
    public const string LoadFailed = "load_failed";

    /// <summary>A contribution broke its rules, or took an ID another plugin already contributed.</summary>
    public const string ContributionInvalid = "contribution_invalid";

    /// <summary>The same version was seen before with a different package hash: treated as tampering.</summary>
    public const string PinMismatch = "pin_mismatch";

    /// <summary>An older version than the one recorded.</summary>
    public const string Downgrade = "downgrade";

    /// <summary>A dependency is missing, refused, disabled or out of range.</summary>
    public const string Dependency = "dependency";

    /// <summary>The plugin is part of a dependency cycle.</summary>
    public const string DependencyCycle = "dependency_cycle";
}
