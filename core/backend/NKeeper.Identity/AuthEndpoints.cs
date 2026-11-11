using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using NKeeper.Core.Audit;
using NKeeper.Core.Persistence;

namespace NKeeper.Identity;

/// <summary>A sign-in request. Properties rather than a positional record, so missing fields are validation errors.</summary>
public sealed class LoginRequest
{
    public string? UserNameOrEmail { get; set; }

    public string? Password { get; set; }

    public bool RememberMe { get; set; }
}

/// <summary>What the shell learns about the signed-in user: only these fields.</summary>
/// <param name="ColorSchemes">The user's colour scheme and the schemes they built (§6.1).</param>
public sealed record CurrentUserResponse(Guid Id, string UserName, string? Email, IReadOnlyList<string> Roles, bool MustChangePassword, ColorSchemesResponse ColorSchemes);

/// <summary>A token for the antiforgery header: send <paramref name="Token"/> as <paramref name="HeaderName"/>.</summary>
public sealed record AntiforgeryTokenResponse(string Token, string HeaderName);

/// <summary><c>/api/auth</c>: sign-in, sign-out, the current user, and antiforgery tokens (docs/architecture.md §6).</summary>
public static class AuthEndpoints
{
    public const int MaxUserNameLength = 256;

    public static void MapAuthEndpoints(this IEndpointRouteBuilder api)
    {
        var auth = api.MapGroup("/auth");

        // Tokens are bound to the user, so the client fetches a new one after signing in or out.
        auth.MapGet("/antiforgery", (HttpContext context, IAntiforgery antiforgery, IOptions<AntiforgeryOptions> options) =>
            {
                var tokens = antiforgery.GetAndStoreTokens(context);
                context.Response.Headers.CacheControl = "no-store";
                return new AntiforgeryTokenResponse(tokens.RequestToken!, options.Value.HeaderName!);
            })
            .AllowAnonymous();

        auth.MapPost("/login", LoginAsync)
            .AllowAnonymous()
            .RequireRateLimiting(LoginRateLimitSettings.PolicyName);

        // Idempotent: succeeds even when not signed in.
        auth.MapPost("/logout", async (HttpContext context, SignInManager<ApplicationUser> signIn, IAuditLog audit, NKeeper.Core.Credentials.CredentialPromptStore prompts, NKeeper.Core.Trust.TrustPresentationStore presentations, NKeeper.Core.Sessions.SessionRegistry sessions) =>
            {
                // Deletes the session row through the ticket store, and clears the cookie, before anything
                // else can go wrong. The request's principal still names the user, for the audit event.
                var sessionKey = CurrentSession.Key(context);
                await signIn.SignOutAsync();

                // The session's typed credentials and presented keys, not yet used, go with it.
                if (sessionKey is not null)
                {
                    prompts.DropSession(sessionKey);
                    presentations.DropSession(sessionKey);
                    await sessions.RecheckAsync(s => s.AuthSessionKey == sessionKey, CancellationToken.None);
                }

                if (context.User.Identity?.IsAuthenticated == true)
                {
                    await audit.RecordAsync(new AuditEntry(AuditActions.SignOut), context.RequestAborted);
                }

                return Results.NoContent();
            })
            .AllowAnonymous();

        auth.MapGet("/me", async (HttpContext context, UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return await users.GetUserAsync(context.User) is { } user
                    ? Results.Ok(await ResponseFor(user, users))
                    : Results.Unauthorized();
            })
            .WithMetadata(new AllowWhilePasswordChangeRequiredAttribute());
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest? request,
        HttpContext context,
        CredentialValidator validator,
        SignInManager<ApplicationUser> signIn,
        UserManager<ApplicationUser> users,
        IOptions<AuthenticationSettings> settings,
        IAuditLog audit,
        TimeProvider time)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(request?.UserNameOrEmail))
        {
            errors["userNameOrEmail"] = ["Enter your user name or email address."];
        }
        else if (request.UserNameOrEmail.Length > MaxUserNameLength)
        {
            errors["userNameOrEmail"] = [$"Use at most {MaxUserNameLength} characters."];
        }

        if (string.IsNullOrEmpty(request?.Password))
        {
            errors["password"] = ["Enter your password."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var user = await validator.ValidateAsync(request!.UserNameOrEmail!, request.Password!, context.RequestAborted);
        if (user is null)
        {
            // The name as typed, never the password, so repeated guessing shows in the trail.
            await audit.RecordAsync(new AuditEntry(AuditActions.SignInFailed, UserName: request.UserNameOrEmail!.Trim()), context.RequestAborted);
            return Problems.Result(StatusCodes.Status401Unauthorized, ProblemCodes.InvalidCredentials, "Sign-in failed", "The user name or password is incorrect.");
        }

        var lifetime = request.RememberMe ? settings.Value.RememberMeDuration : settings.Value.SessionTimeout;
        await signIn.SignInAsync(user, new AuthenticationProperties
        {
            // A cookie that isn't persistent is dropped when the browser closes; the server-side expiry applies either way.
            IsPersistent = request.RememberMe,
            ExpiresUtc = time.GetUtcNow().Add(lifetime),
            AllowRefresh = true,
        });
        await audit.RecordAsync(new AuditEntry(AuditActions.SignIn, UserId: user.Id, UserName: user.UserName), context.RequestAborted);
        return Results.Ok(await ResponseFor(user, users));
    }

    private static async Task<CurrentUserResponse> ResponseFor(ApplicationUser user, UserManager<ApplicationUser> users) =>
        new(user.Id, user.UserName!, user.Email, [.. (await users.GetRolesAsync(user)).Order(StringComparer.Ordinal)], user.MustChangePassword, ColorSchemes.Of(user));
}
