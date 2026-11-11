using NKeeper.Abstractions;

namespace NKeeper.Core.Connectivity;

/// <summary>
/// Tunnels through a host's jump host (docs/architecture.md §7.8, §7.16), for connections core opens other than SSH.
/// <c>NKeeper.Connectivity.Ssh</c> provides them; without it, a host behind a jump host can't be reached this way.
/// </summary>
public interface IJumpTunnels
{
    /// <summary>A loopback port forwarded through the host's jump host to address:port; null when the host has none.</summary>
    /// <exception cref="ConnectException">With <see cref="ConnectFailure.JumpHostUnavailable"/>: the jump host can't be used.</exception>
    Task<IJumpTunnel?> OpenAsync(HostContext host, string address, int port, CancellationToken cancellationToken);
}

/// <summary>An open tunnel. Disposing it closes it, and the jump host's connection.</summary>
public interface IJumpTunnel : IDisposable
{
    /// <summary>The loopback port to dial: 127.0.0.1:LocalPort reaches the target.</summary>
    int LocalPort { get; }
}

/// <summary>No tunnels: a host behind a jump host is refused.</summary>
internal sealed class NoJumpTunnels(Persistence.CoreDbContext db) : IJumpTunnels
{
    public async Task<IJumpTunnel?> OpenAsync(HostContext host, string address, int port, CancellationToken cancellationToken) =>
        await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(db.Hosts, h => h.Id == host.Id && h.JumpHostId != null, cancellationToken)
            ? throw new ConnectException(ConnectFailure.JumpHostUnavailable, $"“{host.Name}” is reached through a jump host, and this server can't open tunnels.")
            : null;
}
