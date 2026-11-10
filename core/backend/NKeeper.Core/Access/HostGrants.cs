using Microsoft.EntityFrameworkCore;
using NKeeper.Core.Audit;
using NKeeper.Core.Inventory;
using NKeeper.Core.Persistence;

namespace NKeeper.Core.Access;

/// <summary>A grant as sent: to one host (<see cref="HostId"/>), or to a group (<see cref="TagName"/>, optionally <see cref="TagValue"/>).</summary>
public sealed record HostGrantRequest(Guid? HostId, string? TagName, string? TagValue, HostAccessLevel? Level);

public sealed record HostGrantResponse(Guid Id, Guid? HostId, string? HostName, string? TagName, string? TagValue, HostAccessLevel Level);

/// <summary>A user's host grants (docs/architecture.md §7.2), set by administrators. Ported from 1.x.</summary>
public sealed class HostGrants(CoreDbContext db, IAuditLog audit, TimeProvider time)
{
    /// <returns>The user's grants, or null when there's no such user.</returns>
    public async Task<List<HostGrantResponse>?> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (!await db.Users.AnyAsync(u => u.Id == userId, cancellationToken))
        {
            return null;
        }

        return await db.HostAccessGrants.AsNoTracking()
            .Where(g => g.UserId == userId)
            .OrderBy(g => g.HostId == null).ThenBy(g => g.TagName).ThenBy(g => g.TagValue)
            .Select(g => new HostGrantResponse(
                g.Id, g.HostId, db.Hosts.Where(h => h.Id == g.HostId).Select(h => h.Name).FirstOrDefault(), g.TagName, g.TagValue, g.Level))
            .ToListAsync(cancellationToken);
    }

    /// <summary>Replaces the user's grants. Access is checked per request, so this applies at the user's next one.</summary>
    /// <returns>The new grants, or null when there's no such user.</returns>
    public async Task<List<HostGrantResponse>?> ReplaceAsync(Guid userId, IReadOnlyList<HostGrantRequest> requests, Guid actorId, CancellationToken cancellationToken)
    {
        if (!await db.Users.AnyAsync(u => u.Id == userId, cancellationToken))
        {
            return null;
        }

        var now = time.GetUtcNow();
        var grants = new List<HostAccessGrant>();
        for (var i = 0; i < requests.Count; i++)
        {
            grants.Add(Validated(requests[i], i, userId, actorId, now));
        }

        var hostIds = grants.Where(g => g.HostId is not null).Select(g => g.HostId!.Value).Distinct().ToList();
        if (await db.Hosts.CountAsync(h => hostIds.Contains(h.Id), cancellationToken) != hostIds.Count)
        {
            throw new InventoryValidationException("grants", "One or more hosts no longer exist. Reload and try again.");
        }

        // One grant per target; the higher level wins.
        grants = [.. grants
            .GroupBy(g => (g.HostId, g.TagName?.ToUpperInvariant(), g.TagValue?.ToUpperInvariant()))
            .Select(group => group.OrderByDescending(g => g.Level).First())];

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Replacements for one user run one at a time: otherwise two could each delete nothing and both
        // insert, leaving the union of what they asked for.
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM core.users WHERE id = {userId} FOR UPDATE", cancellationToken);
        await db.HostAccessGrants.Where(g => g.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        db.HostAccessGrants.AddRange(grants);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await audit.RecordAsync(new AuditEntry(AuditActions.UserGrantsReplace, userId.ToString(), $"{grants.Count} grants"), cancellationToken);
        return await GetAsync(userId, cancellationToken);
    }

    private static HostAccessGrant Validated(HostGrantRequest? request, int index, Guid userId, Guid actorId, DateTimeOffset now)
    {
        // JSON can carry nulls the element type doesn't allow.
        if (request is null)
        {
            throw new InventoryValidationException($"grants[{index}]", "Choose a host or a group.");
        }

        var tagName = string.IsNullOrWhiteSpace(request.TagName) ? null : request.TagName.Trim();
        var tagValue = string.IsNullOrWhiteSpace(request.TagValue) ? null : request.TagValue.Trim();
        if ((request.HostId is null) == (tagName is null))
        {
            throw new InventoryValidationException($"grants[{index}]", "Choose a host or a group, not both.");
        }

        if (tagName?.Length > HostTag.NameMaxLength || tagValue?.Length > HostTag.ValueMaxLength)
        {
            throw new InventoryValidationException($"grants[{index}]", "That tag name or value is too long.");
        }

        if (request.Level is not { } level || !Enum.IsDefined(level))
        {
            throw new InventoryValidationException($"grants[{index}].level", "Choose Use or Manage.");
        }

        return new HostAccessGrant
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            HostId = request.HostId,
            TagName = tagName,
            TagValue = tagName is null ? null : tagValue,
            Level = level,
            CreatedAtUtc = now,
            CreatedByUserId = actorId,
        };
    }
}
