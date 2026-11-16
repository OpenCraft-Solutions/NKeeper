using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using NKeeper.Abstractions;
using NKeeper.Core.Credentials;

namespace NKeeper.Core.Audit;

/// <summary>
/// A plugin's own events in the audit trail (docs/architecture.md §7.12.1). The plugin is the one whose endpoint
/// is running, so it can record only events named after itself, never core's or another plugin's.
/// </summary>
public sealed partial class PluginAudit(IHttpContextAccessor accessor, IAuditLog audit) : IPluginAudit
{
    public async Task RecordAsync(string action, Guid? hostId, string detail, CancellationToken cancellationToken = default)
    {
        var plugin = PluginCaller.Of(accessor.HttpContext)
            ?? throw new InvalidOperationException("Plugin audit events can only be recorded from the plugin's own endpoints.");
        if (action is null || !action.StartsWith(plugin + ".", StringComparison.Ordinal) || !EventName().IsMatch(action[(plugin.Length + 1)..]))
        {
            throw new ArgumentException($"Plugin {plugin} can only record events named '{plugin}.<name>', with a name of lower-case letters, digits, '-' and '_'.", nameof(action));
        }

        await audit.RecordAsync(new AuditEntry(action, hostId?.ToString() ?? string.Empty, detail ?? string.Empty), cancellationToken);
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9_-]{0,63}\z")]
    private static partial Regex EventName();
}
