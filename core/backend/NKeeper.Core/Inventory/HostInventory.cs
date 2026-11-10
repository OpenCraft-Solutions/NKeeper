using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NKeeper.Core.Access;
using NKeeper.Core.Audit;
using NKeeper.Core.Persistence;

namespace NKeeper.Core.Inventory;

public sealed record HostTagDto(string Name, string? Value);

/// <summary>A service as sent: with the <see cref="Id"/> of one the host has, to keep it, or without, to add one.</summary>
public sealed record HostServiceRequest(Guid? Id, string? Type, string? Label, int? Port, string? Address, JsonElement? Settings);

/// <param name="Port">As set; null for the type's default.</param>
/// <param name="Address">As set; null for the host's own.</param>
/// <param name="Available">Whether an enabled plugin provides the type. When none does, the settings are as stored.</param>
public sealed record HostServiceResponse(Guid Id, string Type, string? Label, int? Port, string? Address, JsonElement Settings, bool Available);

public sealed record HostResponse(
    Guid Id,
    string Name,
    string? Hostname,
    string? IpAddress,
    string? Description,
    IReadOnlyList<HostTagDto> Tags,
    IReadOnlyList<HostServiceResponse> Services,
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

    /// <summary>The host's services, in order. Replaces the list: services left out are removed.</summary>
    public List<HostServiceRequest>? Services { get; set; }

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
/// Hosts, their tags and their services (docs/architecture.md §7.1, §7.4). Every read goes through
/// <see cref="HostAccess"/>, so a host the user can't see is indistinguishable from one that doesn't exist.
/// Ported from 1.x.
/// </summary>
public sealed class HostInventory(CoreDbContext db, HostAccess access, ServiceTypeCatalog serviceTypes, IAuditLog audit, TimeProvider time)
{
    /// <summary>
    /// Every host the user can see, by name. The shell searches, groups and sorts them itself, which
    /// keeps the tree instant for hundreds to a few thousand hosts.
    /// </summary>
    public async Task<List<HostResponse>> ListAsync(RequestUser user, CancellationToken cancellationToken)
    {
        var hosts = await access.Visible(user).AsNoTracking().Include(h => h.Tags).Include(h => h.Services)
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
        var host = await access.Visible(user).AsNoTracking().Include(h => h.Tags).Include(h => h.Services).AsSplitQuery()
            .FirstOrDefaultAsync(h => h.Id == id, cancellationToken);
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
        var host = await db.Hosts.Include(h => h.Tags).Include(h => h.Services).AsSplitQuery().FirstOrDefaultAsync(h => h.Id == id, cancellationToken);
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

    private void Apply(Host host, HostRequest request, Guid userId, DateTimeOffset now)
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
        ApplyServices(host, request.Services ?? []);

        // Every edit updates the host row, so its version changes even when only its tags or services did.
        host.UpdatedAtUtc = now;
        host.UpdatedByUserId = userId;
    }

    /// <summary>Replaces the tags, dropping duplicates that differ only in case and keeping the first spelling.</summary>
    private static void ApplyTags(Host host, List<HostTagDto> tags)
    {
        var requested = new List<(string Name, string Value)>();

        // Names can't hold a colon, so "name:value" identifies a tag; case is ignored.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < tags.Count; i++)
        {
            // JSON can carry nulls the element type doesn't allow; a null tag has no name.
            var name = tags[i]?.Name?.Trim() ?? string.Empty;
            var value = tags[i]?.Value?.Trim() ?? string.Empty;
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

            if (seen.Add($"{name}:{value}"))
            {
                if (requested.Count == Host.MaxTags)
                {
                    throw new InventoryValidationException("tags", $"A host can have at most {Host.MaxTags} tags.");
                }

                requested.Add((name, value));
            }
        }

        // Unchanged rows stay, so their IDs are stable; the rest are removed and added.
        host.Tags.RemoveAll(existing => !requested.Contains((existing.Name, existing.Value)));
        foreach (var (name, value) in requested.Where(t => !host.Tags.Exists(e => e.Name == t.Name && e.Value == t.Value)))
        {
            // No preset key: EF Core treats a tracked entity with one as existing, and would update a row that isn't there.
            host.Tags.Add(new HostTag { Name = name, Value = value });
        }
    }

    /// <summary>
    /// Replaces the services (§7.4). One sent with its ID keeps it, and keeps its ID; one of a type no
    /// enabled plugin provides may only be kept as it is.
    /// </summary>
    private void ApplyServices(Host host, List<HostServiceRequest> services)
    {
        if (services.Count > HostService.MaxPerHost)
        {
            throw new InventoryValidationException("services", $"A host can have at most {HostService.MaxPerHost} services.");
        }

        var existing = host.Services.ToDictionary(s => s.Id);
        var requested = new List<HostService>();
        for (var i = 0; i < services.Count; i++)
        {
            var field = $"services[{i}]";

            // JSON can carry nulls the element type doesn't allow.
            var request = services[i] ?? throw new InventoryValidationException($"{field}.type", "Choose a service type.");
            HostService? service = null;
            if (request.Id is { } id && (!existing.Remove(id, out service)))
            {
                throw new InventoryValidationException($"{field}.id", "This service isn't on the host any more. Reload and try again.");
            }

            var typeId = request.Type?.Trim() ?? string.Empty;

            // A stored service of an unavailable type is kept only as it is, whatever type the request names.
            if (service is not null && serviceTypes.Find(service.Type) is null)
            {
                if (service.Type != typeId || !Unchanged(service, request))
                {
                    throw new InventoryValidationException(field, "No enabled plugin provides this service's type, so it can only be kept as it is, or removed.");
                }

                service.Position = i;
                requested.Add(service);
                continue;
            }

            if (serviceTypes.Find(typeId) is not { } type)
            {
                throw new InventoryValidationException($"{field}.type", typeId.Length == 0 ? "Choose a service type." : $"No enabled plugin provides the service type '{typeId}'.");
            }

            var label = TrimToNull(request.Label);
            if (label?.Length > HostService.LabelMaxLength)
            {
                throw new InventoryValidationException($"{field}.label", $"Use at most {HostService.LabelMaxLength} characters.");
            }

            if (request.Port is < 1 or > 65535)
            {
                throw new InventoryValidationException($"{field}.port", "Enter a port from 1 to 65535.");
            }

            var address = TrimToNull(request.Address);
            if (address is not null)
            {
                if (NetworkAddress.TryNormalizeIp(address, out var ip))
                {
                    address = ip;
                }
                else if (address.Length > Host.HostnameMaxLength || !NetworkAddress.IsValidHostname(address))
                {
                    throw new InventoryValidationException($"{field}.address", "Enter a valid host name or IP address.");
                }
            }

            var settings = ServiceTypeCatalog.Validate(type, request.Settings, $"{field}.settings");

            // No preset key for a new one: EF Core treats a tracked entity with one as existing.
            service ??= new HostService();
            service.Position = i;
            service.Type = type.Id;
            service.Label = label;
            service.Port = request.Port;
            service.Address = address;
            service.Settings = settings.ToJsonString();
            service.SettingsVersion = type.SettingsVersion;
            requested.Add(service);
        }

        host.Services.RemoveAll(s => !requested.Contains(s));
        host.Services.AddRange(requested.Where(s => !host.Services.Contains(s)));
    }

    /// <summary>Whether a request leaves a stored service as it is: the same label, port, address and settings.</summary>
    private static bool Unchanged(HostService service, HostServiceRequest request) =>
        service.Label == TrimToNull(request.Label)
        && service.Port == request.Port
        && service.Address == TrimToNull(request.Address)
        && JsonNode.DeepEquals(
            JsonNode.Parse(service.Settings),
            request.Settings is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } settings ? JsonNode.Parse(settings.GetRawText()) : new JsonObject());

    private HostResponse ToResponse(Host host, bool canManage) =>
        new(
            host.Id,
            host.Name,
            host.Hostname,
            host.IpAddress,
            host.Description,
            [.. host.Tags
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Value, StringComparer.OrdinalIgnoreCase)
                .Select(t => new HostTagDto(t.Name, t.Value.Length == 0 ? null : t.Value))],
            [.. host.Services.OrderBy(s => s.Position).Select(s =>
            {
                var (settings, type) = serviceTypes.Read(s);
                return new HostServiceResponse(s.Id, s.Type, s.Label, s.Port, s.Address, settings, type is not null);
            })],
            host.CreatedAtUtc,
            host.UpdatedAtUtc,
            host.Version,
            canManage);

    private static string? TrimToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
