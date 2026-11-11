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
/// An entry in a host's Connect menu (docs/architecture.md §7.12, SDK 0.1.6): it opens one of the plugin's
/// session types on a service. The plugin only names the session type; the shell builds the menu, and core
/// resolves the credential.
/// </summary>
/// <param name="Id">Like a service type's ID, plus dots between segments (<c>ssh.terminal</c>). Unique across plugins.</param>
/// <param name="Label">What users see, e.g. <c>Terminal</c>.</param>
/// <param name="SessionType">A session type the same plugin contributes. The action applies to the services it opens on.</param>
public sealed record HostAction(string Id, string Label, string SessionType)
{
    public HostActionCredential Credential { get; init; } = HostActionCredential.Required;
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

    /// <summary>The probe's name, as <c>GET /api/plugins</c> reports it.</summary>
    public string Kind { get; }

    public override string ToString() => Kind;
}
