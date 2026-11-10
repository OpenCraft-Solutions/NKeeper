using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NKeeper.Core.Audit;
using NKeeper.Core.Persistence;
using NKeeper.Testing;

namespace NKeeper.Server.Tests;

/// <summary>
/// docs/architecture.md §6.1, §6.2: sign-in, sessions and sign-out, against a real database. Ported
/// from 1.x <c>AuthEndpointTests</c>; each test gets a fresh database, so lockout and rate limits never leak.
/// </summary>
public sealed class AuthTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string SessionCookie = "NKeeper.Session";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Signing_in_by_user_name_returns_the_profile_and_a_strict_session_cookie()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        using var client = server.Client();

        using var response = await client.LoginAsync(ServerScenario.AdminUserName, ServerScenario.AdminPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var user = await response.Content.ReadFromJsonAsync<CurrentUser>(Token);
        Assert.Equal((ServerScenario.AdminUserName, ServerScenario.AdminEmail), (user!.UserName, user.Email));
        Assert.Contains("Administrator", user.Roles);
        var cookie = SessionSetCookie(response);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires=", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Signing_in_by_email_with_remember_me_issues_a_persistent_cookie()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        using var client = server.Client();

        using var response = await client.LoginAsync(ServerScenario.AdminEmail, ServerScenario.AdminPassword, rememberMe: true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("expires=", SessionSetCookie(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_wrong_password_and_an_unknown_user_get_the_same_answer_and_no_session()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        using var client = server.Client();

        using var wrongPassword = await client.LoginAsync(ServerScenario.AdminUserName, "not-the-password-123");
        using var unknownUser = await client.LoginAsync("nobody@example.com", "not-the-password-123");

        var (first, second) = (await wrongPassword.ProblemAsync(), await unknownUser.ProblemAsync());
        Assert.Equal((HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized), (wrongPassword.StatusCode, unknownUser.StatusCode));
        Assert.Equal("invalid_credentials", first.GetProperty("code").GetString());
        Assert.Equal(first.GetProperty("detail").GetString(), second.GetProperty("detail").GetString());
        Assert.DoesNotContain(wrongPassword.SetCookies(), c => c.StartsWith(SessionCookie + "=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_fields_are_a_validation_problem()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        using var client = server.Client();

        using var response = await client.LoginAsync("", "");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.ProblemAsync()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("userNameOrEmail", out _));
        Assert.True(errors.TryGetProperty("password", out _));
    }

    [Fact]
    public async Task Signing_in_without_an_antiforgery_token_is_refused()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        using var client = server.Client();

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/auth/login", UriKind.Relative),
            new { userNameOrEmail = ServerScenario.AdminUserName, password = ServerScenario.AdminPassword },
            Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("csrf_token_invalid", (await response.ProblemAsync()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        await server.CreateUserAsync("operator", "Operator-Passw0rd");
        using var client = server.Client();

        for (var i = 0; i < 5; i++)
        {
            (await client.LoginAsync("operator", "wrong-password-123")).Dispose();
        }

        using var response = await client.LoginAsync("operator", "Operator-Passw0rd");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Sign_in_attempts_are_rate_limited_per_client()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        server.Settings["RateLimiting:Login:PermitLimit"] = "3";
        using var client = server.Client();

        for (var i = 0; i < 3; i++)
        {
            (await client.LoginAsync("nobody", "wrong-password-123")).Dispose();
        }

        using var response = await client.LoginAsync(ServerScenario.AdminUserName, ServerScenario.AdminPassword);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("rate_limited", (await response.ProblemAsync()).GetProperty("code").GetString());
        Assert.True(response.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task Me_without_a_session_is_an_unauthorized_problem()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        using var client = server.Client();

        using var response = await client.GetAsync(new Uri("/api/auth/me", UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(401, (await response.ProblemAsync()).GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Me_returns_only_the_safe_profile_fields()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        using var client = await server.SignedInClientAsync();

        using var response = await client.GetAsync(new Uri("/api/auth/me", UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        Assert.Equal(["email", "id", "mustChangePassword", "roles", "userName"], json.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Signing_out_ends_the_server_session_even_for_a_copied_cookie()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        using var client = server.Client();
        using var login = await client.LoginAsync(ServerScenario.AdminUserName, ServerScenario.AdminPassword);
        var copied = SessionSetCookie(login).Split(';')[0];

        using var logout = await client.LogoutAsync();
        using var after = await client.GetAsync(new Uri("/api/auth/me", UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        using var replay = server.Client(handleCookies: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/auth/me", UriKind.Relative));
        request.Headers.Add("Cookie", copied);
        using var replayed = await replay.SendAsync(request, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, replayed.StatusCode);
    }

    [Fact]
    public async Task Signing_out_without_a_session_is_idempotent()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        using var client = server.Client();

        using var response = await client.LogoutAsync();

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task A_disabled_account_is_signed_out_at_its_next_request()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        var userId = await server.CreateUserAsync("operator", "Operator-Passw0rd");
        using var client = server.Client();
        (await client.LoginAsync("operator", "Operator-Passw0rd")).EnsureSuccessStatusCode();

        await server.UpdateUserAsync("operator", u => u.IsDisabled = true);
        using var response = await client.GetAsync(new Uri("/api/auth/me", UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await using var db = new CoreDbContext(CoreDbContext.CreateOptions(server.ConnectionString));
        Assert.False(await db.AuthSessions.AnyAsync(s => s.UserId == userId, Token));
    }

    [Fact]
    public async Task An_account_that_must_change_its_password_can_do_nothing_else()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        await server.CreateUserAsync("operator", "Operator-Passw0rd", u => u.MustChangePassword = true);
        using var client = server.Client();
        (await client.LoginAsync("operator", "Operator-Passw0rd")).EnsureSuccessStatusCode();

        using var plugins = await client.GetAsync(new Uri("/api/plugins", UriKind.Relative), Token);
        using var me = await client.GetAsync(new Uri("/api/auth/me", UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.Forbidden, plugins.StatusCode);
        Assert.Equal("password_change_required", (await plugins.ProblemAsync()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public async Task Sign_ins_failures_and_sign_outs_are_audited_without_passwords()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        using var client = server.Client();
        (await client.LoginAsync("  someone-else  ", "Guessed-Passw0rd")).Dispose();
        (await client.LoginAsync(ServerScenario.AdminUserName, ServerScenario.AdminPassword)).EnsureSuccessStatusCode();
        (await client.LogoutAsync()).EnsureSuccessStatusCode();

        await using var db = new CoreDbContext(CoreDbContext.CreateOptions(server.ConnectionString));
        var events = await db.AuditEvents.Where(a => a.Action.StartsWith("auth.")).OrderBy(a => a.Id).ToListAsync(Token);

        Assert.Equal(
            [(AuditActions.SignInFailed, "someone-else", false), (AuditActions.SignIn, ServerScenario.AdminUserName, true), (AuditActions.SignOut, ServerScenario.AdminUserName, true)],
            events.Select(e => (e.Action, e.Actor, e.UserId is not null)));
        Assert.DoesNotContain(events, e => $"{e.Actor} {e.Target} {e.Detail}".Contains("Passw0rd", StringComparison.Ordinal));
    }

    private static string SessionSetCookie(HttpResponseMessage response) =>
        response.SetCookies().Single(c => c.StartsWith(SessionCookie + "=", StringComparison.Ordinal));

    private sealed record CurrentUser(Guid Id, string UserName, string? Email, string[] Roles, bool MustChangePassword);
}
