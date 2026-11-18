using System.Net.Sockets;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NKeeper.Abstractions;
using NKeeper.Core.Credentials;
using NKeeper.Core.Persistence;

namespace NKeeper.Core.Connectivity;

/// <summary>
/// Plain TCP connections to a service for plugins (docs/architecture.md §7.31, SDK 1.9), through the host's jump host
/// when it has one: for a plugin that speaks the server's own protocol before core signs in. Core sends nothing on them.
/// </summary>
public sealed class TcpConnector(CoreDbContext db, IHttpContextAccessor http, IJumpTunnels tunnels)
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    public async Task<ITcpConnection> OpenAsync(HostContext host, Guid serviceId, CancellationToken cancellationToken)
    {
        // Only for the host core authorized for this plugin request: a plugin could make up another.
        var context = http.HttpContext;
        if (context is null || PluginCaller.Of(context) is null || !ReferenceEquals(context.Features.Get<HostContext>(), host))
        {
            throw new InvalidOperationException("TCP connections can only be opened from a plugin's host-scoped endpoint, for its own host.");
        }

        var service = host.Services.FirstOrDefault(s => s.Id == serviceId);
        var exists = await db.HostServices.AsNoTracking().AnyAsync(s => s.Id == serviceId && s.HostId == host.Id, cancellationToken);
        if (service?.Address is null || !exists)
        {
            throw new ConnectException(ConnectFailure.Unreachable, $"“{host.Name}” has no such service any more. Reload the host and try again.");
        }

        if (service.Port is not { } port)
        {
            throw new ConnectException(ConnectFailure.Unreachable, $"This service of “{host.Name}” has no port. Edit the host to set one.");
        }

        IJumpTunnel? tunnel;
        try
        {
            tunnel = await tunnels.OpenAsync(host, service.Address, port, cancellationToken);
        }
        catch (ConnectException ex)
        {
            throw new ConnectException(ConnectFailure.JumpHostUnavailable, ex.Message);
        }

        var client = new TcpClient();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);
            try
            {
                await client.ConnectAsync(tunnel is null ? service.Address : "127.0.0.1", tunnel?.LocalPort ?? port, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ConnectException(ConnectFailure.TimedOut, $"{service.Address}:{port} didn't answer in time.");
            }
            catch (SocketException)
            {
                throw new ConnectException(ConnectFailure.Unreachable, $"Couldn't connect to {service.Address}:{port}.");
            }

            return new TcpConnection(client, tunnel, service.Address, port);
        }
        catch
        {
            client.Dispose();
            tunnel?.Dispose();
            throw;
        }
    }

    private sealed class TcpConnection(TcpClient client, IJumpTunnel? tunnel, string address, int port) : ITcpConnection
    {
        public Stream Stream { get; } = client.GetStream();

        public string Address => address;

        public int Port => port;

        public async ValueTask DisposeAsync()
        {
            await Stream.DisposeAsync();
            client.Dispose();
            tunnel?.Dispose();
        }
    }
}
