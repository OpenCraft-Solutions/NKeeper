namespace NKeeper.Plugins.Packaging;

/// <summary>The outcome of verifying a package: either everything it holds, or exactly one reason it was refused.</summary>
public sealed class PackageVerification
{
    private PackageVerification(
        string? reasonCode,
        string message,
        string? packageSha256,
        PluginManifest? manifest,
        string? keyId,
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files)
    {
        ReasonCode = reasonCode;
        Message = message;
        PackageSha256 = packageSha256;
        Manifest = manifest;
        KeyId = keyId;
        Files = files;
    }

    /// <summary>Whether every check passed. Only then may anything from the package be used.</summary>
    public bool IsVerified => ReasonCode is null;

    /// <summary>One of <see cref="ReasonCodes"/> when refused; null when verified.</summary>
    public string? ReasonCode { get; }

    /// <summary>A human-readable explanation, for logs and the admin UI.</summary>
    public string Message { get; }

    /// <summary>The SHA-256 of the whole package (lowercase hex), once it was read; used for pinning (§8).</summary>
    public string? PackageSha256 { get; }

    /// <summary>The validated manifest; only when verified.</summary>
    public PluginManifest? Manifest { get; }

    /// <summary>The key that signed the package; only when verified.</summary>
    public string? KeyId { get; }

    /// <summary>
    /// Every listed file's verified contents by package path (not the manifest's signature files);
    /// empty unless verified. These are the bytes code and frontend files must be loaded from (§7).
    /// </summary>
    public IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Files { get; }

    internal static PackageVerification Verified(string packageSha256, PluginManifest manifest, string keyId, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files) =>
        new(null, $"Verified: {manifest.Id} {manifest.Version}, signed by key {keyId}.", packageSha256, manifest, keyId, files);

    internal static PackageVerification Refused(string reasonCode, string message, string? packageSha256) =>
        new(reasonCode, message, packageSha256, null, null, new Dictionary<string, ReadOnlyMemory<byte>>());

    public override string ToString() => IsVerified ? Message : $"{ReasonCode}: {Message}";
}
