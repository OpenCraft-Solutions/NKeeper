using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NKeeper.Abstractions;
using NKeeper.Core.Access;
using NKeeper.Core.Credentials;
using NKeeper.Core.Inventory;
using NKeeper.Core.Persistence;
using NKeeper.Core.Trust;

namespace NKeeper.Core.Connectivity;

/// <summary>One end of an SSH connection, secrets included. In memory only, for one connection attempt.</summary>
/// <param name="Name">The host's name, for messages.</param>
/// <param name="Username">As sent to the server: <c>DOMAIN\user</c> when there's a domain.</param>
/// <param name="KnownKeys">The keys trusted for <paramref name="Trust"/>.</param>
public sealed record SshEndpoint(
    TrustEndpoint Trust,
    string Name,
    string Address,
    int Port,
    string Username,
    Secret? Password,
    Secret? PrivateKey,
    IReadOnlyList<HostTrustedKey> KnownKeys);

/// <summary>
/// What core needs to open one SSH connection (docs/architecture.md §7.9): the target, its jump host, and who
/// asked. Disposing it clears the jump host's secrets; the target's belong to the handle.
/// </summary>
/// <param name="SessionKey">The auth session to remember an unknown key for, so the user can trust it.</param>
/// <param name="Credential">How the audit names the credential used.</param>
public sealed record SshPlan(SshEndpoint Target, SshEndpoint? Jump, string PluginId, string? SessionKey, string Credential) : IDisposable
{
    public void Dispose()
    {
        Jump?.Password?.Dispose();
        Jump?.PrivateKey?.Dispose();
    }
}

/// <summary>
/// Works out an SSH connection from a plugin's request: checks the handle, then finds the service's endpoint, the
/// jump host and its credential, and the trusted keys. <c>NKeeper.Connectivity.Ssh</c> then connects.
/// </summary>
public sealed class SshConnectionPlanner(
    CredentialBroker broker,
    CoreDbContext db,
    HostAccess access,
    ServiceTypeCatalog serviceTypes,
    CredentialProtector protector,
    TrustStore trust,
    IAuthSessionAccessor sessions,
    IHttpContextAccessor http)
{
    /// <exception cref="CredentialDeniedException">The handle isn't valid here.</exception>
    /// <exception cref="SshConnectException">The credential can't sign in, or the jump host can't be used.</exception>
    public async Task<SshPlan> PlanAsync(HostContext host, Guid serviceId, CredentialHandle handle, CancellationToken cancellationToken)
    {
        var (pluginId, _, entry) = await broker.UseAsync(host, serviceId, handle, cancellationToken);
        var context = http.HttpContext!;
        var service = host.Services.First(s => s.Id == serviceId);
        var stored = await db.HostServices.AsNoTracking().Where(s => s.Id == serviceId && s.HostId == host.Id).Select(s => new { s.Address }).FirstOrDefaultAsync(cancellationToken);
        var row = await db.Hosts.AsNoTracking().Where(h => h.Id == host.Id).Select(h => new { h.JumpHostId, h.JumpServiceId, h.JumpCredentialId }).FirstOrDefaultAsync(cancellationToken);
        if (stored is null || row is null || service.Address is null)
        {
            throw new SshConnectException(SshConnectFailure.Unreachable, $"“{host.Name}” or this service was removed meanwhile. Reload the host and try again.");
        }

        if (service.Port is not { } port)
        {
            throw new SshConnectException(SshConnectFailure.Unreachable, $"This service of “{host.Name}” has no port. Edit the host to set one.");
        }

        var material = entry.Material;
        if (string.IsNullOrEmpty(material.Username))
        {
            throw new SshConnectException(SshConnectFailure.AuthenticationFailed, "This credential has no username. Edit the host to add one, or type one.");
        }

        if (material.Password is null && material.PrivateKey is null)
        {
            throw new SshConnectException(SshConnectFailure.NoSecret, "This credential has no password or private key.");
        }

        var endpoint = new TrustEndpoint(host.Id, serviceId, TrustedKeyKind.SshHostKey, stored.Address ?? string.Empty, port);
        var target = new SshEndpoint(
            endpoint,
            host.Name,
            service.Address,
            port,
            UserName(material.Username, material.Domain),
            material.Password,
            material.PrivateKey,
            await trust.KnownAsync(endpoint, cancellationToken));

        var jump = row.JumpHostId is { } jumpHostId
            ? await JumpAsync(jumpHostId, row.JumpServiceId, row.JumpCredentialId, RequestUser.From(context.User), cancellationToken)
            : null;
        var credential = material.Source == CredentialSource.Saved ? $"credential {material.CredentialId}" : "a typed credential";
        if (jump is not null)
        {
            credential += $", through jump host {jump.Trust.HostId} with credential {row.JumpCredentialId}";
        }

        return new SshPlan(target, jump, pluginId, sessions.CurrentSessionKey(context), credential);
    }

    /// <summary>
    /// The jump host's end alone, for a tunnel to one of the host's services that isn't SSH (docs/architecture.md
    /// §7.16): null when the host has no jump host. The plan's <see cref="SshPlan.Target"/> is the jump host, and
    /// disposing the plan clears its secrets.
    /// </summary>
    /// <exception cref="SshConnectException">The jump host can't be used.</exception>
    public async Task<SshPlan?> PlanTunnelAsync(HostContext host, CancellationToken cancellationToken)
    {
        var context = http.HttpContext!;
        var row = await db.Hosts.AsNoTracking().Where(h => h.Id == host.Id).Select(h => new { h.JumpHostId, h.JumpServiceId, h.JumpCredentialId }).FirstOrDefaultAsync(cancellationToken);
        if (row?.JumpHostId is not { } jumpHostId)
        {
            return null;
        }

        var jump = await JumpAsync(jumpHostId, row.JumpServiceId, row.JumpCredentialId, RequestUser.From(context.User), cancellationToken);
        return new SshPlan(jump, null, PluginCaller.Of(context) ?? string.Empty, sessions.CurrentSessionKey(context), $"jump host {jumpHostId} with credential {row.JumpCredentialId}");
    }

    /// <summary>Windows OpenSSH takes a domain account as <c>DOMAIN\user</c>.</summary>
    private static string UserName(string username, string? domain) => domain is null ? username : $"{domain}\\{username}";

    /// <summary>
    /// The jump host's end (§7.8). The user must be able to use the jump host too, so it can't lend its
    /// credential to someone who can't see it. Its credential is read here, and never reaches a plugin.
    /// </summary>
    private async Task<SshEndpoint> JumpAsync(Guid jumpHostId, Guid? serviceId, Guid? credentialId, RequestUser user, CancellationToken cancellationToken)
    {
        if (await access.GetPermissionAsync(jumpHostId, user, cancellationToken) == Access.HostPermission.None)
        {
            throw new SshConnectException(
                SshConnectFailure.JumpHostUnavailable,
                "This host is reached through a jump host you can't use. Ask an administrator for access to the jump host too.");
        }

        var jump = await db.Hosts.AsNoTracking().Include(h => h.Services).Include(h => h.Credentials).AsSplitQuery()
            .FirstAsync(h => h.Id == jumpHostId, cancellationToken);
        var service = jump.Services.FirstOrDefault(s => s.Id == serviceId)
            ?? throw new SshConnectException(SshConnectFailure.JumpHostUnavailable, $"The jump host “{jump.Name}”'s service was removed. Edit the host to choose another.");
        var credential = jump.Credentials.FirstOrDefault(c => c.Id == credentialId)
            ?? throw new SshConnectException(SshConnectFailure.JumpHostUnavailable, $"The jump host “{jump.Name}”'s credential was removed. Edit the host to choose another.");
        if (credential.ServiceIds is { } scope && !scope.Contains(service.Id))
        {
            throw new SshConnectException(SshConnectFailure.JumpHostUnavailable, $"The jump host “{jump.Name}”'s credential isn't used with its service any more. Edit the jump host or this host.");
        }

        if (string.IsNullOrEmpty(credential.Username) || (credential.ProtectedPassword is null && credential.ProtectedPrivateKey is null))
        {
            throw new SshConnectException(SshConnectFailure.JumpHostUnavailable, $"The jump host “{jump.Name}”'s credential has no username, or no password or private key. Edit the jump host.");
        }

        if ((service.Port ?? serviceTypes.Find(service.Type)?.DefaultPort) is not { } port)
        {
            throw new SshConnectException(SshConnectFailure.JumpHostUnavailable, $"The jump host “{jump.Name}”'s service has no port. Edit the jump host to set one.");
        }

        var endpoint = new TrustEndpoint(jump.Id, service.Id, TrustedKeyKind.SshHostKey, service.Address ?? string.Empty, port);
        return new SshEndpoint(
            endpoint,
            jump.Name,
            service.Address ?? jump.IpAddress ?? jump.Hostname!,
            port,
            UserName(credential.Username, credential.Domain),
            credential.ProtectedPassword is null ? null : protector.Unprotect(credential.Id, credential.ProtectedPassword),
            credential.ProtectedPrivateKey is null ? null : protector.Unprotect(credential.Id, credential.ProtectedPrivateKey),
            await trust.KnownAsync(endpoint, cancellationToken));
    }
}
