using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NKeeper.Abstractions;
using NKeeper.Core.Inventory;
using NKeeper.Core.Persistence;

namespace NKeeper.Core.Access;

/// <summary>
/// Authorizes host-scoped endpoints (docs/architecture.md §7.3) before they run, and gives them their
/// <see cref="HostContext"/>. Middleware, so it covers every kind of endpoint a plugin can map.
/// </summary>
/// <remarks>Runs after authorization, which has already required a sign-in.</remarks>
public sealed class HostScopeMiddleware(RequestDelegate next)
{
    public const string HostManageDeniedCode = "host_manage_denied";

    public async Task InvokeAsync(HttpContext context, HostAccess access, CoreDbContext db, ServiceTypeCatalog serviceTypes)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<HostScopeMetadata>() is not { } scope)
        {
            await next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var permission = Guid.TryParse(context.Request.RouteValues[HostScope.RouteParameter]?.ToString(), out var hostId)
            ? await access.GetPermissionAsync(hostId, RequestUser.From(context.User), context.RequestAborted)
            : Access.HostPermission.None;

        if (permission == Access.HostPermission.None)
        {
            // Hidden and missing hosts look the same.
            await Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found").ExecuteAsync(context);
            return;
        }

        if (scope.Required == Abstractions.HostPermission.Manage && permission != Access.HostPermission.Manage)
        {
            await Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Not allowed",
                    detail: "You can use this host but not change it. Ask an administrator for Manage access.",
                    extensions: new Dictionary<string, object?> { ["code"] = HostManageDeniedCode })
                .ExecuteAsync(context);
            return;
        }

        if (await LoadAsync(db, serviceTypes, hostId, permission, context.RequestAborted) is not { } host)
        {
            // Deleted since the check.
            await Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found").ExecuteAsync(context);
            return;
        }

        context.Features.Set(host);
        await next(context);
    }

    /// <summary>The host as plugins see it, for a user with <paramref name="permission"/> on it; null when it's gone.</summary>
    public static async Task<HostContext?> LoadAsync(CoreDbContext db, ServiceTypeCatalog serviceTypes, Guid hostId, Access.HostPermission permission, CancellationToken cancellationToken)
    {
        var host = await db.Hosts.AsNoTracking().Include(h => h.Tags).Include(h => h.Services).AsSplitQuery()
            .FirstOrDefaultAsync(h => h.Id == hostId, cancellationToken);
        return host is null
            ? null
            : new HostContext(
                host.Id,
                host.Name,
                host.Hostname,
                host.IpAddress,
                [.. host.Tags.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Select(t => new Abstractions.HostTag(t.Name, t.Value.Length == 0 ? null : t.Value))],
                permission == Access.HostPermission.Manage ? Abstractions.HostPermission.Manage : Abstractions.HostPermission.Use)
            {
                Services = [.. host.Services.OrderBy(s => s.Position).Select(s => ServiceFor(host, s, serviceTypes))],
            };
    }
    /// <summary>A service as plugins see it: the defaults applied, and the settings in the type's current version.</summary>
    private static Abstractions.HostService ServiceFor(Host host, Persistence.HostService service, ServiceTypeCatalog serviceTypes)
    {
        var (settings, type) = serviceTypes.Read(service);
        return new Abstractions.HostService(
            service.Id,
            service.Type,
            service.Label,
            service.Port ?? type?.DefaultPort,
            service.Address ?? host.IpAddress ?? host.Hostname,
            settings,
            type is not null);
    }
}
