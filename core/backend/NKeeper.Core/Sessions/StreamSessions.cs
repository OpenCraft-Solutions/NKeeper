using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NKeeper.Abstractions;
using NKeeper.Core.Access;
using NKeeper.Core.Audit;
using NKeeper.Core.Credentials;
using NKeeper.Core.Inventory;
using NKeeper.Core.Persistence;
using NKeeper.Core.Trust;

namespace NKeeper.Core.Sessions;

/// <summary>The session types the loaded plugins contributed (docs/architecture.md §7.10).</summary>
public sealed class SessionTypeCatalog(IEnumerable<(string PluginId, SessionType Type)> types)
{
    private readonly Dictionary<string, (string PluginId, SessionType Type)> _types = types.ToDictionary(t => t.Type.Id, StringComparer.Ordinal);

    public (string PluginId, SessionType Type)? Find(string id) => _types.TryGetValue(id, out var type) ? type : null;
}

/// <summary>The codes of <c>error</c> messages (docs/architecture.md §7.10).</summary>
public static class SessionErrorCodes
{
    public const string BadRequest = "bad_request";
    public const string TooManySessions = "too_many_sessions";
    public const string HostNotFound = "host_not_found";
    public const string WrongService = "wrong_service";
    public const string JumpHostUnsupported = "jump_host_unsupported";
    public const string CredentialDenied = "credential_denied";
    public const string HostKeyRejected = "host_key_rejected";
    public const string HostKeyChanged = "host_key_changed";
    public const string AuthenticationFailed = "authentication_failed";
    public const string NoSecret = "no_secret";
    public const string InvalidPrivateKey = "invalid_private_key";
    public const string Unreachable = "unreachable";
    public const string Timeout = "timeout";
    public const string JumpHostUnavailable = "jump_host_unavailable";
    public const string Failed = "failed";

    public static string For(SshConnectFailure failure) => failure switch
    {
        SshConnectFailure.HostKeyUnknown => HostKeyRejected,
        SshConnectFailure.HostKeyChanged => HostKeyChanged,
        SshConnectFailure.AuthenticationFailed => AuthenticationFailed,
        SshConnectFailure.NoSecret => NoSecret,
        SshConnectFailure.InvalidPrivateKey => InvalidPrivateKey,
        SshConnectFailure.TimedOut => Timeout,
        SshConnectFailure.JumpHostUnavailable => JumpHostUnavailable,
        _ => Unreachable,
    };
}

/// <summary>The browser's first message.</summary>
public sealed record SessionStartMessage(Guid HostId, Guid ServiceId, Guid? CredentialId, string? PromptReference, JsonElement? Options);

/// <summary>A failure to report to the browser as an <c>error</c> message, before the session opens.</summary>
public sealed class SessionStartException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class StreamSessionEndpoints
{
    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);

    /// <summary><c>GET /api/sessions/{type}</c>, upgraded to a WebSocket (docs/architecture.md §7.10).</summary>
    public static void MapStreamSessions(this IEndpointRouteBuilder api) =>
        api.Map("/sessions/{type}", RunAsync).WithMetadata(new SessionEndpointMetadata());

    private static async Task RunAsync(HttpContext context, string type)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // Browsers send no antiforgery token with a WebSocket, so the origin must be the server's own.
        if (!SameOrigin(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var services = context.RequestServices;
        if (services.GetRequiredService<SessionTypeCatalog>().Find(type) is not var (pluginId, sessionType)
            || services.GetRequiredService<IAuthSessionAccessor>().CurrentSessionKey(context) is not { } authSessionKey)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        using var socket = new SessionSocket(await context.WebSockets.AcceptWebSocketAsync());
        var runner = new StreamSessionRunner(context, socket, pluginId, sessionType, authSessionKey);
        await runner.RunAsync();
    }

    private static bool SameOrigin(HttpRequest request) =>
        Uri.TryCreate(request.Headers.Origin.ToString(), UriKind.Absolute, out var origin)
        && string.Equals(origin.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(origin.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
}

/// <summary>One stream session, from <c>start</c> to its end.</summary>
internal sealed class StreamSessionRunner(HttpContext context, SessionSocket socket, string pluginId, SessionType sessionType, string authSessionKey)
{
    private const int MaxKeyPrompts = 2;
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(10);
    private readonly IServiceProvider _services = context.RequestServices;
    private readonly ILogger _logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger<StreamSessionRunner>();

    public async Task RunAsync()
    {
        var aborted = context.RequestAborted;
        var registry = _services.GetRequiredService<SessionRegistry>();
        var user = RequestUser.From(context.User);
        using var place = registry.TryReserve(user.Id);
        IStreamSession? session = null;
        OpenSession? open = null;
        try
        {
            if (place is null)
            {
                throw new SessionStartException(SessionErrorCodes.TooManySessions, $"You have {SessionRegistry.MaxPerUser} sessions open. Close one to open another.");
            }

            var start = await socket.ReceiveStartAsync(aborted);
            var host = await AuthorizeAsync(start, user, aborted);
            await socket.SendJsonAsync(new { type = "status", status = "connecting" }, aborted);
            var handle = await ResolveCredentialAsync(host, start, aborted);
            session = await OpenAsync(host, start, handle, user, aborted);

            open = new OpenSession(Guid.NewGuid(), user.Id, authSessionKey, host.Id, start.ServiceId, start.CredentialId, sessionType.Id, pluginId, _services.GetRequiredService<TimeProvider>().GetUtcNow());
            registry.Add(open);
            await AuditAsync(AuditActions.SessionOpen, host.Id, $"{sessionType.Id} on service {start.ServiceId}, plugin {pluginId}, {(start.CredentialId is { } id ? $"credential {id}" : start.PromptReference is null ? "no credential" : "a typed credential")}");
            await socket.SendJsonAsync(new { type = "connected" }, aborted);
        }
        catch (SessionStartException ex)
        {
            await socket.SendErrorAndCloseAsync(ex.Code, ex.Message, CancellationToken.None);
            await DisposeAsync(session);
            return;
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // The browser went away before the session opened.
            await DisposeAsync(session);
            return;
        }

        await RunOpenAsync(session, open, registry);
    }

    /// <summary>The plugin's data phase, until either side is done or core ends it.</summary>
    private async Task RunOpenAsync(IStreamSession session, OpenSession open, SessionRegistry registry)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, open.Ended);
        using var channel = new SocketStreamChannel(socket);
        var pump = channel.PumpAsync();
        var reason = SessionEndReasons.Closed;
        try
        {
            var run = session.RunAsync(channel, stop.Token);

            // A plugin that doesn't stop when told is given a few seconds, then left behind, so it can't hold the session open.
            await Task.WhenAny(run, Task.Delay(Timeout.Infinite, stop.Token)).ContinueWith(_ => { }, TaskScheduler.Default);
            if (!run.IsCompleted && await Task.WhenAny(run, Task.Delay(StopGrace, CancellationToken.None)) != run)
            {
                _logger.LogWarning("Session {SessionId} of type {SessionType} didn't stop within {Grace}; leaving it behind", open.Id, sessionType.Id, StopGrace);
                _ = run.ContinueWith(t => t.Exception, TaskScheduler.Default);
            }
            else
            {
                await run;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Session {SessionId} of type {SessionType} failed", open.Id, sessionType.Id);
            reason = SessionEndReasons.Failed;
        }
        finally
        {
            channel.Finish();
            reason = open.EndReason ?? reason;
            await DisposeAsync(session);
            registry.Remove(open);
        }

        await AuditAsync(
            AuditActions.SessionClose,
            open.HostId,
            $"{sessionType.Id} on service {open.ServiceId}: {reason}, after {(_services.GetRequiredService<TimeProvider>().GetUtcNow() - open.OpenedAtUtc).TotalSeconds:0} s");
        try
        {
            // The pump, still reading, receives the browser's answer to our close.
            await socket.SendJsonAsync(new { type = "closed", reason }, CancellationToken.None);
            await socket.BeginCloseAsync(reason, CancellationToken.None);
            await socket.AcknowledgeCloseAsync();
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or SessionStartException or InvalidOperationException)
        {
            // The browser is already gone.
        }

        if (await Task.WhenAny(pump, Task.Delay(SessionSocket.CloseHandshakeTimeout)) != pump)
        {
            socket.Abort();
        }

        await pump;
    }

    /// <summary>The host, if the user can use it, and the service, if this session type opens on it.</summary>
    private async Task<HostContext> AuthorizeAsync(SessionStartMessage start, RequestUser user, CancellationToken cancellationToken)
    {
        var permission = await _services.GetRequiredService<HostAccess>().GetPermissionAsync(start.HostId, user, cancellationToken);
        var host = permission == Access.HostPermission.None
            ? null
            : await HostScopeMiddleware.LoadAsync(_services.GetRequiredService<CoreDbContext>(), _services.GetRequiredService<ServiceTypeCatalog>(), start.HostId, permission, cancellationToken);
        if (host is null)
        {
            throw new SessionStartException(SessionErrorCodes.HostNotFound, "This host doesn't exist, or you can't use it.");
        }

        if (host.Services.FirstOrDefault(s => s.Id == start.ServiceId) is not { } service || !sessionType.ServiceTypes.Contains(service.Type, StringComparer.Ordinal))
        {
            throw new SessionStartException(SessionErrorCodes.WrongService, $"“{host.Name}” has no such service for a {sessionType.Label} session. Reload the host and try again.");
        }

        if (!sessionType.SupportsJumpHost
            && await _services.GetRequiredService<CoreDbContext>().Hosts.AnyAsync(h => h.Id == host.Id && h.JumpHostId != null, cancellationToken))
        {
            throw new SessionStartException(SessionErrorCodes.JumpHostUnsupported, $"{sessionType.Label} sessions can't go through a jump host, and “{host.Name}” is reached through one.");
        }

        // From here on, this request acts on this host, for the session type's plugin (§7.6).
        context.Features.Set(host);
        context.Features.Set(new SessionPluginFeature(pluginId));
        return host;
    }

    private async Task<CredentialHandle?> ResolveCredentialAsync(HostContext host, SessionStartMessage start, CancellationToken cancellationToken)
    {
        var broker = _services.GetRequiredService<ICredentialBroker>();
        try
        {
            return start.CredentialId is { } credentialId ? await broker.ResolveSavedAsync(host, start.ServiceId, credentialId, cancellationToken)
                : start.PromptReference is { } reference ? await broker.ResolvePromptAsync(host, start.ServiceId, reference, cancellationToken)
                : null;
        }
        catch (CredentialDeniedException ex)
        {
            throw new SessionStartException(SessionErrorCodes.CredentialDenied, ex.Message);
        }
    }

    /// <summary>Opens the plugin's session, asking the user in-band about keys no one trusted yet (§7.7).</summary>
    private async Task<IStreamSession> OpenAsync(HostContext host, SessionStartMessage start, CredentialHandle? handle, RequestUser user, CancellationToken cancellationToken)
    {
        var handler = (IStreamSessionHandler)_services.GetRequiredService(sessionType.Handler);
        var request = new StreamSessionRequest(host, start.ServiceId, handle, start.Options ?? JsonDocument.Parse("{}").RootElement);
        for (var prompts = 0; ; prompts++)
        {
            try
            {
                return await handler.OpenAsync(request, cancellationToken);
            }
            catch (SshConnectException ex) when (ex.Reason == SshConnectFailure.HostKeyUnknown && ex.PresentedKey is { } key && prompts < MaxKeyPrompts)
            {
                if (!await socket.AskToTrustAsync(new { type = "hostKey", hostId = key.HostId, serviceId = key.ServiceId, algorithm = key.Algorithm, fingerprint = key.Fingerprint }, cancellationToken)
                    || await _services.GetRequiredService<TrustStore>().TrustAsync(authSessionKey, key.HostId, key.ServiceId, key.Algorithm, key.Fingerprint, user.Id, cancellationToken) is not null)
                {
                    throw new SessionStartException(SessionErrorCodes.HostKeyRejected, "The server's key wasn't trusted, so the session didn't open.");
                }
            }
            catch (SshConnectException ex)
            {
                throw new SessionStartException(SessionErrorCodes.For(ex.Reason), ex.Message);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or WebSocketException or SessionStartException))
            {
                _logger.LogError(ex, "Opening a {SessionType} session failed", sessionType.Id);
                throw new SessionStartException(SessionErrorCodes.Failed, $"The {sessionType.Label} session couldn't be opened.");
            }
        }
    }

    private Task AuditAsync(string action, Guid hostId, string detail) =>
        _services.GetRequiredService<IAuditLog>().RecordAsync(new AuditEntry(action, hostId.ToString(), detail), CancellationToken.None);

    private async Task DisposeAsync(IStreamSession? session)
    {
        if (session is null)
        {
            return;
        }

        try
        {
            await session.DisposeAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Closing a {SessionType} session failed", sessionType.Id);
        }
    }
}

/// <summary>
/// The plugin's side of the socket. Core reads the socket itself, and is its only reader, and hands the plugin
/// its frames. So core can still say <c>closed</c> when it ends a session (cancelling a WebSocket receive would
/// abort the socket), and the same loop receives the browser's answer to core's close.
/// </summary>
internal sealed class SocketStreamChannel(SessionSocket socket) : IStreamChannel, IDisposable
{
    private readonly Channel<StreamFrame> _frames = Channel.CreateBounded<StreamFrame>(new BoundedChannelOptions(64) { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _finished = new();

    /// <summary>Reads until the socket closes. Frames that arrive once the plugin is done are dropped.</summary>
    public async Task PumpAsync()
    {
        try
        {
            while (true)
            {
                var (type, data) = await socket.ReceiveAsync(CancellationToken.None);
                if (type == WebSocketMessageType.Close)
                {
                    break;
                }

                // Core's own control messages mean nothing once the session is open.
                if (_finished.IsCancellationRequested
                    || (type == WebSocketMessageType.Text && SessionSocket.ReadType(data) is "start" or "hostKeyDecision"))
                {
                    continue;
                }

                try
                {
                    await _frames.Writer.WriteAsync(new StreamFrame(type == WebSocketMessageType.Text, data), _finished.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or SessionStartException or InvalidOperationException)
        {
            // The socket went away.
        }
        finally
        {
            _frames.Writer.TryComplete();
        }
    }

    public void Dispose() => _finished.Dispose();

    /// <summary>The plugin is done: no more frames for it.</summary>
    public void Finish()
    {
        _finished.Cancel();
        _frames.Writer.TryComplete();
    }

    public async Task<StreamFrame?> ReceiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _frames.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        socket.SendAsync(data, WebSocketMessageType.Binary, cancellationToken);

    public Task SendTextAsync(string json, CancellationToken cancellationToken) =>
        socket.SendAsync(System.Text.Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, cancellationToken);
}

public static class StreamSessionServices
{
    /// <summary>Stream sessions (docs/architecture.md §7.10), for the session types the plugins contributed.</summary>
    public static IServiceCollection AddNKeeperSessions(this IServiceCollection services, IEnumerable<(string PluginId, SessionType Type)> sessionTypes)
    {
        var types = sessionTypes.ToList();
        return services
            .AddSingleton(new SessionTypeCatalog(types))
            .AddSingleton<SessionRegistry>()
            .AddScoped<SessionAccessCheck>()
            .AddHostedService<SessionSweeper>();
    }
}
