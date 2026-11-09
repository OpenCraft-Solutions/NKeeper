using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace NKeeper.Plugins.Packaging;

/// <summary>Key IDs and public-key checks shared by signing and verification.</summary>
public static partial class SigningKeys
{
    /// <summary>
    /// A key's ID: the lowercase hex of the first 16 bytes of the SHA-256 of its public key (DER
    /// SubjectPublicKeyInfo), so an ID can't be claimed for a different key.
    /// </summary>
    public static string ComputeKeyId(ReadOnlySpan<byte> subjectPublicKeyInfo) =>
        Convert.ToHexStringLower(SHA256.HashData(subjectPublicKeyInfo)[..16]);

    /// <summary>Whether <paramref name="value"/> has the form of a key ID.</summary>
    public static bool IsKeyId(string? value) => value is not null && KeyIdPattern().IsMatch(value);

    /// <summary>Imports a public key, requiring an ECDSA key on the P-256 curve.</summary>
    internal static ECDsa ImportPublicKey(ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out var read);
            if (read != subjectPublicKeyInfo.Length || !IsP256(key))
            {
                throw new CryptographicException("The key is not an ECDSA P-256 public key.");
            }

            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>Whether a key is on the NIST P-256 curve: not merely 256 bits, which secp256k1 also is.</summary>
    internal static bool IsP256(ECDsa key) =>
        key.KeySize == 256 && key.ExportParameters(includePrivateParameters: false).Curve.Oid.Value == ECCurve.NamedCurves.nistP256.Oid.Value;

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyIdPattern();
}

/// <summary>A private signing key (ECDSA P-256). Only the packaging tool holds one.</summary>
public sealed class PluginSigningKey : IDisposable
{
    private readonly ECDsa _key;

    private PluginSigningKey(ECDsa key)
    {
        _key = key;
        PublicKey = key.ExportSubjectPublicKeyInfo();
        KeyId = SigningKeys.ComputeKeyId(PublicKey.Span);
    }

    /// <summary>The public key, DER SubjectPublicKeyInfo.</summary>
    public ReadOnlyMemory<byte> PublicKey { get; }

    public string KeyId { get; }

    /// <summary>Creates a new random key.</summary>
    public static PluginSigningKey Generate() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>Loads a private key from PKCS#8 PEM, as <see cref="ExportPrivateKeyPem"/> writes it.</summary>
    public static PluginSigningKey FromPem(string pem)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(pem);
            if (!SigningKeys.IsP256(key))
            {
                throw new CryptographicException("The key is not an ECDSA P-256 key.");
            }

            return new PluginSigningKey(key);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    public string ExportPrivateKeyPem() => _key.ExportPkcs8PrivateKeyPem();

    /// <summary>Signs <paramref name="data"/>: SHA-256, IEEE P1363 (r ‖ s, 64 bytes).</summary>
    public byte[] Sign(ReadOnlySpan<byte> data) =>
        _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public void Dispose() => _key.Dispose();
}
