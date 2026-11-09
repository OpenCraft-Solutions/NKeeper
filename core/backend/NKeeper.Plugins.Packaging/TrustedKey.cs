using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace NKeeper.Plugins.Packaging;

/// <summary>Where a key's authority comes from (docs/plugin-security.md §6.1).</summary>
public enum TrustedKeyKind
{
    /// <summary>Embedded in the server: may sign any plugin ID.</summary>
    Official,

    /// <summary>From the trust directory: may sign only the reverse-domain IDs it lists.</summary>
    ThirdParty,

    /// <summary>A developer's or CI run's key: refused outright outside the Development environment.</summary>
    Development,
}

/// <summary>A public key trusted to sign plugin packages, with what it may sign.</summary>
public sealed class TrustedKey
{
    private static readonly JsonSerializerOptions IndentedOptions = new(PackageJson.Options) { WriteIndented = true };

    private TrustedKey(string keyId, byte[] publicKey, TrustedKeyKind kind, IReadOnlyList<string> allowedIds, DateOnly? notAfter, string? comment)
    {
        KeyId = keyId;
        PublicKey = publicKey;
        Kind = kind;
        AllowedIds = allowedIds;
        NotAfter = notAfter;
        Comment = comment;
    }

    public string KeyId { get; }

    /// <summary>DER SubjectPublicKeyInfo.</summary>
    public ReadOnlyMemory<byte> PublicKey { get; }

    public TrustedKeyKind Kind { get; }

    /// <summary>Third-party keys only: exact IDs or <c>prefix.*</c> patterns (§6.2).</summary>
    public IReadOnlyList<string> AllowedIds { get; }

    /// <summary>The last day (UTC) the key is valid on; null for no end.</summary>
    public DateOnly? NotAfter { get; }

    public string? Comment { get; }

    /// <summary>An official key, embedded in a server release.</summary>
    public static TrustedKey Official(ReadOnlySpan<byte> publicKey, string? comment = null, DateOnly? notAfter = null) =>
        Create(publicKey, TrustedKeyKind.Official, [], notAfter, comment);

    /// <summary>A third-party key allowed to sign the IDs matching <paramref name="allowedIds"/>.</summary>
    public static TrustedKey ThirdParty(ReadOnlySpan<byte> publicKey, IReadOnlyList<string> allowedIds, string? comment = null, DateOnly? notAfter = null)
    {
        var invalid = allowedIds.FirstOrDefault(p => !IsValidPattern(p));
        if (allowedIds.Count == 0 || invalid is not null)
        {
            throw new TrustConfigurationException(
                $"A third-party key needs at least one allowed ID, and each must be a reverse-domain ID or a pattern such as 'com.example.*' with at least two fixed segments ('{invalid}' is not).");
        }

        return Create(publicKey, TrustedKeyKind.ThirdParty, allowedIds, notAfter, comment);
    }

    /// <summary>A development key: valid only on a server running in Development.</summary>
    public static TrustedKey Development(ReadOnlySpan<byte> publicKey, string? comment = null) =>
        Create(publicKey, TrustedKeyKind.Development, [], null, comment);

    /// <summary>
    /// Reads a trust-directory key file (§6.1). Files hold third-party or development keys only;
    /// official keys are embedded in the server and can't come from a file.
    /// </summary>
    public static TrustedKey FromKeyFile(ReadOnlySpan<byte> json)
    {
        if (!PackageJson.TryDeserialize<KeyFile>(json, out var file, out var error))
        {
            throw new TrustConfigurationException($"The key file is malformed: {error}");
        }

        byte[] publicKey;
        try
        {
            publicKey = Convert.FromBase64String(file.PublicKey);
        }
        catch (FormatException ex)
        {
            throw new TrustConfigurationException("The key file's public key isn't base64.", ex);
        }

        DateOnly? notAfter = null;
        if (file.NotAfter is { } text)
        {
            if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                throw new TrustConfigurationException("The key file's notAfter must be a date such as 2028-01-01.");
            }

            notAfter = date;
        }

        var key = file.Development == true
            ? (file.AllowedIds is { Count: > 0 } || notAfter is not null
                ? throw new TrustConfigurationException("A development key file can't limit IDs or set an end date: it is valid for any ID, in Development only.")
                : Development(publicKey, file.Comment))
            : ThirdParty(publicKey, file.AllowedIds ?? [], file.Comment, notAfter);

        if (file.KeyId != key.KeyId)
        {
            throw new TrustConfigurationException($"The key file names key ID '{file.KeyId}', but its public key's ID is '{key.KeyId}'.");
        }

        return key;
    }

    /// <summary>Writes this key as a trust-directory key file.</summary>
    public byte[] ToKeyFile()
    {
        if (Kind == TrustedKeyKind.Official)
        {
            throw new InvalidOperationException("Official keys are embedded in the server, not written to key files.");
        }

        var file = new KeyFile(
            KeyId,
            Convert.ToBase64String(PublicKey.Span),
            Kind == TrustedKeyKind.ThirdParty ? AllowedIds : null,
            NotAfter?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Comment,
            Kind == TrustedKeyKind.Development ? true : null);
        return JsonSerializer.SerializeToUtf8Bytes(file, IndentedOptions);
    }

    /// <summary>Whether this key may sign <paramref name="pluginId"/> (§6.2). The environment check for development keys happens earlier.</summary>
    public bool MaySign(string pluginId) => Kind switch
    {
        TrustedKeyKind.Official or TrustedKeyKind.Development => true,
        _ => !PluginId.IsOfficial(pluginId) && AllowedIds.Any(pattern => Matches(pattern, pluginId)),
    };

    /// <summary>Whether the key's validity has ended by <paramref name="now"/>.</summary>
    public bool IsExpired(DateTimeOffset now) => NotAfter is { } last && DateOnly.FromDateTime(now.UtcDateTime) > last;

    /// <summary>Verifies an IEEE P1363 ECDSA P-256 / SHA-256 signature.</summary>
    public bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        using var key = SigningKeys.ImportPublicKey(PublicKey.Span);
        return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    private static TrustedKey Create(ReadOnlySpan<byte> publicKey, TrustedKeyKind kind, IReadOnlyList<string> allowedIds, DateOnly? notAfter, string? comment)
    {
        try
        {
            // Import once to prove it is a P-256 public key.
            using var _ = SigningKeys.ImportPublicKey(publicKey);
        }
        catch (CryptographicException ex)
        {
            throw new TrustConfigurationException("The public key is not an ECDSA P-256 key.", ex);
        }

        return new TrustedKey(SigningKeys.ComputeKeyId(publicKey), publicKey.ToArray(), kind, allowedIds, notAfter, comment);
    }

    /// <summary>An exact reverse-domain ID, or <c>a.b.*</c> with at least two fixed segments; never an official ID.</summary>
    internal static bool IsValidPattern(string pattern)
    {
        var prefix = pattern.EndsWith(".*", StringComparison.Ordinal) ? pattern[..^2] : pattern;
        return PluginId.IsValid(prefix) && !PluginId.IsOfficial(prefix);
    }

    private static bool Matches(string pattern, string id) =>
        pattern.EndsWith(".*", StringComparison.Ordinal)
            ? id.StartsWith(pattern[..^1], StringComparison.Ordinal) && id.Length > pattern.Length - 1
            : id == pattern;

    private sealed record KeyFile(string KeyId, string PublicKey, IReadOnlyList<string>? AllowedIds = null, string? NotAfter = null, string? Comment = null, bool? Development = null);
}
