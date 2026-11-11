using System.Text;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NKeeper.Core.Persistence;

/// <summary>A plugin core has seen: its pin and enabled state (docs/plugin-security.md §8).</summary>
public sealed class InstalledPlugin
{
    public string Id { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string PackageSha256 { get; set; } = string.Empty;

    public string KeyId { get; set; } = string.Empty;

    public bool BuiltIn { get; set; }

    public bool Enabled { get; set; }

    public string? EnabledBy { get; set; }

    public DateTimeOffset? EnabledAtUtc { get; set; }

    public DateTimeOffset FirstSeenUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>One audit event: plugin events, sign-ins, and from Phase 1 on everything core records.</summary>
public sealed class AuditEvent
{
    public long Id { get; set; }

    public DateTimeOffset OccurredAtUtc { get; set; }

    /// <summary>Who did it: a user name, the name as typed for a failed sign-in, or <c>system</c> for the server itself.</summary>
    public string Actor { get; set; } = string.Empty;

    /// <summary>The account that did it, when there is one.</summary>
    public Guid? UserId { get; set; }

    /// <summary>The client's address, for events caused by a request.</summary>
    public string? ClientAddress { get; set; }

    /// <summary>What happened, e.g. <c>plugin.refused</c>.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>What it happened to, e.g. a plugin ID.</summary>
    public string Target { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;
}

/// <summary>
/// The core schema: plugin records, the audit log, accounts and sessions, and the Data Protection
/// keys. Plugins get their own contexts and schemas later; they never see this one.
/// </summary>
public sealed class CoreDbContext(DbContextOptions<CoreDbContext> options)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options), IDataProtectionKeyContext
{
    public const string Schema = "core";

    public DbSet<InstalledPlugin> InstalledPlugins => Set<InstalledPlugin>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public DbSet<AuthSession> AuthSessions => Set<AuthSession>();

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public DbSet<Host> Hosts => Set<Host>();

    public DbSet<HostTag> HostTags => Set<HostTag>();

    public DbSet<HostAccessGrant> HostAccessGrants => Set<HostAccessGrant>();

    public DbSet<HostService> HostServices => Set<HostService>();

    public DbSet<HostCredential> HostCredentials => Set<HostCredential>();

    public DbSet<HostTrustedKey> HostTrustedKeys => Set<HostTrustedKey>();

    /// <summary>Configures Npgsql for the core context, with its migration history inside the core schema.</summary>
    public static DbContextOptions<CoreDbContext> CreateOptions(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<CoreDbContext>();
        Configure(builder, connectionString);
        return builder.Options;
    }

    /// <summary>The same configuration, for <c>AddDbContext</c>.</summary>
    public static void Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", Schema));

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(Schema);

        builder.Entity<InstalledPlugin>(plugin =>
        {
            plugin.ToTable("installed_plugins");
            plugin.HasKey(p => p.Id);
            plugin.Property(p => p.Id).HasColumnName("id").HasMaxLength(100);
            plugin.Property(p => p.Version).HasColumnName("version").HasMaxLength(128);
            plugin.Property(p => p.PackageSha256).HasColumnName("package_sha256").HasMaxLength(64).IsFixedLength();
            plugin.Property(p => p.KeyId).HasColumnName("key_id").HasMaxLength(32).IsFixedLength();
            plugin.Property(p => p.BuiltIn).HasColumnName("built_in");
            plugin.Property(p => p.Enabled).HasColumnName("enabled");
            plugin.Property(p => p.EnabledBy).HasColumnName("enabled_by").HasMaxLength(256);
            plugin.Property(p => p.EnabledAtUtc).HasColumnName("enabled_at_utc");
            plugin.Property(p => p.FirstSeenUtc).HasColumnName("first_seen_utc");
            plugin.Property(p => p.UpdatedAtUtc).HasColumnName("updated_at_utc");
        });

        builder.Entity<AuditEvent>(audit =>
        {
            audit.ToTable("audit_events");
            audit.HasKey(a => a.Id);
            audit.Property(a => a.Id).HasColumnName("id").UseIdentityAlwaysColumn();
            audit.Property(a => a.OccurredAtUtc).HasColumnName("occurred_at_utc");
            audit.Property(a => a.Actor).HasColumnName("actor").HasMaxLength(256);
            audit.Property(a => a.Action).HasColumnName("action").HasMaxLength(64);
            audit.Property(a => a.Target).HasColumnName("target").HasMaxLength(256);
            audit.Property(a => a.Detail).HasColumnName("detail");
            audit.Property(a => a.ClientAddress).HasMaxLength(64);
            audit.HasIndex(a => a.OccurredAtUtc);
            audit.HasIndex(a => new { a.Action, a.OccurredAtUtc });
            audit.HasIndex(a => new { a.UserId, a.OccurredAtUtc });
        });

        builder.Entity<ApplicationUser>(user =>
        {
            user.ToTable("users");
            user.Property(u => u.UserName).HasMaxLength(256);
            user.Property(u => u.ColorScheme).HasMaxLength(64).HasDefaultValue("navy");
            user.Property(u => u.CustomColorSchemes).HasDefaultValue("[]");
        });
        builder.Entity<ApplicationRole>().ToTable("roles");
        builder.Entity<IdentityUserRole<Guid>>().ToTable("user_roles");
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");
        builder.Entity<IdentityRoleClaim<Guid>>().ToTable("role_claims");
        if (builder.Model.FindEntityType(typeof(IdentityUserPasskey<Guid>)) is not null)
        {
            builder.Entity<IdentityUserPasskey<Guid>>().ToTable("user_passkeys");
        }

        builder.Entity<AuthSession>(session =>
        {
            session.ToTable("auth_sessions");
            session.HasKey(s => s.Id);
            session.HasOne<ApplicationUser>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
            session.HasIndex(s => s.UserId);
            session.HasIndex(s => s.ExpiresAtUtc);
        });

        builder.Entity<DataProtectionKey>().ToTable("data_protection_keys");

        builder.Entity<Host>(host =>
        {
            host.ToTable("hosts");
            host.HasKey(h => h.Id);
            host.Property(h => h.Name).HasMaxLength(Host.NameMaxLength);
            host.Property(h => h.Hostname).HasMaxLength(Host.HostnameMaxLength);
            host.Property(h => h.IpAddress).HasMaxLength(Host.IpAddressMaxLength);
            host.Property(h => h.Description).HasMaxLength(Host.DescriptionMaxLength);
            host.Property(h => h.Version).IsRowVersion().HasColumnName("xmin");
            host.HasMany(h => h.Tags).WithOne().HasForeignKey(t => t.HostId).OnDelete(DeleteBehavior.Cascade);
            host.HasMany(h => h.Services).WithOne().HasForeignKey(s => s.HostId).OnDelete(DeleteBehavior.Cascade);
            host.HasMany(h => h.Credentials).WithOne().HasForeignKey(c => c.HostId).OnDelete(DeleteBehavior.Cascade);
            host.HasMany(h => h.TrustedKeys).WithOne().HasForeignKey(k => k.HostId).OnDelete(DeleteBehavior.Cascade);
            host.HasIndex(h => h.Name);

            // Deleting the jump host, or its service or credential, clears the reference to it (§7.8).
            host.HasOne<Host>().WithMany().HasForeignKey(h => h.JumpHostId).OnDelete(DeleteBehavior.SetNull);
            host.HasOne<HostService>().WithMany().HasForeignKey(h => h.JumpServiceId).OnDelete(DeleteBehavior.SetNull);
            host.HasOne<HostCredential>().WithMany().HasForeignKey(h => h.JumpCredentialId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<HostTrustedKey>(key =>
        {
            key.ToTable("host_trusted_keys");
            key.HasKey(k => k.Id);
            key.Property(k => k.Kind).HasConversion<string>().HasMaxLength(32);
            key.Property(k => k.Address).HasMaxLength(Host.HostnameMaxLength);
            key.Property(k => k.Algorithm).HasMaxLength(HostTrustedKey.AlgorithmMaxLength);
            key.Property(k => k.Fingerprint).HasMaxLength(HostTrustedKey.FingerprintMaxLength);
            key.HasOne<ApplicationUser>().WithMany().HasForeignKey(k => k.TrustedByUserId).OnDelete(DeleteBehavior.SetNull);
            key.HasIndex(k => new { k.HostId, k.Kind, k.Address, k.Port, k.Algorithm }).IsUnique();
        });

        builder.Entity<HostService>(service =>
        {
            service.ToTable("host_services");
            service.HasKey(s => s.Id);

            // Set by the inventory before saving, so credentials in the same edit can refer to new services.
            service.Property(s => s.Id).ValueGeneratedNever();
            service.Property(s => s.Type).HasMaxLength(HostService.TypeMaxLength);
            service.Property(s => s.Label).HasMaxLength(HostService.LabelMaxLength);
            service.Property(s => s.Address).HasMaxLength(Host.HostnameMaxLength);
            service.Property(s => s.Settings).HasColumnType("jsonb");
            service.HasIndex(s => s.Type);
        });

        builder.Entity<HostCredential>(credential =>
        {
            credential.ToTable("host_credentials");
            credential.HasKey(c => c.Id);

            // Set by the inventory before saving: the secrets are encrypted bound to it.
            credential.Property(c => c.Id).ValueGeneratedNever();
            credential.Property(c => c.Label).HasMaxLength(HostCredential.LabelMaxLength);
            credential.Property(c => c.Username).HasMaxLength(HostCredential.UsernameMaxLength);
            credential.Property(c => c.Domain).HasMaxLength(HostCredential.DomainMaxLength);
            credential.Property(c => c.Notes).HasMaxLength(HostCredential.NotesMaxLength);
        });

        builder.Entity<HostTag>(tag =>
        {
            tag.ToTable("host_tags");
            tag.HasKey(t => t.Id);
            tag.Property(t => t.Name).HasMaxLength(HostTag.NameMaxLength);
            tag.Property(t => t.Value).HasMaxLength(HostTag.ValueMaxLength);
            tag.HasIndex(t => new { t.HostId, t.Name, t.Value }).IsUnique();
            tag.HasIndex(t => t.Name);
        });

        builder.Entity<HostAccessGrant>(grant =>
        {
            grant.ToTable("host_access_grants");
            grant.HasKey(g => g.Id);
            grant.Property(g => g.TagName).HasMaxLength(HostTag.NameMaxLength);
            grant.Property(g => g.TagValue).HasMaxLength(HostTag.ValueMaxLength);
            grant.Property(g => g.Level).HasConversion<string>().HasMaxLength(16);
            grant.HasOne<ApplicationUser>().WithMany().HasForeignKey(g => g.UserId).OnDelete(DeleteBehavior.Cascade);
            grant.HasOne<Host>().WithMany().HasForeignKey(g => g.HostId).OnDelete(DeleteBehavior.Cascade);
            grant.HasIndex(g => g.UserId);
            grant.ToTable(t => t.HasCheckConstraint("ck_host_access_grants_target", "(host_id IS NULL) <> (tag_name IS NULL)"));
        });

        // One naming style for the whole schema: snake_case columns, Identity's own tables included.
        // Npgsql's row version is PostgreSQL's xmin system column, which keeps its name.
        foreach (var entity in builder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties().Where(p => p.GetColumnName() != "xmin"))
            {
                property.SetColumnName(SnakeCase(property.Name));
            }
        }
    }

    /// <summary><c>PackageSha256</c> → <c>package_sha256</c>, <c>OccurredAtUtc</c> → <c>occurred_at_utc</c>.</summary>
    internal static string SnakeCase(string name)
    {
        var result = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1])
                || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1]))))
            {
                result.Append('_');
            }

            result.Append(char.ToLowerInvariant(c));
        }

        return result.ToString();
    }
}

/// <summary>For <c>dotnet ef migrations add</c>, which needs a context without running the server.</summary>
internal sealed class DesignTimeCoreDbContextFactory : IDesignTimeDbContextFactory<CoreDbContext>
{
    public CoreDbContext CreateDbContext(string[] args) =>
        new(CoreDbContext.CreateOptions(Environment.GetEnvironmentVariable("NKEEPER_DESIGN_CONNECTION") ?? "Host=localhost;Database=nkeeper_design"));
}
