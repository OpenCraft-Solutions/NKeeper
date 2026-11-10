using System.Net.WebSockets;
using System.Text.Json;

namespace NKeeper.Core.Sessions;

/// <summary>
/// The browser's side of a stream session (docs/architecture.md §7.10): whole messages up to 1 MB, one send at
/// a time, the JSON control messages, and a clean close. Ported from 1.x.
/// </summary>
internal sealed class SessionSocket(WebSocket socket) : IDisposable
{
    public const int MaxMessageBytes = 1024 * 1024;
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan TrustDecisionTimeout = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan CloseHandshakeTimeout = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public WebSocketState State => socket.State;

    public void Dispose()
    {
        _sendLock.Dispose();
        socket.Dispose();
    }

    public void Abort() => socket.Abort();

    /// <summary>Receives one whole message, putting its fragments together, or Close.</summary>
    /// <exception cref="SessionStartException">The message is over 1 MB.</exception>
    public async Task<(WebSocketMessageType Type, byte[] Data)> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return (WebSocketMessageType.Close, []);
            }

            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxMessageBytes)
            {
                throw new SessionStartException(SessionErrorCodes.BadRequest, "The message is too large.");
            }

            if (result.EndOfMessage)
            {
                return (result.MessageType, message.ToArray());
            }
        }
    }

    /// <summary>Receives and checks the browser's <c>start</c>, which must come within 15 seconds.</summary>
    public async Task<SessionStartMessage> ReceiveStartAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StartTimeout);
        (WebSocketMessageType Type, byte[] Data) message;
        try
        {
            message = await ReceiveAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SessionStartException(SessionErrorCodes.BadRequest, "No start message came.");
        }

        SessionStartMessage? start = null;
        if (message.Type == WebSocketMessageType.Text && ReadType(message.Data) == "start")
        {
            try
            {
                start = JsonSerializer.Deserialize<SessionStartMessage>(message.Data, Json);
            }
            catch (JsonException)
            {
            }
        }

        return start is { HostId: var host, ServiceId: var service } && host != Guid.Empty && service != Guid.Empty
            && start.PromptReference is not { Length: > 64 }
            && (start.CredentialId is null || start.PromptReference is null)
            && start.Options is null or { ValueKind: JsonValueKind.Object or JsonValueKind.Null }
            ? start
            : throw new SessionStartException(SessionErrorCodes.BadRequest, "The session must begin with a valid start message.");
    }

    /// <summary>
    /// Shows the user a key no one trusted yet, and waits for their decision. Anything but an explicit
    /// <c>trust</c>, a timeout and a closed tab included, is a no.
    /// </summary>
    public async Task<bool> AskToTrustAsync(object prompt, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TrustDecisionTimeout);
        try
        {
            await SendJsonAsync(prompt, timeout.Token);
            while (true)
            {
                var (type, data) = await ReceiveAsync(timeout.Token);
                if (type == WebSocketMessageType.Close)
                {
                    return false;
                }

                if (type == WebSocketMessageType.Text && ReadType(data) == "hostKeyDecision")
                {
                    return JsonSerializer.Deserialize<TrustDecision>(data, Json)?.Trust == true;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or JsonException or SessionStartException)
        {
            return false;
        }
    }

    public Task SendJsonAsync(object message, CancellationToken cancellationToken) =>
        SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, Json), WebSocketMessageType.Text, cancellationToken);

    public async Task SendAsync(ReadOnlyMemory<byte> data, WebSocketMessageType type, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await socket.SendAsync(data, type, endOfMessage: true, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Before the session opens: tells the browser why it failed, then closes cleanly.</summary>
    public async Task SendErrorAndCloseAsync(string code, string message, CancellationToken cancellationToken)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }

        try
        {
            await SendJsonAsync(new { type = "error", code, message }, cancellationToken);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, code, cancellationToken);

            // Returning before the browser answers can lose what was just sent.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CloseHandshakeTimeout);
            while (socket.State == WebSocketState.CloseSent)
            {
                if ((await ReceiveAsync(timeout.Token)).Type == WebSocketMessageType.Close)
                {
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or SessionStartException)
        {
            // The browser is already gone.
        }
    }

    /// <summary>Sends our close; whoever is receiving gets the browser's answer.</summary>
    public async Task BeginCloseAsync(string reason, CancellationToken cancellationToken)
    {
        if (socket.State == WebSocketState.Open)
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, reason, cancellationToken);
        }
    }

    /// <summary>Answers a close the browser started.</summary>
    public async Task AcknowledgeCloseAsync()
    {
        if (socket.State == WebSocketState.CloseReceived)
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Session closed", CancellationToken.None);
        }
    }

    public static string? ReadType(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                ? type.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record TrustDecision(bool Trust);
}
