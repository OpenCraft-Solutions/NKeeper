using System.ComponentModel.DataAnnotations;

namespace NKeeper.Identity;

/// <summary>Sign-in cookies and session lifetimes (docs/architecture.md §6.2).</summary>
public sealed class AuthenticationSettings
{
    public const string SectionName = "Authentication";

    [Required]
    [RegularExpression("^[A-Za-z0-9._-]+$", ErrorMessage = "Authentication:CookieName may only contain letters, digits, '.', '_' and '-'.")]
    public string CookieName { get; set; } = "NKeeper.Session";

    /// <summary>A normal sign-in's lifetime. Sliding: renewed while the user is active.</summary>
    [Range(typeof(TimeSpan), "00:05:00", "7.00:00:00")]
    public TimeSpan SessionTimeout { get; set; } = TimeSpan.FromHours(8);

    /// <summary>A "remember me" sign-in's lifetime. Also sliding.</summary>
    [Range(typeof(TimeSpan), "01:00:00", "90.00:00:00")]
    public TimeSpan RememberMeDuration { get; set; } = TimeSpan.FromDays(14);

    /// <summary>Cookies only over HTTPS. Must stay on in production; local development over http:// turns it off.</summary>
    public bool RequireSecureCookies { get; set; } = true;
}

/// <summary>How many sign-in attempts a client address gets (§6.4).</summary>
public sealed class LoginRateLimitSettings
{
    public const string SectionName = "RateLimiting:Login";
    public const string PolicyName = "login";

    [Range(1, 10_000)]
    public int PermitLimit { get; set; } = 10;

    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// The first administrator (§6.1), created once at startup. Refused outside Development unless
/// <see cref="AllowOutsideDevelopment"/> is set, as the installer does for the first start.
/// </summary>
public sealed class BootstrapAdminOptions
{
    public const string SectionName = "BootstrapAdmin";

    public bool Enabled { get; set; }

    public bool AllowOutsideDevelopment { get; set; }

    public string? UserName { get; set; }

    public string? Email { get; set; }

    public string? Password { get; set; }

    internal bool IsValid() =>
        !Enabled || (!string.IsNullOrWhiteSpace(UserName) && !string.IsNullOrWhiteSpace(Email) && !string.IsNullOrWhiteSpace(Password));
}
