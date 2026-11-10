using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NKeeper.Core.Access;
using NKeeper.Core.Persistence;

namespace NKeeper.Core.Sessions;

/// <summary>Why a session ended, as the browser and the audit see it.</summary>
public static class SessionEndReasons
{
    public const string Closed = "closed";
    public const string AccessRevoked = "access_revoked";
    public const string Failed = "failed";
}

/// <summary>An open stream session (docs/architecture.md §7.10). Its <see cref="Ended"/> token is cancelled when core ends it.</summary>
/// <param name="CredentialId">The saved credential it was opened with; null for a typed one or none.</param>
public sealed class OpenSession(Guid id, Guid userId, string authSessionKey, Guid hostId, Guid serviceId, Guid? credentialId, string type, string pluginId, DateTimeOffset openedAtUtc) : IDisposable
{
    private readonly CancellationTokenSource _ended = new();
    private string? _endReason;

    public Guid Id { get; } = id;

    public Guid UserId { get; } = userId;

    public string AuthSessionKey { get; } = authSessionKey;

    public Guid HostId { get; } = hostId;

    public Guid ServiceId { get; } = serviceId;

    public Guid? CredentialId { get; } = credentialId;

    public string Type { get; } = type;

    public string PluginId { get; } = pluginId;

    public DateTimeOffset OpenedAtUtc { get; } = openedAtUtc;

    public CancellationToken Ended => _ended.Token;

    /// <summary>Why core ended it; null while it's open, or when the browser or plugin ended it.</summary>
    public string? EndReason => Volatile.Read(ref _endReason);

    /// <summary>Ends the session. The first reason given wins.</summary>
    public void End(string reason)
    {
        if (Interlocked.CompareExchange(ref _endReason, reason, null) is null)
        {
            try
            {
                _ended.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // It finished meanwhile.
            }
        }
    }

    public void Dispose() => _ended.Dispose();
}

/// <summary>
/// Every open stream session, whatever its type or plugin (docs/architecture.md §7.10, v2-plan.md §7.1). Caps
/// sessions per user, and ends the ones a user is no longer allowed when access changes.
/// </summary>
public sealed class SessionRegistry(IServiceScopeFactory scopes, ILogger<SessionRegistry> logger)
{
    public const int MaxPerUser = 20;

    private readonly ConcurrentDictionary<Guid, OpenSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, int> _reserved = new();

    /// <summary>For tests: the open sessions.</summary>
    public IReadOnlyCollection<OpenSession> Open => [.. _sessions.Values];

    /// <summary>Reserves one of the user's places, before the session is open. Null when they have none left.</summary>
    public IDisposable? TryReserve(Guid userId)
    {
        var count = _reserved.AddOrUpdate(userId, 1, (_, n) => n + 1);
        if (count > MaxPerUser)
        {
            Release(userId);
            return null;
        }

        return new Reservation(this, userId);
    }

    public void Add(OpenSession session) => _sessions[session.Id] = session;

    public void Remove(OpenSession session)
    {
        if (_sessions.TryRemove(session.Id, out _))
        {
            session.Dispose();
        }
    }

    /// <summary>Re-checks the open sessions that <paramref name="which"/> picks, and ends those no longer allowed.</summary>
    public async Task RecheckAsync(Func<OpenSession, bool> which, CancellationToken cancellationToken = default)
    {
        var picked = _sessions.Values.Where(which).ToList();
        if (picked.Count == 0)
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var checker = scope.ServiceProvider.GetRequiredService<SessionAccessCheck>();
        foreach (var session in picked)
        {
            if (!await checker.AllowedAsync(session, cancellationToken))
            {
                logger.LogInformation("Ending session {SessionId} of user {UserId} on host {HostId}: access changed", session.Id, session.UserId, session.HostId);
                session.End(SessionEndReasons.AccessRevoked);
            }
        }
    }

    private void Release(Guid userId) => _reserved.AddOrUpdate(userId, 0, (_, n) => Math.Max(0, n - 1));

    private sealed class Reservation(SessionRegistry registry, Guid userId) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                registry.Release(userId);
            }
        }
    }
}

/// <summary>Whether an open session is still allowed (docs/architecture.md §7.10). Reads the database every time.</summary>
public sealed class SessionAccessCheck(CoreDbContext db, HostAccess access, TimeProvider time)
{
    public async Task<bool> AllowedAsync(OpenSession session, CancellationToken cancellationToken)
    {
        // The sign-in session that opened it is still live.
        var now = time.GetUtcNow();
        if (!Guid.TryParseExact(session.AuthSessionKey, "N", out var authSessionId)
            || !await db.AuthSessions.AnyAsync(s => s.Id == authSessionId && s.UserId == session.UserId && (s.ExpiresAtUtc == null || s.ExpiresAtUtc > now), cancellationToken))
        {
            return false;
        }

        // The user is enabled, and can still use the host, as their role and grants stand now.
        var user = await db.Users.AsNoTracking().Where(u => u.Id == session.UserId).Select(u => new { u.IsDisabled }).FirstOrDefaultAsync(cancellationToken);
        if (user is null || user.IsDisabled)
        {
            return false;
        }

        var isAdministrator = await db.UserRoles.AnyAsync(
            ur => ur.UserId == session.UserId && db.Roles.Any(r => r.Id == ur.RoleId && r.Name == SystemRoles.Administrator),
            cancellationToken);
        var requestUser = new RequestUser(session.UserId, isAdministrator);
        if (await access.GetPermissionAsync(session.HostId, requestUser, cancellationToken) == Access.HostPermission.None)
        {
            return false;
        }

        // The service, and the saved credential, are still the host's.
        var host = await db.Hosts.AsNoTracking().Where(h => h.Id == session.HostId).Select(h => new { h.JumpHostId }).FirstAsync(cancellationToken);
        if (!await db.HostServices.AnyAsync(s => s.Id == session.ServiceId && s.HostId == session.HostId, cancellationToken)
            || (session.CredentialId is { } credentialId && !await db.HostCredentials.AnyAsync(c => c.Id == credentialId && c.HostId == session.HostId, cancellationToken)))
        {
            return false;
        }

        // A jump host the user can no longer use cuts the route too.
        return host.JumpHostId is not { } jumpHostId
            || await access.GetPermissionAsync(jumpHostId, requestUser, cancellationToken) != Access.HostPermission.None;
    }
}

/// <summary>Re-checks every open session every 30 seconds, for changes no event announces (a sign-in session that expired, say).</summary>
public sealed class SessionSweeper(SessionRegistry registry, TimeProvider time, ILogger<SessionSweeper> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await registry.RecheckAsync(_ => true, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Re-checking open sessions failed");
            }
        }
    }
}
