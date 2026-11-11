namespace NKeeper.Abstractions;

/// <summary>Whether a host action needs a credential (docs/architecture.md §7.12).</summary>
public enum HostActionCredential
{
    /// <summary>A saved credential used with the service, or one the user types into core's dialog.</summary>
    Required,

    /// <summary>No credential.</summary>
    None,
}

/// <summary>
/// An entry in a host's menu (docs/architecture.md §7.12). It either opens one of the plugin's session types on a
/// service (SDK 0.1.6), or runs one of the plugin's host endpoints (<see cref="Endpoint"/>, SDK 0.1.7). The plugin
/// only names what it opens or runs; the shell builds the menu, and core resolves any credential.
/// </summary>
/// <param name="Id">Like a service type's ID, plus dots between segments (<c>ssh.terminal</c>). Unique across plugins.</param>
/// <param name="Label">What users see, e.g. <c>Terminal</c>.</param>
/// <param name="SessionType">A session type the same plugin contributes. The action applies to the services it opens on. Null for a run action.</param>
public sealed record HostAction(string Id, string Label, string? SessionType = null)
{
    public HostActionCredential Credential { get; init; } = HostActionCredential.Required;

    /// <summary>
    /// For a run action: a path in the plugin's host group, such as <c>wake</c>. The shell posts
    /// <c>{ serviceId }</c> (with the credential, if it needs one) to <c>/api/p/&lt;plugin&gt;/hosts/{hostId}/&lt;endpoint&gt;</c>,
    /// and shows the <c>{ message }</c> it answers.
    /// </summary>
    public string? Endpoint { get; init; }

    /// <summary>For a run action: the service types it applies to. A session action takes them from its session type.</summary>
    public IReadOnlyList<string> ServiceTypes { get; init; } = [];
}

/// <summary>
/// How core tells whether a service of a type is up (docs/architecture.md §7.13, SDK 0.1.6). Core runs the
/// probe, never the plugin. More kinds arrive with their first users.
/// </summary>
public sealed class HealthProbe
{
    private HealthProbe(string kind) => Kind = kind;

    /// <summary>A TCP connection to the service's address and port, closed at once. Nothing is sent.</summary>
    public static HealthProbe Tcp { get; } = new("tcp");

    /// <summary>A TCP connection and a TLS handshake that accepts any certificate, reporting its subject and expiry (SDK 0.1.8).</summary>
    public static HealthProbe Tls { get; } = new("tls");

    /// <summary>The probe's name, as <c>GET /api/plugins</c> reports it.</summary>
    public string Kind { get; }

    public override string ToString() => Kind;
}
