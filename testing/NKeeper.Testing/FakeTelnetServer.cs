using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NKeeper.Testing;

/// <summary>
/// Ported from 1.x (`Infrastructure/FakeTelnetServer`).
/// A Telnet server on 127.0.0.1 that behaves like a network device's: it negotiates options (asks
/// for the terminal type and window size, offers to echo, and asks for an option NKeeper declines),
/// prompts "login:" and "Password:", then answers each line with "you said: …" until "exit".
/// </summary>
public sealed class FakeTelnetServer : IAsyncDisposable
{
    public const string UserName = "alice";
    public const string Password = "s3cret";

    private const byte Iac = 255, Dont = 254, Do = 253, Wont = 252, Will = 251, Sb = 250, Se = 240;
    private const byte NewEnviron = 39;

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;

    public FakeTelnetServer()
    {
        _listener.Start();
        _accepting = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    private int _open;

    /// <summary>Connections the device has open now.</summary>
    public int OpenConnections => Volatile.Read(ref _open);

    /// <summary>The terminal type the client reported.</summary>
    public string? TerminalType { get; private set; }

    /// <summary>Window sizes the client reported, in order.</summary>
    public ConcurrentQueue<(int Cols, int Rows)> WindowSizes { get; } = new();

    /// <summary>Negotiation the client sent, as "WONT 39" etc.</summary>
    public ConcurrentQueue<string> Negotiation { get; } = new();

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = ServeAsync(client);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        Interlocked.Increment(ref _open);
        try
        {
            await ServeClientAsync(client);
        }
        finally
        {
            Interlocked.Decrement(ref _open);
        }
    }

    private async Task ServeClientAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var reader = new Reader(this, stream);
            try
            {
                await stream.WriteAsync(new byte[] { Iac, Do, 24, Iac, Do, 31, Iac, Will, 1, Iac, Will, 3, Iac, Do, NewEnviron }, _stop.Token);
                await stream.WriteAsync(new byte[] { Iac, Sb, 24, 1, Iac, Se }, _stop.Token);
                await WriteAsync(stream, "\r\nSwitch-01\r\nlogin: ");
                var user = await reader.ReadLineAsync(_stop.Token);
                await WriteAsync(stream, "Password: ");
                var password = await reader.ReadLineAsync(_stop.Token);
                if (user != UserName || password != Password)
                {
                    await WriteAsync(stream, "\r\nLogin incorrect\r\n");
                    return;
                }

                await WriteAsync(stream, $"\r\nWelcome {user}\r\n$ ");
                while (await reader.ReadLineAsync(_stop.Token) is { } line && line != "exit")
                {
                    await WriteAsync(stream, $"you said: {line}\r\n$ ");
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
    }

    private Task WriteAsync(NetworkStream stream, string text) => stream.WriteAsync(Encoding.ASCII.GetBytes(text), _stop.Token).AsTask();

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _accepting;
        _stop.Dispose();
    }

    /// <summary>Reads lines (ended by CR LF), taking Telnet commands out and recording them.</summary>
    private sealed class Reader(FakeTelnetServer server, NetworkStream stream)
    {
        private readonly byte[] _one = new byte[1];

        public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            var line = new StringBuilder();
            while (await NextAsync(cancellationToken) is { } b)
            {
                if (b == Iac)
                {
                    await CommandAsync(cancellationToken);
                }
                else if (b == '\r')
                {
                    // CR LF (or CR NUL) ends the line.
                    await NextAsync(cancellationToken);
                    return line.ToString();
                }
                else
                {
                    line.Append((char)b);
                }
            }

            return null;
        }

        private async Task CommandAsync(CancellationToken cancellationToken)
        {
            var verb = await NextAsync(cancellationToken);
            if (verb is Do or Dont or Will or Wont)
            {
                var option = await NextAsync(cancellationToken);
                server.Negotiation.Enqueue($"{verb switch { Do => "DO", Dont => "DONT", Will => "WILL", _ => "WONT" }} {option}");
                return;
            }

            if (verb != Sb)
            {
                return;
            }

            var data = new List<byte>();
            while (await NextAsync(cancellationToken) is { } b)
            {
                if (b == Iac)
                {
                    var next = await NextAsync(cancellationToken);
                    if (next == Se)
                    {
                        break;
                    }

                    data.Add(Iac);
                    continue;
                }

                data.Add(b);
            }

            if (data is [24, 0, .. var name])
            {
                server.TerminalType = Encoding.ASCII.GetString([.. name]);
            }
            else if (data is [31, var c1, var c2, var r1, var r2])
            {
                server.WindowSizes.Enqueue(((c1 << 8) | c2, (r1 << 8) | r2));
            }
        }

        private async Task<byte?> NextAsync(CancellationToken cancellationToken) =>
            await stream.ReadAsync(_one, cancellationToken) == 1 ? _one[0] : null;
    }
}
