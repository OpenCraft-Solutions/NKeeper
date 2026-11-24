using Microsoft.EntityFrameworkCore;
using NKeeper.Core.Access;
using NKeeper.Core.Audit;
using NKeeper.Core.Credentials;
using NKeeper.Core.Persistence;

namespace NKeeper.Core.Inventory;

public sealed record HostMergeRequest(IReadOnlyList<Guid>? SourceIds);

/// <summary>
/// Combines duplicate hosts into one (docs/architecture.md §7.46), ported from 1.x's <c>HostMerger</c>: the kept host
/// gets what the others have that it lacks, everything that pointed at the others points at it, and the others are
/// deleted. In one transaction; audited.
/// </summary>
public sealed class HostMerger(CoreDbContext db, HostAccess access, HostInventory hosts, CredentialProtector protector, IAuditLog audit, TimeProvider time)
{
    public const int MaxSources = 50;

    /// <returns>The kept host, combined; null when one of them doesn't exist or the user can't see it.</returns>
    /// <exception cref="InventoryValidationException">No other hosts, too many, or the kept one among them.</exception>
    /// <exception cref="HostManageDeniedException">The user can see the hosts but not change one of them.</exception>
    public async Task<HostResponse?> MergeAsync(Guid targetId, IReadOnlyList<Guid> sourceIds, RequestUser user, CancellationToken cancellationToken)
    {
        var sources = sourceIds.Distinct().ToList();
        if (sources.Count == 0 || sources.Contains(targetId))
        {
            throw new InventoryValidationException("sourceIds", "Choose the other hosts to combine into this one.");
        }

        if (sources.Count > MaxSources)
        {
            throw new InventoryValidationException("sourceIds", $"Combine at most {MaxSources} hosts at a time.");
        }

        var ids = sources.Append(targetId).ToList();
        if (await access.Visible(user).CountAsync(h => ids.Contains(h.Id), cancellationToken) != ids.Count)
        {
            return null;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var loaded = await access.Manageable(user).Where(h => ids.Contains(h.Id))
            .Include(h => h.Services).Include(h => h.Tags).Include(h => h.Credentials).Include(h => h.TrustedKeys)
            .AsSplitQuery().ToListAsync(cancellationToken);
        if (loaded.Count != ids.Count)
        {
            throw new HostManageDeniedException();
        }

        var target = loaded.Single(h => h.Id == targetId);
        var removed = sources.Select(id => loaded.Single(h => h.Id == id)).ToList();
        var serviceMap = new Dictionary<Guid, Guid>();
        var credentialMap = new Dictionary<Guid, Guid>();
        foreach (var source in removed)
        {
            FillDetails(target, source, sources);
            MergeTags(target, source);
            MergeServices(target, source, serviceMap);
            MergeCredentials(target, source, serviceMap, credentialMap);
            MergeTrustedKeys(target, source);
        }

        // A host can't be reached through itself; a jump credential of a removed host is now the kept one's copy.
        if (target.JumpHostId == target.Id)
        {
            (target.JumpHostId, target.JumpServiceId, target.JumpCredentialId) = (null, null, null);
        }
        else
        {
            target.JumpServiceId = Mapped(serviceMap, target.JumpServiceId);
            target.JumpCredentialId = Mapped(credentialMap, target.JumpCredentialId);
        }

        target.UpdatedAtUtc = time.GetUtcNow();
        target.UpdatedByUserId = user.Id;

        // What pointed at the removed hosts points at the kept one.
        foreach (var guest in await db.Hosts.Where(h => h.HypervisorId != null && sources.Contains(h.HypervisorId.Value) && !ids.Contains(h.Id)).ToListAsync(cancellationToken))
        {
            guest.HypervisorId = target.Id;
        }

        // Running on itself, on a removed host, or on something that now runs on it (§7.49): its host is unknown.
        if (target.HypervisorId is { } runsOn && (runsOn == target.Id || sources.Contains(runsOn) || await RunsOnAsync(runsOn, target.Id, cancellationToken)))
        {
            target.HypervisorId = null;
        }

        foreach (var reached in await db.Hosts.Where(h => h.JumpHostId != null && sources.Contains(h.JumpHostId.Value) && !ids.Contains(h.Id)).ToListAsync(cancellationToken))
        {
            reached.JumpHostId = target.Id;
            reached.JumpServiceId = Mapped(serviceMap, reached.JumpServiceId);
            reached.JumpCredentialId = Mapped(credentialMap, reached.JumpCredentialId);
        }

        var grants = await db.HostAccessGrants.Where(g => g.HostId != null && ids.Contains(g.HostId.Value)).ToListAsync(cancellationToken);
        foreach (var byUser in grants.GroupBy(g => g.UserId))
        {
            // One grant per user for the kept host, at the highest level any of them had.
            var keep = byUser.FirstOrDefault(g => g.HostId == target.Id) ?? byUser.First();
            keep.HostId = target.Id;
            keep.Level = byUser.Max(g => g.Level);
            db.HostAccessGrants.RemoveRange(byUser.Where(g => g != keep));
        }

        // Integrations follow their service and credential; plugins' host data moves unless the kept host has its own.
        foreach (var binding in await db.PluginIntegrations.Where(b => sources.Contains(b.HostId)).ToListAsync(cancellationToken))
        {
            if (serviceMap.TryGetValue(binding.ServiceId, out var service) && credentialMap.TryGetValue(binding.CredentialId, out var credential))
            {
                (binding.HostId, binding.ServiceId, binding.CredentialId) = (target.Id, service, credential);
            }
        }

        var data = await db.HostData.Where(d => ids.Contains(d.HostId)).ToListAsync(cancellationToken);
        foreach (var row in data.Where(d => d.HostId != target.Id))
        {
            if (!data.Exists(d => d.HostId == target.Id && d.PluginId == row.PluginId))
            {
                db.HostData.Add(new HostDataRow { PluginId = row.PluginId, HostId = target.Id, Version = row.Version, Data = row.Data, UpdatedAtUtc = row.UpdatedAtUtc });
                data.Add(new HostDataRow { PluginId = row.PluginId, HostId = target.Id });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await db.Hosts.Where(h => sources.Contains(h.Id)).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(AuditActions.HostMerge, target.Id.ToString(), $"{string.Join(", ", removed.Select(h => h.Name))} → {target.Name}"), cancellationToken);
        return await hosts.GetAsync(target.Id, user, cancellationToken);
    }

    /// <summary>Whether <paramref name="host"/> runs on <paramref name="hypervisor"/>, however indirectly, counting links not saved yet.</summary>
    private async Task<bool> RunsOnAsync(Guid host, Guid hypervisor, CancellationToken cancellationToken)
    {
        var links = await db.Hosts.AsNoTracking().Where(h => h.HypervisorId != null).Select(h => new { h.Id, h.HypervisorId }).ToDictionaryAsync(h => h.Id, h => h.HypervisorId!.Value, cancellationToken);
        foreach (var entry in db.ChangeTracker.Entries<Host>())
        {
            if (entry.Entity.HypervisorId is { } changed)
            {
                links[entry.Entity.Id] = changed;
            }
            else
            {
                links.Remove(entry.Entity.Id);
            }
        }

        var seen = new HashSet<Guid>();
        for (Guid? current = host; current is { } next && seen.Add(next); current = links.TryGetValue(next, out var parent) ? parent : null)
        {
            if (next == hypervisor)
            {
                return true;
            }
        }

        return false;
    }

    private static Guid? Mapped(Dictionary<Guid, Guid> map, Guid? id) => id is { } value && map.TryGetValue(value, out var mapped) ? mapped : id;

    private static void FillDetails(Host target, Host source, List<Guid> sources)
    {
        target.Hostname ??= source.Hostname;
        target.IpAddress ??= source.IpAddress;
        target.MacAddress ??= source.MacAddress;
        target.Description ??= source.Description;
        target.CpuCores ??= source.CpuCores;
        target.MemoryMiB ??= source.MemoryMiB;
        target.SerialNumber ??= source.SerialNumber;
        target.IsVirtualMachine |= source.IsVirtualMachine;
        if (target.HypervisorId is null && source.HypervisorId is { } hypervisor && !sources.Contains(hypervisor))
        {
            target.HypervisorId = hypervisor;
        }

        if (target.JumpHostId is null && source.JumpHostId is { } jump && !sources.Contains(jump))
        {
            (target.JumpHostId, target.JumpServiceId, target.JumpCredentialId) = (jump, source.JumpServiceId, source.JumpCredentialId);
        }
    }

    private void MergeTags(Host target, Host source)
    {
        foreach (var tag in source.Tags)
        {
            if (!target.Tags.Exists(t => t.Name.Equals(tag.Name, StringComparison.OrdinalIgnoreCase) && t.Value.Equals(tag.Value, StringComparison.OrdinalIgnoreCase)))
            {
                // Added through its table, so it's inserted (its ID is set here), and fixed up into target.Tags.
                db.HostTags.Add(new HostTag { Id = Guid.NewGuid(), HostId = target.Id, Name = tag.Name, Value = tag.Value });
            }
        }
    }

    private static string ServiceKey(HostService s) => $"{s.Type}|{s.Port}|{s.Address?.ToUpperInvariant()}";

    private void MergeServices(Host target, Host source, Dictionary<Guid, Guid> serviceMap)
    {
        foreach (var service in source.Services.OrderBy(s => s.Position))
        {
            if (target.Services.Find(s => ServiceKey(s) == ServiceKey(service)) is { } same)
            {
                serviceMap[service.Id] = same.Id;
                continue;
            }

            var copy = new HostService
            {
                Id = Guid.NewGuid(),
                HostId = target.Id,
                Type = service.Type,
                Port = service.Port,
                Label = service.Label,
                Address = service.Address,
                Settings = service.Settings,
                SettingsVersion = service.SettingsVersion,
                DisabledTools = [.. service.DisabledTools],
                Position = target.Services.Count == 0 ? 0 : target.Services.Max(s => s.Position) + 1,
            };
            target.Services.Add(copy);
            db.Entry(copy).State = EntityState.Added;
            serviceMap[service.Id] = copy.Id;
        }
    }

    private static bool SameAccount(HostCredential a, HostCredential b) =>
        string.IsNullOrWhiteSpace(a.Username) && string.IsNullOrWhiteSpace(b.Username)
            ? string.Equals(a.Label?.Trim(), b.Label?.Trim(), StringComparison.OrdinalIgnoreCase)
            : string.Equals(a.Username?.Trim(), b.Username?.Trim(), StringComparison.OrdinalIgnoreCase)
              && string.Equals(a.Domain?.Trim() ?? string.Empty, b.Domain?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private void MergeCredentials(Host target, Host source, Dictionary<Guid, Guid> serviceMap, Dictionary<Guid, Guid> credentialMap)
    {
        List<Guid>? MapServices(List<Guid>? serviceIds) =>
            serviceIds is null ? null : [.. serviceIds.Where(serviceMap.ContainsKey).Select(id => serviceMap[id]).Distinct()];

        foreach (var credential in source.Credentials.OrderBy(c => c.Position))
        {
            if (target.Credentials.Find(c => SameAccount(c, credential)) is { } same)
            {
                same.ProtectedPassword ??= Rebind(credential.Id, same.Id, credential.ProtectedPassword);
                same.ProtectedPrivateKey ??= Rebind(credential.Id, same.Id, credential.ProtectedPrivateKey);
                same.Notes ??= credential.Notes;
                same.Label ??= credential.Label;
                // Used with what either was used with; with every service when either was.
                same.ServiceIds = same.ServiceIds is null || credential.ServiceIds is null ? null : [.. same.ServiceIds.Union(MapServices(credential.ServiceIds)!)];
                credentialMap[credential.Id] = same.Id;
                continue;
            }

            var id = Guid.NewGuid();
            var copy = new HostCredential
            {
                Id = id,
                HostId = target.Id,
                Label = credential.Label,
                Username = credential.Username,
                Domain = credential.Domain,
                ProtectedPassword = Rebind(credential.Id, id, credential.ProtectedPassword),
                ProtectedPrivateKey = Rebind(credential.Id, id, credential.ProtectedPrivateKey),
                Notes = credential.Notes,
                Position = target.Credentials.Count == 0 ? 0 : target.Credentials.Max(c => c.Position) + 1,
                ServiceIds = MapServices(credential.ServiceIds),
            };
            target.Credentials.Add(copy);
            db.Entry(copy).State = EntityState.Added;
            credentialMap[credential.Id] = id;
        }
    }

    /// <summary>A secret is bound to its credential (§7.5): decrypted for the old one and encrypted again for the new one.</summary>
    private byte[]? Rebind(Guid from, Guid to, byte[]? protectedSecret)
    {
        if (protectedSecret is null)
        {
            return null;
        }

        using var secret = protector.Unprotect(from, protectedSecret);
        return protector.Protect(to, secret.Reveal());
    }

    /// <summary>Keys and certificates are trusted for an address: they only come from a duplicate at the same one.</summary>
    private void MergeTrustedKeys(Host target, Host source)
    {
        if ((target.Hostname, target.IpAddress) != (source.Hostname, source.IpAddress))
        {
            return;
        }

        foreach (var key in source.TrustedKeys)
        {
            if (!target.TrustedKeys.Exists(k => k.Kind == key.Kind && k.Address == key.Address && k.Port == key.Port && k.Algorithm == key.Algorithm))
            {
                db.HostTrustedKeys.Add(new HostTrustedKey
                {
                    Id = Guid.NewGuid(),
                    HostId = target.Id,
                    Kind = key.Kind,
                    Address = key.Address,
                    Port = key.Port,
                    Algorithm = key.Algorithm,
                    Fingerprint = key.Fingerprint,
                    TrustedAtUtc = key.TrustedAtUtc,
                    TrustedByUserId = key.TrustedByUserId,
                });
            }
        }
    }
}
