using System.Text.Json;

namespace NKeeper.Abstractions;

/// <summary>
/// A kind of stream session, such as a terminal (docs/architecture.md §7.10, SDK 0.1.5): one WebSocket that
/// stays open while the user works. Core runs it up to the moment the plugin's data starts flowing.
/// </summary>
/// <param name="id">Lower-case letters, digits and <c>-</c>, starting with a letter, up to 32 characters. Unique across plugins.</param>
/// <param name="label">What users see, e.g. <c>Terminal</c>.</param>
/// <param name="handler">An <see cref="IStreamSessionHandler"/>, which the plugin registers in <see cref="IPlugin.ConfigureServices"/>.</param>
public sealed class SessionType(string id, string label, Type? handler)
{
    public string Id { get; } = id;

    public string Label { get; } = label;

    /// <summary>The stream session handler; null for a stateful session type (<see cref="Stateful"/>).</summary>
    public Type? Handler { get; } = handler;

    /// <summary>A session the plugin keeps itself (§7.17), registered with <see cref="IPluginSessions"/>: core opens no WebSocket for it.</summary>
    public bool IsStateful => Handler is null;

    /// <summary>A stateful session type (docs/architecture.md §7.17, SDK 0.1.8).</summary>
    public static SessionType Stateful(string id, string label) => new(id, label, null);

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

/// <summary>
/// Sessions a plugin keeps itself (docs/architecture.md §7.17, SDK 0.1.8), registered in core's session registry so
/// they count toward the user's limit, end when access changes, and are audited. Use it from a host endpoint.
/// </summary>
public interface IPluginSessions
{
    /// <summary>Registers a session of one of the plugin's stateful session types, for the signed-in user, on the request's host.</summary>
    /// <exception cref="SessionLimitException">The user has as many sessions open as they may.</exception>
    /// <exception cref="InvalidOperationException">It's called outside the plugin's own host endpoint, or for another host or type.</exception>
    Task<IPluginSession> OpenAsync(HostContext host, Guid serviceId, string sessionType, CancellationToken cancellationToken = default);
}

/// <summary>A registered session. Dispose it when it ends; core audits the reason.</summary>
public interface IPluginSession : IDisposable
{
    Guid Id { get; }

    /// <summary>Cancelled when core ends the session, because access changed, say.</summary>
    CancellationToken Ended { get; }

    /// <summary>Why it ended; null while it's open.</summary>
    string? EndReason { get; }

    /// <summary>Ends it for a reason of the plugin's own (<c>idle</c>, say), recorded when it's disposed.</summary>
    void End(string reason);
}

public sealed class SessionLimitException(string message) : Exception(message);

/// <summary>
/// An origin of the plugin's own (docs/architecture.md §7.18, SDK 0.1.8): core sends every request for
/// <c>&lt;prefix&gt;-&lt;label&gt;.&lt;web domain&gt;</c> to the handler, before its own pipeline.
/// </summary>
/// <param name="Prefix">2 to 16 lower-case letters and digits, unique across plugins: <c>nkw</c>.</param>
/// <param name="Handler">An <see cref="ISessionOriginHandler"/> the plugin registers in ConfigureServices.</param>
public sealed record SessionOrigin(string Prefix, Type Handler);

/// <summary>Answers every request on the plugin's session origins.</summary>
public interface ISessionOriginHandler
{
    /// <param name="label">The label after the prefix: 1 to 63 lower-case letters and digits.</param>
    Task HandleAsync(Microsoft.AspNetCore.Http.HttpContext context, string label);
}

/// <summary>Where session origins are.</summary>
public interface ISessionOrigins
{
    /// <returns><c>https://&lt;prefix&gt;-&lt;label&gt;.&lt;web domain&gt;</c>, or null when no web domain is set.</returns>
    Uri? UrlFor(string prefix, string label);
}
