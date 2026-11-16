using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Core.Audit;
using NKeeper.Core.Persistence;
using NKeeper.Testing;

namespace NKeeper.Server.Tests;

/// <summary>
/// docs/architecture.md §7.16–7.19: the web plugin, packaged with YARP and loaded like any plugin, proxying a real
/// HTTPS server with a self-signed certificate, on a session origin of its own.
/// </summary>
public sealed class WebTests(PostgresFixture postgres, SshServerFixture ssh) : IClassFixture<PostgresFixture>, IClassFixture<SshServerFixture>
{
    private const string WebDomain = "web.test";
    private const string BobPassword = "Bob-Passw0rd-123";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_page_opens_once_its_certificate_is_trusted_and_is_proxied_on_its_own_origin()
    {
        await using var target = await TargetServer.StartAsync();
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, target.Port);

        // Not publicly valid and not trusted: core refuses, and shows the certificate.
        using var refused = await OpenAsync(admin, host);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal("host_key_unknown", problem.GetProperty("code").GetString());
        var certificate = problem.GetProperty("presented");
        Assert.Equal(("tls-certificate", "x509"), (certificate.GetProperty("kind").GetString(), certificate.GetProperty("algorithm").GetString()));
        Assert.Equal(target.Fingerprint, certificate.GetProperty("fingerprint").GetString());
        Assert.Equal("CN=nkeeper-web-target", certificate.GetProperty("subject").GetString());

        using var trusted = await admin.SendWithTokenAsync(HttpMethod.Post, $"/api/hosts/{Id(host)}/services/{ServiceId(host)}/trusted-keys", new { algorithm = "x509", fingerprint = target.Fingerprint });
        Assert.True(trusted.IsSuccessStatusCode, await trusted.Content.ReadAsStringAsync(Token));
        var key = (await admin.GetFromJsonAsync<JsonElement>(new Uri($"/api/hosts/{Id(host)}", UriKind.Relative), Token)).GetProperty("trustedKeys")[0];
        Assert.Equal(("tls-certificate", "x509"), (key.GetProperty("kind").GetString(), key.GetProperty("algorithm").GetString()));

        // The TLS health probe reports the certificate, trusted or not.
        using var checkedNow = await admin.SendWithTokenAsync(HttpMethod.Post, $"/api/hosts/{Id(host)}/health");
        var health = (await checkedNow.Content.ReadFromJsonAsync<JsonElement>(Token))[0];
        Assert.Equal(("up", "CN=nkeeper-web-target"), (health.GetProperty("state").GetString(), health.GetProperty("certificateSubject").GetString()));
        Assert.True(health.GetProperty("certificateExpiresAtUtc").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddDays(29));

        using var opened = await OpenAsync(admin, host);
        Assert.True(opened.IsSuccessStatusCode, await opened.Content.ReadAsStringAsync(Token));
        var session = await opened.Content.ReadFromJsonAsync<JsonElement>(Token);
        var launch = new Uri(session.GetProperty("url").GetString()!);
        Assert.Equal($"nkw-{session.GetProperty("id").GetString()}.{WebDomain}", launch.Host);

        // The launch link sets the session's cookie, once.
        var origin = server.Client(handleCookies: false);
        using var launched = await OriginAsync(origin, launch.Host, launch.PathAndQuery, cookie: null);
        Assert.Equal(HttpStatusCode.Redirect, launched.StatusCode);
        var cookie = launched.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("__nkw_session=", StringComparison.Ordinal)).Split(';')[0];
        using var again = await OriginAsync(origin, launch.Host, launch.PathAndQuery, cookie: null);
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);

        // With the cookie, the target answers through the proxy; its cookie stays on the session origin.
        using var page = await OriginAsync(origin, launch.Host, "/", cookie);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("hello from the target", await page.Content.ReadAsStringAsync(Token));
        Assert.Contains(page.Headers.GetValues("Set-Cookie"), c => c.StartsWith("target=1", StringComparison.Ordinal) && c.Contains("SameSite=None", StringComparison.Ordinal));
        Assert.Contains(page.Headers.GetValues("Content-Security-Policy"), p => p == "frame-ancestors http://localhost");

        // An absolute redirect to the target stays on the session origin.
        using var moved = await OriginAsync(origin, launch.Host, "/moved", cookie);
        Assert.Equal($"http://{launch.Host}/", moved.Headers.Location!.ToString());

        // Without the cookie, nothing is proxied.
        using var stranger = await OriginAsync(origin, launch.Host, "/", cookie: null);
        Assert.Equal(HttpStatusCode.Forbidden, stranger.StatusCode);

        // Closing it ends it.
        using var closed = await admin.SendWithTokenAsync(HttpMethod.Delete, $"/api/p/web/hosts/{Id(host)}/sessions/{session.GetProperty("id").GetString()}");
        Assert.Equal(HttpStatusCode.NoContent, closed.StatusCode);
        using var gone = await OriginAsync(origin, launch.Host, "/", cookie);
        Assert.Equal(HttpStatusCode.Gone, gone.StatusCode);

        using var scope = server.Services.CreateScope();
        var events = await scope.ServiceProvider.GetRequiredService<CoreDbContext>().AuditEvents.AsNoTracking().Where(e => e.Action.StartsWith("session.") || e.Action == "trust.add").Select(e => e.Action).ToListAsync(Token);
        Assert.Equal(["trust.add", "session.open", "session.close"], events);
    }

    [Fact]
    public async Task A_page_ends_when_access_to_its_host_ends()
    {
        await using var target = await TargetServer.StartAsync();
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, target.Port);
        // Core trusts only a certificate it presented: the administrator's first attempt presents it.
        (await OpenAsync(admin, host)).Dispose();
        (await admin.SendWithTokenAsync(HttpMethod.Post, $"/api/hosts/{Id(host)}/services/{ServiceId(host)}/trusted-keys", new { algorithm = "x509", fingerprint = target.Fingerprint })).EnsureSuccessStatusCode();
        var bobId = await server.CreateUserAsync("bob", BobPassword);
        (await admin.SendWithTokenAsync(HttpMethod.Put, $"/api/users/{bobId}/grants", new[] { new { hostId = Id(host), level = "use" } })).EnsureSuccessStatusCode();
        var bob = server.Client();
        (await bob.LoginAsync("bob", BobPassword)).EnsureSuccessStatusCode();

        // The certificate was trusted for the host, so bob's page opens without asking.
        using var opened = await OpenAsync(bob, host);
        Assert.True(opened.IsSuccessStatusCode, await opened.Content.ReadAsStringAsync(Token));
        var launch = new Uri((await opened.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("url").GetString()!);
        var origin = server.Client(handleCookies: false);
        using var launched = await OriginAsync(origin, launch.Host, launch.PathAndQuery, cookie: null);
        var cookie = launched.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("__nkw_session=", StringComparison.Ordinal)).Split(';')[0];
        using var page = await OriginAsync(origin, launch.Host, "/", cookie);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        (await admin.SendWithTokenAsync(HttpMethod.Put, $"/api/users/{bobId}/grants", Array.Empty<object>())).EnsureSuccessStatusCode();
        using var after = await OriginAsync(origin, launch.Host, "/", cookie);
        Assert.Equal(HttpStatusCode.Gone, after.StatusCode);

        using var scope = server.Services.CreateScope();
        var closes = await scope.ServiceProvider.GetRequiredService<CoreDbContext>().AuditEvents.AsNoTracking().Where(e => e.Action == AuditActions.SessionClose).Select(e => e.Detail).ToListAsync(Token);
        Assert.Contains(closes, d => d.Contains(": access_revoked,", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Through_a_jump_host_a_page_needs_its_key_trusted_first_and_then_tunnels()
    {
        await using var server = await ServerAsync();
        server.Install(TestPlugins.Package(server.DevelopmentKey));
        using var admin = await server.SignedInClientAsync();
        using var createdJump = await admin.SendWithTokenAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = "jump",
            ipAddress = SshServerFixture.Address,
            services = new[] { new { type = "hello", port = ssh.Port, settings = new { greeting = "a" } } },
            credentials = new[] { new { username = SshServerFixture.UserName, password = SshServerFixture.Password } },
        });
        var jump = await createdJump.Content.ReadFromJsonAsync<JsonElement>(Token);
        using var createdHost = await admin.SendWithTokenAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = "behind",
            ipAddress = "127.0.0.1",
            services = new[] { new { type = "https", port = 9, settings = new { } } },
            jump = new { hostId = Id(jump), serviceId = ServiceId(jump), credentialId = jump.GetProperty("credentials")[0].GetProperty("id").GetGuid() },
        });
        var host = await createdHost.Content.ReadFromJsonAsync<JsonElement>(Token);

        // A request can't stop to ask about the jump host's key.
        using var untrusted = await OpenAsync(admin, host);
        var refusal = await untrusted.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal((HttpStatusCode.BadGateway, "jump_host_unavailable"), (untrusted.StatusCode, refusal.GetProperty("code").GetString()));
        Assert.Contains("isn't trusted yet", refusal.GetProperty("detail").GetString(), StringComparison.Ordinal);

        // An SSH connection to the jump host presents its key; once it's trusted, the tunnel opens.
        using var probe = await admin.SendWithTokenAsync(HttpMethod.Post, $"/api/p/hello/hosts/{Id(jump)}/ssh", new { serviceId = ServiceId(jump), credentialId = jump.GetProperty("credentials")[0].GetProperty("id").GetGuid() });
        var key = (await probe.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("presentedKey");
        (await admin.SendWithTokenAsync(HttpMethod.Post, $"/api/hosts/{Id(jump)}/services/{ServiceId(jump)}/trusted-keys", new { algorithm = key.GetProperty("algorithm").GetString(), fingerprint = key.GetProperty("fingerprint").GetString() })).EnsureSuccessStatusCode();

        // Nothing listens behind it, so the page itself fails: but only after the tunnel was opened, and audited.
        using var tunnelled = await OpenAsync(admin, host);
        var failure = await tunnelled.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal(HttpStatusCode.BadGateway, tunnelled.StatusCode);
        Assert.Contains(failure.GetProperty("code").GetString(), new[] { "unreachable", "timeout" });
        using var scope = server.Services.CreateScope();
        Assert.Contains(
            await scope.ServiceProvider.GetRequiredService<CoreDbContext>().AuditEvents.AsNoTracking().Where(e => e.Action == AuditActions.CredentialUse).Select(e => e.Detail).ToListAsync(Token),
            d => d.StartsWith("tunnel to 127.0.0.1:9, plugin web, jump host ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_a_web_domain_pages_say_so_and_only_named_origins_reach_the_plugin()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        server.Install(TestPlugins.WebPackage(server.DevelopmentKey));
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, 443);

        using var refused = await OpenAsync(admin, host);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal("web_domain_missing", (await refused.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("code").GetString());

        var router = new NKeeper.Core.Sessions.SessionOriginRouter(WebDomain, [new NKeeper.Abstractions.SessionOrigin("nkw", typeof(object))]);
        Assert.NotNull(router.Match(new HostString("nkw-abc123.web.test")));
        Assert.NotNull(router.Match(new HostString("NKW-ABC123.WEB.TEST:443")));
        Assert.Null(router.Match(new HostString("web.test")));
        Assert.Null(router.Match(new HostString("nkw-abc.def.web.test")));
        Assert.Null(router.Match(new HostString("other-abc.web.test")));
        Assert.Null(router.Match(new HostString("nkw-.web.test")));
        Assert.Null(router.Match(new HostString("nkw-abc.web.test.evil.example")));
        Assert.Throws<InvalidOperationException>(() => new NKeeper.Core.Sessions.SessionOriginRouter("not a domain", []));
    }

    private static Task<HttpResponseMessage> OpenAsync(HttpClient client, JsonElement host) =>
        client.SendWithTokenAsync(HttpMethod.Post, $"/api/p/web/hosts/{Id(host)}/sessions", new { serviceId = ServiceId(host) });

    /// <summary>A request on a session origin: the test server answers any host, so the Host header picks it.</summary>
    private static async Task<HttpResponseMessage> OriginAsync(HttpClient client, string originHost, string pathAndQuery, string? cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(pathAndQuery, UriKind.Relative));
        request.Headers.Host = originHost;
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        return await client.SendAsync(request, Token);
    }

    private static async Task<JsonElement> CreateHostAsync(HttpClient admin, int port)
    {
        using var response = await admin.SendWithTokenAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = "web",
            ipAddress = "127.0.0.1",
            services = new[] { new { type = "https", port, settings = new { } } },
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Token));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    private static Guid Id(JsonElement host) => host.GetProperty("id").GetGuid();

    private static Guid ServiceId(JsonElement host) => host.GetProperty("services")[0].GetProperty("id").GetGuid();

    private async Task<ServerScenario> ServerAsync()
    {
        var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        server.Settings["Origins:BaseDomain"] = WebDomain;
        server.Install(TestPlugins.WebPackage(server.DevelopmentKey));
        return server;
    }

    /// <summary>A real HTTPS server on loopback, with a self-signed certificate: the site behind the proxy.</summary>
    private sealed class TargetServer : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private TargetServer(WebApplication app, int port, string fingerprint)
        {
            _app = app;
            Port = port;
            Fingerprint = fingerprint;
        }

        public int Port { get; }

        public string Fingerprint { get; }

        public static async Task<TargetServer> StartAsync()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=nkeeper-web-target", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var sans = new SubjectAlternativeNameBuilder();
            sans.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(sans.Build());
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            var certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);

            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
            var app = builder.Build();
            var port = 0;
            app.MapGet("/", (HttpContext context) =>
            {
                context.Response.Cookies.Append("target", "1");
                return Results.Text("hello from the target");
            });
            app.MapGet("/moved", (HttpContext context) => Results.Redirect($"https://127.0.0.1:{port}/"));
            await app.StartAsync();
            port = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
            return new TargetServer(app, port, NKeeper.Core.Connectivity.HttpConnector.Fingerprint(certificate));
        }

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }
}
