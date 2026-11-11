using System.Text.Json.Serialization;

namespace NKeeper.Abstractions;

/// <summary>
/// Connections core opens and authenticates for a plugin (docs/architecture.md §7.9, SDK 0.1.4). The plugin
/// passes a credential handle and gets the connection; it never sees the secret.
/// </summary>
public interface IConnectivity
{
    /// <summary>
    /// Connects over SSH to one service of the host, through its jump host if it has one, checks the server's
    /// key against the trust store, and signs in with the handle's credential.
    /// </summary>
    /// <exception cref="CredentialDeniedException">The handle isn't valid for this user, host, service, plugin or request.</exception>
    /// <exception cref="SshConnectException">The connection couldn't be opened; <see cref="SshConnectException.Reason"/> says why.</exception>
    Task<ISshConnection> OpenSshAsync(HostContext host, Guid serviceId, CredentialHandle handle, CancellationToken cancellationToken = default);

    /// <summary>
    /// An HTTP connection to one of the host's services (docs/architecture.md §7.16, SDK 0.1.8): requests go to that
    /// endpoint only. With <paramref name="tls"/>, the certificate must be publicly valid or trusted for the host,
    /// which core checks before it returns and on every later request.
    /// </summary>
    /// <exception cref="HttpConnectException">The certificate isn't trusted, or the service can't be reached.</exception>
    Task<IHttpConnection> OpenHttpAsync(HostContext host, Guid serviceId, bool tls, CancellationToken cancellationToken = default);

    /// <summary>
    /// A remote desktop through guacd (docs/architecture.md §7.20, SDK 0.1.9), RDP so far. Core checks the handle and
    /// the server's certificate, and does the Guacamole handshake with the credential, which the plugin never sees.
    /// </summary>
    /// <exception cref="DesktopConnectException">The certificate isn't trusted, guacd isn't running, or the server can't be reached.</exception>
    /// <exception cref="CredentialDeniedException">The handle isn't valid here.</exception>
    Task<IDesktopConnection> OpenDesktopAsync(HostContext host, Guid serviceId, CredentialHandle handle, DesktopOptions options, CancellationToken cancellationToken = default);
}

/// <summary>How an RDP connection secures itself.</summary>
public enum DesktopSecurity
{
    /// <summary>Network Level Authentication when the server offers it, otherwise TLS.</summary>
    Any,

    /// <summary>Network Level Authentication (CredSSP) only, the Windows default.</summary>
    Nla,

    /// <summary>TLS only, for servers without NLA (xrdp, say): the server shows its own sign-in screen.</summary>
    Tls,
}

/// <summary>What the browser shows a desktop on, checked by core: a size from 200 × 200 to 8192 × 8192.</summary>
public sealed record DesktopOptions(int Width, int Height, int Dpi = 96)
{
    public DesktopSecurity Security { get; init; } = DesktopSecurity.Any;

    /// <summary>Audio the browser plays, such as <c>audio/L16</c>; none by default.</summary>
    public IReadOnlyList<string> AudioMimetypes { get; init; } = [];

    public IReadOnlyList<string> ImageMimetypes { get; init; } = ["image/png", "image/jpeg", "image/webp"];

    /// <summary>The browser's time zone, such as <c>Europe/Paris</c>.</summary>
    public string? Timezone { get; init; }
}

/// <summary>An open remote desktop: Guacamole instructions, after core's handshake with guacd.</summary>
public interface IDesktopConnection : IAsyncDisposable
{
    /// <summary>guacd's next instructions for the browser, <c>ready</c> first, as Guacamole text; null once guacd closed.</summary>
    Task<string?> ReceiveAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The browser's instructions: only what core's allow-list allows reaches guacd (input, display and text clipboard).
    /// </summary>
    /// <exception cref="FormatException">The text isn't Guacamole instructions.</exception>
    Task SendFromBrowserAsync(string instructions, CancellationToken cancellationToken);
}

/// <summary>An open HTTP connection to a service. It sends no cookies, follows no redirects and doesn't decompress.</summary>
public interface IHttpConnection : IAsyncDisposable
{
    /// <summary>Sends requests to the service, and nowhere else.</summary>
    HttpMessageInvoker Client { get; }

    /// <summary>The service's address: <c>https://10.0.0.5:8443/</c>.</summary>
    Uri BaseAddress { get; }
}

/// <summary>An open, authenticated SSH connection. Disposing it closes it, and its jump host tunnel.</summary>
public interface ISshConnection : IAsyncDisposable
{
    /// <summary>Runs a command and waits for it to finish.</summary>
    /// <param name="timeout">How long to wait; 60 seconds when null.</param>
    Task<SshCommandResult> RunAsync(string command, TimeSpan? timeout = null, CancellationToken cancellationToken = default);

    /// <summary>Starts an interactive shell on a pseudo-terminal.</summary>
    Task<ISshShell> OpenShellAsync(SshTerminal terminal, CancellationToken cancellationToken = default);
}

/// <summary>What a command left behind.</summary>
/// <param name="ExitStatus">Null when the server sent none, as when the command was killed by a signal.</param>
public sealed record SshCommandResult(int? ExitStatus, string Output, string Error);

/// <summary>The pseudo-terminal a shell runs on.</summary>
public sealed record SshTerminal(string Type = "xterm-256color", int Columns = 80, int Rows = 24);

/// <summary>An interactive shell. Disposing it ends it; the connection stays open.</summary>
public interface ISshShell : IAsyncDisposable
{
    /// <summary>The terminal's input (write) and output (read).</summary>
    Stream Stream { get; }

    /// <summary>Tells the server the terminal's new size.</summary>
    void Resize(int columns, int rows);
}

/// <summary>Why core couldn't open a connection (SDK 1.0). Each kind of connection uses the reasons that apply to it.</summary>
public enum ConnectFailure
{
    /// <summary>The server presented an identity no one trusted yet: <see cref="ConnectException.Presented"/> has it.</summary>
    HostKeyUnknown,

    /// <summary>The server presented a different identity from the one trusted for it.</summary>
    HostKeyChanged,

    /// <summary>SSH: the server refused the credential.</summary>
    AuthenticationFailed,

    /// <summary>The credential has no secret the connection can use: no password or private key for SSH, no password for a desktop.</summary>
    NoSecret,

    /// <summary>SSH: the private key can't be read, or its passphrase is wrong.</summary>
    InvalidPrivateKey,

    /// <summary>Desktop: the server and the security asked for don't agree, NLA when it only does TLS, say.</summary>
    SecurityNegotiationFailed,

    Unreachable,

    TimedOut,

    /// <summary>Desktop: guacd isn't running, or refused the connection.</summary>
    GatewayUnavailable,

    /// <summary>The host's jump host can't be used: the user can't use it, its key isn't trusted yet, its service or credential is gone, or it wouldn't forward.</summary>
    JumpHostUnavailable,
}

/// <summary>What kind of identity a server presented.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PresentedIdentityKind>))]
public enum PresentedIdentityKind
{
    [JsonStringEnumMemberName("ssh-host-key")]
    SshHostKey,

    [JsonStringEnumMemberName("tls-certificate")]
    TlsCertificate,
}

/// <summary>
/// An identity a server presented that isn't trusted yet, and which host and service presented it. As JSON it's the
/// frontend SDK's <c>TrustPrompt</c>, so a plugin endpoint can return it for its view to pass to <c>confirmTrust</c>.
/// </summary>
/// <param name="HostId">The host, or its jump host when it's the jump host's key.</param>
/// <param name="ServiceId">The service it was presented on.</param>
/// <param name="Algorithm">The key's algorithm, e.g. <c>ssh-ed25519</c>; <c>x509</c> for a certificate.</param>
/// <param name="Fingerprint">SHA-256 as unpadded base64, the way OpenSSH prints it.</param>
public sealed record PresentedIdentity(Guid HostId, Guid ServiceId, PresentedIdentityKind Kind, string Algorithm, string Fingerprint)
{
    /// <summary>The algorithm of a certificate.</summary>
    public const string CertificateAlgorithm = "x509";

    /// <summary>A certificate's subject.</summary>
    public string? Subject { get; init; }

    /// <summary>When a certificate expires.</summary>
    public DateTimeOffset? NotAfterUtc { get; init; }

    /// <summary>A certificate a server presented.</summary>
    public static PresentedIdentity Certificate(Guid hostId, Guid serviceId, string fingerprint, string subject, DateTimeOffset notAfterUtc) =>
        new(hostId, serviceId, PresentedIdentityKind.TlsCertificate, CertificateAlgorithm, fingerprint) { Subject = subject, NotAfterUtc = notAfterUtc };
}

/// <summary>Core couldn't open a connection (SDK 1.0). The message names the host and port, never a secret.</summary>
public sealed class ConnectException(ConnectFailure reason, string message, PresentedIdentity? presented = null) : Exception(message)
{
    public ConnectFailure Reason { get; } = reason;

    /// <summary>With <see cref="ConnectFailure.HostKeyUnknown"/>: the identity, for the user to check and trust through core.</summary>
    public PresentedIdentity? Presented { get; } = presented;
}
