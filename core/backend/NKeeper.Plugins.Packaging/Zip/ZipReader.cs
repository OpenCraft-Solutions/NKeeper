using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace NKeeper.Plugins.Packaging.Zip;

/// <summary>
/// A deliberately strict ZIP reader for plugin packages (docs/plugin-security.md §3, §5.2). It reads
/// the central directory, checks every local header against it, and refuses anything the packaging
/// tool never writes. Structural problems are <see cref="ReasonCodes.BadZip"/>; per-entry policy
/// (paths, types, flags, methods) is left to <see cref="PackageVerifier"/>.
/// </summary>
internal static class ZipReader
{
    private const uint EndOfCentralDirectorySignature = 0x06054b50;
    private const uint Zip64LocatorSignature = 0x07064b50;
    private const uint CentralHeaderSignature = 0x02014b50;
    private const uint LocalHeaderSignature = 0x04034b50;
    private const int EndOfCentralDirectorySize = 22;
    private const int CentralHeaderSize = 46;
    private const int LocalHeaderSize = 30;
    private const ushort Zip64ExtraId = 0x0001;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Reads and cross-checks the archive's directory. Nothing is decompressed.</summary>
    public static IReadOnlyList<ZipEntryInfo> ReadDirectory(ReadOnlySpan<byte> archive, int maxEntries)
    {
        var eocd = FindEndOfCentralDirectory(archive);
        var disk = U16(archive, eocd + 4);
        var centralDisk = U16(archive, eocd + 6);
        var entriesOnDisk = U16(archive, eocd + 8);
        var totalEntries = U16(archive, eocd + 10);
        var centralSize = U32(archive, eocd + 12);
        var centralOffset = U32(archive, eocd + 16);

        if (disk != 0 || centralDisk != 0 || entriesOnDisk != totalEntries)
        {
            throw Bad("The archive spans several disks.");
        }

        if (totalEntries == 0xFFFF || centralSize == 0xFFFFFFFF || centralOffset == 0xFFFFFFFF
            || (eocd >= 20 && U32(archive, eocd - 20) == Zip64LocatorSignature))
        {
            throw Bad("ZIP64 archives are not accepted.");
        }

        if ((long)centralOffset + centralSize != eocd)
        {
            throw Bad("The central directory doesn't end where the archive's directory record says.");
        }

        if (totalEntries > maxEntries)
        {
            throw new PackageRejectedException(ReasonCodes.Limits, $"The archive has {totalEntries} entries; at most {maxEntries} are allowed.");
        }

        var entries = new List<ZipEntryInfo>(totalEntries);
        var position = (int)centralOffset;
        for (var i = 0; i < totalEntries; i++)
        {
            entries.Add(ReadCentralHeader(archive, ref position, (int)centralOffset));
        }

        if (position != eocd)
        {
            throw Bad("The central directory has data after its last entry.");
        }

        CheckNoOverlap(entries, centralOffset);
        return entries;
    }

    /// <summary>
    /// Decompresses one entry, refusing as soon as output passes its declared size. Output shorter than
    /// declared, or a CRC mismatch, is <see cref="ReasonCodes.BadZip"/>.
    /// </summary>
    public static byte[] ReadData(ReadOnlyMemory<byte> archive, ZipEntryInfo entry)
    {
        var compressed = archive.Slice((int)entry.DataOffset, (int)entry.CompressedSize);
        byte[] data;
        if (entry.Method == ZipEntryInfo.MethodStored)
        {
            if (entry.CompressedSize != entry.UncompressedSize)
            {
                throw Bad($"Stored entry '{entry.Name}' has different compressed and uncompressed sizes.");
            }

            data = compressed.ToArray();
        }
        else
        {
            data = Inflate(compressed, entry);
        }

        if (Crc32.Compute(data) != entry.Crc32)
        {
            throw Bad($"Entry '{entry.Name}' doesn't match its CRC-32.");
        }

        return data;
    }

    private static byte[] Inflate(ReadOnlyMemory<byte> compressed, ZipEntryInfo entry)
    {
        var data = new byte[entry.UncompressedSize];
        try
        {
            using var input = new MemoryStream(compressed.ToArray(), writable: false);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            var read = 0;
            while (read < data.Length)
            {
                var n = deflate.Read(data, read, data.Length - read);
                if (n == 0)
                {
                    throw Bad($"Entry '{entry.Name}' decompresses to less than its declared size.");
                }

                read += n;
            }

            // The bounded read: one more byte means the entry lied about its size.
            Span<byte> extra = stackalloc byte[1];
            if (deflate.Read(extra) != 0)
            {
                throw new PackageRejectedException(ReasonCodes.Limits, $"Entry '{entry.Name}' decompresses to more than its declared size.");
            }
        }
        catch (InvalidDataException ex)
        {
            throw new PackageRejectedException($"Entry '{entry.Name}' is not valid deflate data.", ex);
        }

        return data;
    }

    private static int FindEndOfCentralDirectory(ReadOnlySpan<byte> archive)
    {
        // The record is 22 bytes plus a comment of up to 64 KB. It must end exactly at the end of the
        // file: nothing may be appended after it.
        var lowest = Math.Max(0, archive.Length - EndOfCentralDirectorySize - ushort.MaxValue);
        for (var i = archive.Length - EndOfCentralDirectorySize; i >= lowest; i--)
        {
            if (U32(archive, i) == EndOfCentralDirectorySignature
                && i + EndOfCentralDirectorySize + U16(archive, i + 20) == archive.Length)
            {
                return i;
            }
        }

        throw Bad("The file is not a ZIP archive, or has data after it.");
    }

    private static ZipEntryInfo ReadCentralHeader(ReadOnlySpan<byte> archive, ref int position, int centralOffset)
    {
        Require(archive, position, CentralHeaderSize);
        if (U32(archive, position) != CentralHeaderSignature)
        {
            throw Bad("A central directory entry is malformed.");
        }

        var versionMadeBy = U16(archive, position + 4);
        var flags = U16(archive, position + 8);
        var method = U16(archive, position + 10);
        var crc = U32(archive, position + 16);
        var compressedSize = U32(archive, position + 20);
        var uncompressedSize = U32(archive, position + 24);
        var nameLength = U16(archive, position + 28);
        var extraLength = U16(archive, position + 30);
        var commentLength = U16(archive, position + 32);
        var diskStart = U16(archive, position + 34);
        var externalAttributes = U32(archive, position + 38);
        var localOffset = U32(archive, position + 42);

        var variableStart = position + CentralHeaderSize;
        Require(archive, variableStart, nameLength + extraLength + commentLength);
        var nameBytes = archive.Slice(variableStart, nameLength);
        var centralExtra = archive.Slice(variableStart + nameLength, extraLength);
        position = variableStart + nameLength + extraLength + commentLength;

        if (diskStart != 0)
        {
            throw Bad("An entry starts on another disk.");
        }

        var hasZip64 = compressedSize == 0xFFFFFFFF || uncompressedSize == 0xFFFFFFFF || localOffset == 0xFFFFFFFF
            || HasExtra(centralExtra, Zip64ExtraId);
        var name = DecodeName(nameBytes, flags);

        // The local header must describe the same entry. The data follows it.
        var local = (int)Math.Min(localOffset, int.MaxValue);
        Require(archive, local, LocalHeaderSize);
        if (local >= centralOffset || U32(archive, local) != LocalHeaderSignature)
        {
            throw Bad($"Entry '{name}' has no local header where the central directory says.");
        }

        var localNameLength = U16(archive, local + 26);
        var localExtraLength = U16(archive, local + 28);
        Require(archive, local + LocalHeaderSize, localNameLength + localExtraLength);
        if (U16(archive, local + 6) != flags
            || U16(archive, local + 8) != method
            || U32(archive, local + 14) != crc
            || U32(archive, local + 18) != compressedSize
            || U32(archive, local + 22) != uncompressedSize
            || !archive.Slice(local + LocalHeaderSize, localNameLength).SequenceEqual(nameBytes))
        {
            throw Bad($"Entry '{name}' has a local header that disagrees with the central directory.");
        }

        hasZip64 |= HasExtra(archive.Slice(local + LocalHeaderSize + localNameLength, localExtraLength), Zip64ExtraId);
        var dataOffset = (long)local + LocalHeaderSize + localNameLength + localExtraLength;
        if (dataOffset + compressedSize > centralOffset)
        {
            throw Bad($"Entry '{name}' runs into the central directory.");
        }

        return new ZipEntryInfo(name, versionMadeBy, flags, method, crc, compressedSize, uncompressedSize, externalAttributes, local, dataOffset, hasZip64);
    }

    private static string DecodeName(ReadOnlySpan<byte> bytes, ushort flags)
    {
        // Without the UTF-8 flag a name is in an old code page; only plain ASCII means the same in both.
        if ((flags & ZipEntryInfo.FlagUtf8) == 0 && bytes.ContainsAnyExceptInRange((byte)0x00, (byte)0x7F))
        {
            throw new PackageRejectedException(ReasonCodes.BadEntry, "An entry name isn't UTF-8.");
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new PackageRejectedException(ReasonCodes.BadEntry, $"An entry name isn't valid UTF-8: {ex.Message}");
        }
    }

    private static void CheckNoOverlap(List<ZipEntryInfo> entries, uint centralOffset)
    {
        // Overlapping entries are how "zip bombs" reuse one block of data many times.
        long previousEnd = 0;
        foreach (var entry in entries.OrderBy(e => e.LocalHeaderOffset))
        {
            if (entry.LocalHeaderOffset < previousEnd)
            {
                throw Bad($"Entry '{entry.Name}' overlaps another entry.");
            }

            previousEnd = entry.DataOffset + entry.CompressedSize;
        }

        if (previousEnd > centralOffset)
        {
            throw Bad("Entry data runs into the central directory.");
        }
    }

    /// <summary>Whether a well-formed extra field contains block <paramref name="id"/>; a truncated one is <see cref="ReasonCodes.BadZip"/>.</summary>
    private static bool HasExtra(ReadOnlySpan<byte> extra, ushort id)
    {
        var found = false;
        var i = 0;
        while (i < extra.Length)
        {
            if (i + 4 > extra.Length || i + 4 + U16(extra, i + 2) > extra.Length)
            {
                throw Bad("An entry has a malformed extra field.");
            }

            found |= U16(extra, i) == id;
            i += 4 + U16(extra, i + 2);
        }

        return found;
    }

    private static void Require(ReadOnlySpan<byte> archive, int offset, int length)
    {
        if (offset < 0 || length < 0 || (long)offset + length > archive.Length)
        {
            throw Bad("The archive is truncated.");
        }
    }

    private static ushort U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));

    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));

    private static PackageRejectedException Bad(string message) => new(ReasonCodes.BadZip, message);
}
