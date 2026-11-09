using NKeeper.Plugins.Packaging;

namespace NKeeper.Server;

/// <summary>
/// The official plugin signing keys and revocations embedded in this server build
/// (docs/plugin-security.md §6.1, §6.3). Empty until the first release: the release key is generated
/// and embedded then (decision D12), and until that day every build uses development keys only.
/// </summary>
internal static class OfficialKeys
{
    public static IReadOnlyList<TrustedKey> All { get; } = [];

    public static Revocations Revocations { get; } = Revocations.None;
}
