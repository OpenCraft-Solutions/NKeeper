using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NKeeper.Core.Access;
using NKeeper.Core.Audit;
using NKeeper.Core.Credentials;
using NKeeper.Core.Persistence;
using NKeeper.Core.Trust;

namespace NKeeper.Core.Inventory;

public sealed record HostTagDto(string Name, string? Value);

/// <summary>A service as sent: with the <see cref="Id"/> of one the host has, to keep it, or without, to add one.</summary>
public sealed record HostServiceRequest(Guid? Id, string? Type, string? Label, int? Port, string? Address, JsonElement? Settings);

/// <param name="Port">As set; null for the type's default.</param>
/// <param name="Address">As set; null for the host's own.</param>
/// <param name="Available">Whether an enabled plugin provides the type. When none does, the settings are as stored.</param>
public sealed record HostServiceResponse(Guid Id, string Type, string? Label, int? Port, string? Address, JsonElement Settings, bool Available);

/// <summary>
/// A credential as sent (docs/architecture.md §7.5): with the <see cref="Id"/> of one the host has, to keep it,
/// or without, to add one. Secrets are write-only: leave them out to keep them, or set the Clear flag.
/// </summary>
/// <param name="ServiceIndexes">Positions in the same request's services; null for all of them, empty for none.</param>
public sealed record HostCredentialRequest(
    Guid? Id,
    string? Label,
    string? Username,
    string? Domain,
    string? Password,
    bool ClearPassword,
    string? PrivateKey,
    bool ClearPrivateKey,
    string? Notes,
    List<int>? ServiceIndexes);

/// <param name="ServiceIndexes">Positions in the response's services; null for all of them.</param>
public sealed record HostCredentialResponse(
    Guid Id,
    string? Label,
    string? Username,
    string? Domain,
    bool HasPassword,
    bool HasPrivateKey,
    string? Notes,
    IReadOnlyList<int>? ServiceIndexes);

/// <summary>The host one is reached through (docs/architecture.md §7.8), with the service and credential to reach it with.</summary>
/// <param name="ServiceId">Null in a response once the jump host's service was removed.</param>
/// <param name="CredentialId">Null in a response once the jump host's credential was removed.</param>
public sealed record HostJumpDto(Guid HostId, Guid? ServiceId, Guid? CredentialId);

/// <summary>A server identity trusted for the host (docs/architecture.md §7.7).</summary>
/// <param name="Address">The service's own address it was trusted for; null for the host itself.</param>
public sealed record HostTrustedKeyResponse(Guid Id, string Kind, string? Address, int Port, string Algorithm, string Fingerprint, DateTimeOffset TrustedAtUtc);

public sealed record HostResponse(
    Guid Id,
    string Name,
    string? Hostname,
    string? IpAddress,
    string? Description,
    IReadOnlyList<HostTagDto> Tags,
    IReadOnlyList<HostServiceResponse> Services,
    IReadOnlyList<HostCredentialResponse> Credentials,
    HostJumpDto? Jump,
    IReadOnlyList<HostTrustedKeyResponse> TrustedKeys,
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

    /// <summary>The host's credentials, in order. Replaces the list: credentials left out are deleted.</summary>
    public List<HostCredentialRequest>? Credentials { get; set; }

    /// <summary>The host to reach this one through; null for none. Its service and credential are required.</summary>
    public HostJumpDto? Jump { get; set; }

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
public sealed class HostInventory(CoreDbContext db, HostAccess access, ServiceTypeCatalog serviceTypes, CredentialProtector protector, IAuditLog audit, TimeProvider time)
{
    /// <summary>
    /// Every host the user can see, by name. The shell searches, groups and sorts them itself, which
    /// keeps the tree instant for hundreds to a few thousand hosts.
    /// </summary>
    public async Task<List<HostResponse>> ListAsync(RequestUser user, CancellationToken cancellationToken)
    {
        var hosts = await access.Visible(user).AsNoTracking().Include(h => h.Tags).Include(h => h.Services).Include(h => h.Credentials).Include(h => h.TrustedKeys)
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
        var host = await access.Visible(user).AsNoTracking().Include(h => h.Tags).Include(h => h.Services).Include(h => h.Credentials).Include(h => h.TrustedKeys).AsSplitQuery()
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
        await ApplyJumpAsync(host, request.Jump, user, cancellationToken);
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
        var host = await db.Hosts.Include(h => h.Tags).Include(h => h.Services).Include(h => h.Credentials).AsSplitQuery().FirstOrDefaultAsync(h => h.Id == id, cancellationToken);
        if (host is null)
        {
            return null;
        }

        db.Entry(host).Property(h => h.Version).OriginalValue = version;
        Apply(host, request, user.Id, time.GetUtcNow());
        await ApplyJumpAsync(host, request.Jump, user, cancellationToken);
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

    /// <summary>Forgets a trusted key (§7.7), so the next connection asks again.</summary>
    /// <returns>False when the host or the key doesn't exist, or the user can't see the host.</returns>
    /// <exception cref="HostManageDeniedException">The user can see the host but not change it.</exception>
    public async Task<bool> RemoveTrustedKeyAsync(Guid hostId, Guid keyId, RequestUser user, CancellationToken cancellationToken)
    {
        if (!await RequireManageAsync(hostId, user, cancellationToken))
        {
            return false;
        }

        var key = await db.HostTrustedKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Id == keyId && k.HostId == hostId, cancellationToken);
        if (key is null || await db.HostTrustedKeys.Where(k => k.Id == keyId && k.HostId == hostId).ExecuteDeleteAsync(cancellationToken) == 0)
        {
            return false;
        }

        await audit.RecordAsync(
            new AuditEntry(AuditActions.TrustRemove, hostId.ToString(), $"{key.Kind} {key.Algorithm} SHA256:{key.Fingerprint} at {Endpoint(key.Address)}:{key.Port}"),
            cancellationToken);
        return true;
    }

    /// <summary>How an audit detail names a trusted key's endpoint.</summary>
    internal static string Endpoint(string address) => address.Length == 0 ? "the host" : address;

    /// <summary>
    /// Sets or clears the jump host (§7.8). A jump that's sent unchanged isn't checked again, so an edit by
    /// someone who can't see the jump host, or after its service or credential was removed, keeps it.
    /// </summary>
    private async Task ApplyJumpAsync(Host host, HostJumpDto? jump, RequestUser user, CancellationToken cancellationToken)
    {
        if (jump is null)
        {
            host.JumpHostId = host.JumpServiceId = host.JumpCredentialId = null;
            return;
        }

        if (jump.HostId == host.JumpHostId && jump.ServiceId == host.JumpServiceId && jump.CredentialId == host.JumpCredentialId)
        {
            return;
        }

        if (jump.HostId == host.Id)
        {
            throw new InventoryValidationException("jump.hostId", "A host can't be its own jump host.");
        }

        var target = await access.Visible(user).AsNoTracking().Include(h => h.Services).Include(h => h.Credentials).AsSplitQuery()
            .FirstOrDefaultAsync(h => h.Id == jump.HostId, cancellationToken)
            ?? throw new InventoryValidationException("jump.hostId", "Choose a jump host you can use.");
        if (target.JumpHostId is not null)
        {
            throw new InventoryValidationException("jump.hostId", $"“{target.Name}” is reached through a jump host itself, and jump hosts can't be chained.");
        }

        if (await db.Hosts.AnyAsync(h => h.JumpHostId == host.Id, cancellationToken))
        {
            throw new InventoryValidationException("jump.hostId", "Other hosts are reached through this one, so it can't have a jump host itself.");
        }

        var service = target.Services.FirstOrDefault(s => s.Id == jump.ServiceId)
            ?? throw new InventoryValidationException("jump.serviceId", $"Choose one of “{target.Name}”'s services to reach it on.");
        var credential = target.Credentials.FirstOrDefault(c => c.Id == jump.CredentialId)
            ?? throw new InventoryValidationException("jump.credentialId", $"Choose one of “{target.Name}”'s credentials to sign in to it with.");
        if (credential.ServiceIds is { } scope && !scope.Contains(service.Id))
        {
            throw new InventoryValidationException("jump.credentialId", "This credential isn't used with that service. Edit the jump host to use it with that service too.");
        }

        host.JumpHostId = target.Id;
        host.JumpServiceId = service.Id;
        host.JumpCredentialId = credential.Id;
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
        var services = ApplyServices(host, request.Services ?? []);
        ApplyCredentials(host, request.Credentials ?? [], services);

        // Every edit updates the host row, so its version changes even when only its tags, services or credentials did.
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
    /// <returns>The host's services, in the request's order.</returns>
    private List<HostService> ApplyServices(Host host, List<HostServiceRequest> services)
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

            // The key is set here, not generated, so credentials in this edit can refer to a new service.
            service ??= new HostService { Id = Guid.NewGuid() };
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
        return requested;
    }

    /// <summary>
    /// Replaces the credentials (§7.5). One sent with its ID keeps it, and its stored secrets unless the request
    /// replaces or clears them. <paramref name="services"/> resolves each one's service indexes.
    /// </summary>
    private void ApplyCredentials(Host host, List<HostCredentialRequest> credentials, List<HostService> services)
    {
        if (credentials.Count > HostCredential.MaxPerHost)
        {
            throw new InventoryValidationException("credentials", $"A host can have at most {HostCredential.MaxPerHost} credentials.");
        }

        var existing = host.Credentials.ToDictionary(c => c.Id);
        var requested = new List<HostCredential>();
        for (var i = 0; i < credentials.Count; i++)
        {
            var field = $"credentials[{i}]";

            // JSON can carry nulls the element type doesn't allow.
            var request = credentials[i] ?? throw new InventoryValidationException(field, "This credential is empty.");
            HostCredential? credential = null;
            if (request.Id is { } id && !existing.Remove(id, out credential))
            {
                throw new InventoryValidationException($"{field}.id", "This credential isn't on the host any more. Reload and try again.");
            }

            var label = Limited(request.Label, HostCredential.LabelMaxLength, $"{field}.label");
            var username = Limited(request.Username, HostCredential.UsernameMaxLength, $"{field}.username");
            var domain = Limited(request.Domain, HostCredential.DomainMaxLength, $"{field}.domain");
            var notes = Limited(request.Notes, HostCredential.NotesMaxLength, $"{field}.notes");

            // Secrets aren't trimmed: spaces can be part of a password.
            var password = string.IsNullOrEmpty(request.Password) ? null : request.Password;
            if (password?.Length > HostCredential.PasswordMaxLength)
            {
                throw new InventoryValidationException($"{field}.password", $"Use at most {HostCredential.PasswordMaxLength} characters.");
            }

            var privateKey = string.IsNullOrWhiteSpace(request.PrivateKey) ? null : request.PrivateKey.Trim();
            if (privateKey is not null && (privateKey.Length > HostCredential.PrivateKeyMaxLength || !privateKey.StartsWith("-----BEGIN ", StringComparison.Ordinal)))
            {
                throw new InventoryValidationException($"{field}.privateKey", $"Paste the private key in PEM or OpenSSH form, at most {HostCredential.PrivateKeyMaxLength} characters.");
            }

            if ((password is not null && request.ClearPassword) || (privateKey is not null && request.ClearPrivateKey))
            {
                throw new InventoryValidationException(field, "Either replace a secret or clear it, not both.");
            }

            List<Guid>? serviceIds = null;
            if (request.ServiceIndexes is { } indexes)
            {
                if (indexes.Any(index => index < 0 || index >= services.Count) || indexes.Distinct().Count() != indexes.Count)
                {
                    throw new InventoryValidationException($"{field}.serviceIndexes", "Choose services of this host, each once.");
                }

                serviceIds = [.. indexes.Select(index => services[index].Id)];
            }

            // The key is set here, not generated: the secrets are encrypted bound to it.
            credential ??= new HostCredential { Id = Guid.NewGuid() };
            credential.Position = i;
            credential.Label = label;
            credential.Username = username;
            credential.Domain = domain;
            credential.Notes = notes;
            credential.ServiceIds = serviceIds;
            if (request.ClearPassword)
            {
                credential.ProtectedPassword = null;
            }
            else if (password is not null)
            {
                credential.ProtectedPassword = protector.Protect(credential.Id, password);
            }

            if (request.ClearPrivateKey)
            {
                credential.ProtectedPrivateKey = null;
            }
            else if (privateKey is not null)
            {
                credential.ProtectedPrivateKey = protector.Protect(credential.Id, privateKey);
            }

            requested.Add(credential);
        }

        host.Credentials.RemoveAll(c => !requested.Contains(c));
        host.Credentials.AddRange(requested.Where(c => !host.Credentials.Contains(c)));
    }

    private static string? Limited(string? value, int maxLength, string field)
    {
        var trimmed = TrimToNull(value);
        return trimmed?.Length > maxLength
            ? throw new InventoryValidationException(field, $"Use at most {maxLength} characters.")
            : trimmed;
    }

    private static HostCredentialResponse ToResponse(HostCredential credential, List<HostService> services) =>
        new(
            credential.Id,
            credential.Label,
            credential.Username,
            credential.Domain,
            credential.ProtectedPassword is not null,
            credential.ProtectedPrivateKey is not null,
            credential.Notes,

            // IDs of services removed since are ignored; a scope left with none stays empty, not "all".
            credential.ServiceIds is { } ids
                ? [.. services.Select((s, index) => (s, index)).Where(p => ids.Contains(p.s.Id)).Select(p => p.index)]
                : null);

    /// <summary>Whether a request leaves a stored service as it is: the same label, port, address and settings.</summary>
    private static bool Unchanged(HostService service, HostServiceRequest request) =>
        service.Label == TrimToNull(request.Label)
        && service.Port == request.Port
        && service.Address == TrimToNull(request.Address)
        && JsonNode.DeepEquals(
            JsonNode.Parse(service.Settings),
            request.Settings is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } settings ? JsonNode.Parse(settings.GetRawText()) : new JsonObject());

    private HostResponse ToResponse(Host host, bool canManage)
    {
        var services = host.Services.OrderBy(s => s.Position).ToList();
        return new(
            host.Id,
            host.Name,
            host.Hostname,
            host.IpAddress,
            host.Description,
            [.. host.Tags
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Value, StringComparer.OrdinalIgnoreCase)
                .Select(t => new HostTagDto(t.Name, t.Value.Length == 0 ? null : t.Value))],
            [.. services.Select(s =>
            {
                var (settings, type) = serviceTypes.Read(s);
                return new HostServiceResponse(s.Id, s.Type, s.Label, s.Port, s.Address, settings, type is not null);
            })],
            [.. host.Credentials.OrderBy(c => c.Position).Select(c => ToResponse(c, services))],
            host.JumpHostId is { } jumpHostId ? new HostJumpDto(jumpHostId, host.JumpServiceId, host.JumpCredentialId) : null,
            [.. host.TrustedKeys
                .OrderBy(k => k.Address, StringComparer.Ordinal).ThenBy(k => k.Port).ThenBy(k => k.Algorithm, StringComparer.Ordinal)
                .Select(k => new HostTrustedKeyResponse(
                    k.Id, TrustStore.KindName(k.Kind), k.Address.Length == 0 ? null : k.Address, k.Port, k.Algorithm, k.Fingerprint, k.TrustedAtUtc))],
            host.CreatedAtUtc,
            host.UpdatedAtUtc,
            host.Version,
            canManage);
    }

    private static string? TrimToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
