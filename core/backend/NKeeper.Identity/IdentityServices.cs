using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NKeeper.Core.Audit;
using NKeeper.Core.Persistence;

namespace NKeeper.Identity;

public static class IdentityServices
{
    public const string AntiforgeryHeaderName = "X-XSRF-TOKEN";

    /// <summary>Accounts, sessions, the key ring and the request policies (docs/architecture.md §6).</summary>
    public static IServiceCollection AddNKeeperIdentity(
        this IServiceCollection services,
        Microsoft.Extensions.Configuration.IConfiguration configuration,
        string connectionString,
        KeyRingCertificate keyRing)
    {
        services.AddOptions<AuthenticationSettings>().Bind(configuration.GetSection(AuthenticationSettings.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<LoginRateLimitSettings>().Bind(configuration.GetSection(LoginRateLimitSettings.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<BootstrapAdminOptions>()
            .Bind(configuration.GetSection(BootstrapAdminOptions.SectionName))
            .Validate(o => o.IsValid(), "BootstrapAdmin:UserName, BootstrapAdmin:Email and BootstrapAdmin:Password are required when BootstrapAdmin:Enabled is true.")
            .ValidateOnStart();

        // The options are a singleton, so singletons such as the audit log can make short-lived contexts of their own.
        services.AddDbContext<CoreDbContext>(options => CoreDbContext.Configure(options, connectionString), optionsLifetime: ServiceLifetime.Singleton);
        services.AddHttpContextAccessor();
        services.AddSingleton<IAuditLog, AuditLog>();
        services.AddProblemDetails();

        services.AddIdentity<ApplicationUser, ApplicationRole>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.SignIn.RequireConfirmedAccount = false;
                options.Password.RequiredLength = 12;
                options.Password.RequireNonAlphanumeric = false;
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddEntityFrameworkStores<CoreDbContext>();

        // Keys live in the core schema, encrypted with the key ring's certificate when one is set (§6.3).
        var dataProtection = services.AddDataProtection().SetApplicationName("NKeeper").PersistKeysToDbContext<CoreDbContext>();
        if (keyRing.Certificate is { } certificate)
        {
            dataProtection.ProtectKeysWithCertificate(certificate);
        }

        services.AddSingleton(keyRing);
        services.AddSingleton<DatabaseTicketStore>();
        services.AddSingleton<IConfigureOptions<CookieAuthenticationOptions>, ConfigureSessionStore>();
        services.AddScoped<CredentialValidator>();
        services.AddScoped<BootstrapAdministrator>();
        services.AddScoped<SessionRevoker>();
        services.AddScoped<UserAdministration>();

        services.AddAntiforgery(options =>
        {
            options.HeaderName = AntiforgeryHeaderName;
            options.Cookie.Name = "NKeeper.Antiforgery";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            // Not a credential, so Secure whenever the request is HTTPS. With "Always", antiforgery
            // throws on plain-HTTP requests, such as http://localhost behind nginx.
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });

        // Identity's cookie, shaped for a JSON API: problem responses, never redirects.
        services.AddOptions<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme)
            .Configure<IOptions<AuthenticationSettings>>((options, auth) =>
            {
                var settings = auth.Value;
                options.Cookie.Name = settings.CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = settings.RequireSecureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = settings.SessionTimeout;
                options.SlidingExpiration = true;
                options.Events.OnRedirectToLogin = context =>
                    Problems.WriteAsync(context.HttpContext, StatusCodes.Status401Unauthorized, null, "Sign-in required", "Sign in to continue.");
                options.Events.OnRedirectToAccessDenied = context =>
                    Problems.WriteAsync(context.HttpContext, StatusCodes.Status403Forbidden, null, "Forbidden", "You don't have access to this.");
            });

        // Secure by default: every endpoint needs a signed-in user unless it opts out.
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(LoginRateLimitSettings.PolicyName, context =>
            {
                var settings = context.RequestServices.GetRequiredService<IOptions<LoginRateLimitSettings>>().Value;
                return RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = settings.PermitLimit, Window = settings.Window, QueueLimit = 0 });
            });
            options.OnRejected = async (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                await Problems.WriteAsync(
                    context.HttpContext,
                    StatusCodes.Status429TooManyRequests,
                    ProblemCodes.RateLimited,
                    "Too many attempts",
                    "Too many sign-in attempts. Wait a moment and try again.");
            };
        });

        return services;
    }

    /// <summary>
    /// Sign-in, rate limits, authorization and the request policies, in that order. Call after the
    /// shell's static files, which need no sign-in, and before mapping endpoints.
    /// </summary>
    public static void UseNKeeperIdentity(this WebApplication app)
    {
        app.UseRouting();
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();
        app.UseMiddleware<ApiPolicyMiddleware>();
    }

    /// <summary>Startup work that needs the built application's services: the bootstrap administrator (§6.1).</summary>
    public static async Task InitializeIdentityAsync(this WebApplication app, CancellationToken cancellationToken)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<BootstrapAdministrator>().EnsureAsync(cancellationToken);
    }
}
