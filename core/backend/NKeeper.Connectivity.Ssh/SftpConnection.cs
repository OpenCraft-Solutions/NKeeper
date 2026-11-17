using NKeeper.Abstractions;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using SftpException = NKeeper.Abstractions.SftpException;

namespace NKeeper.Connectivity.Ssh;

/// <summary>
/// An open SFTP connection (docs/architecture.md §7.9, SDK 1.3): SSH.NET's client behind <see cref="ISftpConnection"/>,
/// with its exceptions turned into <see cref="SftpException"/>s that name the path. Ported from 1.x's SftpController.
/// </summary>
internal sealed class SftpConnection(SftpClient client, ForwardedPortLocal? forward, SshClient? jump) : ISftpConnection
{
    public string HomeDirectory => client.WorkingDirectory;

    public async Task<IReadOnlyList<SftpEntry>> ListAsync(string path, CancellationToken cancellationToken = default)
    {
        Absolute(path);
        var entries = new List<SftpEntry>();
        await Translated(path, async () =>
        {
            await foreach (var file in client.ListDirectoryAsync(path, cancellationToken))
            {
                if (file.Name is not ("." or ".."))
                {
                    entries.Add(Entry(file.Name, file.FullName, file.Attributes));
                }
            }
        });
        return entries;
    }

    public Task<SftpEntry> GetAsync(string path, CancellationToken cancellationToken = default)
    {
        Absolute(path);
        return Translated(path, async () =>
        {
            var attributes = await client.GetAttributesAsync(path, cancellationToken);
            var trimmed = path.TrimEnd('/');
            return Entry(trimmed.Length == 0 ? "/" : trimmed[(trimmed.LastIndexOf('/') + 1)..], trimmed.Length == 0 ? "/" : trimmed, attributes);
        });
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        Absolute(path);
        return Translated<Stream>(path, async () => new TranslatingStream(await client.OpenAsync(path, FileMode.Open, FileAccess.Read, cancellationToken), path));
    }

    public Task<Stream> OpenWriteAsync(string path, bool overwrite, CancellationToken cancellationToken = default)
    {
        Absolute(path);
        return Translated<Stream>(path, async () =>
        {
            if (!overwrite && await client.ExistsAsync(path, cancellationToken))
            {
                throw new SftpException(SftpFailure.AlreadyExists, $"{path} already exists.");
            }

            return new TranslatingStream(await client.OpenAsync(path, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, cancellationToken), path);
        });
    }

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        Absolute(path);
        return Translated(path, async () =>
        {
            if (await client.ExistsAsync(path, cancellationToken))
            {
                throw new SftpException(SftpFailure.AlreadyExists, $"{path} already exists.");
            }

            await client.CreateDirectoryAsync(path, cancellationToken);
        });
    }

    public Task DeleteFileAsync(string path, CancellationToken cancellationToken = default)
    {
        Absolute(path);
        return Translated(path, () => client.DeleteFileAsync(path, cancellationToken));
    }

    public Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        Absolute(path);
        return Translated(path, () => client.DeleteDirectoryAsync(path, cancellationToken));
    }

    public Task RenameAsync(string path, string newPath, CancellationToken cancellationToken = default)
    {
        Absolute(path);
        Absolute(newPath);
        return Translated(path, async () =>
        {
            if (await client.ExistsAsync(newPath, cancellationToken))
            {
                throw new SftpException(SftpFailure.AlreadyExists, $"{newPath} already exists.");
            }

            await client.RenameFileAsync(path, newPath, cancellationToken);
        });
    }

    public ValueTask DisposeAsync()
    {
        client.Dispose();
        SshConnectivity.Close(forward, jump);
        return ValueTask.CompletedTask;
    }

    private static void Absolute(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!path.StartsWith('/'))
        {
            throw new ArgumentException($"SFTP paths are absolute: {path}", nameof(path));
        }
    }

    private static SftpEntry Entry(string name, string path, SftpFileAttributes a) => new(
        name,
        path,
        a.IsDirectory ? SftpEntryKind.Directory : a.IsRegularFile ? SftpEntryKind.File : a.IsSymbolicLink ? SftpEntryKind.Link : SftpEntryKind.Other,
        a.IsDirectory ? 0 : a.Size,
        new DateTimeOffset(DateTime.SpecifyKind(a.LastWriteTimeUtc, DateTimeKind.Utc)),
        string.Concat(
            a.OwnerCanRead ? 'r' : '-', a.OwnerCanWrite ? 'w' : '-', a.OwnerCanExecute ? 'x' : '-',
            a.GroupCanRead ? 'r' : '-', a.GroupCanWrite ? 'w' : '-', a.GroupCanExecute ? 'x' : '-',
            a.OthersCanRead ? 'r' : '-', a.OthersCanWrite ? 'w' : '-', a.OthersCanExecute ? 'x' : '-'));

    private static async Task Translated(string path, Func<Task> operation) =>
        await Translated(path, async () =>
        {
            await operation();
            return true;
        });

    private static async Task<T> Translated<T>(string path, Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (Exception ex) when (Translate(ex, path) is { } translated)
        {
            throw translated;
        }
    }

    /// <summary>SSH.NET's exception as the SDK's; null for what isn't SSH.NET's (cancellation, a plugin's own).</summary>
    internal static SftpException? Translate(Exception ex, string path) => ex switch
    {
        SftpPathNotFoundException => new SftpException(SftpFailure.NotFound, $"{path} doesn't exist (any more)."),
        SftpPermissionDeniedException => new SftpException(SftpFailure.PermissionDenied, $"The account isn't allowed to do that with {path}."),
        SshConnectionException or ObjectDisposedException => new SftpException(SftpFailure.ConnectionLost, "The SFTP connection closed."),
        SshException => new SftpException(SftpFailure.Failed, $"The server refused, for {path}: {ex.Message}"),
        _ => null,
    };
}

/// <summary>A file's stream whose SSH.NET failures are <see cref="IOException"/>s naming the path, so a plugin needn't know SSH.NET.</summary>
internal sealed class TranslatingStream(Stream inner, string path) : Stream
{
    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => Guard(inner.Flush);

    public override Task FlushAsync(CancellationToken cancellationToken) => GuardAsync(() => inner.FlushAsync(cancellationToken));

    public override int Read(byte[] buffer, int offset, int count) => Guard(() => inner.Read(buffer, offset, count));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            return await inner.ReadAsync(buffer, cancellationToken);
        }
        catch (Exception ex) when (SftpConnection.Translate(ex, path) is { } translated)
        {
            throw new IOException(translated.Message, ex);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) => Guard(() => inner.Write(buffer, offset, count));

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            await inner.WriteAsync(buffer, cancellationToken);
        }
        catch (Exception ex) when (SftpConnection.Translate(ex, path) is { } translated)
        {
            throw new IOException(translated.Message, ex);
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Guard(inner.Dispose);
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await GuardAsync(async () => await inner.DisposeAsync());
        await base.DisposeAsync();
    }

    private void Guard(Action action) => Guard(() =>
    {
        action();
        return true;
    });

    private T Guard<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (SftpConnection.Translate(ex, path) is { } translated)
        {
            throw new IOException(translated.Message, ex);
        }
    }

    private async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (SftpConnection.Translate(ex, path) is { } translated)
        {
            throw new IOException(translated.Message, ex);
        }
    }
}
