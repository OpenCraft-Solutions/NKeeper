using System.Net.Mail;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NKeeper.Core.Audit;
using NKeeper.Core.Persistence;

namespace NKeeper.Identity;

public enum UserRole
{
    /// <summary>Manages users and, from step 3, sees and manages every host.</summary>
    Administrator,

    /// <summary>Sees only the hosts granted to them (step 3).</summary>
    User,
}

public sealed record UserResponse(
    Guid Id,
    string UserName,
    string? Email,
    UserRole Role,
    bool IsDisabled,
    bool MustChangePassword,
    bool IsLockedOut,
    DateTimeOffset CreatedAtUtc);

public sealed class CreateUserRequest
{
    public string? UserName { get; set; }

    /// <summary>Required and unique: users can sign in with it.</summary>
    public string? Email { get; set; }

    public UserRole? Role { get; set; }

    /// <summary>An initial password. With <see cref="MustChangePassword"/>, the user replaces it at first sign-in.</summary>
    public string? Password { get; set; }

    public bool MustChangePassword { get; set; } = true;
}

public sealed class UpdateUserRequest
{
    public string? Email { get; set; }

    public UserRole? Role { get; set; }

    public bool IsDisabled { get; set; }
}

public sealed class ResetPasswordRequest
{
    public string? Password { get; set; }

    public bool MustChangePassword { get; set; } = true;
}

public sealed class ChangePasswordRequest
{
    public string? CurrentPassword { get; set; }

    public string? NewPassword { get; set; }
}

/// <summary>A request that breaks a rule, reported against one field.</summary>
public sealed class AccountValidationException(string field, string message) : Exception(message)
{
    public string Field { get; } = field;
}

/// <summary>
/// Administrators' operations on accounts (docs/architecture.md §6.1), ported from 1.x. They can't
/// lock everyone out: no one demotes, disables or deletes themselves, and one active administrator
/// always remains. A user's sessions end whenever their access is cut, their role changes, their
/// password is reset, or the account is deleted.
/// </summary>
public sealed partial class UserAdministration(
    CoreDbContext db,
    UserManager<ApplicationUser> users,
    SessionRevoker sessions,
    IAuditLog audit,
    TimeProvider time,
    ILogger<UserAdministration> logger)
{
    public const int MaxPasswordLength = 512;

    public async Task<List<UserResponse>> ListAsync(CancellationToken cancellationToken)
    {
        var admins = await AdministratorIdsAsync(cancellationToken);
        var all = await db.Users.AsNoTracking().OrderBy(u => u.NormalizedUserName).ToListAsync(cancellationToken);
        return [.. all.Select(u => ToResponse(u, admins))];
    }

    public async Task<UserResponse?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        return user is null ? null : ToResponse(user, await AdministratorIdsAsync(cancellationToken));
    }

    public async Task<UserResponse> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken)
    {
        var userName = ValidUserName(request.UserName);
        var email = ValidEmail(request.Email);
        var role = request.Role ?? throw new AccountValidationException("role", "Choose a role.");
        var password = ValidPassword(request.Password, "password");

        var user = new ApplicationUser
        {
            UserName = userName,
            Email = email,
            EmailConfirmed = true,
            MustChangePassword = request.MustChangePassword,
            CreatedAtUtc = time.GetUtcNow(),
        };
        Check(await users.CreateAsync(user, password), "password");
        if (role == UserRole.Administrator)
        {
            Check(await users.AddToRoleAsync(user, SystemRoles.Administrator), "role");
        }

        await audit.RecordAsync(new AuditEntry(AuditActions.UserCreate, user.Id.ToString(), $"{user.UserName} as {role}"), cancellationToken);
        return (await GetAsync(user.Id, cancellationToken))!;
    }

    public async Task<UserResponse?> UpdateAsync(Guid id, UpdateUserRequest request, Guid actorId, CancellationToken cancellationToken)
    {
        var email = ValidEmail(request.Email);
        var role = request.Role ?? throw new AccountValidationException("role", "Choose a role.");
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null)
        {
            return null;
        }

        var wasAdmin = await users.IsInRoleAsync(user, SystemRoles.Administrator);
        var makeAdmin = role == UserRole.Administrator;
        if (id == actorId && (!makeAdmin || request.IsDisabled))
        {
            throw new AccountValidationException(request.IsDisabled ? "isDisabled" : "role", "You can't remove your own administrator access or disable yourself.");
        }

        if (wasAdmin && !user.IsDisabled && (!makeAdmin || request.IsDisabled))
        {
            await EnsureAnotherActiveAdministratorAsync(id, request.IsDisabled ? "isDisabled" : "role", cancellationToken);
        }

        var accessCut = (wasAdmin && !makeAdmin) || (request.IsDisabled && !user.IsDisabled);
        user.Email = email;
        user.IsDisabled = request.IsDisabled;
        Check(await users.UpdateAsync(user), "email");
        if (makeAdmin && !wasAdmin)
        {
            Check(await users.AddToRoleAsync(user, SystemRoles.Administrator), "role");
        }
        else if (!makeAdmin && wasAdmin)
        {
            Check(await users.RemoveFromRoleAsync(user, SystemRoles.Administrator), "role");
        }

        if (accessCut || makeAdmin != wasAdmin)
        {
            // Roles are carried in the session, so a role change takes effect through a fresh sign-in.
            await EndSessionsAsync(id, cancellationToken);
        }

        await audit.RecordAsync(
            new AuditEntry(AuditActions.UserUpdate, id.ToString(), $"{user.UserName}: role {role}, disabled {request.IsDisabled}"),
            cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<bool> ResetPasswordAsync(Guid id, ResetPasswordRequest request, Guid actorId, CancellationToken cancellationToken)
    {
        var password = ValidPassword(request.Password, "password");
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null)
        {
            return false;
        }

        // An administrator's reset needs no emailed token: check the policy, then set the hash.
        foreach (var validator in users.PasswordValidators)
        {
            Check(await validator.ValidateAsync(users, user, password), "password");
        }

        user.PasswordHash = users.PasswordHasher.HashPassword(user, password);
        await users.UpdateSecurityStampAsync(user);
        user.MustChangePassword = request.MustChangePassword && id != actorId;
        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
        Check(await users.UpdateAsync(user), "password");
        await EndSessionsAsync(id, cancellationToken);
        await audit.RecordAsync(new AuditEntry(AuditActions.UserPasswordReset, id.ToString(), user.UserName!), cancellationToken);
        return true;
    }

    public async Task<bool> DeleteAsync(Guid id, Guid actorId, CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null)
        {
            return false;
        }

        if (id == actorId)
        {
            throw new AccountValidationException("id", "You can't delete your own account.");
        }

        if (!user.IsDisabled && await users.IsInRoleAsync(user, SystemRoles.Administrator))
        {
            await EnsureAnotherActiveAdministratorAsync(id, "id", cancellationToken);
        }

        await EndSessionsAsync(id, cancellationToken);
        Check(await users.DeleteAsync(user), "id");
        await audit.RecordAsync(new AuditEntry(AuditActions.UserDelete, id.ToString(), user.UserName!), cancellationToken);
        return true;
    }

    /// <summary>The caller's own password. Their other sessions end; <paramref name="keepSessionKey"/> continues.</summary>
    public async Task ChangeOwnPasswordAsync(ApplicationUser user, ChangePasswordRequest request, string? keepSessionKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.CurrentPassword))
        {
            throw new AccountValidationException("currentPassword", "Enter your current password.");
        }

        var newPassword = ValidPassword(request.NewPassword, "newPassword");
        if (request.CurrentPassword == newPassword)
        {
            throw new AccountValidationException("newPassword", "Choose a password different from your current one.");
        }

        Check(await users.ChangePasswordAsync(user, request.CurrentPassword, newPassword), "newPassword");
        user.MustChangePassword = false;
        Check(await users.UpdateAsync(user), "newPassword");
        await sessions.RevokeAllAsync(user.Id, cancellationToken, exceptSessionKey: keepSessionKey);
        await audit.RecordAsync(new AuditEntry(AuditActions.AccountPasswordChange, user.Id.ToString()), cancellationToken);
    }

    private async Task EndSessionsAsync(Guid userId, CancellationToken cancellationToken)
    {
        // Sign-in sessions only, for now. Terminal and other plugin sessions end here too once the
        // session framework exists (Phase 1 step 7).
        logger.LogInformation("Ending the sessions of user {UserId}.", userId);
        await sessions.RevokeAllAsync(userId, cancellationToken);
    }

    private async Task EnsureAnotherActiveAdministratorAsync(Guid exceptUserId, string field, CancellationToken cancellationToken)
    {
        var admins = await AdministratorIdsAsync(cancellationToken);
        var others = await db.Users.CountAsync(u => admins.Contains(u.Id) && u.Id != exceptUserId && !u.IsDisabled, cancellationToken);
        if (others == 0)
        {
            throw new AccountValidationException(field, "NKeeper needs at least one active administrator. Make someone else an administrator first.");
        }
    }

    private async Task<HashSet<Guid>> AdministratorIdsAsync(CancellationToken cancellationToken)
    {
        var admins = await users.GetUsersInRoleAsync(SystemRoles.Administrator);
        cancellationToken.ThrowIfCancellationRequested();
        return [.. admins.Select(u => u.Id)];
    }

    private UserResponse ToResponse(ApplicationUser user, HashSet<Guid> admins) =>
        new(
            user.Id,
            user.UserName!,
            user.Email,
            admins.Contains(user.Id) ? UserRole.Administrator : UserRole.User,
            user.IsDisabled,
            user.MustChangePassword,
            user.LockoutEnd > time.GetUtcNow(),
            user.CreatedAtUtc);

    private static string ValidUserName(string? value)
    {
        var name = value?.Trim() ?? string.Empty;
        return name.Length is < 2 or > 64
            ? throw new AccountValidationException("userName", "User names are 2 to 64 characters.")
            : !UserNamePattern().IsMatch(name)
                ? throw new AccountValidationException("userName", "Use letters, digits, '.', '_', '-' or '@'.")
                : name;
    }

    private static string ValidEmail(string? value)
    {
        var email = value?.Trim() ?? string.Empty;
        return email.Length is 0 or > 256 || !MailAddress.TryCreate(email, out var parsed) || parsed.Address != email
            ? throw new AccountValidationException("email", "Enter a valid email address.")
            : email;
    }

    private static string ValidPassword(string? value, string field) =>
        string.IsNullOrEmpty(value)
            ? throw new AccountValidationException(field, "Enter a password.")
            : value.Length > MaxPasswordLength
                ? throw new AccountValidationException(field, $"Use at most {MaxPasswordLength} characters.")
                : value;

    /// <summary>Turns Identity's errors (password policy, duplicate names…) into a field error.</summary>
    private static void Check(IdentityResult result, string defaultField)
    {
        if (result.Succeeded)
        {
            return;
        }

        var field = result.Errors.First().Code switch
        {
            "DuplicateUserName" or "InvalidUserName" => "userName",
            "DuplicateEmail" or "InvalidEmail" => "email",
            "PasswordMismatch" => "currentPassword",
            _ => defaultField,
        };
        throw new AccountValidationException(field, string.Join(" ", result.Errors.Select(e => e.Description)));
    }

    [GeneratedRegex("^[A-Za-z0-9._@-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex UserNamePattern();
}
