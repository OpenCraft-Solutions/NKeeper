namespace NKeeper.Abstractions;

/// <summary>
/// The version of the plugin SDK this build of NKeeper implements. A plugin's manifest names the
/// SDK versions it is compatible with, and a package outside that range is refused
/// (docs/plugin-security.md §5.5).
/// </summary>
public static class SdkVersion
{
    /// <summary>SDK v0: unstable until it is frozen as v1 at the end of Phase 3 (docs/v2-plan.md §8).</summary>
    public const string Current = "0.1.8";
}
