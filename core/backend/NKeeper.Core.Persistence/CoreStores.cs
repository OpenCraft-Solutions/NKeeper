using Microsoft.EntityFrameworkCore;
using NKeeper.Plugins.Host;

namespace NKeeper.Core.Persistence;

/// <summary>Plugin records in <c>core.installed_plugins</c>. Each call uses its own short-lived context.</summary>
public sealed class DbPluginStateStore(DbContextOptions<CoreDbContext> options) : IPluginStateStore
{
    public async Task<IReadOnlyDictionary<string, PluginRecord>> LoadAsync(CancellationToken cancellationToken)
    {
        await using var db = new CoreDbContext(options);
        return await db.InstalledPlugins.AsNoTracking()
            .ToDictionaryAsync(p => p.Id, ToRecord, StringComparer.Ordinal, cancellationToken);
    }

    public async Task SaveAsync(PluginRecord record, CancellationToken cancellationToken)
    {
        await using var db = new CoreDbContext(options);
        var row = await db.InstalledPlugins.FindAsync([record.Id], cancellationToken);
        if (row is null)
        {
            row = new InstalledPlugin { Id = record.Id };
            db.InstalledPlugins.Add(row);
        }

        row.Version = record.Version;
        row.PackageSha256 = record.PackageSha256;
        row.KeyId = record.KeyId;
        row.BuiltIn = record.BuiltIn;
        row.Enabled = record.Enabled;
        row.EnabledBy = record.EnabledBy;
        row.EnabledAtUtc = record.EnabledAtUtc;
        row.FirstSeenUtc = record.FirstSeenUtc;
        row.UpdatedAtUtc = record.UpdatedAtUtc;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static PluginRecord ToRecord(InstalledPlugin p) =>
        new(p.Id, p.Version, p.PackageSha256, p.KeyId, p.BuiltIn, p.Enabled, p.EnabledBy, p.EnabledAtUtc, p.FirstSeenUtc, p.UpdatedAtUtc);
}

/// <summary>Plugin audit events in <c>core.audit_events</c>, recorded as the server itself.</summary>
public sealed class DbPluginAuditSink(DbContextOptions<CoreDbContext> options, TimeProvider time) : IPluginAuditSink
{
    public const string SystemActor = "system";

    public async Task WriteAsync(string action, string target, string detail, CancellationToken cancellationToken)
    {
        await using var db = new CoreDbContext(options);
        db.AuditEvents.Add(new AuditEvent
        {
            OccurredAtUtc = time.GetUtcNow(),
            Actor = SystemActor,
            Action = action,
            Target = target,
            Detail = detail,
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}
