namespace NKeeper.Plugins.Packaging.Zip;

/// <summary>One entry of an archive, as its central directory and local header describe it.</summary>
internal sealed record ZipEntryInfo(
    string Name,
    ushort VersionMadeBy,
    ushort Flags,
    ushort Method,
    uint Crc32,
    long CompressedSize,
    long UncompressedSize,
    uint ExternalAttributes,
    long LocalHeaderOffset,
    long DataOffset,
    bool HasZip64Extra)
{
    public const ushort MethodStored = 0;
    public const ushort MethodDeflate = 8;

    public const ushort FlagEncrypted = 0x0001;
    public const ushort FlagDataDescriptor = 0x0008;
    public const ushort FlagStrongEncryption = 0x0040;
    public const ushort FlagUtf8 = 0x0800;

    public const int HostMsDos = 0;
    public const int HostUnix = 3;
}
