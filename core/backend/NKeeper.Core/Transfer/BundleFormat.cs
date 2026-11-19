using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NKeeper.Core.Transfer;

/// <summary>The bundle's plain header: what it is and how its key is derived. Authenticated, not secret.</summary>
/// <param name="Format">The bundle format version; <see cref="BundleFormat.Version"/>.</param>
/// <param name="NKeeperVersion">The version of NKeeper that wrote it.</param>
/// <param name="Migration">The source database's latest migration: the target must know it.</param>
/// <param name="Salt">Base64; the KDF salt.</param>
/// <param name="NoncePrefix">Base64; the first bytes of every chunk's nonce.</param>
public sealed record BundleHeader(
    int Format,
    string NKeeperVersion,
    string Migration,
    DateTimeOffset ExportedAtUtc,
    string ExportedBy,
    string Kdf,
    int Iterations,
    string Salt,
    string NoncePrefix,
    int ChunkSize);

/// <summary>A bundle that can't be read: a wrong passphrase, a damaged or altered file, or not a bundle at all.</summary>
public sealed class BundleException(string message) : Exception(message);

/// <summary>
/// The encrypted file a whole instance moves in (docs/architecture.md §7.45), ported from 1.x. Layout:
/// <code>
/// "NKEEPERB" | header length (uint32, big-endian) | header (JSON)
/// then chunks: ciphertext length (uint32) | ciphertext | 16-byte GCM tag
/// </code>
/// The key comes from the passphrase (PBKDF2-HMAC-SHA512). Each chunk is AES-256-GCM with the nonce
/// <c>prefix (7) | chunk index (uint32) | last-chunk flag (1)</c> and the magic and header as associated
/// data, so the header can't be altered and chunks can't be reordered, dropped or appended (the STREAM
/// construction). Nothing is returned until every chunk has been authenticated.
/// </summary>
public static class BundleFormat
{
    /// <summary>2: NKeeper 2.0's data. 1.x wrote 1, which this doesn't read.</summary>
    public const int Version = 2;
    public const string Kdf = "pbkdf2-sha512";

    /// <summary>OWASP's recommendation for PBKDF2-HMAC-SHA512.</summary>
    public const int DefaultIterations = 600_000;

    public const int MinimumPassphraseLength = 16;
    private const int ChunkSize = 64 * 1024;
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MinIterations = 100_000;
    private const int MaxIterations = 10_000_000;
    private const int NoncePrefixLength = 7;
    private const int TagLength = 16;
    private static readonly byte[] Magic = "NKEEPERB"u8.ToArray();

    /// <summary>Encrypts <paramref name="plaintext"/> under a key derived from the passphrase.</summary>
    /// <param name="header">The header to write; its salt, nonce prefix and KDF fields are filled in here.</param>
    public static async Task WriteAsync(Stream output, BundleHeader header, string passphrase, ReadOnlyMemory<byte> plaintext, CancellationToken cancellationToken, int iterations = DefaultIterations)
    {
        var salt = RandomNumberGenerator.GetBytes(32);
        var prefix = RandomNumberGenerator.GetBytes(NoncePrefixLength);
        header = header with
        {
            Format = Version,
            Kdf = Kdf,
            Iterations = iterations,
            Salt = Convert.ToBase64String(salt),
            NoncePrefix = Convert.ToBase64String(prefix),
            ChunkSize = ChunkSize,
        };
        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, JsonSerializerOptions.Web);
        var aad = AssociatedData(headerBytes);

        var key = DeriveKey(passphrase, salt, iterations);
        try
        {
            using var gcm = new AesGcm(key, TagLength);
            await output.WriteAsync(Magic, cancellationToken);
            await output.WriteAsync(Length(headerBytes.Length), cancellationToken);
            await output.WriteAsync(headerBytes, cancellationToken);

            var chunks = Math.Max(1, (plaintext.Length + ChunkSize - 1) / ChunkSize);
            var ciphertext = new byte[ChunkSize];
            var tag = new byte[TagLength];
            for (var index = 0; index < chunks; index++)
            {
                var chunk = plaintext.Slice(index * ChunkSize, Math.Min(ChunkSize, plaintext.Length - (index * ChunkSize)));
                gcm.Encrypt(Nonce(prefix, index, index == chunks - 1), chunk.Span, ciphertext.AsSpan(0, chunk.Length), tag, aad);
                await output.WriteAsync(Length(chunk.Length), cancellationToken);
                await output.WriteAsync(ciphertext.AsMemory(0, chunk.Length), cancellationToken);
                await output.WriteAsync(tag, cancellationToken);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Reads the header without decrypting anything (it isn't secret).</summary>
    public static BundleHeader ReadHeader(ReadOnlySpan<byte> bundle) => ParseHeader(bundle, out _).Header;

    /// <summary>Decrypts and authenticates the whole bundle.</summary>
    /// <exception cref="BundleException">Wrong passphrase, or the file is damaged, altered, truncated or not a bundle.</exception>
    public static (BundleHeader Header, byte[] Plaintext) Read(ReadOnlySpan<byte> bundle, string passphrase)
    {
        var (header, headerBytes) = ParseHeader(bundle, out var offset);
        var salt = Base64(header.Salt, 16, 64);
        var prefix = Base64(header.NoncePrefix, NoncePrefixLength, NoncePrefixLength);
        var aad = AssociatedData(headerBytes);

        var key = DeriveKey(passphrase, salt, header.Iterations);
        try
        {
            using var gcm = new AesGcm(key, TagLength);
            using var plaintext = new MemoryStream();
            var buffer = new byte[header.ChunkSize];
            for (var index = 0; ; index++)
            {
                if (bundle.Length - offset < 4)
                {
                    throw new BundleException("The file is incomplete: it was cut off.");
                }

                var length = (int)BinaryPrimitives.ReadUInt32BigEndian(bundle[offset..]);
                offset += 4;
                if (length > header.ChunkSize || bundle.Length - offset < length + TagLength)
                {
                    throw new BundleException("The file is incomplete or damaged.");
                }

                var ciphertext = bundle.Slice(offset, length);
                var tag = bundle.Slice(offset + length, TagLength);
                offset += length + TagLength;
                var last = offset == bundle.Length;
                try
                {
                    gcm.Decrypt(Nonce(prefix, index, last), ciphertext, tag, buffer.AsSpan(0, length), aad);
                }
                catch (AuthenticationTagMismatchException)
                {
                    // The first chunk failing is almost always the passphrase; later ones mean damage.
                    throw new BundleException(index == 0
                        ? "The passphrase is wrong, or the file was changed."
                        : "The file was changed or damaged after it was exported.");
                }

                plaintext.Write(buffer, 0, length);
                CryptographicOperations.ZeroMemory(buffer.AsSpan(0, length));
                if (last)
                {
                    return (header, plaintext.ToArray());
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static (BundleHeader Header, byte[] HeaderBytes) ParseHeader(ReadOnlySpan<byte> bundle, out int offset)
    {
        if (bundle.Length < Magic.Length + 4 || !bundle[..Magic.Length].SequenceEqual(Magic))
        {
            throw new BundleException("This isn't an NKeeper export file.");
        }

        var headerLength = (int)BinaryPrimitives.ReadUInt32BigEndian(bundle[Magic.Length..]);
        offset = Magic.Length + 4;
        if (headerLength is <= 0 or > MaxHeaderBytes || bundle.Length - offset < headerLength)
        {
            throw new BundleException("The file is damaged: its header can't be read.");
        }

        var headerBytes = bundle.Slice(offset, headerLength).ToArray();
        offset += headerLength;
        BundleHeader? header;
        try
        {
            header = JsonSerializer.Deserialize<BundleHeader>(headerBytes, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            throw new BundleException("The file is damaged: its header can't be read.");
        }

        if (header is null || header.Format != Version)
        {
            throw new BundleException($"This export file has format {header?.Format}, which this version of NKeeper can't read. Update NKeeper first.");
        }

        if (header.Kdf != Kdf || header.Iterations is < MinIterations or > MaxIterations || header.ChunkSize is <= 0 or > 1024 * 1024)
        {
            throw new BundleException("The file's encryption settings aren't ones NKeeper writes, so it wasn't read.");
        }

        return (header, headerBytes);
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt, int iterations)
    {
        var bytes = Encoding.UTF8.GetBytes(passphrase);
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(bytes, salt, iterations, HashAlgorithmName.SHA512, 32);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static byte[] AssociatedData(byte[] headerBytes) => [.. Magic, .. headerBytes];

    private static byte[] Nonce(byte[] prefix, int index, bool last)
    {
        var nonce = new byte[12];
        prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(NoncePrefixLength), (uint)index);
        nonce[11] = last ? (byte)1 : (byte)0;
        return nonce;
    }

    private static byte[] Length(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)value);
        return bytes;
    }

    private static byte[] Base64(string value, int minLength, int maxLength)
    {
        try
        {
            var bytes = Convert.FromBase64String(value);
            return bytes.Length >= minLength && bytes.Length <= maxLength ? bytes : throw new BundleException("The file's encryption settings are invalid.");
        }
        catch (FormatException)
        {
            throw new BundleException("The file's encryption settings are invalid.");
        }
    }
}
