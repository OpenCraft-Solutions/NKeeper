using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using NKeeper.Core.Persistence;
using NKeeper.Core.Sessions;

namespace NKeeper.Identity;

/// <summary><c>/api/users</c> for administrators, and <c>/api/account</c> for the signed-in user (docs/architecture.md §6.1).</summary>
public static class UserEndpoints
{
    public static void MapUserEndpoints(this IEndpointRouteBuilder api)
    {
        var admin = api.MapGroup("/users").RequireAuthorization(policy => policy.RequireRole(SystemRoles.Administrator));

        admin.MapGet("/", (UserAdministration users, HttpContext context) => users.ListAsync(context.RequestAborted));

        admin.MapGet("/{id:guid}", async (Guid id, UserAdministration users, HttpContext context) =>
            await users.GetAsync(id, context.RequestAborted) is { } user ? Results.Ok(user) : Results.NotFound());

        admin.MapPost("/", (CreateUserRequest? request, UserAdministration users, HttpContext context) =>
            Validated(async () =>
            {
                var user = await users.CreateAsync(request ?? new CreateUserRequest(), context.RequestAborted);
                return Results.Created($"/api/users/{user.Id}", user);
            }));

        // Each of these can end the user's sign-in sessions or cut their access, and so their open sessions (§7.10).
        admin.MapPut("/{id:guid}", (Guid id, UpdateUserRequest? request, UserAdministration users, SessionRegistry sessions, HttpContext context) =>
            Validated(async () =>
            {
                var user = await users.UpdateAsync(id, request ?? new UpdateUserRequest(), context.User.UserId(), context.RequestAborted);
                await sessions.RecheckAsync(s => s.UserId == id, context.RequestAborted);
                return user is null ? Results.NotFound() : Results.Ok(user);
            }));

        admin.MapPost("/{id:guid}/password", (Guid id, ResetPasswordRequest? request, UserAdministration users, SessionRegistry sessions, HttpContext context) =>
            Validated(async () =>
            {
                var reset = await users.ResetPasswordAsync(id, request ?? new ResetPasswordRequest(), context.User.UserId(), context.RequestAborted);
                await sessions.RecheckAsync(s => s.UserId == id, context.RequestAborted);
                return reset ? Results.NoContent() : Results.NotFound();
            }));

        admin.MapDelete("/{id:guid}", (Guid id, UserAdministration users, SessionRegistry sessions, HttpContext context) =>
            Validated(async () =>
            {
                var deleted = await users.DeleteAsync(id, context.User.UserId(), context.RequestAborted);
                await sessions.RecheckAsync(s => s.UserId == id, context.RequestAborted);
                return deleted ? Results.NoContent() : Results.NotFound();
            }));

        // Verifies the current password, so it's rate limited like sign-in.
        api.MapPost("/account/password", (ChangePasswordRequest? request, UserAdministration users, UserManager<ApplicationUser> accounts, SignInManager<ApplicationUser> signIn, HttpContext context) =>
                Validated(async () =>
                {
                    if (await accounts.GetUserAsync(context.User) is not { } user)
                    {
                        return Results.Unauthorized();
                    }

                    await users.ChangeOwnPasswordAsync(user, request ?? new ChangePasswordRequest(), CurrentSession.Key(context), context.RequestAborted);

                    // The change gave the account a new security stamp. Reissue this session with it, in place,
                    // or Identity's next stamp check would sign this browser out too.
                    await signIn.RefreshSignInAsync(user);
                    return Results.NoContent();
                }))
            .RequireRateLimiting(LoginRateLimitSettings.PolicyName)
            .WithMetadata(new AllowWhilePasswordChangeRequiredAttribute());
    }

    /// <summary>Runs an operation, turning a broken rule into a validation problem against its field.</summary>
    private static async Task<IResult> Validated(Func<Task<IResult>> operation)
    {
        try
        {
            return await operation();
        }
        catch (AccountValidationException ex)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { [ex.Field] = [ex.Message] });
        }
    }
}
