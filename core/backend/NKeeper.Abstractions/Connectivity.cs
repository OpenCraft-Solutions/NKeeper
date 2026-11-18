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
    /// An HTTPS connection that core signs in to (docs/architecture.md §7.16, SDK 1.6): every request carries HTTP Basic
    /// authentication from the handle's credential, which the plugin never sees, as a Redfish BMC needs. TLS only, with
    /// the certificate checked as for <see cref="OpenHttpAsync(HostContext, Guid, bool, CancellationToken)"/>.
    /// </summary>
    /// <exception cref="CredentialDeniedException">The handle isn't valid here.</exception>
    /// <exception cref="ConnectException">The certificate isn't trusted, the service can't be reached, or the credential has no password.</exception>
    Task<IHttpConnection> OpenHttpAsync(HostContext host, Guid serviceId, CredentialHandle handle, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This server doesn't sign in to HTTP services: it needs SDK 1.6.");

    /// <summary>
    /// Like <see cref="OpenSshAsync(HostContext, Guid, CredentialHandle, CancellationToken)"/>, to another port on the
    /// service's address (SDK 1.6): RACADM, an iDRAC's SSH command line, beside its HTTPS service. The server's key is
    /// trusted for that address and port.
    /// </summary>
    /// <exception cref="CredentialDeniedException">The handle isn't valid here.</exception>
    /// <exception cref="ConnectException">The connection couldn't be opened.</exception>
    Task<ISshConnection> OpenSshAsync(HostContext host, Guid serviceId, CredentialHandle handle, int port, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This server doesn't open SSH on another port: it needs SDK 1.6.");

    /// <summary>
    /// A database connection with the plugin's own driver (docs/architecture.md §7.31, SDK 1.7): core adds the handle's
    /// login, opens it through the jump host if there is one, and refuses it if the driver kept the password.
    /// </summary>
    /// <exception cref="CredentialDeniedException">The handle isn't valid here.</exception>
    /// <exception cref="ConnectException">The connection couldn't be opened; <see cref="ConnectException.Reason"/> says why.</exception>
    Task<IDatabaseLogin> OpenDatabaseAsync(HostContext host, Guid serviceId, CredentialHandle handle, DatabaseConnectRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This server doesn't open database connections: it needs SDK 1.7.");

    /// <summary>
    /// Checks a server identity the plugin read in its own protocol (docs/architecture.md §7.31, SDK 1.7), such as SQL
    /// Server's certificate inside TDS, against the trust store, as for an HTTPS certificate. It returns when the identity
    /// may be used; the plugin then pins its driver to it.
    /// </summary>
    /// <param name="publiclyValid">Whether the certificate chained to a trusted root for the service's address.</param>
    /// <exception cref="ConnectException"><see cref="ConnectFailure.HostKeyUnknown"/> with the identity, or <see cref="ConnectFailure.HostKeyChanged"/>.</exception>
    Task CheckIdentityAsync(HostContext host, Guid serviceId, PresentedIdentity identity, bool publiclyValid, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This server doesn't check identities for plugins: it needs SDK 1.7.");

    /// <summary>
    /// A plain TCP connection to a service (docs/architecture.md §7.31, SDK 1.9), through its jump host when it has one,
    /// for a plugin that speaks the server's own protocol before core signs in, as SQL Server's pre-login hands over its
    /// certificate. Core sends nothing on it.
    /// </summary>
    /// <exception cref="ConnectException">The service can't be reached; <see cref="ConnectException.Reason"/> says why.</exception>
    Task<ITcpConnection> OpenTcpAsync(HostContext host, Guid serviceId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This server doesn't open TCP connections for plugins: it needs SDK 1.9.");

    /// <summary>
    /// A remote desktop through guacd (docs/architecture.md §7.20, SDK 0.1.9): RDP, or VNC with
    /// <see cref="DesktopOptions.Protocol"/> (SDK 1.2). Core checks the handle and, for RDP, the server's certificate,
    /// and does the Guacamole handshake with the credential, which the plugin never sees.
    /// </summary>
    /// <exception cref="DesktopConnectException">The certificate isn't trusted, guacd isn't running, or the server can't be reached.</exception>
    /// <exception cref="CredentialDeniedException">The handle isn't valid here.</exception>
    Task<IDesktopConnection> OpenDesktopAsync(HostContext host, Guid serviceId, CredentialHandle handle, DesktopOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// A Telnet terminal on one of the host's services (docs/architecture.md §7.22, SDK 1.1), through its jump host if
    /// it has one. Core speaks the Telnet protocol, and with a <paramref name="handle"/> answers the server's first login
    /// and password prompts with its credential, which the plugin never sees. Without one, the user types them.
    /// Telnet isn't encrypted.
    /// </summary>
    /// <exception cref="ConnectException">The service can't be reached, or its jump host can't be used.</exception>
    /// <exception cref="CredentialDeniedException">The handle isn't valid here.</exception>
    Task<ITelnetConnection> OpenTelnetAsync(HostContext host, Guid serviceId, CredentialHandle? handle, TerminalSize size, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This server doesn't open Telnet connections: it needs SDK 1.1.");

    /// <summary>
    /// An SFTP connection to one SSH service of the host (docs/architecture.md §7.9, SDK 1.3), with every check of
    /// <see cref="OpenSshAsync"/>: the handle, the jump host, the server's key and the sign-in.
    /// </summary>
    /// <exception cref="CredentialDeniedException">The handle isn't valid here.</exception>
    /// <exception cref="ConnectException">The connection couldn't be opened; <see cref="ConnectException.Reason"/> says why.</exception>
    Task<ISftpConnection> OpenSftpAsync(HostContext host, Guid serviceId, CredentialHandle handle, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This server doesn't open SFTP connections: it needs SDK 1.3.");

    /// <summary>
    /// A WMI connection to one of a Windows host's RPC services (docs/architecture.md §7.28, SDK 1.5): core signs in
    /// over DCOM with the handle's credential, which needs a password. Not through a jump host.
    /// </summary>
    /// <exception cref="CredentialDeniedException">The handle isn't valid here.</exception>
    /// <exception cref="ConnectException">The connection couldn't be opened; <see cref="ConnectException.Reason"/> says why.</exception>
    Task<IWmiConnection> OpenWmiAsync(HostContext host, Guid serviceId, CredentialHandle handle, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This server doesn't open WMI connections: it needs SDK 1.5.");

    /// <summary>
    /// A DCE/RPC connection to one of a Windows host's RPC services (docs/architecture.md §7.28, SDK 1.5), bound to one
    /// interface with NTLM sign-in, which needs a password. Not through a jump host.
    /// </summary>
    /// <exception cref="CredentialDeniedException">The handle isn't valid here.</exception>
    /// <exception cref="ConnectException">The connection couldn't be opened; <see cref="ConnectException.Reason"/> says why.</exception>
    Task<IRpcConnection> OpenRpcAsync(HostContext host, Guid serviceId, CredentialHandle handle, RpcInterface rpcInterface, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This server doesn't open RPC connections: it needs SDK 1.5.");
}

/// <summary>
/// One read-only WQL query (SDK 1.5). Event logs list newest first, so <see cref="TimeProperty"/> and
/// <see cref="Since"/> read a page without the rest of the log.
/// </summary>
/// <param name="Wql">The query, such as <c>SELECT Caption FROM Win32_OperatingSystem</c>.</param>
public sealed record WmiQuery(string Wql)
{
    /// <summary>The most rows a query answers, whatever its <see cref="Limit"/>.</summary>
    public const int MaxRows = 10_000;

    public string Namespace { get; init; } = "root/cimv2";

    /// <summary>At most this many rows; null for all of them, up to <see cref="MaxRows"/>.</summary>
    public int? Limit { get; init; }

    /// <summary>A CIM date property (<c>TimeGenerated</c>): reading stops at the first row older than <see cref="Since"/>.</summary>
    public string? TimeProperty { get; init; }

    public DateTimeOffset? Since { get; init; }
}

/// <summary>An open WMI connection (SDK 1.5). Disposing it signs out and ends core's helper.</summary>
public interface IWmiConnection : IAsyncDisposable
{
    /// <summary>The query's rows, each a JSON object of property names and values.</summary>
    /// <exception cref="WmiException">The host refused the query; the connection stays usable.</exception>
    /// <exception cref="ConnectException">The connection was lost, or the query took too long.</exception>
    Task<IReadOnlyList<System.Text.Json.JsonElement>> QueryAsync(WmiQuery query, CancellationToken cancellationToken = default);
}

/// <summary>The host refused a WMI query (SDK 1.5); the message is fit to show the user.</summary>
public sealed class WmiException(string message) : Exception(message);

/// <summary>A DCE/RPC interface: its UUID and version (SDK 1.5).</summary>
public sealed record RpcInterface(Guid Id, ushort Major, ushort Minor)
{
    /// <summary>The endpoint mapper, e1af8308-5d1f-11c9-91a4-08002b14a0fa v3.0.</summary>
    public static RpcInterface EndpointMapper { get; } = new(new Guid("e1af8308-5d1f-11c9-91a4-08002b14a0fa"), 3, 0);
}

/// <summary>An open DCE/RPC connection, bound to one interface (SDK 1.5).</summary>
public interface IRpcConnection : IAsyncDisposable
{
    /// <summary>The most a response's stub may be.</summary>
    public const int MaxResponseBytes = 4 * 1024 * 1024;

    /// <summary>Calls one operation of the bound interface with an NDR stub; returns the response's stub.</summary>
    /// <exception cref="RpcFaultException">The server reported a fault.</exception>
    /// <exception cref="ConnectException">The connection was lost, or the call took too long.</exception>
    Task<byte[]> CallAsync(ushort opnum, ReadOnlyMemory<byte> stub, CancellationToken cancellationToken = default);
}

/// <summary>The server answered a call with a fault (SDK 1.5), with its status code when it gave one.</summary>
public sealed class RpcFaultException(string message, uint? status = null) : Exception(message)
{
    public uint? Status { get; } = status;
}

/// <summary>A terminal's size, from 1 × 1 to 1000 × 1000 (SDK 1.1).</summary>
public sealed record TerminalSize(int Columns = 80, int Rows = 24);

/// <summary>An open Telnet terminal (SDK 1.1). Disposing it closes the connection, and its jump host tunnel.</summary>
public interface ITelnetConnection : IAsyncDisposable
{
    /// <summary>
    /// The terminal's input (write what's typed: core escapes it for Telnet) and output (read: terminal data only,
    /// the protocol's commands removed).
    /// </summary>
    Stream Stream { get; }

    /// <summary>Tells the server the terminal's new size, when it asked to be told.</summary>
    void Resize(int columns, int rows);
}

/// <summary>The protocol guacd speaks to the server (SDK 1.2).</summary>
public enum DesktopProtocol
{
    Rdp,

    /// <summary>VNC: no server identity to check, and usually a password without a user name.</summary>
    Vnc,
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
    /// <summary>RDP by default; VNC needs SDK 1.2.</summary>
    public DesktopProtocol Protocol { get; init; } = DesktopProtocol.Rdp;

    /// <summary>RDP only: VNC takes <see cref="DesktopSecurity.Any"/>.</summary>
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
/// <summary>A plain TCP connection to a service (SDK 1.9). Disposing it closes it, and its jump host tunnel.</summary>
public interface ITcpConnection : IAsyncDisposable
{
    Stream Stream { get; }

    /// <summary>The service's own address, which its certificate is checked against, even when the connection goes through a tunnel.</summary>
    string Address { get; }

    /// <summary>The service's port.</summary>
    int Port { get; }
}

public interface ISshConnection : IAsyncDisposable
{
    /// <summary>Runs a command and waits for it to finish.</summary>
    /// <param name="timeout">How long to wait; 60 seconds when null.</param>
    Task<SshCommandResult> RunAsync(string command, TimeSpan? timeout = null, CancellationToken cancellationToken = default);

    /// <summary>Starts an interactive shell on a pseudo-terminal.</summary>
    Task<ISshShell> OpenShellAsync(SshTerminal terminal, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a command and returns at once, for output read as it comes, such as <c>journalctl --follow</c>
    /// (docs/architecture.md §7.9, SDK 1.3). Disposing it before it ends stops it.
    /// </summary>
    Task<ISshCommand> StartAsync(string command, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This server doesn't stream commands: it needs SDK 1.3.");
}

/// <summary>A running command (SDK 1.3). Disposing it stops it if it's still running; the connection stays open.</summary>
public interface ISshCommand : IAsyncDisposable
{
    /// <summary>The command's standard output, as it comes. It ends when the command does.</summary>
    Stream Output { get; }

    /// <summary>Waits for the command to end: its exit status and error output (at most 64 KB), with no output (that was <see cref="Output"/>).</summary>
    Task<SshCommandResult> WaitAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// An open, authenticated SFTP connection (docs/architecture.md §7.9, SDK 1.3). Paths are absolute. Disposing it
/// closes it, and its jump host tunnel.
/// </summary>
public interface ISftpConnection : IAsyncDisposable
{
    /// <summary>Where the login starts, as an absolute path.</summary>
    string HomeDirectory { get; }

    /// <summary>A folder's entries, without <c>.</c> and <c>..</c>.</summary>
    Task<IReadOnlyList<SftpEntry>> ListAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>One entry. A link is reported as the link, not what it points to.</summary>
    Task<SftpEntry> GetAsync(string path, CancellationToken cancellationToken = default);

    Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Creates the file, or with <paramref name="overwrite"/> replaces it; without, a file that exists is refused (<see cref="SftpFailure.AlreadyExists"/>).</summary>
    Task<Stream> OpenWriteAsync(string path, bool overwrite, CancellationToken cancellationToken = default);

    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);

    Task DeleteFileAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Deletes an empty folder.</summary>
    Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken = default);

    Task RenameAsync(string path, string newPath, CancellationToken cancellationToken = default);
}

public enum SftpEntryKind
{
    File,
    Directory,
    Link,
    Other,
}

/// <param name="Path">Absolute.</param>
/// <param name="Size">In bytes; 0 for a folder.</param>
/// <param name="Permissions">Like <c>rwxr-xr-x</c>.</param>
public sealed record SftpEntry(string Name, string Path, SftpEntryKind Kind, long Size, DateTimeOffset ModifiedUtc, string Permissions);

/// <summary>Why an SFTP operation failed (SDK 1.3).</summary>
public enum SftpFailure
{
    NotFound,
    PermissionDenied,
    AlreadyExists,
    /// <summary>The connection closed.</summary>
    ConnectionLost,
    /// <summary>The server refused for another reason; the message has it.</summary>
    Failed,
}

/// <summary>An SFTP operation failed; the message names the path, and is fit to show the user.</summary>
public sealed class SftpException(SftpFailure reason, string message) : Exception(message)
{
    public SftpFailure Reason { get; } = reason;
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
