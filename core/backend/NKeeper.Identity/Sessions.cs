using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NKeeper.Core.Persistence;

namespace NKeeper.Identity;

/// <summary>
/// Keeps sign-in tickets in <c>core.auth_sessions</c> (docs/architecture.md §6.2): the cookie holds
/// only an opaque reference, and signing out deletes the row.
/// </summary>
/// <remarks>A singleton, as the cookie options require; each call uses its own context.</remarks>
public sealed class DatabaseTicketStore(
    IServiceScopeFactory scopes,
    IDataProtectionProvider dataProtection,
    TimeProvider time) : ITicketStore
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("NKeeper.Identity.AuthSessions.v1");

    public Task<string> StoreAsync(AuthenticationTicket ticket) => StoreAsync(ticket, CancellationToken.None);

    public async Task<string> StoreAsync(AuthenticationTicket ticket, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            UserId = UserIdOf(ticket),
            ProtectedTicket = Protect(ticket),
            CreatedAtUtc = now,
            LastRenewedAtUtc = now,
            ExpiresAtUtc = ticket.Properties.ExpiresUtc,
        };

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreDbContext>();

        // Expired sessions are purged as new ones start, so the table doesn't grow without bound.
        await db.AuthSessions.Where(s => s.ExpiresAtUtc < now).ExecuteDeleteAsync(cancellationToken);
        db.AuthSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        return session.Id.ToString("N");
    }

    public Task RenewAsync(string key, AuthenticationTicket ticket) => RenewAsync(key, ticket, CancellationToken.None);

    public async Task RenewAsync(string key, AuthenticationTicket ticket, CancellationToken cancellationToken)
    {
        if (!TryParseKey(key, out var id))
        {
            return;
        }

        var protectedTicket = Protect(ticket);
        var userId = UserIdOf(ticket);
        var now = time.GetUtcNow();
        var expires = ticket.Properties.ExpiresUtc;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreDbContext>();

        // In place only: a session ended meanwhile must not come back.
        await db.AuthSessions.Where(s => s.Id == id).ExecuteUpdateAsync(
            setters => setters
                .SetProperty(s => s.ProtectedTicket, protectedTicket)
                .SetProperty(s => s.UserId, userId)
                .SetProperty(s => s.LastRenewedAtUtc, now)
                .SetProperty(s => s.ExpiresAtUtc, expires),
            cancellationToken);
    }

    public Task<AuthenticationTicket?> RetrieveAsync(string key) => RetrieveAsync(key, CancellationToken.None);

    public async Task<AuthenticationTicket?> RetrieveAsync(string key, CancellationToken cancellationToken)
    {
        if (!TryParseKey(key, out var id))
        {
            return null;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreDbContext>();
        var session = await db.AuthSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (session is null)
        {
            return null;
        }

        if (session.ExpiresAtUtc is { } expires && expires <= time.GetUtcNow())
        {
            await db.AuthSessions.Where(s => s.Id == id).ExecuteDeleteAsync(cancellationToken);
            return null;
        }

        try
        {
            return TicketSerializer.Default.Deserialize(_protector.Unprotect(session.ProtectedTicket));
        }
        catch (CryptographicException)
        {
            // The keys were removed, or the row was tampered with.
            await db.AuthSessions.Where(s => s.Id == id).ExecuteDeleteAsync(cancellationToken);
            return null;
        }
    }

    public Task RemoveAsync(string key) => RemoveAsync(key, CancellationToken.None);

    public async Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        if (!TryParseKey(key, out var id))
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreDbContext>();
        await db.AuthSessions.Where(s => s.Id == id).ExecuteDeleteAsync(cancellationToken);
    }

    private byte[] Protect(AuthenticationTicket ticket) => _protector.Protect(TicketSerializer.Default.Serialize(ticket));

    private static bool TryParseKey(string key, out Guid id) => Guid.TryParseExact(key, "N", out id);

    private static Guid UserIdOf(AuthenticationTicket ticket) =>
        Guid.TryParse(ticket.Principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            ? userId
            : throw new InvalidOperationException("The authentication ticket has no valid user ID.");
}

/// <summary>Attaches <see cref="DatabaseTicketStore"/> to Identity's sign-in cookie.</summary>
internal sealed class ConfigureSessionStore(DatabaseTicketStore store) : IConfigureNamedOptions<CookieAuthenticationOptions>
{
    public void Configure(string? name, CookieAuthenticationOptions options)
    {
        if (name == IdentityConstants.ApplicationScheme)
        {
            options.SessionStore = store;
        }
    }

    public void Configure(CookieAuthenticationOptions options) => Configure(Options.DefaultName, options);
}

/// <summary>Ends a user's server-side sessions, so copied or open cookies stop working at once.</summary>
public sealed class SessionRevoker(CoreDbContext db)
{
    /// <param name="userId">The user whose sessions end.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <param name="exceptSessionKey">A session to keep, such as the caller's own (<see cref="CurrentSession.Key"/>).</param>
    public Task<int> RevokeAllAsync(Guid userId, CancellationToken cancellationToken, string? exceptSessionKey = null)
    {
        var sessions = db.AuthSessions.Where(s => s.UserId == userId);
        if (Guid.TryParseExact(exceptSessionKey, "N", out var keep))
        {
            sessions = sessions.Where(s => s.Id != keep);
        }

        return sessions.ExecuteDeleteAsync(cancellationToken);
    }
}

/// <summary>The server-side session of the current request.</summary>
/// <summary>Gives core the current auth session's key, for binding prompt references to it (docs/architecture.md §7.6).</summary>
internal sealed class CookieSessionAccessor : NKeeper.Core.Credentials.IAuthSessionAccessor
{
    public string? CurrentSessionKey(HttpContext context) => CurrentSession.Key(context);
}

public static class CurrentSession
{
    /// <summary>
    /// This request's session key. The cookie handler keeps it inside the encrypted cookie, not on the
    /// principal, so it's read the way the handler reads it.
    /// </summary>
    public static string? Key(HttpContext context)
    {
        const string sessionIdClaim = "Microsoft.AspNetCore.Authentication.Cookies-SessionId";
        var options = context.RequestServices.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        var cookie = options.CookieManager.GetRequestCookie(context, options.Cookie.Name!);
        return cookie is null ? null : options.TicketDataFormat.Unprotect(cookie)?.Principal.FindFirst(sessionIdClaim)?.Value;
    }
}
