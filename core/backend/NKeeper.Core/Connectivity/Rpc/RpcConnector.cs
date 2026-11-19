using System.Net.Sockets;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NKeeper.Abstractions;
using NKeeper.Core.Audit;
using NKeeper.Core.Credentials;
using NKeeper.Core.Persistence;

namespace NKeeper.Core.Connectivity.Rpc;

/// <summary>
/// What WMI and RPC connections sign in with (docs/architecture.md §7.28): the service's address and port, and the
/// handle's credential, which needs a password. Neither goes through a jump host.
/// </summary>
internal sealed record WindowsTarget(string Address, int Port, string Username, string? Domain, string Password, string PluginId, string CredentialDescription)
{
    public static async Task<WindowsTarget> ResolveAsync(
        HostContext host, Guid serviceId, CredentialHandle handle, string what, CredentialBroker broker, CoreDbContext db, IHttpContextAccessor http, CancellationToken cancellationToken)
    {
        // Only for the host core authorized for this plugin request: a plugin could make up another.
        var context = http.HttpContext;
        var pluginId = PluginCaller.Of(context);
        if (context is null || pluginId is null || !PluginCaller.Authorized(context, host))
        {
            throw new InvalidOperationException($"{what} connections can only be opened from a plugin's host-scoped endpoint, for its own host.");
        }

        var (_, _, entry) = await broker.UseAsync(host, serviceId, handle, cancellationToken);
        var material = entry.Material;
        if (material.Password?.Reveal() is not { Length: > 0 } password || material.Username is not { Length: > 0 } typed)
        {
            throw new ConnectException(ConnectFailure.NoSecret, $"{what} signs in with a user name and password, and this credential doesn't have both.");
        }

        var service = host.Services.FirstOrDefault(s => s.Id == serviceId);
        var row = await db.HostServices.AsNoTracking().Where(s => s.Id == serviceId && s.HostId == host.Id).Select(s => new { s.Id }).FirstOrDefaultAsync(cancellationToken);
        if (service?.Address is null || row is null)
        {
            throw new ConnectException(ConnectFailure.Unreachable, $"“{host.Name}” has no such service any more. Reload the host and try again.");
        }

        if (await db.Hosts.AsNoTracking().Where(h => h.Id == host.Id).Select(h => h.JumpHostId).FirstOrDefaultAsync(cancellationToken) is not null)
        {
            throw new ConnectException(ConnectFailure.JumpHostUnavailable, $"{what} can't go through a jump host, and “{host.Name}” is reached through one.");
        }

        // DOMAIN\user, as 1.x accepted it, when the credential has no domain of its own.
        var (username, domain) = material.Domain is { Length: > 0 } || !typed.Contains('\\', StringComparison.Ordinal)
            ? (typed, material.Domain)
            : (typed[(typed.IndexOf('\\', StringComparison.Ordinal) + 1)..], typed[..typed.IndexOf('\\', StringComparison.Ordinal)]);
        var credential = material.Source == CredentialSource.Saved ? $"credential {material.CredentialId}" : "a typed credential";
        return new WindowsTarget(service.Address, service.Port ?? RpcClient.EndpointMapperPort, username, domain, password, pluginId, credential);
    }
}

/// <summary>DCE/RPC connections for plugins (docs/architecture.md §7.28, SDK 1.5), with 1.x's <see cref="RpcClient"/>.</summary>
public sealed class RpcConnector(CredentialBroker broker, CoreDbContext db, IHttpContextAccessor http, IAuditLog audit)
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    public async Task<IRpcConnection> OpenAsync(HostContext host, Guid serviceId, CredentialHandle handle, RpcInterface rpcInterface, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rpcInterface);
        var target = await WindowsTarget.ResolveAsync(host, serviceId, handle, "RPC", broker, db, http, cancellationToken);
        var client = new RpcClient();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);
            try
            {
                await client.ConnectAsync(target.Address, target.Port, timeout.Token);
                await client.BindAsync(rpcInterface.Id, rpcInterface.Major, rpcInterface.Minor, new RpcCredentials(target.Username, target.Password, target.Domain), target.Address, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ConnectException(ConnectFailure.TimedOut, $"{target.Address}:{target.Port} didn't answer in time.");
            }
            catch (RpcException ex)
            {
                throw new ConnectException(ConnectFailure.AuthenticationFailed, ex.Message);
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                throw new ConnectException(ConnectFailure.Unreachable, $"Couldn't connect to {target.Address}:{target.Port} over RPC.");
            }

            await audit.RecordAsync(new AuditEntry(AuditActions.CredentialUse, host.Id.ToString(), $"rpc to service {serviceId}, plugin {target.PluginId}, {target.CredentialDescription}"), cancellationToken);
            return new RpcConnection(client);
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    private sealed class RpcConnection(RpcClient client) : IRpcConnection
    {
        // One call at a time: the protocol answers calls in order on the connection.
        private readonly SemaphoreSlim _lock = new(1, 1);

        public async Task<byte[]> CallAsync(ushort opnum, ReadOnlyMemory<byte> stub, CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(CallTimeout);
                return await client.CallAsync(opnum, stub, timeout.Token);
            }
            catch (RpcException ex)
            {
                throw new RpcFaultException(ex.Message, ex.Status);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ConnectException(ConnectFailure.TimedOut, "The server didn't answer the call in time.");
            }
            catch (Exception ex) when (ex is IOException or SocketException or EndOfStreamException)
            {
                throw new ConnectException(ConnectFailure.Unreachable, "The connection to the server was lost.");
            }
            finally
            {
                _lock.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await client.DisposeAsync();
            _lock.Dispose();
        }
    }
}
