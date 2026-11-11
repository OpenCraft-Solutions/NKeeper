using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NKeeper.Abstractions;

namespace NKeeper.Plugins.Web;

/// <summary>
/// One web session (docs/architecture.md §7.19): a service one user browses through NKeeper. Requests go through
/// core's connection (§7.16), which owns certificate trust, and the session is registered in core's session
/// registry (§7.17), which ends it when the user's access changes. Ported from 1.x.
/// </summary>
public sealed class WebSession : IDisposable
{
    private string? _startPath;

    internal WebSession(Guid userId, Guid hostId, IHttpConnection connection, IPluginSession registration, string? embedderOrigin, string startPath, DateTimeOffset now)
    {
        Id = Convert.ToHexStringLower(registration.Id.ToByteArray());
        Secret = Base64Url(32);
        LaunchToken = Base64Url(32);
        UserId = userId;
        HostId = hostId;
        Connection = connection;
        Registration = registration;
        EmbedderOrigin = embedderOrigin;
        _startPath = startPath;
        CreatedAtUtc = LastSeenUtc = now;
        LaunchTokenExpiresUtc = now.AddMinutes(2);
    }

    /// <summary>The registration's 128 random bits as hex, used as a DNS label: <c>nkw-&lt;Id&gt;</c>.</summary>
    public string Id { get; }

    /// <summary>Value of the browser cookie that proves the request comes from the user who launched it.</summary>
    public string Secret { get; }

    /// <summary>One-time token in the launch URL, exchanged for the cookie.</summary>
    public string? LaunchToken { get; private set; }

    public DateTimeOffset LaunchTokenExpiresUtc { get; }

    public Guid UserId { get; }

    public Guid HostId { get; }

    /// <summary>Core's connection to the service: every request goes through it.</summary>
    public IHttpConnection Connection { get; }

    /// <summary>The session in core's registry: its <see cref="IPluginSession.Ended"/> is cancelled when access changes.</summary>
    public IPluginSession Registration { get; }

    /// <summary>NKeeper's own origin, the only page allowed to show this session in a frame.</summary>
    public string? EmbedderOrigin { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset LastSeenUtc { get; internal set; }

    /// <summary>Consumes the launch token: valid once, and only for a short time.</summary>
    internal bool TryRedeemLaunchToken(string token, DateTimeOffset now)
    {
        var expected = LaunchToken;
        if (expected is null || now > LaunchTokenExpiresUtc
            || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(token), System.Text.Encoding.ASCII.GetBytes(expected)))
        {
            return false;
        }

        LaunchToken = null;
        return true;
    }

    /// <summary>Where the launch link lands: the start path, only the first time; "/" after that.</summary>
    internal string TakeStartPath() => Interlocked.Exchange(ref _startPath, null) ?? "/";

    internal bool HasSecret(string? value) =>
        value is not null && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(value), System.Text.Encoding.ASCII.GetBytes(Secret));

    public void Dispose()
    {
        Connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Registration.Dispose();
    }

    private static string Base64Url(int bytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Open web sessions, in memory (per API instance; restarting the API ends them).</summary>
public sealed class WebSessionStore(TimeProvider timeProvider, ILogger<WebSessionStore> logger) : IDisposable
{
    /// <summary>A session no request or open tab has touched for this long ends.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    /// <summary>No session lasts longer than this.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(12);

    private readonly ConcurrentDictionary<string, WebSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>Adds a session core has registered, with core's connection to its service; it ends when core ends its registration.</summary>
    /// <param name="startPath">The path (and query) the launch link opens on the target.</param>
    internal WebSession Add(Guid userId, Guid hostId, IHttpConnection connection, IPluginSession registration, string? embedderOrigin, string startPath = "/")
    {
        RemoveExpired();
        var session = new WebSession(userId, hostId, connection, registration, embedderOrigin, startPath, timeProvider.GetUtcNow());
        _sessions[session.Id] = session;
        registration.Ended.Register(() => End(session.Id));
        logger.LogInformation("Web session {SessionId} opened by user {UserId} for host {HostId} ({Target})", session.Id, userId, hostId, connection.BaseAddress);
        return session;
    }

    /// <summary>The live session with this id, or null when unknown or expired (expired ones are ended).</summary>
    public WebSession? Find(string id)
    {
        if (!_sessions.TryGetValue(id, out var session))
        {
            return null;
        }

        if (IsExpired(session, timeProvider.GetUtcNow()))
        {
            End(session.Id, "idle");
            return null;
        }

        return session;
    }

    /// <summary>Ends a session. Core audits it, with <paramref name="reason"/> when the plugin ends it, or core's own.</summary>
    public bool End(string id, string? reason = null)
    {
        if (!_sessions.TryRemove(id, out var session))
        {
            return false;
        }

        if (reason is not null)
        {
            session.Registration.End(reason);
        }

        logger.LogInformation("Web session {SessionId} of user {UserId} ended: {Reason}", id, session.UserId, session.Registration.EndReason ?? "closed");
        session.Dispose();
        return true;
    }

    /// <summary>The user's open NKeeper tab says the session is still in use (the site itself may be idle).</summary>
    public bool TouchOwned(string id, Guid userId)
    {
        if (!_sessions.TryGetValue(id, out var session) || session.UserId != userId)
        {
            return false;
        }

        session.LastSeenUtc = timeProvider.GetUtcNow();
        return true;
    }

    /// <summary>Ends a session only if it belongs to the user (closing from the NKeeper tab).</summary>
    public bool EndOwned(string id, Guid userId) =>
        _sessions.TryGetValue(id, out var session) && session.UserId == userId && End(id);

    public void RemoveExpired()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var session in _sessions.Values.Where(s => IsExpired(s, now)).ToList())
        {
            End(session.Id, "idle");
        }
    }

    private static bool IsExpired(WebSession session, DateTimeOffset now) =>
        now - session.LastSeenUtc > IdleTimeout || now - session.CreatedAtUtc > MaxLifetime;

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }

        _sessions.Clear();
    }
}

/// <summary>Ends idle and over-age web sessions once a minute.</summary>
internal sealed class WebSessionSweeper(WebSessionStore store, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            store.RemoveExpired();
        }
    }
}
