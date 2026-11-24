using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NKeeper.Core.Access;
using NKeeper.Core.Audit;
using NKeeper.Core.Persistence;

namespace NKeeper.Core.Inventory;

/// <summary>A tag to take off: every value of the name, or only <see cref="Value"/>.</summary>
public sealed record BulkTagRemoval(string? Name, string? Value);

/// <summary>A service to take off: the type's service on that port, or every one of the type.</summary>
public sealed record BulkServiceRemoval(string? Type, int? Port);

/// <summary>A credential to take off: the account with that user name, in that domain when one is given.</summary>
public sealed record BulkCredentialRemoval(string? Username, string? Domain);

/// <summary>The host the selected hosts run on (§7.49); null to clear it.</summary>
public sealed record BulkHypervisorChange(Guid? HypervisorId);

/// <summary>
/// The same changes to several hosts (docs/architecture.md §7.50, from 1.x), applied to each in this order: <see cref="RemoveTags"/>,
/// <see cref="SetTags"/> (which replace every value of their names), <see cref="AddTags"/>, <see cref="Hypervisor"/>,
/// <see cref="RemoveServices"/>, <see cref="AddServices"/>, <see cref="RemoveCredentials"/>, then <see cref="AddCredentials"/>.
/// Everything else about each host stays as it is.
/// </summary>
public sealed class BulkHostRequest
{
    public const int MaxHosts = 1000;

    public List<Guid>? HostIds { get; set; }

    public List<BulkTagRemoval>? RemoveTags { get; set; }

    public List<HostTagDto>? SetTags { get; set; }

    public List<HostTagDto>? AddTags { get; set; }

    /// <summary>Null leaves the hosts as they are; set, they become virtual machines running on it, or on nothing known.</summary>
    public BulkHypervisorChange? Hypervisor { get; set; }

    public List<BulkServiceRemoval>? RemoveServices { get; set; }

    /// <summary>Services each host gets after its own; a host with the type on that port already keeps its own.</summary>
    public List<HostServiceRequest>? AddServices { get; set; }

    public List<BulkCredentialRemoval>? RemoveCredentials { get; set; }

    /// <summary>Credentials each host gets after its own, used with all its services; a host with the account already keeps its own.</summary>
    public List<HostCredentialRequest>? AddCredentials { get; set; }
}

public sealed partial class HostInventory
{
    /// <summary>
    /// Applies the same changes to every host named, as one change: each is checked as an edit of it would be, and
    /// if any can't take them, none changes. The user must be able to manage every one.
    /// </summary>
    /// <returns>The hosts as they are now, or null when one doesn't exist or the user can't see it.</returns>
    /// <exception cref="HostManageDeniedException">The user can see a host but not change it.</exception>
    public async Task<List<HostResponse>?> BulkUpdateAsync(BulkHostRequest request, RequestUser user, CancellationToken cancellationToken)
    {
        var ids = (request.HostIds ?? []).Distinct().ToList();
        if (ids.Count is 0 or > BulkHostRequest.MaxHosts)
        {
            throw new InventoryValidationException("hostIds", $"Select from 1 to {BulkHostRequest.MaxHosts} hosts.");
        }

        if ((request.RemoveTags?.Count ?? 0) + (request.SetTags?.Count ?? 0) + (request.AddTags?.Count ?? 0) + (request.RemoveServices?.Count ?? 0)
            + (request.AddServices?.Count ?? 0) + (request.RemoveCredentials?.Count ?? 0) + (request.AddCredentials?.Count ?? 0) == 0 && request.Hypervisor is null)
        {
            throw new InventoryValidationException("hostIds", "Choose a change to make.");
        }

        if (request.AddServices?.FindIndex(s => s?.Id is not null) is >= 0 and var service)
        {
            throw new InventoryValidationException($"addServices[{service}]", "A service to add can't name an existing one.");
        }

        if (request.AddCredentials?.FindIndex(c => c?.Id is not null) is >= 0 and var credential)
        {
            throw new InventoryValidationException($"addCredentials[{credential}]", "A credential to add can't name an existing one.");
        }

        foreach (var id in ids)
        {
            if (!await RequireManageAsync(id, user, cancellationToken))
            {
                return null;
            }
        }

        var hosts = await db.Hosts.Include(h => h.Tags).Include(h => h.Services).Include(h => h.Credentials).AsSplitQuery()
            .Where(h => ids.Contains(h.Id)).OrderBy(h => h.Name).ToListAsync(cancellationToken);
        if (hosts.Count != ids.Count)
        {
            return null;
        }

        var now = time.GetUtcNow();
        var changed = new List<Host>();
        foreach (var host in hosts)
        {
            var edit = Edited(host, request);
            if (edit is null)
            {
                continue;
            }

            try
            {
                Apply(host, edit, user.Id, now);
                await ApplyHypervisorAsync(host, edit, user, cancellationToken);
            }
            catch (InventoryValidationException e)
            {
                throw new InventoryValidationException(e.Field, $"“{host.Name}”: {e.Message}");
            }

            changed.Add(host);
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var host in changed)
        {
            await audit.RecordAsync(new AuditEntry(AuditActions.HostUpdate, host.Id.ToString(), host.Name), cancellationToken);
        }

        return [.. (await ListAsync(user, cancellationToken)).Where(h => ids.Contains(h.Id))];
    }

    /// <summary>The edit that makes the changes to the host, as the host editor would send it; null when they change nothing.</summary>
    private static HostRequest? Edited(Host host, BulkHostRequest request)
    {
        static bool Same(string? a, string? b) => string.Equals(a?.Trim() ?? string.Empty, b?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var tags = host.Tags.Select(t => new HostTagDto(t.Name, t.Value.Length == 0 ? null : t.Value)).ToList();
        foreach (var removal in request.RemoveTags ?? [])
        {
            tags.RemoveAll(t => Same(t.Name, removal?.Name) && (removal?.Value is null || Same(t.Value, removal.Value)));
        }

        foreach (var set in request.SetTags ?? [])
        {
            tags.RemoveAll(t => Same(t.Name, set?.Name));
            tags.Add(set!);
        }

        foreach (var add in request.AddTags ?? [])
        {
            if (!tags.Exists(t => Same(t.Name, add?.Name) && Same(t.Value, add?.Value)))
            {
                tags.Add(add!);
            }
        }

        var services = host.Services.OrderBy(s => s.Position).ToList();
        foreach (var removal in request.RemoveServices ?? [])
        {
            services.RemoveAll(s => s.Type == removal?.Type && (removal.Port is null || s.Port == removal.Port));
        }

        var serviceRequests = services
            .Select(s => new HostServiceRequest(s.Id, s.Type, s.Label, s.Port, s.Address, JsonDocument.Parse(s.Settings).RootElement.Clone(), s.DisabledTools))
            .ToList();
        foreach (var add in request.AddServices ?? [])
        {
            if (add is not null && !serviceRequests.Exists(s => s.Type == add.Type && s.Port == add.Port))
            {
                serviceRequests.Add(add);
            }
        }

        var credentials = host.Credentials.OrderBy(c => c.Position).ToList();
        foreach (var removal in request.RemoveCredentials ?? [])
        {
            credentials.RemoveAll(c => Same(c.Username, removal?.Username) && (removal?.Domain is null || Same(c.Domain, removal.Domain)));
        }

        var credentialRequests = credentials
            .Select(c => new HostCredentialRequest(
                c.Id, c.Label, c.Username, c.Domain, null, false, null, false, c.Notes,
                c.ServiceIds is { } scope ? [.. services.Select((s, index) => (s, index)).Where(p => scope.Contains(p.s.Id)).Select(p => p.index)] : null))
            .ToList();
        foreach (var add in request.AddCredentials ?? [])
        {
            if (!credentialRequests.Exists(c => Same(c.Username, add?.Username) && Same(c.Domain, add?.Domain)))
            {
                credentialRequests.Add(add! with { ServiceIndexes = null });
            }
        }

        var hypervisor = request.Hypervisor is { } change ? change.HypervisorId : host.HypervisorId;
        var edit = new HostRequest
        {
            Name = host.Name,
            Hostname = host.Hostname,
            IpAddress = host.IpAddress,
            Description = host.Description,
            MacAddress = host.MacAddress,
            CpuCores = host.CpuCores,
            MemoryMiB = host.MemoryMiB,
            SerialNumber = host.SerialNumber,
            IsVirtualMachine = host.IsVirtualMachine || request.Hypervisor is not null,
            HypervisorId = hypervisor,
            Tags = tags,
            Services = serviceRequests,
            Credentials = credentialRequests,
        };

        var unchanged = tags.Count == host.Tags.Count && tags.Zip(host.Tags).All(p => Same(p.First.Name, p.Second.Name) && Same(p.First.Value, p.Second.Value))
            && serviceRequests.Count == host.Services.Count && serviceRequests.TrueForAll(s => s.Id is not null)
            && credentialRequests.Count == host.Credentials.Count && credentialRequests.TrueForAll(c => c.Id is not null)
            && edit.IsVirtualMachine == host.IsVirtualMachine && hypervisor == host.HypervisorId;
        return unchanged ? null : edit;
    }
}
