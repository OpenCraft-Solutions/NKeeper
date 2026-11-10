using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NKeeper.Abstractions;
using NKeeper.Core.Audit;
using NKeeper.Core.Persistence;

namespace NKeeper.Core.Credentials;

/// <summary>
/// Marks an endpoint as a plugin's, with the plugin's ID. Core adds it to each plugin's route group; it isn't
/// in the SDK, so a plugin can't claim to be another.
/// </summary>
public sealed class PluginEndpointMetadata(string pluginId)
{
    public string PluginId { get; } = pluginId;
}

/// <summary>Reads the current auth session's key. Identity provides it; core can't depend on Identity.</summary>
public interface IAuthSessionAccessor
{
    string? CurrentSessionKey(HttpContext context);
}

/// <summary>
/// <see cref="ICredentialBroker"/> (docs/architecture.md §7.6). Each method first checks that it's called from
/// a plugin's endpoint, with the <see cref="HostContext"/> core gave this request, for one of its services.
/// </summary>
public sealed class CredentialBroker(
    IHttpContextAccessor http,
    CoreDbContext db,
    CredentialProtector protector,
    CredentialHandleStore handles,
    CredentialPromptStore prompts,
    IAuthSessionAccessor sessions,
    IAuditLog audit) : ICredentialBroker
{
    public async Task<CredentialHandle> ResolveSavedAsync(HostContext host, Guid serviceId, Guid credentialId, CancellationToken cancellationToken = default)
    {
        var caller = await AllowedAsync(host, serviceId, cancellationToken);
        var credential = await db.HostCredentials.AsNoTracking().FirstOrDefaultAsync(c => c.Id == credentialId && c.HostId == host.Id, cancellationToken);
        if (credential is null)
        {
            throw await DenyAsync(caller, host, serviceId, CredentialDenial.NotFound, "This credential isn't on the host any more.", cancellationToken);
        }

        if (credential.ServiceIds is { } scope && !scope.Contains(serviceId))
        {
            throw await DenyAsync(caller, host, serviceId, CredentialDenial.NotForService, "This credential isn't used with this service. Edit the host to use it with this service too.", cancellationToken);
        }

        var material = new CredentialMaterial(
            CredentialSource.Saved,
            credential.Id,
            credential.Username,
            credential.Domain,
            credential.ProtectedPassword is null ? null : protector.Unprotect(credential.Id, credential.ProtectedPassword),
            credential.ProtectedPrivateKey is null ? null : protector.Unprotect(credential.Id, credential.ProtectedPrivateKey));
        return Issue(caller, host, serviceId, material);
    }

    public async Task<CredentialHandle> ResolvePromptAsync(HostContext host, Guid serviceId, string promptReference, CancellationToken cancellationToken = default)
    {
        var caller = await AllowedAsync(host, serviceId, cancellationToken);
        var material = string.IsNullOrEmpty(promptReference)
            ? null
            : prompts.Take(promptReference, sessions.CurrentSessionKey(caller.Context), host.Id, serviceId);
        return material is null
            ? throw await DenyAsync(caller, host, serviceId, CredentialDenial.PromptInvalid, "The typed credential has expired or was already used. Enter it again.", cancellationToken)
            : Issue(caller, host, serviceId, material);
    }

    public async Task<CredentialDescription> DescribeAsync(HostContext host, Guid serviceId, CredentialHandle handle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var caller = await AllowedAsync(host, serviceId, cancellationToken);
        return handles.Find(handle) is { } entry
            && entry.UserId == caller.UserId
            && entry.HostId == host.Id
            && entry.ServiceId == serviceId
            && entry.PluginId == caller.PluginId
            && entry.RequestId == Tokens.RequestId(caller.Context)
            ? entry.Material.Describe()
            : throw await DenyAsync(caller, host, serviceId, CredentialDenial.HandleInvalid, "This credential handle isn't valid here.", cancellationToken);
    }

    private CredentialHandle Issue(Caller caller, HostContext host, Guid serviceId, CredentialMaterial material) =>
        handles.Add(caller.Context, new CredentialHandleEntry(caller.UserId, host.Id, serviceId, caller.PluginId, Tokens.RequestId(caller.Context), material));

    private async Task<Caller> AllowedAsync(HostContext host, Guid serviceId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        var context = http.HttpContext;
        var plugin = context?.GetEndpoint()?.Metadata.GetMetadata<PluginEndpointMetadata>()?.PluginId;
        var userId = Guid.TryParse(context?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : (Guid?)null;
        var caller = new Caller(context!, plugin ?? string.Empty, userId ?? Guid.Empty);

        // Only the host context core authorized for this request counts: a plugin could make up another.
        if (context is null || plugin is null || userId is null || !ReferenceEquals(context.Features.Get<HostContext>(), host))
        {
            throw await DenyAsync(caller, host, serviceId, CredentialDenial.NotAllowed, "Credentials can only be resolved from a plugin's host-scoped endpoint, for its own host.", cancellationToken);
        }

        if (!host.Services.Any(s => s.Id == serviceId))
        {
            throw await DenyAsync(caller, host, serviceId, CredentialDenial.NotForService, "The host has no such service. Reload the host and try again.", cancellationToken);
        }

        return caller;
    }

    private async Task<CredentialDeniedException> DenyAsync(Caller caller, HostContext host, Guid serviceId, CredentialDenial reason, string message, CancellationToken cancellationToken)
    {
        var plugin = caller.PluginId.Length == 0 ? "(none)" : caller.PluginId;
        await audit.RecordAsync(new AuditEntry(AuditActions.CredentialDenied, host.Id.ToString(), $"{reason}: service {serviceId}, plugin {plugin}"), cancellationToken);
        return new CredentialDeniedException(reason, message);
    }

    private sealed record Caller(HttpContext Context, string PluginId, Guid UserId);
}
