using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace NKeeper.Plugins.Packaging.Zip;

/// <summary>One file to write into an archive. The overrides exist so tests can build malformed archives.</summary>
internal sealed record ZipWriteEntry(string Name, byte[] Data)
{
    /// <summary>Store the data uncompressed even when deflate would be smaller.</summary>
    public bool ForceStored { get; init; }

    public ushort? FlagsOverride { get; init; }

    public ushort? MethodOverride { get; init; }

    public ushort? VersionMadeByOverride { get; init; }

    public uint? ExternalAttributesOverride { get; init; }

    public uint? CrcOverride { get; init; }

    public uint? UncompressedSizeOverride { get; init; }

    /// <summary>Raw bytes written as the entry's data instead of compressing <see cref="Data"/>.</summary>
    public byte[]? RawDataOverride { get; init; }

    /// <summary>An extra field written into both headers.</summary>
    public byte[]? ExtraField { get; init; }
}

/// <summary>
/// Writes the plain, deterministic archives of package format version 1: sorted entries, fixed
/// timestamps, UTF-8 names, regular-file permissions, no ZIP64, data descriptors or directories.
/// The same input always produces the same bytes.
/// </summary>
internal static class ZipWriter
{
    // 1980-01-01 00:00:00, the earliest MS-DOS timestamp, so dates never vary.
    private const ushort FixedTime = 0;
    private const ushort FixedDate = (0 << 9) | (1 << 5) | 1;
    private const ushort VersionNeeded = 20;
    private const ushort VersionMadeByUnix = (ZipEntryInfo.HostUnix << 8) | 30;
    private const uint RegularFileMode = 0x81A4u << 16; // S_IFREG | 0644

    public static byte[] Write(IEnumerable<ZipWriteEntry> entries)
    {
        var sorted = entries.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
        using var output = new MemoryStream();
        var central = new List<byte[]>();

        foreach (var entry in sorted)
        {
            var name = Encoding.UTF8.GetBytes(entry.Name);
            var (method, payload) = Compress(entry);
            var flags = entry.FlagsOverride ?? ZipEntryInfo.FlagUtf8;
            var crc = entry.CrcOverride ?? Crc32.Compute(entry.Data);
            var uncompressed = entry.UncompressedSizeOverride ?? (uint)entry.Data.Length;
            var extra = entry.ExtraField ?? [];
            var localOffset = (uint)output.Position;

            var local = new byte[30 + name.Length + extra.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(local, 0x04034b50);
            BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(4), VersionNeeded);
            BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(6), flags);
            BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(8), method);
            BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(10), FixedTime);
            BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(12), FixedDate);
            BinaryPrimitives.WriteUInt32LittleEndian(local.AsSpan(14), crc);
            BinaryPrimitives.WriteUInt32LittleEndian(local.AsSpan(18), (uint)payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(local.AsSpan(22), uncompressed);
            BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(26), (ushort)name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(28), (ushort)extra.Length);
            name.CopyTo(local, 30);
            extra.CopyTo(local, 30 + name.Length);
            output.Write(local);
            output.Write(payload);

            var header = new byte[46 + name.Length + extra.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(header, 0x02014b50);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), entry.VersionMadeByOverride ?? VersionMadeByUnix);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), VersionNeeded);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), flags);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), method);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), FixedTime);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), FixedDate);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), crc);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), (uint)payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), uncompressed);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(28), (ushort)name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(30), (ushort)extra.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(38), entry.ExternalAttributesOverride ?? RegularFileMode);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(42), localOffset);
            name.CopyTo(header, 46);
            extra.CopyTo(header, 46 + name.Length);
            central.Add(header);
        }

        var centralOffset = (uint)output.Position;
        foreach (var header in central)
        {
            output.Write(header);
        }

        var centralSize = (uint)output.Position - centralOffset;
        var end = new byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(end, 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(8), (ushort)sorted.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(10), (ushort)sorted.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(12), centralSize);
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(16), centralOffset);
        output.Write(end);
        return output.ToArray();
    }

    private static (ushort Method, byte[] Payload) Compress(ZipWriteEntry entry)
    {
        if (entry.RawDataOverride is { } raw)
        {
            return (entry.MethodOverride ?? ZipEntryInfo.MethodDeflate, raw);
        }

        byte[] payload = entry.Data;
        var method = ZipEntryInfo.MethodStored;
        if (!entry.ForceStored && entry.Data.Length > 0)
        {
            var deflated = Deflate(entry.Data);
            if (deflated.Length < entry.Data.Length)
            {
                (method, payload) = (ZipEntryInfo.MethodDeflate, deflated);
            }
        }

        return (entry.MethodOverride ?? method, payload);
    }

    internal static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            deflate.Write(data);
        }

        return output.ToArray();
    }
}
