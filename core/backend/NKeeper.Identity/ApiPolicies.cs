using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NKeeper.Core.Persistence;

namespace NKeeper.Identity;

/// <summary>Marks an endpoint a user may call while they still have to choose a new password.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class AllowWhilePasswordChangeRequiredAttribute : Attribute;

/// <summary>Stable problem codes the frontend can act on.</summary>
public static class ProblemCodes
{
    public const string CsrfTokenInvalid = "csrf_token_invalid";
    public const string PasswordChangeRequired = "password_change_required";
    public const string InvalidCredentials = "invalid_credentials";
    public const string RateLimited = "rate_limited";
}

/// <summary>
/// The request policies on everything under <c>/api</c> and <c>/plugins</c> (docs/architecture.md §6.4):
/// antiforgery on every unsafe request, and the account re-checked on every signed-in one. Middleware
/// rather than endpoint filters, so they cover every kind of endpoint a plugin can map.
/// </summary>
/// <remarks>Runs after authentication and authorization, which require the sign-in itself.</remarks>
internal sealed class ApiPolicyMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IAntiforgery antiforgery, CoreDbContext db, SignInManager<ApplicationUser> signIn)
    {
        var path = context.Request.Path;
        if (!path.StartsWithSegments("/api") && !path.StartsWithSegments("/plugins"))
        {
            await next(context);
            return;
        }

        if (!IsSafe(context.Request.Method) && !await antiforgery.IsRequestValidAsync(context))
        {
            await Problems.WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                ProblemCodes.CsrfTokenInvalid,
                "Invalid security token",
                "The request's security token is missing or expired. Refresh the page and try again.");
            return;
        }

        var endpoint = context.GetEndpoint();
        if (context.User.Identity?.IsAuthenticated == true && endpoint?.Metadata.GetMetadata<IAllowAnonymous>() is null)
        {
            var userId = context.User.UserId();
            var account = await db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new { u.IsDisabled, u.MustChangePassword })
                .FirstOrDefaultAsync(context.RequestAborted);

            if (account is null || account.IsDisabled)
            {
                // Changes to an account apply at once, not at the next sign-in.
                await signIn.SignOutAsync();
                await Problems.WriteAsync(context, StatusCodes.Status401Unauthorized, null, "Signed out", "Your session has ended. Sign in again.");
                return;
            }

            if (account.MustChangePassword && endpoint?.Metadata.GetMetadata<AllowWhilePasswordChangeRequiredAttribute>() is null)
            {
                await Problems.WriteAsync(
                    context,
                    StatusCodes.Status403Forbidden,
                    ProblemCodes.PasswordChangeRequired,
                    "Password change required",
                    "Choose a new password before continuing.");
                return;
            }
        }

        await next(context);
    }

    private static bool IsSafe(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method);
}

/// <summary>Problem responses (<c>application/problem+json</c>) with an optional stable <c>code</c>.</summary>
public static class Problems
{
    public static async Task WriteAsync(HttpContext context, int status, string? code, string title, string detail)
    {
        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail };
        if (code is not null)
        {
            problem.Extensions["code"] = code;
        }

        context.Response.StatusCode = status;
        var problems = context.RequestServices.GetService(typeof(IProblemDetailsService)) as IProblemDetailsService;
        if (problems is null || !await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem }))
        {
            await context.Response.WriteAsJsonAsync(problem, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json", context.RequestAborted);
        }
    }

    /// <summary>The same problem as an endpoint result.</summary>
    public static IResult Result(int status, string? code, string title, string detail) =>
        Results.Problem(
            statusCode: status,
            title: title,
            detail: detail,
            extensions: code is null ? null : new Dictionary<string, object?> { ["code"] = code });
}

public static class ClaimsPrincipalExtensions
{
    /// <summary>The signed-in user's ID.</summary>
    public static Guid UserId(this System.Security.Claims.ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var id)
            ? id
            : throw new InvalidOperationException("The signed-in user has no valid ID.");
}
