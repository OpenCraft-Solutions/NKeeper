using System.Linq.Expressions;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NKeeper.Core.Persistence;

namespace NKeeper.Core.Access;

/// <summary>Who is asking, as far as host access is concerned.</summary>
public sealed record RequestUser(Guid Id, bool IsAdministrator)
{
    public static RequestUser From(ClaimsPrincipal principal) =>
        new(
            Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
                ? id
                : throw new InvalidOperationException("The signed-in user has no valid ID."),
            principal.IsInRole(SystemRoles.Administrator));
}

/// <summary>A user's permission on one host.</summary>
public enum HostPermission
{
    /// <summary>The host doesn't exist, or the user can't see it: either way, the answer is 404.</summary>
    None,

    Use,
    Manage,
}

/// <summary>
/// The one place that decides which hosts a user can see, use and manage (docs/architecture.md
/// §7.2). Administrators can do everything; other users what their grants allow. Ported from 1.x.
/// </summary>
public sealed class HostAccess(CoreDbContext db)
{
    /// <summary>The hosts the user can see and use.</summary>
    public IQueryable<Host> Visible(RequestUser user) =>
        user.IsAdministrator ? db.Hosts : db.Hosts.Where(Granted(user.Id, manageOnly: false));

    /// <summary>The hosts the user may change and delete.</summary>
    public IQueryable<Host> Manageable(RequestUser user) =>
        user.IsAdministrator ? db.Hosts : db.Hosts.Where(Granted(user.Id, manageOnly: true));

    public async Task<HostPermission> GetPermissionAsync(Guid hostId, RequestUser user, CancellationToken cancellationToken)
    {
        var hosts = db.Hosts.Where(h => h.Id == hostId);
        if (user.IsAdministrator)
        {
            return await hosts.AnyAsync(cancellationToken) ? HostPermission.Manage : HostPermission.None;
        }

        if (!await hosts.Where(Granted(user.Id, manageOnly: false)).AnyAsync(cancellationToken))
        {
            return HostPermission.None;
        }

        return await hosts.Where(Granted(user.Id, manageOnly: true)).AnyAsync(cancellationToken) ? HostPermission.Manage : HostPermission.Use;
    }

    /// <summary>The IDs of the hosts the user may change; null means all of them (administrators).</summary>
    public async Task<HashSet<Guid>?> ManageableIdsAsync(RequestUser user, CancellationToken cancellationToken) =>
        user.IsAdministrator ? null : [.. await Manageable(user).Select(h => h.Id).ToListAsync(cancellationToken)];

    // Runs as SQL: ToUpper() becomes upper(), whereas string.Equals(…, StringComparison) can't be translated.
#pragma warning disable CA1862, CA1304, CA1311
    private Expression<Func<Host, bool>> Granted(Guid userId, bool manageOnly) =>
        h => db.HostAccessGrants.Any(g => g.UserId == userId
            && (!manageOnly || g.Level == HostAccessLevel.Manage)
            && (g.HostId == h.Id
                || (g.TagName != null && h.Tags.Any(t => t.Name.ToUpper() == g.TagName.ToUpper()
                    && (g.TagValue == null || t.Value.ToUpper() == g.TagValue.ToUpper())))));
#pragma warning restore CA1862, CA1304, CA1311
}
