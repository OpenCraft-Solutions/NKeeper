using System.Globalization;
using System.Net.Sockets;
using System.Text;
using NKeeper.Abstractions;

namespace NKeeper.Core.Connectivity.Guacamole;

/// <summary>What the browser can display, sent to guacd during the handshake.</summary>
public sealed record GuacdClientInfo(
    int Width,
    int Height,
    int Dpi,
    IReadOnlyList<string> AudioMimetypes,
    IReadOnlyList<string> ImageMimetypes,
    string? Timezone);

/// <summary>
/// A connection to guacd. NKeeper performs the Guacamole handshake itself, so the browser can
/// never choose the protocol, target or options; afterwards instructions are relayed.
/// </summary>
public sealed class GuacdConnection : IAsyncDisposable
{
    private const int MaxInstructionLength = 8 * 1024 * 1024;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly Decoder _decoder = new UTF8Encoding(false, true).GetDecoder();
    private readonly GuacamoleParser _parser = new(MaxInstructionLength);
    private readonly byte[] _readBuffer = new byte[64 * 1024];
    private readonly char[] _charBuffer = new char[64 * 1024];
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private GuacdConnection(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    /// <remarks>Ported from 1.x.</remarks>
    public static async Task<GuacdConnection> OpenAsync(string host, int port, CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, cancellationToken);
            return new GuacdConnection(client);
        }
        catch (SocketException)
        {
            client.Dispose();
            throw new DesktopConnectException(
                DesktopConnectFailure.GatewayUnavailable,
                "NKeeper's remote desktop gateway (guacd) isn't running. An administrator can check: systemctl status nkeeper-guacd.");
        }
    }

    /// <summary>
    /// Selects the protocol ("rdp", "vnc"), sends the client's capabilities and the connection
    /// parameters, and waits for "ready", which is returned so it can be relayed to the browser's
    /// Guacamole client. The remote connection itself completes afterwards; failures arrive as
    /// "error" instructions. Parameters not in <paramref name="values"/> are left empty (off).
    /// </summary>
    public async Task<GuacamoleInstruction> HandshakeAsync(
        string protocol, GuacdClientInfo client, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HandshakeTimeout);

        await WriteAsync(GuacamoleInstruction.Encode("select", protocol), timeout.Token);
        var args = await ReadInstructionAsync(timeout.Token);
        if (args is not { Opcode: "args" })
        {
            throw new DesktopConnectException(DesktopConnectFailure.GatewayUnavailable, "The remote desktop gateway (guacd) didn't respond as expected.");
        }

        var handshake = new StringBuilder()
            .Append(GuacamoleInstruction.Encode("size", Invariant(client.Width), Invariant(client.Height), Invariant(client.Dpi)))
            .Append(GuacamoleInstruction.Encode("audio", client.AudioMimetypes))
            .Append(GuacamoleInstruction.Encode("video"))
            .Append(GuacamoleInstruction.Encode("image", client.ImageMimetypes));
        if (client.Timezone is not null)
        {
            handshake.Append(GuacamoleInstruction.Encode("timezone", client.Timezone));
        }

        handshake.Append(GuacamoleInstruction.Encode("connect", args.Args.Select(name => name.StartsWith("VERSION_", StringComparison.Ordinal)
            ? name
            : values.GetValueOrDefault(name, string.Empty))));
        await WriteAsync(handshake.ToString(), timeout.Token);

        var reply = await ReadInstructionAsync(timeout.Token);
        if (reply is not { Opcode: "ready" })
        {
            throw new DesktopConnectException(
                DesktopConnectFailure.Unreachable,
                reply is { Opcode: "error", Args.Count: > 0 } ? $"The connection failed: {reply.Args[0]}" : "The remote desktop gateway (guacd) refused the connection.");
        }

        return reply;
    }

    internal static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The next instruction, or null when guacd closed the connection.</summary>
    public async Task<GuacamoleInstruction?> ReadInstructionAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_parser.TryRead(out var instruction))
            {
                return instruction;
            }

            var read = await _stream.ReadAsync(_readBuffer, cancellationToken);
            if (read == 0)
            {
                return null;
            }

            var chars = _decoder.GetChars(_readBuffer, 0, read, _charBuffer, 0);
            _parser.Append(_charBuffer.AsSpan(0, chars));
        }
    }

    /// <summary>All instructions already received after the next one, so they can be sent together.</summary>
    public IEnumerable<GuacamoleInstruction> DrainBuffered()
    {
        while (_parser.BufferedLength > 0 && _parser.TryRead(out var instruction))
        {
            yield return instruction;
        }
    }

    public async Task WriteAsync(string instructions, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(instructions);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _stream.WriteAsync(bytes, cancellationToken);
            await _stream.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        _client.Dispose();
        _writeLock.Dispose();
    }
}
