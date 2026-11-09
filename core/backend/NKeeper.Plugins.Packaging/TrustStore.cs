using System.Text.RegularExpressions;

namespace NKeeper.Plugins.Packaging;

/// <summary>Trust configuration that can't be used. The plugin host refuses every plugin when this happens (§4).</summary>
public sealed class TrustConfigurationException : Exception
{
    public TrustConfigurationException()
    {
    }

    public TrustConfigurationException(string message)
        : base(message)
    {
    }

    public TrustConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Revoked key IDs and package hashes (§6.3): embedded in a release, plus <c>revoked.json</c> locally.</summary>
public sealed partial record Revocations(IReadOnlySet<string> KeyIds, IReadOnlySet<string> PackageSha256)
{
    public static Revocations None { get; } = new(new HashSet<string>(), new HashSet<string>());

    /// <summary>Reads <c>revoked.json</c>: <c>{ "revokedKeyIds": [...], "revokedPackageSha256": [...] }</c>.</summary>
    public static Revocations FromJson(ReadOnlySpan<byte> json)
    {
        if (!PackageJson.TryDeserialize<RevocationsFile>(json, out var file, out var error))
        {
            throw new TrustConfigurationException($"The revocation list is malformed: {error}");
        }

        var keyIds = file.RevokedKeyIds ?? [];
        var hashes = file.RevokedPackageSha256 ?? [];
        if (keyIds.Any(k => !SigningKeys.IsKeyId(k)) || hashes.Any(h => !Sha256Pattern().IsMatch(h)))
        {
            throw new TrustConfigurationException("The revocation list has a malformed key ID or package hash.");
        }

        return new Revocations(keyIds.ToHashSet(StringComparer.Ordinal), hashes.ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>Both lists combined.</summary>
    public Revocations Union(Revocations other) =>
        new(KeyIds.Union(other.KeyIds).ToHashSet(StringComparer.Ordinal), PackageSha256.Union(other.PackageSha256).ToHashSet(StringComparer.Ordinal));

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    private sealed record RevocationsFile(IReadOnlyList<string>? RevokedKeyIds, IReadOnlyList<string>? RevokedPackageSha256);
}

/// <summary>The keys a server trusts and what it has revoked.</summary>
public sealed class TrustStore
{
    private readonly Dictionary<string, TrustedKey> _keys;

    public TrustStore(IEnumerable<TrustedKey> keys, Revocations? revocations = null)
    {
        _keys = new Dictionary<string, TrustedKey>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (!_keys.TryAdd(key.KeyId, key))
            {
                throw new TrustConfigurationException($"Key '{key.KeyId}' is configured more than once.");
            }
        }

        Revocations = revocations ?? Revocations.None;
    }

    public Revocations Revocations { get; }

    public IEnumerable<TrustedKey> Keys => _keys.Values;

    public TrustedKey? Find(string keyId) => _keys.GetValueOrDefault(keyId);
}
