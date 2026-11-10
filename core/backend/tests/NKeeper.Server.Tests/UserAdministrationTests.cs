using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Core.Audit;
using NKeeper.Core.Persistence;
using NKeeper.Testing;

namespace NKeeper.Server.Tests;

/// <summary>docs/architecture.md §6.1: user administration and password changes. Ported from 1.x <c>UserAccessTests</c>.</summary>
public sealed class UserAdministrationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string BobPassword = "Bob-Passw0rd-123";
    private const string AnyEndpoint = "/api/plugins";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_administrator_creates_lists_edits_and_deletes_users()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        using var admin = await server.SignedInClientAsync();

        using var created = await admin.SendWithTokenAsync(HttpMethod.Post, "/api/users", new { userName = "bob", email = "bob@example.com", role = "user", password = BobPassword });
        var bob = (await created.Content.ReadFromJsonAsync<User>(Json, Token))!;
        var listed = await admin.GetFromJsonAsync<List<User>>("/api/users", Json, Token);
        using var updated = await admin.SendWithTokenAsync(HttpMethod.Put, $"/api/users/{bob.Id}", new { email = "robert@example.com", role = "administrator" });
        var changed = (await updated.Content.ReadFromJsonAsync<User>(Json, Token))!;
        using var deleted = await admin.SendWithTokenAsync(HttpMethod.Delete, $"/api/users/{bob.Id}");
        using var gone = await admin.GetAsync(new Uri($"/api/users/{bob.Id}", UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(("bob", "user", true), (bob.UserName, bob.Role, bob.MustChangePassword));
        Assert.Equal([ServerScenario.AdminUserName, "bob"], listed!.Select(u => u.UserName));
        Assert.Equal(("robert@example.com", "administrator"), (changed.Email, changed.Role));
        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NotFound), (deleted.StatusCode, gone.StatusCode));

        await using var db = new CoreDbContext(CoreDbContext.CreateOptions(server.ConnectionString));
        var actions = await db.AuditEvents.Where(a => a.Action.StartsWith("user.")).OrderBy(a => a.Id).Select(a => a.Action).ToListAsync(Token);
        Assert.Equal([AuditActions.UserCreate, AuditActions.UserUpdate, AuditActions.UserDelete], actions);
    }

    [Fact]
    public async Task Only_administrators_manage_users()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        await server.CreateUserAsync("bob", BobPassword);
        using var bob = await SignInAsync(server, "bob", BobPassword);

        using var list = await bob.GetAsync(new Uri("/api/users", UriKind.Relative), Token);
        using var create = await bob.SendWithTokenAsync(HttpMethod.Post, "/api/users", new { userName = "eve", email = "eve@example.com", role = "administrator", password = "Eve-Passw0rd-123" });

        Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.Forbidden), (list.StatusCode, create.StatusCode));
    }

    [Fact]
    public async Task A_new_user_must_change_the_assigned_password_before_anything_else()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        await server.CreateUserAsync("bob", BobPassword, u => u.MustChangePassword = true);
        using var bob = await SignInAsync(server, "bob", BobPassword);

        using var blocked = await bob.GetAsync(new Uri(AnyEndpoint, UriKind.Relative), Token);
        using var wrong = await bob.SendWithTokenAsync(HttpMethod.Post, "/api/account/password", new { currentPassword = "nope", newPassword = "Bob-New-Passw0rd" });
        using var same = await bob.SendWithTokenAsync(HttpMethod.Post, "/api/account/password", new { currentPassword = BobPassword, newPassword = BobPassword });
        using var changed = await bob.SendWithTokenAsync(HttpMethod.Post, "/api/account/password", new { currentPassword = BobPassword, newPassword = "Bob-New-Passw0rd" });
        using var allowed = await bob.GetAsync(new Uri(AnyEndpoint, UriKind.Relative), Token);

        Assert.Equal("password_change_required", (await blocked.ProblemAsync()).GetProperty("code").GetString());
        Assert.True((await wrong.ProblemAsync()).GetProperty("errors").TryGetProperty("currentPassword", out _));
        Assert.True((await same.ProblemAsync()).GetProperty("errors").TryGetProperty("newPassword", out _));
        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.OK), (changed.StatusCode, allowed.StatusCode));
    }

    [Fact]
    public async Task Changing_ones_own_password_keeps_this_session_and_ends_the_others()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        await server.CreateUserAsync("bob", BobPassword);
        using var laptop = await SignInAsync(server, "bob", BobPassword);
        using var phone = await SignInAsync(server, "bob", BobPassword);

        using var changed = await laptop.SendWithTokenAsync(HttpMethod.Post, "/api/account/password", new { currentPassword = BobPassword, newPassword = "Bob-New-Passw0rd" });

        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await laptop.GetAsync(new Uri(AnyEndpoint, UriKind.Relative), Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync(new Uri(AnyEndpoint, UriKind.Relative), Token)).StatusCode);
    }

    [Fact]
    public async Task The_session_that_changed_its_password_passes_the_next_security_stamp_check()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync())
        {
            // Check the stamp on every request, rather than every 30 minutes.
            TestServices = services => services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.Zero),
        };
        await server.CreateUserAsync("bob", BobPassword);
        using var bob = await SignInAsync(server, "bob", BobPassword);

        using var changed = await bob.SendWithTokenAsync(HttpMethod.Post, "/api/account/password", new { currentPassword = BobPassword, newPassword = "Bob-New-Passw0rd" });

        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync(new Uri(AnyEndpoint, UriKind.Relative), Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync(new Uri(AnyEndpoint, UriKind.Relative), Token)).StatusCode);
    }

    [Fact]
    public async Task Two_administrators_demoting_each_other_at_once_leave_one_administrator()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        Guid adminId;
        using (var admin = await server.SignedInClientAsync())
        {
            adminId = (await admin.GetFromJsonAsync<JsonElement>("/api/auth/me", Token)).GetProperty("id").GetGuid();
        }

        // Three rounds: each signs in twice, and sign-in is rate limited to 10 a minute.
        for (var round = 0; round < 3; round++)
        {
            var name = $"alice{round}";
            var aliceId = await server.CreateUserAsync(name, BobPassword);
            await server.AddToAdministratorsAsync(name);
            using var alice = await SignInAsync(server, name, BobPassword);
            using var me = await server.SignedInClientAsync();

            var demoteAlice = me.SendWithTokenAsync(HttpMethod.Put, $"/api/users/{aliceId}", new { email = $"{name}@example.com", role = "user" });
            var demoteAdmin = alice.SendWithTokenAsync(HttpMethod.Put, $"/api/users/{adminId}", new { email = ServerScenario.AdminEmail, role = "user" });
            using var first = await demoteAlice;
            using var second = await demoteAdmin;

            int administrators;
            using (var scope = server.Services.CreateScope())
            {
                var accounts = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                administrators = (await accounts.GetUsersInRoleAsync(SystemRoles.Administrator)).Count(u => !u.IsDisabled);
            }

            Assert.True(administrators >= 1, $"Round {round}: {(int)first.StatusCode} and {(int)second.StatusCode} left no administrator.");

            if (administrators == 1 && !first.IsSuccessStatusCode)
            {
                // Alice won: make the bootstrap administrator one again for the next round.
                await server.AddToAdministratorsAsync(ServerScenario.AdminUserName);
            }
        }
    }

    [Fact]
    public async Task Disabling_a_user_ends_their_sessions_at_once_and_blocks_sign_in()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        var bobId = await server.CreateUserAsync("bob", BobPassword);
        using var admin = await server.SignedInClientAsync();
        using var bob = await SignInAsync(server, "bob", BobPassword);

        using var disable = await admin.SendWithTokenAsync(HttpMethod.Put, $"/api/users/{bobId}", new { email = "bob@example.com", role = "user", isDisabled = true });

        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await bob.GetAsync(new Uri(AnyEndpoint, UriKind.Relative), Token)).StatusCode);
        using var again = server.Client(handleCookies: true);
        Assert.Equal(HttpStatusCode.Unauthorized, (await again.LoginAsync("bob", BobPassword)).StatusCode);
    }

    [Fact]
    public async Task Resetting_a_password_ends_the_sessions_and_requires_a_change()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        var bobId = await server.CreateUserAsync("bob", BobPassword);
        using var admin = await server.SignedInClientAsync();
        using var bob = await SignInAsync(server, "bob", BobPassword);

        using var reset = await admin.SendWithTokenAsync(HttpMethod.Post, $"/api/users/{bobId}/password", new { password = "Temp-Passw0rd-999" });

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await bob.GetAsync(new Uri(AnyEndpoint, UriKind.Relative), Token)).StatusCode);
        using var old = server.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await old.LoginAsync("bob", BobPassword)).StatusCode);
        using var fresh = await SignInAsync(server, "bob", "Temp-Passw0rd-999");
        Assert.Equal(HttpStatusCode.Forbidden, (await fresh.GetAsync(new Uri(AnyEndpoint, UriKind.Relative), Token)).StatusCode);
    }

    [Fact]
    public async Task Promoting_a_user_ends_their_sessions_so_the_new_role_applies()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        var bobId = await server.CreateUserAsync("bob", BobPassword);
        using var admin = await server.SignedInClientAsync();
        using var bob = await SignInAsync(server, "bob", BobPassword);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync(new Uri("/api/users", UriKind.Relative), Token)).StatusCode);

        (await admin.SendWithTokenAsync(HttpMethod.Put, $"/api/users/{bobId}", new { email = "bob@example.com", role = "administrator" })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Unauthorized, (await bob.GetAsync(new Uri(AnyEndpoint, UriKind.Relative), Token)).StatusCode);
        using var again = await SignInAsync(server, "bob", BobPassword);
        Assert.Equal(HttpStatusCode.OK, (await again.GetAsync(new Uri("/api/users", UriKind.Relative), Token)).StatusCode);
    }

    [Fact]
    public async Task Administrators_cant_lock_themselves_out()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        using var admin = await server.SignedInClientAsync();
        var me = (await admin.GetFromJsonAsync<JsonElement>("/api/auth/me", Token)).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendWithTokenAsync(HttpMethod.Put, $"/api/users/{me}", new { email = ServerScenario.AdminEmail, role = "user" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendWithTokenAsync(HttpMethod.Put, $"/api/users/{me}", new { email = ServerScenario.AdminEmail, role = "administrator", isDisabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendWithTokenAsync(HttpMethod.Delete, $"/api/users/{me}")).StatusCode);

        // A second administrator may demote the first; the last one left stays protected.
        var aliceId = await server.CreateUserAsync("alice", BobPassword);
        await server.AddToAdministratorsAsync("alice");
        using var alice = await SignInAsync(server, "alice", BobPassword);
        Assert.Equal(HttpStatusCode.OK, (await alice.SendWithTokenAsync(HttpMethod.Put, $"/api/users/{me}", new { email = ServerScenario.AdminEmail, role = "user" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.SendWithTokenAsync(HttpMethod.Put, $"/api/users/{aliceId}", new { email = "alice@example.com", role = "user" })).StatusCode);
    }

    [Theory]
    [InlineData("""{"userName":"bob","email":"bob@example.com","role":"user","password":"short"}""", "password")]
    [InlineData("""{"userName":"admin","email":"other@example.com","role":"user","password":"Long-Enough-Passw0rd"}""", "userName")]
    [InlineData("""{"userName":"bad name","email":"x@example.com","role":"user","password":"Long-Enough-Passw0rd"}""", "userName")]
    [InlineData("""{"userName":"bob","email":"not-an-email","role":"user","password":"Long-Enough-Passw0rd"}""", "email")]
    [InlineData("""{"userName":"bob","email":"bob@example.com","password":"Long-Enough-Passw0rd"}""", "role")]
    public async Task Creating_a_user_with_invalid_input_reports_the_field(string body, string field)
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        using var admin = await server.SignedInClientAsync();
        var (header, token) = await admin.AntiforgeryAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/users", UriKind.Relative))
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(header, token);

        using var response = await admin.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, (await response.ProblemAsync()).GetProperty("errors").EnumerateObject().Select(p => p.Name));
    }

    private static async Task<HttpClient> SignInAsync(ServerScenario server, string userName, string password)
    {
        var client = server.Client();
        using var response = await client.LoginAsync(userName, password);
        response.EnsureSuccessStatusCode();
        return client;
    }

    private sealed record User(Guid Id, string UserName, string? Email, string Role, bool IsDisabled, bool MustChangePassword, bool IsLockedOut);
}
