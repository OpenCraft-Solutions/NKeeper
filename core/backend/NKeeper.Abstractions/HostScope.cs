using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace NKeeper.Abstractions;

/// <summary>What a host-scoped endpoint needs the user to be allowed on its host (docs/architecture.md §7.3).</summary>
public enum HostPermission
{
    /// <summary>See the host and connect to it.</summary>
    Use = 1,

    /// <summary>Also change it.</summary>
    Manage = 2,
}

/// <summary>A host's tag: a name with an optional value.</summary>
public sealed record HostTag(string Name, string? Value);

/// <summary>
/// The host a host-scoped request acts on, already authorized by core (SDK 0.1.1; services 0.1.2). It holds
/// no secrets: a plugin gets a credential from <see cref="ICredentialBroker"/>, as a handle.
/// </summary>
public sealed class HostContext(Guid id, string name, string? hostname, string? ipAddress, IReadOnlyList<HostTag> tags, HostPermission permission)
{
    public Guid Id { get; } = id;

    public string Name { get; } = name;

    public string? Hostname { get; } = hostname;

    public string? IpAddress { get; } = ipAddress;

    public IReadOnlyList<HostTag> Tags { get; } = tags;

    /// <summary>The host's services, in order (SDK 0.1.2).</summary>
    public IReadOnlyList<HostService> Services { get; init; } = [];

    /// <summary>The user's permission on the host: at least what the endpoint's group requires.</summary>
    public HostPermission Permission { get; } = permission;

    /// <summary>Lets a route handler take <see cref="HostContext"/> as a parameter. Fails outside a host group.</summary>
    public static ValueTask<HostContext?> BindAsync(HttpContext context) => ValueTask.FromResult<HostContext?>(context.GetHostContext());
}

/// <summary>Marks an endpoint as acting on the host in its route, needing <see cref="Required"/>. Core enforces it.</summary>
public sealed class HostScopeMetadata(HostPermission required)
{
    public HostPermission Required { get; } = required;
}

public static class HostScope
{
    /// <summary>The route parameter that names the host.</summary>
    public const string RouteParameter = "hostId";

    /// <summary>
    /// A group at <c>hosts/{hostId}/</c> whose endpoints act on one host. Core checks the user's
    /// permission on it before any of them runs: a host the user can't see answers 404, and a
    /// Use-only user gets 403 on a <see cref="HostPermission.Manage"/> group.
    /// </summary>
    public static RouteGroupBuilder MapHostGroup(this IEndpointRouteBuilder endpoints, HostPermission required) =>
        endpoints.MapGroup($"/hosts/{{{RouteParameter}:guid}}").WithMetadata(new HostScopeMetadata(required));

    /// <summary>The authorized host of this request.</summary>
    /// <exception cref="InvalidOperationException">The endpoint isn't in a host group.</exception>
    public static HostContext GetHostContext(this HttpContext context) =>
        context.Features.Get<HostContext>()
        ?? throw new InvalidOperationException("This endpoint isn't in a host group (MapHostGroup), so it has no host.");
}
