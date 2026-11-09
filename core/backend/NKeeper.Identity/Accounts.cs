using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NKeeper.Core.Persistence;

namespace NKeeper.Identity;

/// <summary>
/// Checks a user name (or email address) and password (docs/architecture.md §6.1). Every failure
/// looks the same to the caller, and takes comparable time, so accounts can't be enumerated.
/// </summary>
public sealed class CredentialValidator(
    UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signIn,
    IPasswordHasher<ApplicationUser> hasher,
    ILogger<CredentialValidator> logger)
{
    private static readonly ApplicationUser TimingUser = new();
    private static readonly Lazy<string> TimingHash = new(() => new PasswordHasher<ApplicationUser>().HashPassword(TimingUser, Guid.NewGuid().ToString()));

    public async Task<ApplicationUser?> ValidateAsync(string userNameOrEmail, string password, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var identifier = userNameOrEmail.Trim();
        var user = await users.FindByNameAsync(identifier);
        if (user is null && identifier.Contains('@', StringComparison.Ordinal))
        {
            user = await users.FindByEmailAsync(identifier);
        }

        if (user is null || user.IsDisabled)
        {
            // Spend the time a real check would, so the response doesn't reveal which accounts exist.
            hasher.VerifyHashedPassword(TimingUser, TimingHash.Value, password);
            logger.LogInformation("Sign-in failed: {Reason}.", user is null ? "no such account" : $"account {user.Id} is disabled");
            return null;
        }

        var result = await signIn.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
        if (result.Succeeded)
        {
            return user;
        }

        logger.LogInformation(
            "Sign-in failed for {UserId}: {Reason}.",
            user.Id,
            result.IsLockedOut ? "locked out" : result.IsNotAllowed ? "not allowed" : "wrong password");
        return null;
    }
}

/// <summary>Creates the first administrator from <see cref="BootstrapAdminOptions"/>, once (§6.1).</summary>
public sealed class BootstrapAdministrator(
    UserManager<ApplicationUser> users,
    RoleManager<ApplicationRole> roles,
    IOptions<BootstrapAdminOptions> options,
    IHostEnvironment environment,
    TimeProvider time,
    ILogger<BootstrapAdministrator> logger)
{
    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await roles.RoleExistsAsync(SystemRoles.Administrator))
        {
            Check(await roles.CreateAsync(new ApplicationRole(SystemRoles.Administrator)), "create the Administrator role");
        }

        var settings = options.Value;
        if (!settings.Enabled)
        {
            return;
        }

        if (!environment.IsDevelopment() && !settings.AllowOutsideDevelopment)
        {
            logger.LogWarning(
                "BootstrapAdmin is enabled, but the environment is {Environment}: skipped. Set BootstrapAdmin:AllowOutsideDevelopment to create it deliberately.",
                environment.EnvironmentName);
            return;
        }

        if (await users.FindByNameAsync(settings.UserName!) is { } existing)
        {
            logger.LogInformation("The bootstrap administrator {UserId} already exists; left unchanged.", existing.Id);
            return;
        }

        var admin = new ApplicationUser
        {
            UserName = settings.UserName,
            Email = settings.Email,
            EmailConfirmed = true,
            CreatedAtUtc = time.GetUtcNow(),
        };
        Check(await users.CreateAsync(admin, settings.Password!), "create the bootstrap administrator");
        Check(await users.AddToRoleAsync(admin, SystemRoles.Administrator), "make the bootstrap administrator an administrator");
        logger.LogInformation("Created the bootstrap administrator {UserId}.", admin.Id);
    }

    private static void Check(IdentityResult result, string what)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Couldn't {what}: {string.Join(" ", result.Errors.Select(e => e.Description))}");
        }
    }
}
