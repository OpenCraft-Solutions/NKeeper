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

/// <summary>One audit event. Phase 0 records plugin events; Phase 1 adds users, sessions and the rest.</summary>
public sealed class AuditEvent
{
    public long Id { get; set; }

    public DateTimeOffset OccurredAtUtc { get; set; }

    /// <summary>Who did it; <c>system</c> for the server itself.</summary>
    public string Actor { get; set; } = string.Empty;

    /// <summary>What happened, e.g. <c>plugin.refused</c>.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>What it happened to, e.g. a plugin ID.</summary>
    public string Target { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;
}

/// <summary>The core schema. Plugins get their own contexts and schemas later; they never see this one.</summary>
public sealed class CoreDbContext(DbContextOptions<CoreDbContext> options) : DbContext(options)
{
    public const string Schema = "core";

    public DbSet<InstalledPlugin> InstalledPlugins => Set<InstalledPlugin>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    /// <summary>Configures Npgsql for the core context, with its migration history inside the core schema.</summary>
    public static DbContextOptions<CoreDbContext> CreateOptions(string connectionString) =>
        new DbContextOptionsBuilder<CoreDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", Schema))
            .Options;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<InstalledPlugin>(plugin =>
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

        modelBuilder.Entity<AuditEvent>(audit =>
        {
            audit.ToTable("audit_events");
            audit.HasKey(a => a.Id);
            audit.Property(a => a.Id).HasColumnName("id").UseIdentityAlwaysColumn();
            audit.Property(a => a.OccurredAtUtc).HasColumnName("occurred_at_utc");
            audit.Property(a => a.Actor).HasColumnName("actor").HasMaxLength(256);
            audit.Property(a => a.Action).HasColumnName("action").HasMaxLength(64);
            audit.Property(a => a.Target).HasColumnName("target").HasMaxLength(256);
            audit.Property(a => a.Detail).HasColumnName("detail");
            audit.HasIndex(a => a.OccurredAtUtc);
            audit.HasIndex(a => new { a.Action, a.OccurredAtUtc });
        });
    }
}

/// <summary>For <c>dotnet ef migrations add</c>, which needs a context without running the server.</summary>
internal sealed class DesignTimeCoreDbContextFactory : IDesignTimeDbContextFactory<CoreDbContext>
{
    public CoreDbContext CreateDbContext(string[] args) =>
        new(CoreDbContext.CreateOptions(Environment.GetEnvironmentVariable("NKEEPER_DESIGN_CONNECTION") ?? "Host=localhost;Database=nkeeper_design"));
}
