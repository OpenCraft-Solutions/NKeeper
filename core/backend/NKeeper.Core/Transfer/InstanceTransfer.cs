using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NKeeper.Core.Credentials;
using NKeeper.Core.Persistence;

namespace NKeeper.Core.Transfer;

/// <summary>What an instance moves with (docs/architecture.md §7.45): everything but sessions, the audit log and the key ring.</summary>
public sealed record BundleData(
    IReadOnlyList<BundleUser> Users,
    IReadOnlyList<BundleGrant> Grants,
    IReadOnlyList<BundleHost> Hosts,
    IReadOnlyList<BundleIntegration> Integrations,
    IReadOnlyList<BundlePluginSettings> PluginSettings,
    IReadOnlyList<BundleHostData> HostData,
    IReadOnlyList<BundlePlugin> Plugins);

/// <summary>An account, with its password hash so its user signs in as before.</summary>
public sealed record BundleUser(
    Guid Id,
    string UserName,
    string? Email,
    string? PasswordHash,
    bool IsDisabled,
    bool MustChangePassword,
    string ColorScheme,
    string CustomColorSchemes,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<string> Roles);

public sealed record BundleGrant(Guid Id, Guid UserId, Guid? HostId, string? TagName, string? TagValue, HostAccessLevel Level, DateTimeOffset CreatedAtUtc, Guid? CreatedByUserId);

public sealed record BundleHost(
    Guid Id,
    string Name,
    string? Hostname,
    string? IpAddress,
    string? Description,
    string? MacAddress,
    int? CpuCores,
    int? MemoryMiB,
    string? SerialNumber,
    bool IsVirtualMachine,
    Guid? JumpHostId,
    Guid? JumpServiceId,
    Guid? JumpCredentialId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<BundleTag> Tags,
    IReadOnlyList<BundleService> Services,
    IReadOnlyList<BundleCredential> Credentials,
    IReadOnlyList<BundleTrustedKey> TrustedKeys);

public sealed record BundleTag(Guid Id, string Name, string Value);

public sealed record BundleService(Guid Id, int Position, string Type, string? Label, int? Port, string? Address, string Settings, int SettingsVersion, IReadOnlyList<string> DisabledTools);

/// <summary>A credential, its secrets decrypted: the bundle's own encryption protects them, and the import encrypts them again.</summary>
public sealed record BundleCredential(Guid Id, int Position, string? Label, string? Username, string? Domain, string? Password, string? PrivateKey, string? Notes, IReadOnlyList<Guid>? ServiceIds);

public sealed record BundleTrustedKey(Guid Id, TrustedKeyKind Kind, string Address, int Port, string Algorithm, string Fingerprint, DateTimeOffset TrustedAtUtc);

public sealed record BundleIntegration(string PluginId, string Name, Guid HostId, Guid ServiceId, Guid CredentialId, DateTimeOffset SetAtUtc);

public sealed record BundlePluginSettings(string PluginId, int Version, string Data, DateTimeOffset UpdatedAtUtc);

public sealed record BundleHostData(string PluginId, Guid HostId, int Version, string Data, DateTimeOffset UpdatedAtUtc);

/// <summary>A plugin the instance had, so the import can say which this server lacks. Packages don't move.</summary>
public sealed record BundlePlugin(string Id, string Version, bool Enabled);

/// <summary>What a file holds, and what this server lacks for it.</summary>
public sealed record ImportPreview(
    DateTimeOffset ExportedAtUtc,
    string ExportedBy,
    string NKeeperVersion,
    int Users,
    int Hosts,
    int Credentials,
    IReadOnlyList<string> MissingPlugins,
    IReadOnlyList<string> Warnings);

/// <summary>Why an import can't go ahead: this instance isn't fresh, or the file is from a newer server.</summary>
public sealed class ImportRefusedException(string message) : Exception(message);

/// <summary>
/// Exports this instance into a bundle, and imports one into a fresh instance (docs/architecture.md §7.45), ported
/// from 1.x's <c>InstanceExporter</c> and <c>InstanceImporter</c>.
/// </summary>
public sealed class InstanceTransfer(CoreDbContext db, CredentialProtector protector, TimeProvider time)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Everything this instance holds, as the bundle's plaintext. The secrets are decrypted here: write it only into a bundle.</summary>
    public async Task<byte[]> ExportAsync(CancellationToken cancellationToken)
    {
        var roles = await db.Roles.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Name!, cancellationToken);
        var userRoles = await db.UserRoles.AsNoTracking().ToListAsync(cancellationToken);
        var users = await db.Users.AsNoTracking().OrderBy(u => u.UserName).ToListAsync(cancellationToken);
        var hosts = await db.Hosts.AsNoTracking()
            .Include(h => h.Tags).Include(h => h.Services).Include(h => h.Credentials).Include(h => h.TrustedKeys)
            .AsSplitQuery().OrderBy(h => h.Name).ToListAsync(cancellationToken);

        var data = new BundleData(
            [.. users.Select(u => new BundleUser(
                u.Id, u.UserName!, u.Email, u.PasswordHash, u.IsDisabled, u.MustChangePassword, u.ColorScheme, u.CustomColorSchemes, u.CreatedAtUtc,
                [.. userRoles.Where(r => r.UserId == u.Id).Select(r => roles.GetValueOrDefault(r.RoleId)).OfType<string>().Order(StringComparer.Ordinal)]))],
            [.. (await db.HostAccessGrants.AsNoTracking().ToListAsync(cancellationToken)).Select(g =>
                new BundleGrant(g.Id, g.UserId, g.HostId, g.TagName, g.TagValue, g.Level, g.CreatedAtUtc, g.CreatedByUserId))],
            [.. hosts.Select(Export)],
            [.. (await db.PluginIntegrations.AsNoTracking().ToListAsync(cancellationToken)).Select(b =>
                new BundleIntegration(b.PluginId, b.Name, b.HostId, b.ServiceId, b.CredentialId, b.SetAtUtc))],
            [.. (await db.PluginSettings.AsNoTracking().ToListAsync(cancellationToken)).Select(s => new BundlePluginSettings(s.PluginId, s.Version, s.Data, s.UpdatedAtUtc))],
            [.. (await db.HostData.AsNoTracking().ToListAsync(cancellationToken)).Select(d => new BundleHostData(d.PluginId, d.HostId, d.Version, d.Data, d.UpdatedAtUtc))],
            [.. (await db.InstalledPlugins.AsNoTracking().OrderBy(p => p.Id).ToListAsync(cancellationToken)).Select(p => new BundlePlugin(p.Id, p.Version, p.Enabled))]);
        return JsonSerializer.SerializeToUtf8Bytes(data, Json);
    }

    /// <summary>The core schema's latest migration here: a bundle must not come from a newer one.</summary>
    public string LatestMigration() => db.Database.GetMigrations().LastOrDefault() ?? string.Empty;

    /// <summary>Reads a bundle's plaintext, and says what it holds and what this server lacks for it.</summary>
    /// <exception cref="BundleException">The plaintext isn't a bundle's data.</exception>
    public async Task<(BundleData Data, ImportPreview Preview)> PreviewAsync(BundleHeader header, byte[] plaintext, CancellationToken cancellationToken)
    {
        BundleData? data;
        try
        {
            data = JsonSerializer.Deserialize<BundleData>(plaintext, Json);
        }
        catch (JsonException)
        {
            data = null;
        }

        if (data is null || data.Users is null || data.Hosts is null)
        {
            throw new BundleException("The file decrypted, but what's in it isn't an instance's data.");
        }

        var installed = await db.InstalledPlugins.AsNoTracking().Select(p => p.Id).ToListAsync(cancellationToken);
        var missing = (data.Plugins ?? []).Where(p => p.Enabled && !installed.Contains(p.Id)).Select(p => $"{p.Id} {p.Version}").ToList();
        var warnings = new List<string>();
        if (!db.Database.GetMigrations().Contains(header.Migration))
        {
            warnings.Add("The file is from a newer NKeeper than this server: update this server first.");
        }

        if (missing.Count > 0)
        {
            warnings.Add($"The old server had plugins this one doesn't: {string.Join(", ", missing)}. Their hosts' services and data come across, and work once they're installed.");
        }

        return (data, new ImportPreview(
            header.ExportedAtUtc, header.ExportedBy, header.NKeeperVersion,
            data.Users.Count, data.Hosts.Count, data.Hosts.Sum(h => h.Credentials?.Count ?? 0), missing, warnings));
    }

    /// <summary>
    /// Replaces this fresh instance's accounts, inventory and plugin data with the bundle's, in one transaction,
    /// encrypting every secret again with this server's key ring. Every session ends, the importer's included.
    /// </summary>
    /// <exception cref="ImportRefusedException">This instance has hosts or other users, or the file is from a newer core schema.</exception>
    public async Task ImportAsync(BundleHeader header, BundleData data, Guid importerId, CancellationToken cancellationToken)
    {
        if (!db.Database.GetMigrations().Contains(header.Migration))
        {
            throw new ImportRefusedException("The file is from a newer NKeeper than this server. Update this server, then import it.");
        }

        if (await db.Hosts.AnyAsync(cancellationToken) || await db.Users.AnyAsync(u => u.Id != importerId, cancellationToken))
        {
            throw new ImportRefusedException("This instance already has hosts or other users. Import into a fresh NKeeper, where you're the only user.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Out with this instance's own: its sessions, the importer's account and its grants.
        await db.AuthSessions.ExecuteDeleteAsync(cancellationToken);
        await db.HostAccessGrants.ExecuteDeleteAsync(cancellationToken);
        await db.PluginIntegrations.ExecuteDeleteAsync(cancellationToken);
        await db.PluginSettings.ExecuteDeleteAsync(cancellationToken);
        await db.UserRoles.ExecuteDeleteAsync(cancellationToken);
        await db.Users.ExecuteDeleteAsync(cancellationToken);

        var roles = await db.Roles.ToListAsync(cancellationToken);
        foreach (var name in data.Users.SelectMany(u => u.Roles).Distinct(StringComparer.Ordinal).Where(n => !roles.Exists(r => r.Name == n)))
        {
            var role = new ApplicationRole(name) { NormalizedName = name.ToUpperInvariant() };
            db.Roles.Add(role);
            roles.Add(role);
        }

        foreach (var u in data.Users)
        {
            db.Users.Add(new ApplicationUser
            {
                Id = u.Id,
                UserName = u.UserName,
                NormalizedUserName = u.UserName.ToUpperInvariant(),
                Email = u.Email,
                NormalizedEmail = u.Email?.ToUpperInvariant(),
                EmailConfirmed = true,
                PasswordHash = u.PasswordHash,
                // A new stamp: nothing signed for the old server's accounts is valid here.
                SecurityStamp = Guid.NewGuid().ToString("N"),
                ConcurrencyStamp = Guid.NewGuid().ToString("N"),
                LockoutEnabled = true,
                IsDisabled = u.IsDisabled,
                MustChangePassword = u.MustChangePassword,
                ColorScheme = u.ColorScheme,
                CustomColorSchemes = u.CustomColorSchemes,
                CreatedAtUtc = u.CreatedAtUtc,
            });
            foreach (var name in u.Roles)
            {
                db.UserRoles.Add(new Microsoft.AspNetCore.Identity.IdentityUserRole<Guid> { UserId = u.Id, RoleId = roles.First(r => r.Name == name).Id });
            }
        }

        // Hosts first, then the jump hosts they name, since a jump host is another host in the file.
        foreach (var h in data.Hosts)
        {
            db.Hosts.Add(Import(h, time.GetUtcNow()));
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var h in data.Hosts.Where(h => h.JumpHostId is not null))
        {
            await db.Hosts.Where(x => x.Id == h.Id).ExecuteUpdateAsync(
                set => set.SetProperty(x => x.JumpHostId, h.JumpHostId).SetProperty(x => x.JumpServiceId, h.JumpServiceId).SetProperty(x => x.JumpCredentialId, h.JumpCredentialId),
                cancellationToken);
        }

        foreach (var g in data.Grants ?? [])
        {
            db.HostAccessGrants.Add(new HostAccessGrant
            {
                Id = g.Id,
                UserId = g.UserId,
                HostId = g.HostId,
                TagName = g.TagName,
                TagValue = g.TagValue,
                Level = g.Level,
                CreatedAtUtc = g.CreatedAtUtc,
                CreatedByUserId = g.CreatedByUserId,
            });
        }

        foreach (var b in data.Integrations ?? [])
        {
            db.PluginIntegrations.Add(new PluginIntegrationBinding { PluginId = b.PluginId, Name = b.Name, HostId = b.HostId, ServiceId = b.ServiceId, CredentialId = b.CredentialId, SetAtUtc = b.SetAtUtc });
        }

        foreach (var s in data.PluginSettings ?? [])
        {
            db.PluginSettings.Add(new PluginSettingsRow { PluginId = s.PluginId, Version = s.Version, Data = s.Data, UpdatedAtUtc = s.UpdatedAtUtc });
        }

        foreach (var d in data.HostData ?? [])
        {
            db.HostData.Add(new HostDataRow { PluginId = d.PluginId, HostId = d.HostId, Version = d.Version, Data = d.Data, UpdatedAtUtc = d.UpdatedAtUtc });
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private BundleHost Export(Host h) => new(
        h.Id, h.Name, h.Hostname, h.IpAddress, h.Description, h.MacAddress, h.CpuCores, h.MemoryMiB, h.SerialNumber, h.IsVirtualMachine,
        h.JumpHostId, h.JumpServiceId, h.JumpCredentialId, h.CreatedAtUtc, h.UpdatedAtUtc,
        [.. h.Tags.Select(t => new BundleTag(t.Id, t.Name, t.Value))],
        [.. h.Services.OrderBy(s => s.Position).Select(s => new BundleService(s.Id, s.Position, s.Type, s.Label, s.Port, s.Address, s.Settings, s.SettingsVersion, s.DisabledTools))],
        [.. h.Credentials.OrderBy(c => c.Position).Select(c => new BundleCredential(
            c.Id, c.Position, c.Label, c.Username, c.Domain, Reveal(c.Id, c.ProtectedPassword), Reveal(c.Id, c.ProtectedPrivateKey), c.Notes, c.ServiceIds))],
        [.. h.TrustedKeys.Select(k => new BundleTrustedKey(k.Id, k.Kind, k.Address, k.Port, k.Algorithm, k.Fingerprint, k.TrustedAtUtc))]);

    private Host Import(BundleHost h, DateTimeOffset now) => new()
    {
        Id = h.Id,
        Name = h.Name,
        Hostname = h.Hostname,
        IpAddress = h.IpAddress,
        Description = h.Description,
        MacAddress = h.MacAddress,
        CpuCores = h.CpuCores,
        MemoryMiB = h.MemoryMiB,
        SerialNumber = h.SerialNumber,
        IsVirtualMachine = h.IsVirtualMachine,
        CreatedAtUtc = h.CreatedAtUtc,
        UpdatedAtUtc = now,
        Tags = [.. h.Tags.Select(t => new HostTag { Id = t.Id, HostId = h.Id, Name = t.Name, Value = t.Value })],
        Services = [.. h.Services.Select(s => new HostService
        {
            Id = s.Id, HostId = h.Id, Position = s.Position, Type = s.Type, Label = s.Label, Port = s.Port, Address = s.Address, Settings = s.Settings, SettingsVersion = s.SettingsVersion, DisabledTools = [.. s.DisabledTools],
        })],
        Credentials = [.. h.Credentials.Select(c => new HostCredential
        {
            Id = c.Id,
            HostId = h.Id,
            Position = c.Position,
            Label = c.Label,
            Username = c.Username,
            Domain = c.Domain,
            // Encrypted again, with this server's key ring.
            ProtectedPassword = string.IsNullOrEmpty(c.Password) ? null : protector.Protect(c.Id, c.Password),
            ProtectedPrivateKey = string.IsNullOrEmpty(c.PrivateKey) ? null : protector.Protect(c.Id, c.PrivateKey),
            Notes = c.Notes,
            ServiceIds = c.ServiceIds is null ? null : [.. c.ServiceIds],
        })],
        TrustedKeys = [.. h.TrustedKeys.Select(k => new HostTrustedKey
        {
            Id = k.Id, HostId = h.Id, Kind = k.Kind, Address = k.Address, Port = k.Port, Algorithm = k.Algorithm, Fingerprint = k.Fingerprint, TrustedAtUtc = k.TrustedAtUtc,
        })],
    };

    private string? Reveal(Guid credentialId, byte[]? protectedSecret)
    {
        if (protectedSecret is null)
        {
            return null;
        }

        using var secret = protector.Unprotect(credentialId, protectedSecret);
        return secret.Reveal();
    }
}
