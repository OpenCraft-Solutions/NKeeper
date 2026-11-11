using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace NKeeper.Server.Tests;

/// <summary>The browser's end of a session.</summary>
internal sealed class SessionClient(WebSocket socket) : IAsyncDisposable
{
    private readonly StringBuilder _output = new();

    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    public static async Task<SessionClient> ConnectAsync(ServerScenario server, string cookie, string type = "hello")
    {
        var client = server.WebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers.Origin = "http://localhost";
            request.Headers.Cookie = cookie;
        };
        return new SessionClient(await client.ConnectAsync(new Uri($"ws://localhost/api/sessions/{type}"), Token));
    }

    public Task SendAsync(object message) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, JsonSerializerOptions.Web), WebSocketMessageType.Text, true, Token);

    public Task SendBinaryAsync(byte[] data) => socket.SendAsync(data, WebSocketMessageType.Binary, true, Token);

    /// <summary>Reads until a text message of <paramref name="type"/>, failing on a different control message.</summary>
    public async Task<JsonElement> ExpectAsync(string type)
    {
        while (true)
        {
            var (kind, data) = await ReceiveAsync();
            if (kind == WebSocketMessageType.Close)
            {
                Assert.Fail($"The session closed while waiting for {type}.");
            }

            if (kind != WebSocketMessageType.Text)
            {
                continue;
            }

            var message = JsonDocument.Parse(data).RootElement;
            var actual = message.GetProperty("type").GetString();
            if (actual == type)
            {
                return message;
            }

            Assert.False(actual is "error" or "closed", $"Expected {type}, got {Encoding.UTF8.GetString(data)}");
        }
    }

    public async Task<byte[]> ReceiveBinaryAsync()
    {
        var (kind, data) = await ReceiveAsync();
        Assert.Equal(WebSocketMessageType.Binary, kind);
        return data;
    }

    /// <summary>Reads terminal output until <paramref name="text"/> appears.</summary>
    public async Task ReadUntilAsync(string text)
    {
        while (!_output.ToString().Contains(text, StringComparison.Ordinal))
        {
            var (kind, data) = await ReceiveAsync();
            Assert.NotEqual(WebSocketMessageType.Close, kind);
            if (kind == WebSocketMessageType.Binary)
            {
                _output.Append(Encoding.UTF8.GetString(data));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
        }

        socket.Dispose();
    }

    private async Task<(WebSocketMessageType Kind, byte[] Data)> ReceiveAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return (WebSocketMessageType.Close, []);
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return (result.MessageType, message.ToArray());
            }
        }
    }
}
