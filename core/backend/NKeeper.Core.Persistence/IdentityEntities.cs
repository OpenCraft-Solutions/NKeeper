using Microsoft.AspNetCore.Identity;

namespace NKeeper.Core.Persistence;

/// <summary>
/// An NKeeper user account (docs/architecture.md §6.1). Its <see cref="IdentityUser{TKey}.Id"/> is the
/// stable identifier everything else refers to.
/// </summary>
public sealed class ApplicationUser : IdentityUser<Guid>
{
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>A disabled account can't sign in, and its sessions end.</summary>
    public bool IsDisabled { get; set; }

    /// <summary>Set when an administrator assigns a password: the user must choose their own before anything else.</summary>
    public bool MustChangePassword { get; set; }
}

public sealed class ApplicationRole : IdentityRole<Guid>
{
    public ApplicationRole()
    {
    }

    public ApplicationRole(string name)
        : base(name)
    {
    }
}

/// <summary>System-wide roles. Access to hosts comes from grants, not roles.</summary>
public static class SystemRoles
{
    public const string Administrator = "Administrator";
}

/// <summary>
/// A server-side sign-in session (§6.2). The cookie carries only an encrypted reference to this row,
/// so deleting it ends the session at once.
/// </summary>
public sealed class AuthSession
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>The serialized authentication ticket, encrypted with Data Protection.</summary>
    public byte[] ProtectedTicket { get; set; } = [];

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset LastRenewedAtUtc { get; set; }

    public DateTimeOffset? ExpiresAtUtc { get; set; }
}
