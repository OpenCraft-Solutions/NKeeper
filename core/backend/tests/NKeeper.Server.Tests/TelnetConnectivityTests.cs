using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Core.Audit;
using NKeeper.Core.Connectivity.Telnet;
using NKeeper.Core.Persistence;
using NKeeper.Testing;

namespace NKeeper.Server.Tests;

/// <summary>
/// docs/architecture.md §7.22: Telnet terminals core opens for a plugin (through the test plugin's <c>/telnet</c>
/// endpoint), against a fake network device, and the protocol underneath. Ported from 1.x's <c>TelnetSessionTests</c>.
/// </summary>
public sealed class TelnetConnectivityTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private readonly FakeTelnetServer _device = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _device.DisposeAsync();

    [Fact]
    public async Task With_a_credential_core_signs_in_at_the_prompts_and_negotiates_the_terminal()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, _device.Port, withCredential: true);

        var result = await TelnetAsync(admin, host, new { serviceId = ServiceId(host), credentialId = CredentialId(host), steps = new[] { new { expect = "$ ", send = (string?)"show version" }, new { expect = "you said: show version", send = (string?)null } } });

        var output = result.GetProperty("output").GetString()!;
        Assert.Contains($"Welcome {FakeTelnetServer.UserName}", output, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeTelnetServer.Password, output, StringComparison.Ordinal);
        Assert.Equal("XTERM-256COLOR", _device.TerminalType);
        // The probe resizes at once, often before the server asked for the size: the server ends up with the latest.
        Assert.Equal((120, 40), _device.WindowSizes.Last());
        Assert.Contains("WONT 39", _device.Negotiation);

        var used = Assert.Single(await AuditAsync(server, AuditActions.CredentialUse));
        Assert.Contains($"telnet to service {ServiceId(host)}, plugin hello, credential {CredentialId(host)}", used.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_credential_the_user_signs_in_in_the_terminal()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, _device.Port, withCredential: false);

        var result = await TelnetAsync(admin, host, new
        {
            serviceId = ServiceId(host),
            steps = new[]
            {
                new { expect = "login: ", send = FakeTelnetServer.UserName },
                new { expect = "Password: ", send = FakeTelnetServer.Password },
                new { expect = $"Welcome {FakeTelnetServer.UserName}", send = "exit" },
            },
        });

        Assert.Contains("Switch-01", result.GetProperty("output").GetString(), StringComparison.Ordinal);
        Assert.Empty(await AuditAsync(server, AuditActions.CredentialUse));
    }

    [Fact]
    public async Task A_service_that_isnt_listening_is_unreachable()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, UnusedPort(), withCredential: false);

        var failed = await TelnetAsync(admin, host, new { serviceId = ServiceId(host) }, HttpStatusCode.BadGateway);

        Assert.Equal("Unreachable", failed.GetProperty("reason").GetString());
    }

    // ---- The protocol, ported from 1.x ----

    [Fact]
    public void Commands_are_taken_out_of_the_data_even_when_split_across_reads()
    {
        var protocol = new TelnetProtocol();
        var data = new List<byte>();
        var replies = new List<byte>();

        // "ab", an escaped 255, then DO NAWS split in the middle, then "c".
        protocol.Receive([(byte)'a', (byte)'b', 255, 255, 255, 253], data, replies);
        protocol.Receive([31, (byte)'c'], data, replies);

        Assert.Equal([(byte)'a', (byte)'b', 255, (byte)'c'], data);
        // WILL NAWS, then the window size (80 x 24 until resized).
        Assert.Equal([255, 251, 31, 255, 250, 31, 0, 80, 0, 24, 255, 240], replies);
    }

    [Fact]
    public void Unsupported_options_are_refused_once_so_negotiation_cant_loop()
    {
        var protocol = new TelnetProtocol();
        var replies = new List<byte>();

        protocol.Receive([255, 253, 39, 255, 253, 39, 255, 251, 5], [], replies);

        Assert.Equal([255, 252, 39, 255, 254, 5], replies);
        Assert.False(protocol.ServerEchoes);
        protocol.Receive([255, 251, 1], [], replies = []);
        Assert.True(protocol.ServerEchoes);
        Assert.Equal([255, 253, 1], replies);
    }

    [Fact]
    public void Typed_text_escapes_iac_and_enter_becomes_cr_lf()
    {
        Assert.Equal(Encoding.ASCII.GetBytes("ls\r\n"), TelnetProtocol.Encode(Encoding.ASCII.GetBytes("ls\r")));
        Assert.Equal(Encoding.ASCII.GetBytes("a\r\nb"), TelnetProtocol.Encode(Encoding.ASCII.GetBytes("a\r\nb")));
        Assert.Equal([1, 255, 255, 2], TelnetProtocol.Encode([1, 255, 2]));
    }

    // ---- Helpers ----

    private static Guid Id(JsonElement host) => host.GetProperty("id").GetGuid();

    private static Guid ServiceId(JsonElement host) => host.GetProperty("services")[0].GetProperty("id").GetGuid();

    private static Guid CredentialId(JsonElement host) => host.GetProperty("credentials")[0].GetProperty("id").GetGuid();

    private static int UnusedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<JsonElement> CreateHostAsync(HttpClient admin, int port, bool withCredential)
    {
        using var response = await admin.SendWithTokenAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = $"switch-{Guid.NewGuid():N}",
            ipAddress = "127.0.0.1",
            services = new[] { new { type = "hello", port, settings = new { greeting = "a" } } },
            credentials = withCredential ? new[] { new { username = FakeTelnetServer.UserName, password = FakeTelnetServer.Password } } : [],
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Token));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    /// <summary>Calls the test plugin's Telnet endpoint, expecting <paramref name="status"/>.</summary>
    private static async Task<JsonElement> TelnetAsync(HttpClient client, JsonElement host, object probe, HttpStatusCode status = HttpStatusCode.OK)
    {
        using var response = await client.SendWithTokenAsync(HttpMethod.Post, $"/api/p/hello/hosts/{Id(host)}/telnet", probe);
        var body = await response.Content.ReadAsStringAsync(Token);
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    private static async Task<List<AuditEvent>> AuditAsync(ServerScenario server, string action)
    {
        using var scope = server.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CoreDbContext>().AuditEvents.AsNoTracking().Where(e => e.Action == action).ToListAsync(Token);
    }

    private async Task<ServerScenario> ServerAsync()
    {
        var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        server.Install(TestPlugins.Package(server.DevelopmentKey));
        return server;
    }
}
