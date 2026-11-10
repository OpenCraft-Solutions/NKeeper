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

/// <summary>Why an SSH connection couldn't be opened.</summary>
public enum SshConnectFailure
{
    /// <summary>The server presented a key nobody has trusted yet: see <see cref="SshConnectException.PresentedKey"/>.</summary>
    HostKeyUnknown,

    /// <summary>The server presented a different key from the one trusted for it.</summary>
    HostKeyChanged,

    AuthenticationFailed,

    /// <summary>The credential has neither a password nor a private key.</summary>
    NoSecret,

    /// <summary>The private key can't be read, or its passphrase is wrong.</summary>
    InvalidPrivateKey,

    Unreachable,

    TimedOut,

    /// <summary>The user can't use the jump host, its service or credential is gone, or it wouldn't forward.</summary>
    JumpHostUnavailable,
}

/// <summary>A key a server presented that isn't trusted yet, and which host and service presented it.</summary>
/// <param name="HostId">The host, or its jump host when it's the jump host's key.</param>
/// <param name="ServiceId">The service it was presented on.</param>
/// <param name="Algorithm">The key's algorithm, e.g. <c>ssh-ed25519</c>.</param>
/// <param name="Fingerprint">SHA-256 as unpadded base64, the way OpenSSH prints it.</param>
public sealed record PresentedHostKey(Guid HostId, Guid ServiceId, string Algorithm, string Fingerprint);

/// <summary>An SSH connection couldn't be opened. The message names the host and port, never a secret.</summary>
public sealed class SshConnectException(SshConnectFailure reason, string message, PresentedHostKey? presentedKey = null) : Exception(message)
{
    public SshConnectFailure Reason { get; } = reason;

    /// <summary>With <see cref="SshConnectFailure.HostKeyUnknown"/>: the key, for the user to check and trust through core.</summary>
    public PresentedHostKey? PresentedKey { get; } = presentedKey;
}
