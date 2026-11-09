using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace NKeeper.Plugins.Host;

/// <summary>A path's owner and mode, read without following symlinks.</summary>
public readonly record struct FileStatus(uint OwnerUid, uint Mode)
{
    private const uint TypeMask = 0xF000;

    public bool IsSymlink => (Mode & TypeMask) == 0xA000;

    public bool IsDirectory => (Mode & TypeMask) == 0x4000;

    public bool IsRegularFile => (Mode & TypeMask) == 0x8000;

    /// <summary>Writable by its group or by others (mode bits 0o022).</summary>
    public bool IsGroupOrOtherWritable => (Mode & 0x12) != 0;
}

/// <summary>Reads ownership and modes; real on Linux, faked in tests.</summary>
public interface IFileSystemInspector
{
    /// <summary>The path's status without following a final symlink; null if it doesn't exist.</summary>
    FileStatus? Stat(string path);

    /// <summary>The process's effective user ID.</summary>
    uint EffectiveUserId { get; }
}

/// <summary>
/// Linux <c>statx</c> with <c>AT_SYMLINK_NOFOLLOW</c>. The <c>struct statx</c> layout is the same on every
/// architecture, unlike <c>struct stat</c>, so no per-architecture code is needed.
/// </summary>
public sealed partial class LinuxFileSystem : IFileSystemInspector
{
    private const int AtFdCwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxBasicStats = 0x7FF;
    private const int Enoent = 2;
    private const int Enotdir = 20;

    public uint EffectiveUserId => GetEffectiveUserId();

    public FileStatus? Stat(string path)
    {
        // struct statx is 256 bytes: stx_uid at offset 20, stx_mode (u16) at offset 28.
        var buffer = new byte[256];
        if (StatX(AtFdCwd, path, AtSymlinkNoFollow, StatxBasicStats, ref buffer[0]) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            return error is Enoent or Enotdir
                ? null
                : throw new IOException($"statx('{path}') failed with errno {error}.");
        }

        return new FileStatus(BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(20)), BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(28)));
    }

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int StatX(int dirfd, string path, int flags, uint mask, ref byte buffer);

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();
}

/// <summary>The checks of docs/plugin-security.md §4 for one directory, its ancestors and its entries.</summary>
public static class LocationChecks
{
    /// <summary>
    /// Returns a problem description, or null when <paramref name="directory"/> is safe: every path
    /// component from the root down is a real directory owned by root (or <paramref name="expectedOwner"/>)
    /// and not group- or other-writable, and every entry in it is such a regular file.
    /// </summary>
    public static string? Check(string directory, uint expectedOwner, IFileSystemInspector fileSystem)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var current = Path.GetPathRoot(full) ?? "/";
        foreach (var part in full[current.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries).Prepend(string.Empty))
        {
            current = part.Length == 0 ? current : Path.Combine(current, part);
            var problem = Problem(current, fileSystem.Stat(current), expectedOwner, mustBeDirectory: true);
            if (problem is not null)
            {
                return problem;
            }
        }

        foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(full).Order(StringComparer.Ordinal))
        {
            var problem = Problem(entry, fileSystem.Stat(entry), expectedOwner, mustBeDirectory: false);
            if (problem is not null)
            {
                return problem;
            }
        }

        return null;
    }

    private static string? Problem(string path, FileStatus? status, uint expectedOwner, bool mustBeDirectory) => status switch
    {
        null => $"'{path}' doesn't exist.",
        { IsSymlink: true } => $"'{path}' is a symlink.",
        { IsDirectory: false } when mustBeDirectory => $"'{path}' is not a directory.",
        { IsRegularFile: false } when !mustBeDirectory => $"'{path}' is not a regular file.",
        { } s when s.OwnerUid != 0 && s.OwnerUid != expectedOwner => $"'{path}' is owned by user {s.OwnerUid}, not root.",
        { IsGroupOrOtherWritable: true } => $"'{path}' is writable by its group or by others.",
        _ => null,
    };
}
