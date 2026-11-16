namespace NKeeper.Abstractions;

/// <summary>
/// A plugin's own events in core's audit trail (docs/architecture.md §7.12.1, SDK 0.1.7). Use it from the plugin's
/// endpoints: core knows the plugin from the endpoint, and records the signed-in user and the client's address.
/// Core records sessions, credentials and trust itself, so a plugin records only its own operations.
/// </summary>
public interface IPluginAudit
{
    /// <summary>Records an event. It never throws for a failed write, which core logs instead.</summary>
    /// <param name="action">The plugin's ID, a dot, then lower-case letters, digits, <c>-</c> and <c>_</c>: <c>wake-on-lan.send</c>.</param>
    /// <param name="hostId">The host it concerns, if any.</param>
    /// <exception cref="ArgumentException">The action isn't one of the plugin's own.</exception>
    /// <exception cref="InvalidOperationException">It's called outside a plugin's endpoint.</exception>
    Task RecordAsync(string action, Guid? hostId, string detail, CancellationToken cancellationToken = default);
}
