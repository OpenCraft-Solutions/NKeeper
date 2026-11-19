using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace NKeeper.Abstractions;

/// <summary>
/// An outside system the plugin works with for the whole instance, such as a Zabbix server (docs/architecture.md §7.39,
/// SDK 1.13). An administrator binds it to one host, one of its services of <see cref="ServiceTypes"/>, and one
/// credential used with that service. The outside system is an ordinary host, so its address, certificate and secret
/// stay core's.
/// </summary>
/// <param name="Name">Lower-case letters, digits and <c>-</c>, starting with a letter, up to 32 characters; unique within the plugin.</param>
/// <param name="Label">What administrators see, e.g. <c>Zabbix server</c>.</param>
public sealed record Integration(string Name, string Label)
{
    /// <summary>The service types it may be bound to; at least one.</summary>
    public IReadOnlyList<string> ServiceTypes { get; init; } = [];
}

/// <summary>A bound integration, resolved for the current user: its host, service and a handle for its credential.</summary>
public sealed record IntegrationHandle(HostContext Host, Guid ServiceId, CredentialHandle Credential);

/// <summary>Why an integration can't be used for this request (SDK 1.13).</summary>
public enum IntegrationUnavailableReason
{
    /// <summary>No administrator has bound it.</summary>
    NotBound,

    /// <summary>Its host, service or credential is gone.</summary>
    Gone,

    /// <summary>The user may not use its host: an integration serves only its host's users (§7.39).</summary>
    NoAccess,
}

/// <summary>An integration that can't be used for this request (SDK 1.13); <see cref="Reason"/> says why.</summary>
public sealed class IntegrationUnavailableException(IntegrationUnavailableReason reason, string message) : Exception(message)
{
    public IntegrationUnavailableReason Reason { get; } = reason;
}

/// <summary>How core signs HTTP requests in with a credential (docs/architecture.md §7.16, §7.39).</summary>
public enum HttpSignIn
{
    /// <summary>HTTP Basic, from the username and password (SDK 1.6).</summary>
    Basic,

    /// <summary><c>Authorization: Bearer</c> with the password, as an API token (SDK 1.13).</summary>
    Bearer,
}

/// <summary>
/// The plugin's settings (docs/architecture.md §7.39, SDK 1.13): one JSON document, with the schema version the plugin
/// gave it. It holds no secrets: those are credentials. Only the plugin's own endpoints read and write it.
/// </summary>
public interface IPluginSettings
{
    /// <summary>The document and its version, or null if none was saved.</summary>
    Task<PluginDocument?> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Replaces the document; audited as <c>plugin.settings_changed</c>.</summary>
    Task SetAsync(int version, object data, CancellationToken cancellationToken = default);
}

/// <summary>
/// The plugin's own small document per host (docs/architecture.md §7.39, SDK 1.13), such as which Zabbix host a host
/// is: at most 16 KB, deleted with the host, kept while the plugin is disabled. It checks no permission: the plugin's
/// endpoints decide who may read and change it.
/// </summary>
public interface IHostData
{
    public const int MaxBytes = 16 * 1024;

    Task<PluginDocument?> GetAsync(Guid hostId, CancellationToken cancellationToken = default);

    Task SetAsync(Guid hostId, int version, object data, CancellationToken cancellationToken = default);

    Task ClearAsync(Guid hostId, CancellationToken cancellationToken = default);

    /// <summary>Every host's document, by host.</summary>
    Task<IReadOnlyDictionary<Guid, PluginDocument>> AllAsync(CancellationToken cancellationToken = default);
}

/// <summary>A plugin's stored JSON and the schema version it was saved with (v2-plan.md §5).</summary>
public sealed record PluginDocument(int Version, JsonElement Data);

/// <summary>Endpoints only administrators reach (docs/architecture.md §7.39, SDK 1.13).</summary>
public static class AdministratorScope
{
    /// <summary>The role core gives administrators.</summary>
    public const string Role = "Administrator";

    /// <summary>A group at <c>admin/</c> under the plugin's endpoints that only administrators reach.</summary>
    public static RouteGroupBuilder MapAdministratorGroup(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup("admin");
        group.RequireAuthorization(new AuthorizeAttribute { Roles = Role });
        return group;
    }
}
