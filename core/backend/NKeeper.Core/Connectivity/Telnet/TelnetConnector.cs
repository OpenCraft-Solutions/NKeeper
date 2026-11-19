using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NKeeper.Abstractions;
using NKeeper.Core.Audit;
using NKeeper.Core.Credentials;
using NKeeper.Core.Persistence;

namespace NKeeper.Core.Connectivity.Telnet;

/// <summary>
/// Telnet terminals for plugins (docs/architecture.md §7.22). Core opens the connection and speaks the protocol, and
/// with a credential answers the server's first login and password prompts, so a plugin never sees the password.
/// Ported from 1.x (<c>Telnet/TelnetTerminalSession</c>).
/// </summary>
public sealed partial class TelnetConnector(
    CredentialBroker broker,
    CoreDbContext db,
    IHttpContextAccessor http,
    IJumpTunnels tunnels,
    IAuditLog audit,
    ILogger<TelnetConnector> logger)
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Prompts are answered only this soon after connecting, never later in the session.</summary>
    public static readonly TimeSpan SignInWindow = TimeSpan.FromSeconds(30);

    public const int MaxSize = 1000;

    /// <exception cref="ConnectException">The service can't be reached, or its jump host can't be used.</exception>
    /// <exception cref="CredentialDeniedException">The handle isn't valid here.</exception>
    public async Task<ITelnetConnection> OpenAsync(HostContext host, Guid serviceId, CredentialHandle? handle, TerminalSize size, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(size);
        if (size.Columns is < 1 or > MaxSize || size.Rows is < 1 or > MaxSize)
        {
            throw new ArgumentOutOfRangeException(nameof(size), $"A terminal is from 1 × 1 to {MaxSize} × {MaxSize}.");
        }

        // Only for the host core authorized for this plugin request: a plugin could make up another.
        var context = http.HttpContext;
        var pluginId = PluginCaller.Of(context);
        if (context is null || pluginId is null || !PluginCaller.Authorized(context, host))
        {
            throw new InvalidOperationException("Telnet connections can only be opened from a plugin's host-scoped endpoint, for its own host.");
        }

        SignIn? signIn = null;
        string? credential = null;
        if (handle is not null)
        {
            var (_, _, entry) = await broker.UseAsync(host, serviceId, handle, cancellationToken);
            var material = entry.Material;
            signIn = new SignIn(material.Username, material.Password?.Reveal());
            credential = material.Source == CredentialSource.Saved ? $"credential {material.CredentialId}" : "a typed credential";
        }

        var service = host.Services.FirstOrDefault(s => s.Id == serviceId);
        var exists = await db.HostServices.AsNoTracking().AnyAsync(s => s.Id == serviceId && s.HostId == host.Id, cancellationToken);
        if (service?.Address is null || !exists)
        {
            throw new ConnectException(ConnectFailure.Unreachable, $"“{host.Name}” has no such service any more. Reload the host and try again.");
        }

        if (service.Port is not { } port)
        {
            throw new ConnectException(ConnectFailure.Unreachable, $"This service of “{host.Name}” has no port. Edit the host to set one.");
        }

        IJumpTunnel? tunnel;
        try
        {
            tunnel = await tunnels.OpenAsync(host, service.Address, port, cancellationToken);
        }
        catch (ConnectException ex)
        {
            throw new ConnectException(ConnectFailure.JumpHostUnavailable, ex.Message);
        }

        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);
            try
            {
                await tcp.ConnectAsync(tunnel is null ? service.Address : "127.0.0.1", tunnel?.LocalPort ?? port, timeout.Token);
            }
            catch (SocketException)
            {
                throw new ConnectException(ConnectFailure.Unreachable, $"Couldn't connect to {service.Address}:{port} over Telnet.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ConnectException(ConnectFailure.TimedOut, $"Timed out connecting to {service.Address}:{port} over Telnet.");
            }

            if (credential is not null)
            {
                await audit.RecordAsync(new AuditEntry(AuditActions.CredentialUse, host.Id.ToString(), $"telnet to service {serviceId}, plugin {pluginId}, {credential}"), cancellationToken);
                if (signIn?.Password is not null)
                {
                    logger.LogWarning("Signing in to host {HostId} over Telnet: the password crosses the network unencrypted.", host.Id);
                }
            }

            return new TelnetConnection(tcp, tunnel, size, signIn);
        }
        catch
        {
            tcp.Dispose();
            tunnel?.Dispose();
            throw;
        }
    }

    /// <summary>What core types at the server's prompts: the credential's user name and password.</summary>
    internal sealed record SignIn(string? Username, string? Password);

    // A prompt at the end of what the server sent so far, e.g. "login: " or "Password:".
    [GeneratedRegex(@"(login|username|user name|user)\s*:\s*$", RegexOptions.IgnoreCase)]
    internal static partial Regex UsernamePrompt();

    [GeneratedRegex(@"(password|passcode)\s*:\s*$", RegexOptions.IgnoreCase)]
    internal static partial Regex PasswordPrompt();

    private sealed class TelnetConnection(TcpClient tcp, IJumpTunnel? tunnel, TerminalSize size, SignIn? signIn) : ITelnetConnection
    {
        private readonly TelnetStream _stream = new(tcp, size, signIn);

        public Stream Stream => _stream;

        public void Resize(int columns, int rows)
        {
            if (columns is >= 1 and <= MaxSize && rows is >= 1 and <= MaxSize)
            {
                _stream.Resize(columns, rows);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stream.DisposeAsync();
            tcp.Dispose();
            tunnel?.Dispose();
        }
    }

    /// <summary>
    /// The terminal's side of the connection: reads give terminal data only (the protocol's commands are answered
    /// here), and writes are escaped for Telnet. Signs in at the first prompts when it has a credential.
    /// </summary>
    private sealed class TelnetStream : Stream
    {
        private readonly NetworkStream _network;
        private readonly TelnetProtocol _protocol = new();
        private readonly Lock _protocolLock = new();
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly byte[] _buffer = new byte[16 * 1024];
        private readonly Stopwatch _since = Stopwatch.StartNew();
        private readonly StringBuilder _recent = new();
        private readonly string? _username;
        private string? _password;
        private bool _sentUsername;
        private byte[] _pending = [];
        private int _pendingOffset;

        public TelnetStream(TcpClient tcp, TerminalSize size, SignIn? signIn)
        {
            _network = tcp.GetStream();
            _username = string.IsNullOrEmpty(signIn?.Username) ? null : signIn.Username;
            _password = signIn?.Password;
            lock (_protocolLock)
            {
                _protocol.Resize(size.Columns, size.Rows);
            }
        }

        public override bool CanRead => true;

        public override bool CanWrite => true;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public void Resize(int columns, int rows)
        {
            byte[] naws;
            lock (_protocolLock)
            {
                naws = _protocol.Resize(columns, rows);
            }

            if (naws.Length > 0)
            {
                _ = WriteRawAsync(naws, CancellationToken.None).AsTask().ContinueWith(static _ => { }, TaskScheduler.Default);
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_pendingOffset >= _pending.Length)
            {
                var read = await _network.ReadAsync(_buffer, cancellationToken);
                if (read == 0)
                {
                    return 0;
                }

                var data = new List<byte>(read);
                var replies = new List<byte>();
                lock (_protocolLock)
                {
                    _protocol.Receive(_buffer.AsSpan(0, read), data, replies);
                }

                if (replies.Count > 0)
                {
                    await WriteRawAsync([.. replies], cancellationToken);
                }

                if (data.Count > 0)
                {
                    _pending = [.. data];
                    _pendingOffset = 0;
                    await AnswerPromptAsync(_pending, cancellationToken);
                }
            }

            var count = Math.Min(buffer.Length, _pending.Length - _pendingOffset);
            _pending.AsMemory(_pendingOffset, count).CopyTo(buffer);
            _pendingOffset += count;
            return count;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            WriteRawAsync(TelnetProtocol.Encode(buffer.Span), cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _password = null;
                _network.Dispose();
                _writeLock.Dispose();
            }

            base.Dispose(disposing);
        }

        /// <summary>
        /// Answers the first login and password prompts, once each, only just after connecting. Prompts are matched on
        /// the end of the text the server has sent so far.
        /// </summary>
        private async Task AnswerPromptAsync(byte[] data, CancellationToken cancellationToken)
        {
            if (_since.Elapsed > SignInWindow)
            {
                _password = null;
            }

            if (_password is null && (_sentUsername || _username is null))
            {
                return;
            }

            _recent.Append(Encoding.UTF8.GetString(data));
            if (_recent.Length > 256)
            {
                _recent.Remove(0, _recent.Length - 256);
            }

            var text = _recent.ToString();
            if (!_sentUsername && _username is not null && UsernamePrompt().IsMatch(text))
            {
                _sentUsername = true;
                _recent.Clear();
                await WriteRawAsync(TelnetProtocol.Encode(Encoding.UTF8.GetBytes(_username + "\r")), cancellationToken);
            }
            else if (_password is { } password && PasswordPrompt().IsMatch(text))
            {
                _password = null;
                _recent.Clear();
                await WriteRawAsync(TelnetProtocol.Encode(Encoding.UTF8.GetBytes(password + "\r")), cancellationToken);
            }
        }

        private async ValueTask WriteRawAsync(byte[] bytes, CancellationToken cancellationToken)
        {
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await _network.WriteAsync(bytes, cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }
        }
    }
}
