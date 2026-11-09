namespace NKeeper.Plugins.Packaging;

/// <summary>The size limits of docs/plugin-security.md §5.1 and §5.2.</summary>
public sealed record PackageLimits
{
    /// <summary>The limits every server uses.</summary>
    public static PackageLimits Default { get; } = new();

    /// <summary>The largest package file accepted.</summary>
    public long MaxPackageBytes { get; init; } = 200L * 1024 * 1024;

    /// <summary>The most entries an archive may have.</summary>
    public int MaxEntries { get; init; } = 5_000;

    /// <summary>The most bytes all entries may decompress to, together.</summary>
    public long MaxTotalUncompressedBytes { get; init; } = 500L * 1024 * 1024;

    /// <summary>The highest uncompressed-to-compressed ratio of any one entry.</summary>
    public int MaxCompressionRatio { get; init; } = 100;

    /// <summary>The largest <c>nkeeper-plugin.json</c>.</summary>
    public int MaxManifestBytes { get; init; } = 64 * 1024;

    /// <summary>The largest <c>files.json</c>.</summary>
    public int MaxFileListBytes { get; init; } = 1024 * 1024;

    /// <summary>The largest <c>files.json.sig</c>.</summary>
    public int MaxSignatureBytes { get; init; } = 4 * 1024;
}
