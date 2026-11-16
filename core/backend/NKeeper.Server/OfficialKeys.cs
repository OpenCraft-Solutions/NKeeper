using NKeeper.Plugins.Packaging;

namespace NKeeper.Server;

/// <summary>
/// The official plugin signing keys and revocations embedded in this server build
/// (docs/plugin-security.md §6.1, §6.3). The public half of the official release key (decision D12) signs the
/// plugins each plugin repository releases; its private half is an organisation secret, never in this repository.
/// </summary>
internal static class OfficialKeys
{
    private const string ReleasePublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEJGOfmdjLgmoex/qdvkgPqR9Ht/tU1W2Hm9CMLD7A5n+yGvl9QBLEm40WPGl0vyuo1E6otle48ejZ/yU9tzYPXw==";

    public static IReadOnlyList<TrustedKey> All { get; } =
    [
        TrustedKey.Official(Convert.FromBase64String(ReleasePublicKey), "official release key (D12)"),
    ];

    public static Revocations Revocations { get; } = Revocations.None;
}
