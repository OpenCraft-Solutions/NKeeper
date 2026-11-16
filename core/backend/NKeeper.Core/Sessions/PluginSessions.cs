using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NKeeper.Abstractions;
using NKeeper.Core.Access;
using NKeeper.Core.Audit;
using NKeeper.Core.Credentials;
using NKeeper.Core.Persistence;

namespace NKeeper.Core.Sessions;

/// <summary>
/// Stateful sessions (docs/architecture.md §7.17): sessions a plugin keeps itself, registered in core's session
/// registry like stream sessions, so they share the user's limit, end when access changes, and are audited.
/// </summary>
public sealed class PluginSessions(
    IHttpContextAccessor http,
    SessionRegistry registry,
    SessionTypeCatalog types,
    IAuthSessionAccessor authSessions,
    CoreDbContext db,
    IAuditLog audit,
    TimeProvider time) : IPluginSessions
{
    public async Task<IPluginSession> OpenAsync(HostContext host, Guid serviceId, string sessionType, CancellationToken cancellationToken = default)
    {
        var context = http.HttpContext;
        var plugin = PluginCaller.Of(context);

        // Only from the plugin's own host endpoint, for the host core authorized and one of its stateful types.
        if (context is null || plugin is null || !ReferenceEquals(context.Features.Get<HostContext>(), host))
        {
            throw new InvalidOperationException("Sessions can only be opened from a plugin's host-scoped endpoint, for its own host.");
        }

        if (types.Find(sessionType) is not var (owner, type) || owner != plugin || !type.IsStateful)
        {
            throw new InvalidOperationException($"'{sessionType}' isn't one of plugin {plugin}'s stateful session types.");
        }

        if (!host.Services.Any(s => s.Id == serviceId))
        {
            throw new InvalidOperationException("The host has no such service.");
        }

        var user = RequestUser.From(context.User);
        var authSession = authSessions.CurrentSessionKey(context)
            ?? throw new InvalidOperationException("There's no signed-in session to open it for.");
        var place = registry.TryReserve(user.Id)
            ?? throw new SessionLimitException($"You have {SessionRegistry.MaxPerUser} sessions open. Close one to open another.");
        try
        {
            var jumpHostId = await db.Hosts.Where(h => h.Id == host.Id).Select(h => h.JumpHostId).FirstOrDefaultAsync(cancellationToken);
            var open = new OpenSession(Guid.NewGuid(), user.Id, authSession, host.Id, serviceId, null, jumpHostId, sessionType, plugin, time.GetUtcNow());
            registry.Add(open);
            await audit.RecordAsync(new AuditEntry(AuditActions.SessionOpen, host.Id.ToString(), $"{sessionType} on service {serviceId}, plugin {plugin}"), CancellationToken.None);
            return new PluginSession(open, place, registry, audit, time);
        }
        catch
        {
            place.Dispose();
            throw;
        }
    }

    private sealed class PluginSession(OpenSession open, IDisposable place, SessionRegistry registry, IAuditLog audit, TimeProvider time) : IPluginSession
    {
        // Kept here, since the registry disposes the session's own token source when it's removed.
        private readonly CancellationToken _ended = open.Ended;
        private int _disposed;

        public Guid Id => open.Id;

        public CancellationToken Ended => _ended;

        public string? EndReason => open.EndReason;

        public void End(string reason) => open.End(reason);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            var reason = open.EndReason ?? SessionEndReasons.Closed;
            registry.Remove(open);
            place.Dispose();

            // Often outside any request of the user's (an idle timer, say), so the user is named explicitly.
            _ = audit.RecordAsync(
                new AuditEntry(
                    AuditActions.SessionClose,
                    open.HostId.ToString(),
                    $"{open.Type} on service {open.ServiceId}: {reason}, after {(time.GetUtcNow() - open.OpenedAtUtc).TotalSeconds:0} s",
                    open.UserId),
                CancellationToken.None);
        }
    }
}
