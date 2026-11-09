using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace NKeeper.Plugins.Packaging;

/// <summary><c>files.json.sig</c>: which key signed the file list, and the signature (docs/plugin-security.md §3).</summary>
public sealed record PackageSignature(string KeyId, string Algorithm, string Signature)
{
    /// <summary>The length of a P-256 signature in IEEE P1363 form (r ‖ s).</summary>
    public const int SignatureLength = 64;

    public byte[] ToJson() => JsonSerializer.SerializeToUtf8Bytes(this, PackageJson.Options);

    /// <summary>Parses a signature file, checking its algorithm and the signature's encoding.</summary>
    public static bool TryParse(ReadOnlySpan<byte> json, [NotNullWhen(true)] out PackageSignature? signature, [NotNullWhen(true)] out byte[]? signatureBytes)
    {
        signatureBytes = null;
        if (!PackageJson.TryDeserialize(json, out signature, out _)
            || signature.Algorithm != PackageFormat.SignatureAlgorithm
            || !SigningKeys.IsKeyId(signature.KeyId))
        {
            signature = null;
            return false;
        }

        var buffer = new byte[SignatureLength + 3];
        if (!Convert.TryFromBase64String(signature.Signature, buffer, out var written) || written != SignatureLength)
        {
            signature = null;
            return false;
        }

        signatureBytes = buffer[..SignatureLength];
        return true;
    }
}
