using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Core.Audit;
using NKeeper.Core.Persistence;
using NKeeper.Testing;
using Npgsql;

namespace NKeeper.Server.Tests;

/// <summary>
/// docs/architecture.md §7.31, SDK 1.7: database connections core opens with the plugin's driver (the test plugin's
/// <c>/db</c>, with Npgsql against the tests' own PostgreSQL), and identities a plugin read checked against the trust
/// store (<c>/identity</c>), and plain TCP connections (<c>/tcp</c>, SDK 1.9).
/// </summary>
public sealed class DatabaseConnectivityTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Core_signs_the_drivers_connections_in_and_the_driver_forgets_the_password()
    {
        var database = new NpgsqlConnectionStringBuilder(await postgres.CreateDatabaseAsync());
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, database.Port, database.Username!, database.Password);

        var answer = await ProbeAsync(admin, host, "db", new { serviceId = ServiceId(host), credentialId = CredentialId(host), database = database.Database });

        Assert.Equal((database.Username, database.Username, false), (answer.GetProperty("first").GetString(), answer.GetProperty("second").GetString(), answer.GetProperty("passwordInConnectionString").GetBoolean()));
        var use = Assert.Single(await AuditAsync(server, AuditActions.CredentialUse));
        Assert.StartsWith($"database to service {ServiceId(host)}, plugin hello, credential ", use.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_login_a_driver_that_keeps_the_password_and_no_password_are_refused()
    {
        var database = new NpgsqlConnectionStringBuilder(await postgres.CreateDatabaseAsync());
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();

        var wrong = await CreateHostAsync(admin, database.Port, database.Username!, "not-the-password");
        var refused = await ProbeAsync(admin, wrong, "db", new { serviceId = ServiceId(wrong), credentialId = CredentialId(wrong), database = database.Database }, HttpStatusCode.BadGateway);
        Assert.Equal("AuthenticationFailed", refused.GetProperty("reason").GetString());
        Assert.DoesNotContain("not-the-password", refused.GetProperty("message").GetString(), StringComparison.Ordinal);

        var host = await CreateHostAsync(admin, database.Port, database.Username!, database.Password);
        var kept = await ProbeAsync(admin, host, "db", new { serviceId = ServiceId(host), credentialId = CredentialId(host), keep = true }, HttpStatusCode.BadGateway);
        Assert.Equal("SecurityNegotiationFailed", kept.GetProperty("reason").GetString());

        // A driver whose error isn't a DbException, repeating the password: refused, in its words, without the password.
        var thrown = await ProbeAsync(admin, host, "db", new { serviceId = ServiceId(host), credentialId = CredentialId(host), @throw = true }, HttpStatusCode.BadGateway);
        Assert.Equal("AuthenticationFailed", thrown.GetProperty("reason").GetString());
        Assert.Contains("Refused:", thrown.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(database.Password!, thrown.GetProperty("message").GetString(), StringComparison.Ordinal);

        var none = await CreateHostAsync(admin, database.Port, database.Username!, null);
        Assert.Equal("NoSecret", (await ProbeAsync(admin, none, "db", new { serviceId = ServiceId(none), credentialId = CredentialId(none) }, HttpStatusCode.BadGateway)).GetProperty("reason").GetString());
        Assert.Empty(await AuditAsync(server, AuditActions.CredentialUse));
    }

    [Fact]
    public async Task A_driver_that_keeps_the_password_is_refused_unless_the_manifest_declares_why()
    {
        var database = new NpgsqlConnectionStringBuilder(await postgres.CreateDatabaseAsync());
        var probe = (JsonElement host) => new { serviceId = ServiceId(host), credentialId = CredentialId(host), keep = true, driverKeeps = true };

        await using (var undeclared = await ServerAsync())
        {
            using var admin = await undeclared.SignedInClientAsync();
            var host = await CreateHostAsync(admin, database.Port, database.Username!, database.Password);
            var refused = await ProbeAsync(admin, host, "db", probe(host), HttpStatusCode.BadGateway);
            Assert.Equal("SecurityNegotiationFailed", refused.GetProperty("reason").GetString());
            Assert.Contains("credentialRelease", refused.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.Empty(await AuditAsync(undeclared, AuditActions.CredentialReveal));
        }

        await using var declared = new ServerScenario(await postgres.CreateDatabaseAsync());
        declared.Install(TestPlugins.Package(declared.DevelopmentKey, credentialRelease: "Its test driver keeps the password."));
        using var client = await declared.SignedInClientAsync();
        var plugins = await client.GetFromJsonAsync<JsonElement>("/api/plugins", Token);
        Assert.Equal("Its test driver keeps the password.", plugins.EnumerateArray().Single(p => p.GetProperty("id").GetString() == "hello").GetProperty("credentialRelease").GetString());

        var opened = await CreateHostAsync(client, database.Port, database.Username!, database.Password);
        Assert.True((await ProbeAsync(client, opened, "db", probe(opened))).GetProperty("kept").GetBoolean());
        var reveal = Assert.Single(await AuditAsync(declared, AuditActions.CredentialReveal));
        Assert.StartsWith($"database to service {ServiceId(opened)}, plugin hello, credential ", reveal.Detail, StringComparison.Ordinal);
        Assert.EndsWith("the driver keeps the password (Its test driver keeps the password.)", reveal.Detail, StringComparison.Ordinal);
        Assert.Single(await AuditAsync(declared, AuditActions.CredentialUse));
    }

    [Fact]
    public async Task An_identity_the_plugin_read_is_asked_about_then_trusted_and_a_changed_one_refused()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, 1433, "sa", "x");
        var probe = new { serviceId = ServiceId(host), fingerprint = "AAAABBBBCCCCDDDDEEEEFFFFGGGGHHHHIIIIJJJJKKK" };

        var unknown = await ProbeAsync(admin, host, "identity", probe, HttpStatusCode.BadGateway);
        Assert.Equal("HostKeyUnknown", unknown.GetProperty("reason").GetString());
        var presented = unknown.GetProperty("presented");
        using (var trusted = await admin.SendWithTokenAsync(
            HttpMethod.Post,
            $"/api/hosts/{Id(host)}/services/{ServiceId(host)}/trusted-keys",
            new { algorithm = presented.GetProperty("algorithm").GetString(), fingerprint = presented.GetProperty("fingerprint").GetString() }))
        {
            Assert.True(trusted.IsSuccessStatusCode, await trusted.Content.ReadAsStringAsync(Token));
        }

        Assert.True((await ProbeAsync(admin, host, "identity", probe)).GetProperty("ok").GetBoolean());
        var changed = await ProbeAsync(admin, host, "identity", new { serviceId = ServiceId(host), fingerprint = "ZZZZBBBBCCCCDDDDEEEEFFFFGGGGHHHHIIIIJJJJKKK" }, HttpStatusCode.BadGateway);
        Assert.Equal("HostKeyChanged", changed.GetProperty("reason").GetString());

        // A publicly valid certificate needs no trust.
        Assert.True((await ProbeAsync(admin, host, "identity", new { serviceId = ServiceId(host), fingerprint = "PUBLIC", publiclyValid = true })).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task A_plain_TCP_connection_reaches_the_service_and_an_unreachable_one_is_refused()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var answering = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(Token);
            var stream = client.GetStream();
            var buffer = new byte[64];
            var read = await stream.ReadAsync(buffer, Token);
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("pong:" + System.Text.Encoding.ASCII.GetString(buffer, 0, read).Trim()), Token);
        }, Token);
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, port, "sa", "x");

        var answer = await ProbeAsync(admin, host, "tcp", new { serviceId = ServiceId(host), send = "hello" });
        await answering;

        Assert.Equal(("127.0.0.1", port, "pong:hello"), (answer.GetProperty("address").GetString(), answer.GetProperty("port").GetInt32(), answer.GetProperty("answer").GetString()));
        listener.Stop();
        var closed = await CreateHostAsync(admin, port, "sa", "x");
        Assert.Equal("Unreachable", (await ProbeAsync(admin, closed, "tcp", new { serviceId = ServiceId(closed) }, HttpStatusCode.BadGateway)).GetProperty("reason").GetString());
    }

    private async Task<ServerScenario> ServerAsync()
    {
        var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        server.Install(TestPlugins.Package(server.DevelopmentKey));
        return server;
    }

    private static async Task<JsonElement> CreateHostAsync(HttpClient admin, int port, string username, string? password)
    {
        using var response = await admin.SendWithTokenAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = $"db-{Guid.NewGuid():N}",
            ipAddress = "127.0.0.1",
            services = new[] { new { type = "hello", port, settings = new { greeting = "a" } } },
            credentials = new[] { new { username, password } },
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Token));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    private static async Task<JsonElement> ProbeAsync(HttpClient client, JsonElement host, string endpoint, object probe, HttpStatusCode status = HttpStatusCode.OK)
    {
        using var response = await client.SendWithTokenAsync(HttpMethod.Post, $"/api/p/hello/hosts/{Id(host)}/{endpoint}", probe);
        var body = await response.Content.ReadAsStringAsync(Token);
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    private static async Task<List<AuditEvent>> AuditAsync(ServerScenario server, string action)
    {
        using var scope = server.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CoreDbContext>().AuditEvents.AsNoTracking().Where(e => e.Action == action).ToListAsync(Token);
    }

    private static Guid Id(JsonElement host) => host.GetProperty("id").GetGuid();

    private static Guid ServiceId(JsonElement host) => host.GetProperty("services")[0].GetProperty("id").GetGuid();

    private static Guid CredentialId(JsonElement host) => host.GetProperty("credentials")[0].GetProperty("id").GetGuid();
}
