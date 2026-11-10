using System.Text.Json;

namespace NKeeper.Abstractions;

/// <summary>
/// A kind of stream session, such as a terminal (docs/architecture.md §7.10, SDK 0.1.5): one WebSocket that
/// stays open while the user works. Core runs it up to the moment the plugin's data starts flowing.
/// </summary>
/// <param name="id">Lower-case letters, digits and <c>-</c>, starting with a letter, up to 32 characters. Unique across plugins.</param>
/// <param name="label">What users see, e.g. <c>Terminal</c>.</param>
/// <param name="handler">An <see cref="IStreamSessionHandler"/>, which the plugin registers in <see cref="IPlugin.ConfigureServices"/>.</param>
public sealed class SessionType(string id, string label, Type handler)
{
    public string Id { get; } = id;

    public string Label { get; } = label;

    public Type Handler { get; } = handler;

    /// <summary>The service types it opens on: at least one. They may come from other plugins.</summary>
    public IReadOnlyList<string> ServiceTypes { get; init; } = [];

    /// <summary>Whether it can reach a host through its jump host.</summary>
    public bool SupportsJumpHost { get; init; } = true;
}

/// <summary>Opens a plugin's stream sessions. Resolved from the request's services, once per open.</summary>
public interface IStreamSessionHandler
{
    /// <summary>
    /// Connects, typically through <see cref="IConnectivity"/>. Let an <see cref="SshConnectException"/> escape:
    /// core asks the user about an unknown key and calls this again, and reports the rest.
    /// </summary>
    Task<IStreamSession> OpenAsync(StreamSessionRequest request, CancellationToken cancellationToken);
}

/// <summary>What to open: core has already checked the host, the service and the credential.</summary>
/// <param name="Credential">The credential the user chose, as a handle; null when they chose none.</param>
/// <param name="Options">The <c>options</c> of the browser's <c>start</c> message, as the plugin defines them.</param>
public sealed record StreamSessionRequest(HostContext Host, Guid ServiceId, CredentialHandle? Credential, JsonElement Options);

/// <summary>An open stream session. Core disposes it when the session ends.</summary>
public interface IStreamSession : IAsyncDisposable
{
    /// <summary>
    /// Carries the session's data until either side is done. <paramref name="cancellationToken"/> is cancelled when
    /// the browser goes, or core ends the session (because access changed, say).
    /// </summary>
    Task RunAsync(IStreamChannel channel, CancellationToken cancellationToken);
}

/// <summary>The plugin's side of the session's WebSocket, after <c>connected</c>.</summary>
public interface IStreamChannel
{
    /// <summary>The next frame from the browser; null once it closed the session.</summary>
    Task<StreamFrame?> ReceiveAsync(CancellationToken cancellationToken);

    /// <summary>Sends a binary frame.</summary>
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>Sends a text frame: JSON with a <c>type</c> of the plugin's own.</summary>
    Task SendTextAsync(string json, CancellationToken cancellationToken);
}

/// <summary>A frame from the browser: binary, or a text frame (JSON) whose <c>type</c> isn't core's.</summary>
public sealed record StreamFrame(bool IsText, ReadOnlyMemory<byte> Data);
