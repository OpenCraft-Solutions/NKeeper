using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Core.Audit;
using NKeeper.Core.Persistence;
using NKeeper.Testing;

namespace NKeeper.Server.Tests;

/// <summary>
/// docs/architecture.md §7.28: WMI and RPC connections core opens for a plugin (through the test plugin's <c>/wmi</c> and
/// <c>/rpc</c> endpoints). WMI runs a fake helper that speaks the real one's JSON lines; RPC talks to a fake DCE/RPC
/// server that offers an NTLM challenge, as Windows does, and answers calls.
/// </summary>
public sealed class WindowsConnectivityTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- WMI ----

    [Fact]
    public async Task Wmi_signs_in_once_through_the_helpers_input_and_runs_each_query_on_it()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, 135, "CORP\\ops", "Secret-1");

        var answers = await ProbeAsync(admin, host, "wmi", new { serviceId = ServiceId(host), credentialId = CredentialId(host), queries = new[] { "WHOAMI", "REFUSE", "SELECT * FROM Win32_NTLogEvent" }, limit = 3 });

        // DOMAIN\user is split; the password reached the helper, and only through its input.
        var me = answers[0][0];
        Assert.Equal(("127.0.0.1", "ops", "CORP", 8, "root/cimv2"), (me.GetProperty("host").GetString(), me.GetProperty("username").GetString(), me.GetProperty("domain").GetString(), me.GetProperty("passwordLength").GetInt32(), me.GetProperty("namespace").GetString()));
        // A refused query leaves the connection usable.
        Assert.Equal("WMI failed: WBEM_E_INVALID_QUERY", answers[1].GetProperty("refused").GetString());
        Assert.Equal([0, 1, 2], answers[2].EnumerateArray().Select(r => r.GetProperty("n").GetInt32()));

        var use = Assert.Single(await AuditAsync(server, AuditActions.CredentialUse));
        Assert.StartsWith($"wmi to service {ServiceId(host)}, plugin hello, credential ", use.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wmi_stops_at_the_first_row_older_than_since()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, 135, "ops", "Secret-1");

        var answers = await ProbeAsync(admin, host, "wmi", new { serviceId = ServiceId(host), credentialId = CredentialId(host), queries = new[] { "SELECT * FROM Win32_NTLogEvent" }, since = new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero) });

        Assert.Equal([0, 1, 2], answers[0].EnumerateArray().Select(r => r.GetProperty("n").GetInt32()));
    }

    [Fact]
    public async Task Wmi_reports_a_refused_login_a_helper_that_stops_and_a_missing_python()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var refused = await CreateHostAsync(admin, 135, "bad", "Secret-1");
        Assert.Equal("AuthenticationFailed", (await FailedAsync(admin, refused, "wmi", new { serviceId = ServiceId(refused), credentialId = CredentialId(refused), queries = new[] { "WHOAMI" } })).GetProperty("reason").GetString());

        var host = await CreateHostAsync(admin, 135, "ops", "Secret-1");
        Assert.Equal("Unreachable", (await FailedAsync(admin, host, "wmi", new { serviceId = ServiceId(host), credentialId = CredentialId(host), queries = new[] { "DIE" } })).GetProperty("reason").GetString());
        Assert.DoesNotContain(await AuditAsync(server, AuditActions.CredentialUse), e => e.Detail.Contains(ServiceId(refused).ToString(), StringComparison.Ordinal));

        await using var without = await ServerAsync(python: "no-such-python-nkeeper");
        using var admin2 = await without.SignedInClientAsync();
        var other = await CreateHostAsync(admin2, 135, "ops", "Secret-1");
        var missing = await FailedAsync(admin2, other, "wmi", new { serviceId = ServiceId(other), credentialId = CredentialId(other), queries = new[] { "WHOAMI" } });
        Assert.Equal("Unreachable", missing.GetProperty("reason").GetString());
        Assert.Contains("Impacket", missing.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Windows_connections_need_a_password_and_no_jump_host()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var noPassword = await CreateHostAsync(admin, 135, "ops", null);
        Assert.Equal("NoSecret", (await FailedAsync(admin, noPassword, "wmi", new { serviceId = ServiceId(noPassword), credentialId = CredentialId(noPassword) })).GetProperty("reason").GetString());
        Assert.Equal("NoSecret", (await FailedAsync(admin, noPassword, "rpc", new { serviceId = ServiceId(noPassword), credentialId = CredentialId(noPassword) })).GetProperty("reason").GetString());

        var jump = await CreateHostAsync(admin, 22, "jump", "Secret-1");
        var behind = await CreateHostAsync(admin, 135, "ops", "Secret-1", new { hostId = Id(jump), serviceId = ServiceId(jump), credentialId = CredentialId(jump) });
        Assert.Equal("JumpHostUnavailable", (await FailedAsync(admin, behind, "wmi", new { serviceId = ServiceId(behind), credentialId = CredentialId(behind) })).GetProperty("reason").GetString());
        Assert.Equal("JumpHostUnavailable", (await FailedAsync(admin, behind, "rpc", new { serviceId = ServiceId(behind), credentialId = CredentialId(behind) })).GetProperty("reason").GetString());
    }

    [Fact]
    public async Task The_real_helper_ships_with_the_server_and_says_when_impacket_is_missing()
    {
        var helper = Path.Combine(AppContext.BaseDirectory, "Connectivity", "wmi_helper.py");
        Assert.True(File.Exists(helper), helper);

        var start = new ProcessStartInfo(Python) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("import ast, sys; ast.parse(open(sys.argv[1], encoding='utf-8').read()); import importlib.util; print('impacket' if importlib.util.find_spec('impacket') else 'none')");
        start.ArgumentList.Add(helper);
        using (var check = Process.Start(start)!)
        {
            var found = (await check.StandardOutput.ReadToEndAsync(Token)).Trim();
            await check.WaitForExitAsync(Token);
            Assert.True(check.ExitCode == 0, await check.StandardError.ReadToEndAsync(Token));
            if (found == "impacket")
            {
                // Where Impacket is installed, signing in would need a Windows host.
                return;
            }
        }

        var run = new ProcessStartInfo(Python) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
        run.ArgumentList.Add(helper);
        using var process = Process.Start(run)!;
        var answer = JsonDocument.Parse(await process.StandardOutput.ReadLineAsync(Token) ?? "{}").RootElement;
        await process.WaitForExitAsync(Token);
        Assert.Equal("missing", answer.GetProperty("kind").GetString());
    }

    // ---- RPC ----

    [Fact]
    public async Task Rpc_binds_with_ntlm_and_carries_calls_and_faults()
    {
        await using var rpc = new FakeRpcServer();
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, rpc.Port, "CORP\\ops", "Secret-1");

        var answers = await ProbeAsync(admin, host, "rpc", new { serviceId = ServiceId(host), credentialId = CredentialId(host), queries = new[] { "2:010203", "9:00" } });

        // The fake answers each stub reversed, and opnum 9 with a fault.
        Assert.Equal(["030201", "fault 1783"], answers.Select(a => a.GetString()));
        Assert.Equal(("e1af8308-5d1f-11c9-91a4-08002b14a0fa", 3, true), (rpc.BoundInterface.ToString(), rpc.BoundMajor, rpc.Authenticated));
        Assert.StartsWith($"rpc to service {ServiceId(host)}, plugin hello, credential ", Assert.Single(await AuditAsync(server, AuditActions.CredentialUse)).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rpc_reports_a_refused_bind_and_a_server_that_isnt_there()
    {
        await using var rpc = new FakeRpcServer { RefuseBind = true };
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, rpc.Port, "ops", "Secret-1");
        Assert.Equal("AuthenticationFailed", (await FailedAsync(admin, host, "rpc", new { serviceId = ServiceId(host), credentialId = CredentialId(host) })).GetProperty("reason").GetString());

        var closed = await CreateHostAsync(admin, UnusedPort(), "ops", "Secret-1");
        Assert.Equal("Unreachable", (await FailedAsync(admin, closed, "rpc", new { serviceId = ServiceId(closed), credentialId = CredentialId(closed) })).GetProperty("reason").GetString());
        Assert.Empty(await AuditAsync(server, AuditActions.CredentialUse));
    }

    // ---- Helpers ----

    /// <summary>The Python on this machine, for the fake helper: python3 on Linux, python on Windows.</summary>
    private static readonly string Python = OperatingSystem.IsWindows() ? "python" : "python3";

    private async Task<ServerScenario> ServerAsync(string? python = null)
    {
        var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        server.Settings["Connectivity:Wmi:Python"] = python ?? Python;
        server.Settings["Connectivity:Wmi:Helper"] = Path.Combine(AppContext.BaseDirectory, "Windows", "fake_wmi_helper.py");
        server.Install(TestPlugins.Package(server.DevelopmentKey));
        return server;
    }

    private static int UnusedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<JsonElement> CreateHostAsync(HttpClient admin, int port, string username, string? password, object? jump = null)
    {
        using var response = await admin.SendWithTokenAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = $"win-{Guid.NewGuid():N}",
            ipAddress = "127.0.0.1",
            services = new[] { new { type = "hello", port, settings = new { greeting = "a" } } },
            credentials = new[] { new { username, password } },
            jump,
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Token));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    private static async Task<List<JsonElement>> ProbeAsync(HttpClient client, JsonElement host, string endpoint, object probe)
    {
        using var response = await client.SendWithTokenAsync(HttpMethod.Post, $"/api/p/hello/hosts/{Id(host)}/{endpoint}", probe);
        var body = await response.Content.ReadAsStringAsync(Token);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}");
        return [.. JsonDocument.Parse(body).RootElement.EnumerateArray()];
    }

    private static async Task<JsonElement> FailedAsync(HttpClient client, JsonElement host, string endpoint, object probe)
    {
        using var response = await client.SendWithTokenAsync(HttpMethod.Post, $"/api/p/hello/hosts/{Id(host)}/{endpoint}", probe);
        var body = await response.Content.ReadAsStringAsync(Token);
        Assert.True(response.StatusCode == HttpStatusCode.BadGateway, $"{response.StatusCode}: {body}");
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

    /// <summary>
    /// One DCE/RPC connection at a time: it acknowledges a bind with an NTLM challenge (accepting whatever answer comes
    /// back, since checking it isn't what's tested), then answers each call with its stub reversed, or opnum 9 with a fault.
    /// </summary>
    private sealed class FakeRpcServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serving;

        public FakeRpcServer()
        {
            _listener.Start();
            _serving = ServeAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public bool RefuseBind { get; init; }

        public Guid BoundInterface { get; private set; }

        public ushort BoundMajor { get; private set; }

        public bool Authenticated { get; private set; }

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }

                using (client)
                {
                    try
                    {
                        await ConverseAsync(client.GetStream());
                    }
                    catch (Exception ex) when (ex is IOException or EndOfStreamException or OperationCanceledException)
                    {
                        // The client went away.
                    }
                }
            }
        }

        private async Task ConverseAsync(NetworkStream stream)
        {
            while (true)
            {
                var (type, callId, body) = await ReadAsync(stream);
                switch (type)
                {
                    case 11: // bind
                        BoundInterface = new Guid(body.AsSpan(16, 16));
                        BoundMajor = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(32));
                        if (RefuseBind)
                        {
                            await stream.WriteAsync(Pdu(13, callId, new byte[4], null), _stop.Token);
                            return;
                        }

                        // max fragments, association group, no secondary address, then one accepted result.
                        var ack = new byte[40];
                        BinaryPrimitives.WriteUInt16LittleEndian(ack, 5840);
                        BinaryPrimitives.WriteUInt16LittleEndian(ack.AsSpan(2), 5840);
                        BinaryPrimitives.WriteUInt32LittleEndian(ack.AsSpan(4), 0x1234);
                        ack[12] = 1;
                        await stream.WriteAsync(Pdu(12, callId, ack, Challenge()), _stop.Token);
                        break;
                    case 16: // auth3, the client's answer to the challenge
                        Authenticated = true;
                        break;
                    case 0: // request
                        var opnum = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(6));
                        var reply = new byte[8 + (opnum == 9 ? 8 : body.Length - 8)];
                        if (opnum == 9)
                        {
                            BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(8), 1783);
                            await stream.WriteAsync(Pdu(3, callId, reply, null), _stop.Token);
                        }
                        else
                        {
                            body.AsSpan(8).ToArray().Reverse().ToArray().CopyTo(reply, 8);
                            await stream.WriteAsync(Pdu(2, callId, reply, null), _stop.Token);
                        }

                        break;
                    default:
                        return;
                }
            }
        }

        private async Task<(byte Type, uint CallId, byte[] Body)> ReadAsync(NetworkStream stream)
        {
            var header = new byte[16];
            await stream.ReadExactlyAsync(header, _stop.Token);
            var body = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8)) - 16];
            await stream.ReadExactlyAsync(body, _stop.Token);
            return (header[2], BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12)), body);
        }

        private static byte[] Pdu(byte type, uint callId, byte[] body, byte[]? token)
        {
            var total = 16 + body.Length + (token is null ? 0 : 8 + token.Length);
            var pdu = new byte[total];
            pdu[0] = 5;
            pdu[2] = type;
            pdu[3] = 3;
            pdu[4] = 0x10;
            BinaryPrimitives.WriteUInt16LittleEndian(pdu.AsSpan(8), (ushort)total);
            BinaryPrimitives.WriteUInt16LittleEndian(pdu.AsSpan(10), (ushort)(token?.Length ?? 0));
            BinaryPrimitives.WriteUInt32LittleEndian(pdu.AsSpan(12), callId);
            body.CopyTo(pdu, 16);
            if (token is not null)
            {
                pdu[16 + body.Length] = 10;
                pdu[16 + body.Length + 1] = 2;
                token.CopyTo(pdu, 16 + body.Length + 8);
            }

            return pdu;
        }

        /// <summary>An NTLM CHALLENGE message (MS-NLMP 2.2.1.2), with a target name and the target information NTLMv2 needs.</summary>
        private static byte[] Challenge()
        {
            var name = Encoding.Unicode.GetBytes("FAKE");
            var info = new List<byte>();
            void Pair(ushort id, byte[] value)
            {
                info.AddRange(BitConverter.GetBytes(id));
                info.AddRange(BitConverter.GetBytes((ushort)value.Length));
                info.AddRange(value);
            }

            Pair(2, name); // NetBIOS domain
            Pair(1, name); // NetBIOS computer
            Pair(7, BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc())); // timestamp
            Pair(0, []);
            // What Windows answers a client asking for signing, which .NET's managed NTLM requires: unicode, target, sign, NTLM,
            // always sign, target type domain, extended session security, target info, version, 128-bit and key exchange.
            const uint Flags = 0x628A8215;
            var message = new byte[56 + name.Length + info.Count];
            Encoding.ASCII.GetBytes("NTLMSSP\0").CopyTo(message, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(8), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(12), (ushort)name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(14), (ushort)name.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(16), 56);
            BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(20), Flags);
            Random.Shared.NextBytes(message.AsSpan(24, 8));
            BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(40), (ushort)info.Count);
            BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(42), (ushort)info.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(44), (uint)(56 + name.Length));
            message[48] = 10; // version 10.0, build 19041, NTLM revision 15
            BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(50), 19041);
            message[55] = 15;
            name.CopyTo(message, 56);
            info.ToArray().CopyTo(message, 56 + name.Length);
            return message;
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _serving;
            _stop.Dispose();
        }
    }
}
