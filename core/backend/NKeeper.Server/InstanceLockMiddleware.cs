using Microsoft.EntityFrameworkCore;
using NKeeper.Core.Persistence;

namespace NKeeper.Server;

/// <summary>
/// 1.x's lock after an export to move the instance (docs/architecture.md §7.45): while it's locked, changes to hosts and
/// users answer 409 <c>instance_locked</c>, so nothing is changed here that the new server would never get. Reading,
/// and connecting to hosts (a typed credential, trusting a server's key), still work.
/// </summary>
internal sealed class InstanceLockMiddleware(RequestDelegate next)
{
    public const string LockedCode = "instance_locked";

    public async Task InvokeAsync(HttpContext context, CoreDbContext db)
    {
        if (Guarded(context.Request) && await db.InstanceLock.AsNoTracking().AnyAsync(context.RequestAborted))
        {
            await Results.Problem(
                    "This instance is locked because its data was exported to move it to a new server. Make changes on the new server, or unlock it under Settings ▸ Move to a new server.",
                    statusCode: StatusCodes.Status409Conflict,
                    extensions: new Dictionary<string, object?> { ["code"] = LockedCode })
                .ExecuteAsync(context);
            return;
        }

        await next(context);
    }

    private static bool Guarded(HttpRequest request)
    {
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method))
        {
            return false;
        }

        var path = request.Path.Value ?? string.Empty;
        if (path.EndsWith("/credential-prompt", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/trusted-keys", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return request.Path.StartsWithSegments("/api/hosts", StringComparison.OrdinalIgnoreCase)
            || request.Path.StartsWithSegments("/api/users", StringComparison.OrdinalIgnoreCase)
            || request.Path.StartsWithSegments("/api/transfer/import", StringComparison.OrdinalIgnoreCase);
    }
}
