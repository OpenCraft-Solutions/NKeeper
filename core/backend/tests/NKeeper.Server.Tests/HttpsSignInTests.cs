using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Core.Audit;
using NKeeper.Core.Persistence;
using NKeeper.Testing;

namespace NKeeper.Server.Tests;

/// <summary>
/// docs/architecture.md §7.16, SDK 1.6: HTTPS connections core signs in to with HTTP Basic, through the test plugin's
/// <c>/https</c> endpoint, against a fake BMC that answers each request with the authorization it received.
/// </summary>
public sealed class HttpsSignInTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Core_signs_every_request_in_with_the_credential_once_the_certificate_is_trusted()
    {
        await using var bmc = new FakeBmc();
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, bmc.Port, "root", "calvin");
        var probe = new { serviceId = ServiceId(host), credentialId = CredentialId(host), command = "/redfish/v1/Systems" };

        // Self-signed: the user trusts it first, as for any HTTPS service.
        var unknown = await ProbeAsync(admin, host, probe, HttpStatusCode.BadGateway);
        Assert.Equal("HostKeyUnknown", unknown.GetProperty("reason").GetString());
        var presented = unknown.GetProperty("presented");
        using (var trusted = await admin.SendWithTokenAsync(
            HttpMethod.Post,
            $"/api/hosts/{Id(host)}/services/{ServiceId(host)}/trusted-keys",
            new { algorithm = presented.GetProperty("algorithm").GetString(), fingerprint = presented.GetProperty("fingerprint").GetString() }))
        {
            Assert.True(trusted.IsSuccessStatusCode, await trusted.Content.ReadAsStringAsync(Token));
        }

        var answer = await ProbeAsync(admin, host, probe);

        // The plugin's own Authorization was replaced by core's, from the credential.
        Assert.Equal((200, "GET /redfish/v1/Systems Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("root:calvin"))), (answer.GetProperty("status").GetInt32(), answer.GetProperty("body").GetString()));
        var use = Assert.Single(await AuditAsync(server, AuditActions.CredentialUse));
        Assert.StartsWith($"https to service {ServiceId(host)}, plugin hello, credential ", use.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_credential_without_a_password_isnt_used()
    {
        await using var bmc = new FakeBmc();
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, bmc.Port, "root", null);

        var refused = await ProbeAsync(admin, host, new { serviceId = ServiceId(host), credentialId = CredentialId(host) }, HttpStatusCode.BadGateway);

        Assert.Equal("NoSecret", refused.GetProperty("reason").GetString());
        Assert.Empty(await AuditAsync(server, AuditActions.CredentialUse));
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
            name = $"bmc-{Guid.NewGuid():N}",
            ipAddress = "127.0.0.1",
            services = new[] { new { type = "hello", port, settings = new { greeting = "a" } } },
            credentials = new[] { new { username, password } },
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Token));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    private static async Task<JsonElement> ProbeAsync(HttpClient client, JsonElement host, object probe, HttpStatusCode status = HttpStatusCode.OK)
    {
        using var response = await client.SendWithTokenAsync(HttpMethod.Post, $"/api/p/hello/hosts/{Id(host)}/https", probe);
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

    /// <summary>An HTTPS server with a self-signed certificate that answers every request with its method, path and authorization.</summary>
    internal sealed class FakeBmc : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly X509Certificate2 _certificate;
        private readonly Task _serving;

        public FakeBmc()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=idrac.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            _certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
            _listener.Start();
            _serving = ServeAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

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

                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            await using var tls = new SslStream(client.GetStream());
                            await tls.AuthenticateAsServerAsync(_certificate);
                            using var reader = new StreamReader(tls, Encoding.ASCII, leaveOpen: true);
                            var first = await reader.ReadLineAsync();
                            string? authorization = null;
                            for (var line = await reader.ReadLineAsync(); !string.IsNullOrEmpty(line); line = await reader.ReadLineAsync())
                            {
                                if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                                {
                                    authorization = line["Authorization:".Length..].Trim();
                                }
                            }

                            var parts = first?.Split(' ') ?? [];
                            var body = Encoding.UTF8.GetBytes($"{parts.ElementAtOrDefault(0)} {parts.ElementAtOrDefault(1)} {authorization}");
                            await tls.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
                            await tls.WriteAsync(body);
                        }
                        catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or ObjectDisposedException)
                        {
                            // A handshake that only reads the certificate, or a client that went away.
                        }
                    }
                });
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _serving;
            _stop.Dispose();
            _certificate.Dispose();
        }
    }
}
