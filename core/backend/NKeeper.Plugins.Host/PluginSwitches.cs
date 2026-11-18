using NKeeper.Plugins.Packaging;

namespace NKeeper.Plugins.Host;

/// <summary>
/// Turning plugins on and off (docs/architecture.md §9.2), recorded at once and applied at the next restart (§7.1).
/// Dependencies are followed one layer deep: enabling a plugin enables its shared plugins, and disabling a shared
/// plugin disables its dependants.
/// </summary>
public sealed class PluginSwitches(IPluginStateStore store, IPluginAuditSink audit, TimeProvider time)
{
    /// <summary>The plugins that enabling <paramref name="id"/> turns on: its disabled dependencies, then itself.</summary>
    public static IReadOnlyList<string> PlanEnable(string id, IReadOnlyDictionary<string, PluginManifest> manifests, IReadOnlyDictionary<string, PluginRecord> records)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        ArgumentNullException.ThrowIfNull(records);
        if (!manifests.TryGetValue(id, out var manifest))
        {
            return [];
        }

        return [.. manifest.DependsOn.Select(d => d.Id).Append(id).Distinct(StringComparer.Ordinal)
            .Where(d => manifests.ContainsKey(d) && records.TryGetValue(d, out var r) && !r.Enabled)];
    }

    /// <summary>The plugins that disabling <paramref name="id"/> turns off: its enabled dependants, then itself.</summary>
    public static IReadOnlyList<string> PlanDisable(string id, IReadOnlyDictionary<string, PluginManifest> manifests, IReadOnlyDictionary<string, PluginRecord> records)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        ArgumentNullException.ThrowIfNull(records);
        return [.. manifests.Values.Where(m => m.DependsOn.Any(d => d.Id == id)).Select(m => m.Id).Order(StringComparer.Ordinal).Append(id)
            .Where(d => records.TryGetValue(d, out var r) && r.Enabled)];
    }

    /// <summary>Enables or disables a plugin and those its dependencies bring with it, and returns what changed.</summary>
    /// <param name="by">Who did it, for the record and the audit.</param>
    public async Task<IReadOnlyList<string>> SwitchAsync(string id, bool enable, string by, IReadOnlyDictionary<string, PluginManifest> manifests, CancellationToken cancellationToken)
    {
        var records = await store.LoadAsync(cancellationToken);
        var plan = enable ? PlanEnable(id, manifests, records) : PlanDisable(id, manifests, records);
        var now = time.GetUtcNow();
        foreach (var target in plan)
        {
            var record = records[target];
            await store.SaveAsync(
                record with { Enabled = enable, EnabledBy = enable ? by : null, EnabledAtUtc = enable ? now : null, UpdatedAtUtc = now },
                cancellationToken);
            var why = target == id ? string.Empty : enable ? $", needed by {id}" : $", which needs {id}";
            await audit.WriteAsync(enable ? "plugin.enabled" : "plugin.disabled", target, $"{record.Version}, by {by}{why}; at the next restart", cancellationToken);
        }

        return plan;
    }
}
