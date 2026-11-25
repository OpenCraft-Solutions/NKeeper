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

    /// <summary>Duplicate hosts combined into one (architecture.md §7.46).</summary>
    public const string HostMerge = "host.merge";
    public const string CredentialDenied = "credential.denied";
    public const string CredentialUse = "credential.use";

    /// <summary>A credential's plaintext reached a plugin that declared it needs it (plugin-security.md §12.3, SDK 1.10).</summary>
    public const string CredentialReveal = "credential.reveal";

    public const string TrustAdd = "trust.add";
    public const string TrustRemove = "trust.remove";
    public const string TrustMismatch = "trust.mismatch";
    public const string SessionOpen = "session.open";
    public const string SessionClose = "session.close";

    /// <summary>An administrator watched a terminal session's recording (§7.51).</summary>
    public const string RecordingView = "recording.view";

    /// <summary>An administrator asked root's helper to install or upgrade a catalogued plugin (architecture.md §9.1).</summary>
    public const string PluginInstallRequested = "plugin.install_requested";

    /// <summary>Root's helper installed a plugin from the catalog.</summary>
    public const string PluginInstalled = "plugin.installed";

    /// <summary>Root's helper upgraded a plugin from the catalog.</summary>
    public const string PluginUpgraded = "plugin.upgraded";

    /// <summary>A plugin saved its settings (architecture.md §7.39).</summary>
    public const string PluginSettingsChanged = "plugin.settings_changed";

    /// <summary>An administrator bound a plugin's integration (architecture.md §7.39).</summary>
    public const string IntegrationSet = "integration.set";

    /// <summary>An administrator cleared a plugin's integration.</summary>
    public const string IntegrationCleared = "integration.cleared";

    /// <summary>Root's helper didn't do what the plugins page asked.</summary>
    public const string PluginRequestFailed = "plugin.request_failed";

    /// <summary>An administrator uploaded a plugin package (architecture.md §9.3).</summary>
    public const string PluginUploaded = "plugin.uploaded";

    /// <summary>An administrator added a publisher's catalog (architecture.md §9.3).</summary>
    public const string PluginCatalogAdded = "plugin.catalog_added";

    /// <summary>An administrator removed a publisher's catalog.</summary>
    public const string PluginCatalogRemoved = "plugin.catalog_removed";

    /// <summary>An administrator asked root's helper to remove a plugin (architecture.md §9.2).</summary>
    public const string PluginRemoveRequested = "plugin.remove_requested";

    /// <summary>Root's helper removed a plugin's package.</summary>
    public const string PluginRemoved = "plugin.removed";

    /// <summary>An administrator asked root's helper to restart NKeeper.</summary>
    public const string PluginRestartRequested = "plugin.restart_requested";

    /// <summary>Root's helper restarted NKeeper.</summary>
    public const string PluginRestarted = "plugin.restarted";

    /// <summary>An administrator asked root's helper to trust or untrust a key (architecture.md §9.3).</summary>
    public const string PluginTrustRequested = "plugin.trust_requested";

    /// <summary>Root's helper trusted a third-party key.</summary>
    public const string PluginPublisherAdded = "plugin.publisher_added";

    /// <summary>Root's helper stopped trusting a third-party key.</summary>
    public const string PluginPublisherRemoved = "plugin.publisher_removed";

    /// <summary>An administrator exported the instance into an encrypted file (architecture.md §7.45).</summary>
    public const string TransferExport = "transfer.export";

    /// <summary>An administrator replaced a fresh instance's data with an export file's.</summary>
    public const string TransferImport = "transfer.import";

    /// <summary>An export locked the instance against changes to hosts and users (§7.45).</summary>
    public const string TransferLock = "transfer.lock";

    /// <summary>An administrator unlocked it again.</summary>
    public const string TransferUnlock = "transfer.unlock";
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
            // The actor can be a name typed at a failed sign-in: no line breaks into the log, so it can't forge entries.
            logger.LogError(ex, "Couldn't record the audit event {Action} for {Actor}.", entry.Action, OneLine(actor));
        }
    }

    private static string OneLine(string value) => value.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal);

    private static string Truncate(string value, int length) => value.Length > length ? value[..length] : value;
}
