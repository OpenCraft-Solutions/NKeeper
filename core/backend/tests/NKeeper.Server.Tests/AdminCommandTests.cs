using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Core.Audit;
using NKeeper.Core.Persistence;
using NKeeper.Testing;

namespace NKeeper.Server.Tests;

/// <summary>docs/architecture.md §8: <c>nkeeper admin reset-password</c>, root's way back in when nobody can sign in.</summary>
public sealed class AdminCommandTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string BobPassword = "Bob-Passw0rd-123";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Resetting_a_password_prints_a_new_one_that_must_be_changed_and_ends_the_users_sessions()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        var bobId = await server.CreateUserAsync("bob", BobPassword);
        using var bob = server.Client();
        (await bob.LoginAsync("bob", BobPassword)).EnsureSuccessStatusCode();

        var (code, output, _) = await RunAsync(server, "reset-password", "bob");

        Assert.Equal(0, code);
        var password = output.Split("password is now: ")[1].Split('\n')[0].Trim();
        Assert.Equal(20, password.Length);
        Assert.Equal(HttpStatusCode.Unauthorized, (await bob.GetAsync("/api/auth/me", Token)).StatusCode);

        using var again = server.Client();
        Assert.False((await again.LoginAsync("bob", BobPassword)).IsSuccessStatusCode);
        (await again.LoginAsync("bob", password)).EnsureSuccessStatusCode();
        var me = await again.GetFromJsonAsync<JsonElement>("/api/auth/me", Token);
        Assert.True(me.GetProperty("mustChangePassword").GetBoolean());

        using var scope = server.Services.CreateScope();
        var reset = await scope.ServiceProvider.GetRequiredService<CoreDbContext>().AuditEvents.AsNoTracking()
            .SingleAsync(e => e.Action == AuditActions.UserCredentialReset && e.Target == bobId.ToString(), Token);
        Assert.Equal("system", reset.Actor);
    }

    [Fact]
    public void Every_generated_password_meets_the_policy()
    {
        for (var i = 0; i < 2000; i++)
        {
            var password = AdminCommand.NewPassword();
            Assert.True(password.Length == 20 && password.Any(char.IsAsciiDigit) && password.Any(char.IsAsciiLetterUpper) && password.Any(char.IsAsciiLetterLower), password);
        }
    }

    [Fact]
    public async Task An_unknown_user_or_a_wrong_usage_changes_nothing()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        // Started, so its database is set up.
        using var admin = await server.SignedInClientAsync();

        var (unknown, _, error) = await RunAsync(server, "reset-password", "nobody");
        Assert.Equal((1, true), (unknown, error.Contains("no user named nobody", StringComparison.Ordinal)));
        Assert.Equal(2, (await RunAsync(server, "reset-password")).Code);
        Assert.Equal(2, (await RunAsync(server, "delete-everything", "bob")).Code);
    }

    private static async Task<(int Code, string Output, string Error)> RunAsync(ServerScenario server, params string[] args)
    {
        // The server's own database; Development, so the test needn't be root.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:NKeeper"] = server.ConnectionString, ["ASPNETCORE_ENVIRONMENT"] = "Development" })
            .Build();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await AdminCommand.RunAsync(args, configuration, output, error, Token);
        return (code, output.ToString(), error.ToString());
    }
}
