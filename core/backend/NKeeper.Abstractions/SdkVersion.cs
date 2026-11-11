namespace NKeeper.Abstractions;

/// <summary>
/// The version of the plugin SDK this build of NKeeper implements. A plugin's manifest names the
/// SDK versions it is compatible with, and a package outside that range is refused
/// (docs/plugin-security.md §5.5).
/// </summary>
public static class SdkVersion
{
    /// <summary>
    /// SDK v1, frozen at the end of Phase 3 (docs/architecture.md §4): from here an addition raises the minor
    /// number, and a breaking change the major one.
    /// </summary>
    public const string Current = "1.0.0";
}
