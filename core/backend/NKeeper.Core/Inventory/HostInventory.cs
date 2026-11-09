using Microsoft.EntityFrameworkCore;
using NKeeper.Core.Access;
using NKeeper.Core.Audit;
using NKeeper.Core.Persistence;

namespace NKeeper.Core.Inventory;

public sealed record HostTagDto(string Name, string? Value);

public sealed record HostResponse(
    Guid Id,
    string Name,
    string? Hostname,
    string? IpAddress,
    string? Description,
    IReadOnlyList<HostTagDto> Tags,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    uint Version,
    bool CanManage);

/// <summary>A host to create, or the new state of one to update (with the <see cref="Version"/> it was based on).</summary>
public sealed class HostRequest
{
    public string? Name { get; set; }

    public string? Hostname { get; set; }

    public string? IpAddress { get; set; }

    public string? Description { get; set; }

    public List<HostTagDto>? Tags { get; set; }

    /// <summary>Required to update: the version the edit is based on.</summary>
    public uint? Version { get; set; }
}

/// <summary>A request that breaks a rule, reported against one field.</summary>
public sealed class InventoryValidationException(string field, string message) : Exception(message)
{
    public string Field { get; } = field;
}

/// <summary>The user can see the host, but not change it (403).</summary>
public sealed class HostManageDeniedException() : Exception("You can use this host but not change it. Ask an administrator for Manage access.");

/// <summary>
/// Hosts and their tags (docs/architecture.md §7.1). Every read goes through <see cref="HostAccess"/>,
/// so a host the user can't see is indistinguishable from one that doesn't exist. Ported from 1.x.
/// </summary>
public sealed class HostInventory(CoreDbContext db, HostAccess access, IAuditLog audit, TimeProvider time)
{
    /// <summary>
    /// Every host the user can see, by name. The shell searches, groups and sorts them itself, which
    /// keeps the tree instant for hundreds to a few thousand hosts.
    /// </summary>
    public async Task<List<HostResponse>> ListAsync(RequestUser user, CancellationToken cancellationToken)
    {
        var hosts = await access.Visible(user).AsNoTracking().Include(h => h.Tags)
#pragma warning disable CA1304, CA1311 // Runs as SQL: upper().
            .OrderBy(h => h.Name.ToUpper()).ThenBy(h => h.Id)
#pragma warning restore CA1304, CA1311
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
        var manageable = await access.ManageableIdsAsync(user, cancellationToken);
        return [.. hosts.Select(h => ToResponse(h, manageable?.Contains(h.Id) ?? true))];
    }

    /// <returns>The host, or null when it doesn't exist or the user can't see it.</returns>
    public async Task<HostResponse?> GetAsync(Guid id, RequestUser user, CancellationToken cancellationToken)
    {
        var host = await access.Visible(user).AsNoTracking().Include(h => h.Tags).FirstOrDefaultAsync(h => h.Id == id, cancellationToken);
        return host is null
            ? null
            : ToResponse(host, await access.GetPermissionAsync(id, user, cancellationToken) == HostPermission.Manage);
    }

    /// <summary>Creates a host. Only administrators may; the endpoint enforces it.</summary>
    public async Task<HostResponse> CreateAsync(HostRequest request, RequestUser user, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var host = new Host { Id = Guid.NewGuid(), CreatedAtUtc = now, CreatedByUserId = user.Id };
        Apply(host, request, user.Id, now);
        db.Hosts.Add(host);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(AuditActions.HostCreate, host.Id.ToString(), host.Name), cancellationToken);
        return (await GetAsync(host.Id, user, cancellationToken))!;
    }

    /// <returns>The updated host, or null when it doesn't exist or the user can't see it.</returns>
    /// <exception cref="DbUpdateConcurrencyException">The host changed since the request's version.</exception>
    /// <exception cref="HostManageDeniedException">The user can see the host but not change it.</exception>
    public async Task<HostResponse?> UpdateAsync(Guid id, HostRequest request, RequestUser user, CancellationToken cancellationToken)
    {
        if (!await RequireManageAsync(id, user, cancellationToken))
        {
            return null;
        }

        var version = request.Version ?? throw new InventoryValidationException("version", "The version of the host being edited is required.");
        var host = await db.Hosts.Include(h => h.Tags).FirstOrDefaultAsync(h => h.Id == id, cancellationToken);
        if (host is null)
        {
            return null;
        }

        db.Entry(host).Property(h => h.Version).OriginalValue = version;
        Apply(host, request, user.Id, time.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(AuditActions.HostUpdate, id.ToString(), host.Name), cancellationToken);

        // New tags can take the host out of the user's own grants; then it's simply no longer visible.
        return await GetAsync(id, user, cancellationToken);
    }

    /// <summary>Deletes the host; its tags and its host grants go with it.</summary>
    /// <returns>False when the host doesn't exist or the user can't see it.</returns>
    public async Task<bool> DeleteAsync(Guid id, RequestUser user, CancellationToken cancellationToken)
    {
        if (!await RequireManageAsync(id, user, cancellationToken))
        {
            return false;
        }

        var name = await db.Hosts.Where(h => h.Id == id).Select(h => h.Name).FirstOrDefaultAsync(cancellationToken);
        if (await db.Hosts.Where(h => h.Id == id).ExecuteDeleteAsync(cancellationToken) == 0)
        {
            return false;
        }

        await audit.RecordAsync(new AuditEntry(AuditActions.HostDelete, id.ToString(), name ?? string.Empty), cancellationToken);
        return true;
    }

    /// <returns>False when the host isn't visible; throws when it's visible but can't be changed.</returns>
    private async Task<bool> RequireManageAsync(Guid id, RequestUser user, CancellationToken cancellationToken) =>
        await access.GetPermissionAsync(id, user, cancellationToken) switch
        {
            HostPermission.None => false,
            HostPermission.Manage => true,
            _ => throw new HostManageDeniedException(),
        };

    private static void Apply(Host host, HostRequest request, Guid userId, DateTimeOffset now)
    {
        var name = TrimToNull(request.Name) ?? throw new InventoryValidationException("name", "Enter a name.");
        if (name.Length > Host.NameMaxLength)
        {
            throw new InventoryValidationException("name", $"Use at most {Host.NameMaxLength} characters.");
        }

        var hostname = TrimToNull(request.Hostname);
        if (hostname is not null && (hostname.Length > Host.HostnameMaxLength || !NetworkAddress.IsValidHostname(hostname)))
        {
            throw new InventoryValidationException("hostname", "Enter a valid host name, such as server.example.com.");
        }

        string? ip = null;
        if (TrimToNull(request.IpAddress) is { } address && !NetworkAddress.TryNormalizeIp(address, out ip))
        {
            throw new InventoryValidationException("ipAddress", "Enter a valid IPv4 or IPv6 address.");
        }

        if (hostname is null && ip is null)
        {
            throw new InventoryValidationException("hostname", "Enter a host name, an IP address, or both.");
        }

        var description = TrimToNull(request.Description);
        if (description?.Length > Host.DescriptionMaxLength)
        {
            throw new InventoryValidationException("description", $"Use at most {Host.DescriptionMaxLength} characters.");
        }

        host.Name = name;
        host.Hostname = hostname;
        host.IpAddress = string.IsNullOrEmpty(ip) ? null : ip;
        host.Description = description;
        ApplyTags(host, request.Tags ?? []);
        // Every edit updates the host row, so its version changes even when only its tags did.
        host.UpdatedAtUtc = now;
        host.UpdatedByUserId = userId;
    }

    /// <summary>Replaces the tags, dropping duplicates that differ only in case and keeping the first spelling.</summary>
    private static void ApplyTags(Host host, List<HostTagDto> tags)
    {
        var requested = new List<(string Name, string Value)>();
        for (var i = 0; i < tags.Count; i++)
        {
            var name = tags[i].Name?.Trim() ?? string.Empty;
            var value = tags[i].Value?.Trim() ?? string.Empty;
            if (name.Length == 0)
            {
                throw new InventoryValidationException($"tags[{i}].name", "Enter a tag name.");
            }

            if (name.Length > HostTag.NameMaxLength || name.Contains(':', StringComparison.Ordinal))
            {
                throw new InventoryValidationException($"tags[{i}].name", $"Tag names are at most {HostTag.NameMaxLength} characters, without a colon.");
            }

            if (value.Length > HostTag.ValueMaxLength)
            {
                throw new InventoryValidationException($"tags[{i}].value", $"Tag values are at most {HostTag.ValueMaxLength} characters.");
            }

            if (!requested.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase) && string.Equals(t.Value, value, StringComparison.OrdinalIgnoreCase)))
            {
                requested.Add((name, value));
            }
        }

        if (requested.Count > Host.MaxTags)
        {
            throw new InventoryValidationException("tags", $"A host can have at most {Host.MaxTags} tags.");
        }

        // Unchanged rows stay, so their IDs are stable; the rest are removed and added.
        host.Tags.RemoveAll(existing => !requested.Contains((existing.Name, existing.Value)));
        foreach (var (name, value) in requested.Where(t => !host.Tags.Exists(e => e.Name == t.Name && e.Value == t.Value)))
        {
            // No preset key: EF Core treats a tracked entity with one as existing, and would update a row that isn't there.
            host.Tags.Add(new HostTag { Name = name, Value = value });
        }
    }

    private static HostResponse ToResponse(Host host, bool canManage) =>
        new(
            host.Id,
            host.Name,
            host.Hostname,
            host.IpAddress,
            host.Description,
            [.. host.Tags
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Value, StringComparer.OrdinalIgnoreCase)
                .Select(t => new HostTagDto(t.Name, t.Value.Length == 0 ? null : t.Value))],
            host.CreatedAtUtc,
            host.UpdatedAtUtc,
            host.Version,
            canManage);

    private static string? TrimToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
