using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NKeeper.Abstractions;
using NKeeper.Core.Audit;
using NKeeper.Core.Credentials;
using NKeeper.Core.Persistence;

namespace NKeeper.Core.Plugins;

/// <summary>
/// A plugin's settings (docs/architecture.md §7.39, SDK 1.13). The plugin is the one whose endpoint is running, so
/// it reads and writes only its own.
/// </summary>
public sealed class PluginSettingsStore(IHttpContextAccessor http, CoreDbContext db, IAuditLog audit, TimeProvider time) : IPluginSettings
{
    public const int MaxBytes = 64 * 1024;

    public async Task<PluginDocument?> GetAsync(CancellationToken cancellationToken = default)
    {
        var plugin = PluginData.Caller(http);
        var row = await db.PluginSettings.AsNoTracking().FirstOrDefaultAsync(s => s.PluginId == plugin, cancellationToken);
        return row is null ? null : PluginData.Document(row.Version, row.Data);
    }

    public async Task SetAsync(int version, object data, CancellationToken cancellationToken = default)
    {
        var plugin = PluginData.Caller(http);
        var json = PluginData.Serialize(data, MaxBytes);
        var row = await db.PluginSettings.FirstOrDefaultAsync(s => s.PluginId == plugin, cancellationToken);
        if (row is null)
        {
            row = new PluginSettingsRow { PluginId = plugin };
            db.PluginSettings.Add(row);
        }

        row.Version = version;
        row.Data = json;
        row.UpdatedAtUtc = time.GetUtcNow();
        row.UpdatedByUserId = Guid.TryParse(http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var user) ? user : null;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(AuditActions.PluginSettingsChanged, plugin, $"version {version}"), cancellationToken);
    }
}

/// <summary>A plugin's document per host (§7.39), its own only. It checks no permission: the plugin's endpoints do.</summary>
public sealed class HostDataStore(IHttpContextAccessor http, CoreDbContext db, TimeProvider time) : IHostData
{
    public async Task<PluginDocument?> GetAsync(Guid hostId, CancellationToken cancellationToken = default)
    {
        var plugin = PluginData.Caller(http);
        var row = await db.HostData.AsNoTracking().FirstOrDefaultAsync(d => d.PluginId == plugin && d.HostId == hostId, cancellationToken);
        return row is null ? null : PluginData.Document(row.Version, row.Data);
    }

    public async Task SetAsync(Guid hostId, int version, object data, CancellationToken cancellationToken = default)
    {
        var plugin = PluginData.Caller(http);
        var json = PluginData.Serialize(data, IHostData.MaxBytes);
        if (!await db.Hosts.AnyAsync(h => h.Id == hostId, cancellationToken))
        {
            throw new ArgumentException("There's no such host.", nameof(hostId));
        }

        var row = await db.HostData.FirstOrDefaultAsync(d => d.PluginId == plugin && d.HostId == hostId, cancellationToken);
        if (row is null)
        {
            row = new HostDataRow { PluginId = plugin, HostId = hostId };
            db.HostData.Add(row);
        }

        row.Version = version;
        row.Data = json;
        row.UpdatedAtUtc = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ClearAsync(Guid hostId, CancellationToken cancellationToken = default)
    {
        var plugin = PluginData.Caller(http);
        await db.HostData.Where(d => d.PluginId == plugin && d.HostId == hostId).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, PluginDocument>> AllAsync(CancellationToken cancellationToken = default)
    {
        var plugin = PluginData.Caller(http);
        var rows = await db.HostData.AsNoTracking().Where(d => d.PluginId == plugin).ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.HostId, r => PluginData.Document(r.Version, r.Data));
    }
}

internal static class PluginData
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The plugin whose endpoint is running; nothing a plugin says counts.</summary>
    public static string Caller(IHttpContextAccessor http) =>
        PluginCaller.Of(http.HttpContext) ?? throw new InvalidOperationException("Plugin settings and host data can only be used from the plugin's own endpoints.");

    public static string Serialize(object data, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(data);
        var json = JsonSerializer.Serialize(data, Json);
        return Encoding.UTF8.GetByteCount(json) <= maxBytes
            ? json
            : throw new ArgumentException($"The document is larger than {maxBytes} bytes.", nameof(data));
    }

    public static PluginDocument Document(int version, string json)
    {
        using var parsed = JsonDocument.Parse(json);
        return new PluginDocument(version, parsed.RootElement.Clone());
    }
}
