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

/// <summary>Marks core's stream session endpoint (docs/architecture.md §7.10). Not in the SDK.</summary>
public sealed class SessionEndpointMetadata;

/// <summary>
/// Which plugin a request acts for, from where core routed it, never from anything the plugin says: a plugin's
/// own endpoint, or a stream session of the plugin's type. Used by the credential broker and the plugin audit.
/// </summary>
public static class PluginCaller
{
    public static string? Of(HttpContext? context)
    {
        var metadata = context?.GetEndpoint()?.Metadata;
        return metadata?.GetMetadata<PluginEndpointMetadata>()?.PluginId
            ?? (metadata?.GetMetadata<SessionEndpointMetadata>() is null ? null : context!.Features.Get<SessionPluginFeature>()?.PluginId);
    }

    /// <summary>
    /// Whether <paramref name="host"/> is one core authorized for this request: the host-scoped endpoint's own, or one
    /// an integration resolved for this user (docs/architecture.md §7.39). A plugin could make up any other.
    /// </summary>
    public static bool Authorized(HttpContext context, HostContext host) =>
        ReferenceEquals(context.Features.Get<HostContext>(), host)
        || context.Features.Get<IntegrationHosts>()?.Contains(host) == true;
}

/// <summary>The hosts integrations resolved for this request's user (SDK 1.13), which its plugin may then use.</summary>
public sealed class IntegrationHosts
{
    private readonly List<HostContext> _hosts = [];

    public void Add(HostContext host) => _hosts.Add(host);

    public bool Contains(HostContext host) => _hosts.Exists(h => ReferenceEquals(h, host));
}

/// <summary>On core's stream session endpoint: the plugin whose session type is being opened.</summary>
public sealed class SessionPluginFeature(string pluginId)
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
    IAuditLog audit,
    NKeeper.Core.Access.HostAccess access,
    NKeeper.Core.Inventory.ServiceTypeCatalog serviceTypes) : ICredentialBroker
{
    /// <summary>
    /// The plugin's bound integration, for this user only if they may use its host (docs/architecture.md §7.39, SDK
    /// 1.13, the owner's decision): then its credential is resolved exactly as a saved one the user chose.
    /// </summary>
    public async Task<IntegrationHandle> ResolveIntegrationAsync(string name, CancellationToken cancellationToken = default)
    {
        var context = http.HttpContext;
        var plugin = CallerPlugin(context);
        if (context is null || plugin is null || context.User.Identity?.IsAuthenticated != true)
        {
            throw new InvalidOperationException("Integrations can only be resolved from a plugin's own endpoints.");
        }

        var binding = await db.PluginIntegrations.AsNoTracking().FirstOrDefaultAsync(b => b.PluginId == plugin && b.Name == name, cancellationToken)
            ?? throw new IntegrationUnavailableException(IntegrationUnavailableReason.NotBound, "An administrator hasn't chosen which service this uses yet. Set it on the Settings page.");

        var permission = await access.GetPermissionAsync(binding.HostId, NKeeper.Core.Access.RequestUser.From(context.User), cancellationToken);
        if (permission == NKeeper.Core.Access.HostPermission.None)
        {
            throw new IntegrationUnavailableException(IntegrationUnavailableReason.NoAccess, "You don't have access to the host this uses. Ask an administrator for access to it.");
        }

        var host = await NKeeper.Core.Access.HostScopeMiddleware.LoadAsync(db, serviceTypes, binding.HostId, permission, cancellationToken);
        if (host is null || !host.Services.Any(s => s.Id == binding.ServiceId))
        {
            throw new IntegrationUnavailableException(IntegrationUnavailableReason.Gone, "The host or service this uses is gone. Choose another on the Settings page.");
        }

        // The host is now one this request may use (§7.39), as a host-scoped endpoint's own is.
        var hosts = context.Features.Get<IntegrationHosts>();
        if (hosts is null)
        {
            hosts = new IntegrationHosts();
            context.Features.Set(hosts);
        }

        hosts.Add(host);
        try
        {
            return new IntegrationHandle(host, binding.ServiceId, await ResolveSavedAsync(host, binding.ServiceId, binding.CredentialId, cancellationToken));
        }
        catch (CredentialDeniedException ex)
        {
            throw new IntegrationUnavailableException(IntegrationUnavailableReason.Gone, $"The credential this uses can't be used any more: {ex.Message}");
        }
    }

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

    public async Task<CredentialDescription> DescribeAsync(HostContext host, Guid serviceId, CredentialHandle handle, CancellationToken cancellationToken = default) =>
        (await UseAsync(host, serviceId, handle, cancellationToken)).Entry.Material.Describe();

    /// <summary>
    /// Checks a handle for a use: called from a plugin's endpoint, with this request's host context, for one of
    /// its services, and every binding of the handle matching (docs/architecture.md §7.6). Refusals are audited.
    /// </summary>
    /// <exception cref="CredentialDeniedException">Any check failed.</exception>
    internal async Task<(string PluginId, Guid UserId, CredentialHandleEntry Entry)> UseAsync(HostContext host, Guid serviceId, CredentialHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var caller = await AllowedAsync(host, serviceId, cancellationToken);
        return handles.Find(handle) is { } entry
            && entry.UserId == caller.UserId
            && entry.HostId == host.Id
            && entry.ServiceId == serviceId
            && entry.PluginId == caller.PluginId
            && entry.RequestId == Tokens.RequestId(caller.Context)
            ? (caller.PluginId, caller.UserId, entry)
            : throw await DenyAsync(caller, host, serviceId, CredentialDenial.HandleInvalid, "This credential handle isn't valid here.", cancellationToken);
    }

    private CredentialHandle Issue(Caller caller, HostContext host, Guid serviceId, CredentialMaterial material) =>
        handles.Add(caller.Context, new CredentialHandleEntry(caller.UserId, host.Id, serviceId, caller.PluginId, Tokens.RequestId(caller.Context), material));

    private async Task<Caller> AllowedAsync(HostContext host, Guid serviceId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        var context = http.HttpContext;
        var plugin = CallerPlugin(context);
        var userId = Guid.TryParse(context?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : (Guid?)null;
        var caller = new Caller(context!, plugin ?? string.Empty, userId ?? Guid.Empty);

        // Only the host context core authorized for this request counts: a plugin could make up another.
        if (context is null || plugin is null || userId is null || !PluginCaller.Authorized(context, host))
        {
            throw await DenyAsync(caller, host, serviceId, CredentialDenial.NotAllowed, "Credentials can only be resolved from a plugin's host-scoped endpoint, for its own host.", cancellationToken);
        }

        if (!host.Services.Any(s => s.Id == serviceId))
        {
            throw await DenyAsync(caller, host, serviceId, CredentialDenial.NotForService, "The host has no such service. Reload the host and try again.", cancellationToken);
        }

        return caller;
    }

    /// <summary>
    /// The plugin this request acts for: a plugin's own endpoint names it in core's metadata, and core's stream
    /// session endpoint (§7.10) in a feature it sets for the session type's plugin. Nothing a plugin says counts.
    /// </summary>
    private static string? CallerPlugin(HttpContext? context) => PluginCaller.Of(context);

    private async Task<CredentialDeniedException> DenyAsync(Caller caller, HostContext host, Guid serviceId, CredentialDenial reason, string message, CancellationToken cancellationToken)
    {
        var plugin = caller.PluginId.Length == 0 ? "(none)" : caller.PluginId;
        await audit.RecordAsync(new AuditEntry(AuditActions.CredentialDenied, host.Id.ToString(), $"{reason}: service {serviceId}, plugin {plugin}"), cancellationToken);
        return new CredentialDeniedException(reason, message);
    }

    private sealed record Caller(HttpContext Context, string PluginId, Guid UserId);
}
