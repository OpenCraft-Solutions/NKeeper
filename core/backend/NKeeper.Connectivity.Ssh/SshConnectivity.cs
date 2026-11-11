using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NKeeper.Abstractions;
using NKeeper.Core.Audit;
using NKeeper.Core.Connectivity;
using NKeeper.Core.Credentials;
using NKeeper.Core.Trust;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace NKeeper.Connectivity.Ssh;

/// <summary>
/// <see cref="IConnectivity"/> over SSH.NET (docs/architecture.md §7.9). Core works out the connection
/// (<see cref="SshConnectionPlanner"/>); this opens it, through the jump host's tunnel when there is one, checks
/// each server's key against the trust store, and signs in. Ported from 1.x's SshCommandConnection and JumpTunnel.
/// </summary>
public sealed class SshConnectivity(SshConnectionPlanner planner, TrustStore trust, IAuditLog audit, IServiceProvider services) : IConnectivity, IJumpTunnels
{
    /// <summary>
    /// HTTP connections are core's own (docs/architecture.md §7.16); this assembly adds the SSH tunnels they take
    /// through a jump host (<see cref="IJumpTunnels"/>). Resolved when used: the connector needs those tunnels.
    /// </summary>
    public Task<IHttpConnection> OpenHttpAsync(HostContext host, Guid serviceId, bool tls, CancellationToken cancellationToken = default) =>
        services.GetRequiredService<HttpConnector>().OpenAsync(host, serviceId, tls, cancellationToken);

    /// <summary>Desktops are core's own too (docs/architecture.md §7.20), through guacd, tunnelled like HTTP.</summary>
    public Task<IDesktopConnection> OpenDesktopAsync(HostContext host, Guid serviceId, CredentialHandle handle, DesktopOptions options, CancellationToken cancellationToken = default) =>
        services.GetRequiredService<DesktopConnector>().OpenAsync(host, serviceId, handle, options, cancellationToken);

    /// <summary>
    /// A loopback port forwarded through the host's jump host (§7.16). The jump host's key must already be trusted,
    /// since a request can't stop to ask; an SSH session to it asks once. Opening it is audited as credential.use.
    /// </summary>
    public async Task<IJumpTunnel?> OpenAsync(HostContext host, string address, int port, CancellationToken cancellationToken)
    {
        SshPlan? plan;
        try
        {
            plan = await planner.PlanTunnelAsync(host, cancellationToken);
        }
        catch (SshConnectException ex)
        {
            throw new HttpConnectException(HttpConnectFailure.JumpHostUnavailable, ex.Message);
        }

        if (plan is null)
        {
            return null;
        }

        using (plan)
        {
            var jumpEnd = plan.Target;
            SshClient? jump = null;
            try
            {
                jump = await ConnectAsync(jumpEnd, jumpEnd.Address, jumpEnd.Port, plan, isJump: true, cancellationToken);
                var forward = Forward(jump, jumpEnd, address, port);
                await audit.RecordAsync(
                    new AuditEntry(AuditActions.CredentialUse, host.Id.ToString(), $"tunnel to {address}:{port}, plugin {plan.PluginId}, {plan.Credential}"),
                    cancellationToken);
                return new Tunnel(forward, jump);
            }
            catch (SshConnectException ex)
            {
                jump?.Dispose();
                var message = ex.Reason == SshConnectFailure.HostKeyUnknown
                    ? $"The jump host “{jumpEnd.Name}”'s SSH key isn't trusted yet. Open a terminal to it once to check and trust it."
                    : ex.Message;
                throw new HttpConnectException(HttpConnectFailure.JumpHostUnavailable, message);
            }
            finally
            {
                // The jump host's secrets were read for this tunnel only.
                jumpEnd.Password?.Dispose();
                jumpEnd.PrivateKey?.Dispose();
            }
        }
    }

    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);

    public async Task<ISshConnection> OpenSshAsync(HostContext host, Guid serviceId, CredentialHandle handle, CancellationToken cancellationToken = default)
    {
        using var plan = await planner.PlanAsync(host, serviceId, handle, cancellationToken);
        SshClient? jump = null;
        ForwardedPortLocal? forward = null;
        try
        {
            var (address, port) = (plan.Target.Address, plan.Target.Port);
            if (plan.Jump is { } jumpEnd)
            {
                jump = await ConnectAsync(jumpEnd, jumpEnd.Address, jumpEnd.Port, plan, isJump: true, cancellationToken);
                forward = Forward(jump, jumpEnd, address, port);
                (address, port) = ("127.0.0.1", (int)forward.BoundPort);
            }

            // The target's own key is checked against the target, even through the tunnel.
            var client = await ConnectAsync(plan.Target, address, port, plan, isJump: false, cancellationToken);
            await audit.RecordAsync(
                new AuditEntry(AuditActions.CredentialUse, host.Id.ToString(), $"ssh to service {serviceId}, plugin {plan.PluginId}, {plan.Credential}"),
                cancellationToken);
            return new SshConnection(client, forward, jump);
        }
        catch
        {
            Close(forward, jump);
            throw;
        }
    }

    /// <summary>Connects to one end and signs in, checking its key first.</summary>
    /// <param name="address">Where to dial: the end itself, or the tunnel's local end.</param>
    private async Task<SshClient> ConnectAsync(SshEndpoint end, string address, int port, SshPlan plan, bool isJump, CancellationToken cancellationToken)
    {
        var methods = Methods(end, isJump);
        var client = new SshClient(new ConnectionInfo(address, port, end.Username, [.. methods]) { Timeout = ConnectTimeout })
        {
            KeepAliveInterval = KeepAliveInterval,
        };

        (string Algorithm, string Fingerprint, TrustVerdict Verdict)? presented = null;
        client.HostKeyReceived += (_, e) =>
        {
            var verdict = TrustStore.Check(end.KnownKeys, e.HostKeyName, e.FingerPrintSHA256);
            presented = (e.HostKeyName, e.FingerPrintSHA256, verdict);
            e.CanTrust = verdict == TrustVerdict.Trusted;
        };

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);
            await client.ConnectAsync(timeout.Token);
            return client;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            var who = isJump ? $"the jump host “{end.Name}”" : $"“{end.Name}”";
            var where = $"{end.Address}:{end.Port}";
            switch (presented)
            {
                case { Verdict: TrustVerdict.Unknown } unknown:
                    trust.Presented(plan.SessionKey, end.Trust, unknown.Algorithm, unknown.Fingerprint);
                    throw new SshConnectException(
                        SshConnectFailure.HostKeyUnknown,
                        $"The SSH host key of {who} ({where}) isn't trusted yet. Check its fingerprint, SHA256:{unknown.Fingerprint}, and trust it to connect.",
                        new PresentedHostKey(end.Trust.HostId, end.Trust.ServiceId, unknown.Algorithm, unknown.Fingerprint));
                case { Verdict: TrustVerdict.Changed } changed:
                    var knownFingerprint = end.KnownKeys.First(k => k.Algorithm == changed.Algorithm).Fingerprint;
                    await trust.MismatchAsync(end.Trust, changed.Algorithm, knownFingerprint, changed.Fingerprint, CancellationToken.None);
                    throw new SshConnectException(
                        SshConnectFailure.HostKeyChanged,
                        $"The SSH host key of {who} ({where}) has changed since it was trusted. This happens when a server is reinstalled, "
                        + "but it can also mean someone is intercepting the connection. If the change is expected, remove the trusted key from the host and connect again.");
            }

            throw Translate(ex, who, where, end.Username, isJump);
        }
        finally
        {
            // Signed in or not, the credential isn't needed any more: SSH doesn't sign in again on the same connection.
            foreach (var method in methods.OfType<IDisposable>())
            {
                method.Dispose();
            }
        }
    }

    /// <summary>The private key first (the password is its passphrase), then the password, also through keyboard-interactive.</summary>
    private static List<AuthenticationMethod> Methods(SshEndpoint end, bool isJump)
    {
        var methods = new List<AuthenticationMethod>();
        var password = end.Password?.Reveal();
        if (end.PrivateKey is { } privateKey)
        {
            try
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(privateKey.Reveal()));
                methods.Add(new PrivateKeyAuthenticationMethod(end.Username, string.IsNullOrEmpty(password) ? new PrivateKeyFile(stream) : new PrivateKeyFile(stream, password)));
            }
            catch (Exception ex) when (ex is SshException or ArgumentException or InvalidOperationException or FormatException or NotSupportedException)
            {
                throw new SshConnectException(
                    isJump ? SshConnectFailure.JumpHostUnavailable : SshConnectFailure.InvalidPrivateKey,
                    $"The SSH private key{(isJump ? $" of the jump host “{end.Name}”'s credential" : string.Empty)} couldn't be read. "
                    + "Check its format, and that the credential's password is the key's passphrase if it has one.");
            }
        }

        if (password is not null)
        {
            methods.Add(new PasswordAuthenticationMethod(end.Username, password));

            // Many servers (Ubuntu's default, for one) only take passwords through keyboard-interactive.
            var keyboard = new KeyboardInteractiveAuthenticationMethod(end.Username);
            keyboard.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                {
                    prompt.Response = password;
                }
            };
            methods.Add(keyboard);
        }

        return methods;
    }

    /// <summary>A port on the loopback, forwarded through the jump host to the target.</summary>
    private static ForwardedPortLocal Forward(SshClient jump, SshEndpoint end, string address, int port)
    {
        var forward = new ForwardedPortLocal("127.0.0.1", 0, address, (uint)port);
        try
        {
            jump.AddForwardedPort(forward);
            forward.Start();
            return forward;
        }
        catch (Exception ex) when (ex is SshException or SocketException or InvalidOperationException)
        {
            forward.Dispose();
            throw new SshConnectException(
                SshConnectFailure.JumpHostUnavailable,
                $"The jump host “{end.Name}” wouldn't forward to {address}:{port}. It must allow TCP forwarding.");
        }
    }

    private static SshConnectException Translate(Exception ex, string who, string where, string username, bool isJump)
    {
        var (reason, message) = ex switch
        {
            SshAuthenticationException => (SshConnectFailure.AuthenticationFailed, $"{who} refused the sign-in for {username}. Check the username, password or key."),
            SocketException => (SshConnectFailure.Unreachable, $"Couldn't connect to {who} at {where}."),
            SshOperationTimeoutException or OperationCanceledException or TimeoutException => (SshConnectFailure.TimedOut, $"Timed out connecting to {who} at {where}."),
            SshConnectionException => (SshConnectFailure.Unreachable, $"The SSH connection to {who} at {where} failed."),
            _ => (SshConnectFailure.Unreachable, $"Couldn't open an SSH connection to {who} at {where}."),
        };

        // A jump host that can't be used makes the host unreachable, whatever the reason.
        return new SshConnectException(isJump ? SshConnectFailure.JumpHostUnavailable : reason, message);
    }

    internal static void Close(ForwardedPortLocal? forward, SshClient? jump)
    {
        try
        {
            forward?.Stop();
        }
        catch (ObjectDisposedException)
        {
        }

        forward?.Dispose();
        jump?.Dispose();
    }
}

/// <summary>A tunnel through a jump host. Disposing it closes the forward and the jump host's connection.</summary>
internal sealed class Tunnel(ForwardedPortLocal forward, SshClient jump) : IJumpTunnel
{
    public int LocalPort { get; } = (int)forward.BoundPort;

    public void Dispose() => SshConnectivity.Close(forward, jump);
}

/// <summary>An open connection. Disposing it closes it, then its tunnel and the jump host's connection.</summary>
internal sealed class SshConnection(SshClient client, ForwardedPortLocal? forward, SshClient? jump) : ISshConnection
{
    public static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(60);

    public async Task<SshCommandResult> RunAsync(string command, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(command);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout ?? DefaultCommandTimeout);
        using var run = client.CreateCommand(command);
        await run.ExecuteAsync(limit.Token);
        return new SshCommandResult(run.ExitStatus, run.Result, run.Error);
    }

    public Task<ISshShell> OpenShellAsync(SshTerminal terminal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        var stream = client.CreateShellStream(terminal.Type, (uint)terminal.Columns, (uint)terminal.Rows, 0, 0, 4096);
        return Task.FromResult<ISshShell>(new SshShell(stream));
    }

    public ValueTask DisposeAsync()
    {
        client.Dispose();
        SshConnectivity.Close(forward, jump);
        return ValueTask.CompletedTask;
    }
}

internal sealed class SshShell(ShellStream stream) : ISshShell
{
    public Stream Stream => stream;

    public void Resize(int columns, int rows) => stream.ChangeWindowSize((uint)columns, (uint)rows, 0, 0);

    public ValueTask DisposeAsync() => stream.DisposeAsync();
}

public static class SshConnectivityServices
{
    /// <summary><see cref="IConnectivity"/>, for plugins' endpoints. Needs the inventory (AddNKeeperInventory).</summary>
    /// <summary>SSH connections, and the jump host tunnels core's other connections take (replacing core's refusal).</summary>
    public static IServiceCollection AddNKeeperSshConnectivity(this IServiceCollection services) =>
        services
            .AddScoped<SshConnectivity>()
            .AddScoped<IConnectivity>(provider => provider.GetRequiredService<SshConnectivity>())
            .Replace(ServiceDescriptor.Scoped<IJumpTunnels>(provider => provider.GetRequiredService<SshConnectivity>()));
}
