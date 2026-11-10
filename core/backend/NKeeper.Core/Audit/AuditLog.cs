using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NKeeper.Core.Persistence;

namespace NKeeper.Core.Audit;

/// <summary>What core records. Plugins declare their own actions later (docs/v2-plan.md §4.2).</summary>
public static class AuditActions
{
    public const string SignIn = "auth.sign_in";
    public const string SignInFailed = "auth.sign_in_failed";
    public const string SignOut = "auth.sign_out";
    public const string UserCreate = "user.create";
    public const string UserUpdate = "user.update";
    public const string UserCredentialReset = "user.password_reset";
    public const string UserDelete = "user.delete";
    public const string AccountCredentialChange = "account.password_change";
    public const string UserGrantsReplace = "user.grants_replace";
    public const string HostCreate = "host.create";
    public const string HostUpdate = "host.update";
    public const string HostDelete = "host.delete";
}

/// <summary>
/// One event to record. The actor, account and client address are taken from the current request
/// unless given; <paramref name="UserName"/> overrides the actor, e.g. with the name typed at a failed sign-in.
/// </summary>
public sealed record AuditEntry(string Action, string Target = "", string Detail = "", Guid? UserId = null, string? UserName = null);

/// <summary>The audit trail (<c>core.audit_events</c>).</summary>
public interface IAuditLog
{
    /// <summary>
    /// Records an event in its own transaction, so it's never rolled back with the change it describes.
    /// Never throws: a failed write is logged instead, and the caller carries on.
    /// </summary>
    Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default);
}

public sealed class AuditLog(
    DbContextOptions<CoreDbContext> database,
    IHttpContextAccessor httpContext,
    TimeProvider time,
    ILogger<AuditLog> logger) : IAuditLog
{
    public const int MaxActorLength = 256;
    public const int MaxDetailLength = 2000;

    public async Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        var context = httpContext.HttpContext;
        var user = context?.User;
        var signedIn = user?.Identity?.IsAuthenticated == true;
        var userId = entry.UserId
            ?? (signedIn && Guid.TryParse(user!.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null);
        var actor = entry.UserName ?? (signedIn ? user!.Identity!.Name : null) ?? (context is null ? DbPluginAuditSink.SystemActor : "anonymous");

        try
        {
            await using var db = new CoreDbContext(database);
            db.AuditEvents.Add(new AuditEvent
            {
                OccurredAtUtc = time.GetUtcNow(),
                Actor = Truncate(actor, MaxActorLength),
                UserId = userId,
                ClientAddress = context?.Connection.RemoteIpAddress?.ToString(),
                Action = entry.Action,
                Target = entry.Target,
                Detail = Truncate(entry.Detail, MaxDetailLength),
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is DbUpdateException or System.Data.Common.DbException or InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            logger.LogError(ex, "Couldn't record the audit event {Action} for {Actor}.", entry.Action, actor);
        }
    }

    private static string Truncate(string value, int length) => value.Length > length ? value[..length] : value;
}
