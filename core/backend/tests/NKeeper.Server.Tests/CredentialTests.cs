using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NKeeper.Core.Audit;
using NKeeper.Core.Credentials;
using NKeeper.Core.Persistence;
using NKeeper.Testing;

namespace NKeeper.Server.Tests;

/// <summary>
/// docs/architecture.md §7.5 and §7.6, plugin-security.md §12.5: saved credentials, the broker, handles and the
/// core prompt. Secrets are distinctive strings, so their absence from responses, logs and audit can be checked.
/// </summary>
public sealed class CredentialTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Password = "Pa55-secret-PASSWORD";
    private const string NewPassword = "N3w-secret-PASSWORD";
    private const string TypedPassword = "Typ3d-secret-PASSWORD";
    private const string PrivateKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nc2VjcmV0LWtleS1tYXRlcmlhbA==\n-----END OPENSSH PRIVATE KEY-----";
    private const string BobPassword = "Bob-Passw0rd-123";

    private static readonly string[] Secrets = [Password, NewPassword, TypedPassword, "c2VjcmV0LWtleS1tYXRlcmlhbA"];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- Saved credentials (§7.5) ----

    [Fact]
    public async Task Credentials_are_saved_with_their_host_and_their_secrets_never_come_back()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();

        using var created = await SendRawAsync(admin, HttpMethod.Post, "/api/hosts", HostBody($$"""
            [{ "label": " Root ", "username": "root", "password": "{{Password}}", "privateKey": {{JsonSerializer.Serialize(PrivateKey)}}, "notes": "rotate yearly", "serviceIndexes": [1] },
             { "username": "admin", "domain": "CORP" }]
            """));

        var body = await created.Content.ReadAsStringAsync(Token);
        Assert.True(created.StatusCode == HttpStatusCode.Created, body);
        AssertNoSecrets(body);
        var host = JsonDocument.Parse(body).RootElement;
        var credentials = host.GetProperty("credentials").EnumerateArray().ToList();
        Assert.Equal(("Root", "root", true, true, "rotate yearly"), (
            credentials[0].GetProperty("label").GetString(),
            credentials[0].GetProperty("username").GetString(),
            credentials[0].GetProperty("hasPassword").GetBoolean(),
            credentials[0].GetProperty("hasPrivateKey").GetBoolean(),
            credentials[0].GetProperty("notes").GetString()));
        Assert.Equal([1], credentials[0].GetProperty("serviceIndexes").EnumerateArray().Select(i => i.GetInt32()));
        Assert.Equal(("CORP", false, JsonValueKind.Null), (credentials[1].GetProperty("domain").GetString(), credentials[1].GetProperty("hasPassword").GetBoolean(), credentials[1].GetProperty("serviceIndexes").ValueKind));

        var stored = await StoredCredentialAsync(server, credentials[0].GetProperty("id").GetGuid());
        Assert.False(Contains(stored.ProtectedPassword!, Password));
        Assert.False(Contains(stored.ProtectedPrivateKey!, "c2VjcmV0LWtleS1tYXRlcmlhbA"));
    }

    [Fact]
    public async Task An_edit_keeps_replaces_or_clears_each_secret_and_deletes_credentials_left_out()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, $$"""
            [{ "username": "root", "password": "{{Password}}", "privateKey": {{JsonSerializer.Serialize(PrivateKey)}} }, { "username": "gone" }]
            """);
        var id = host.GetProperty("credentials")[0].GetProperty("id").GetGuid();
        var before = await StoredCredentialAsync(server, id);

        var kept = await UpdateHostAsync(admin, host, $$"""[{ "id": "{{id}}", "username": "root" }]""");
        var keptCredential = Assert.Single(kept.GetProperty("credentials").EnumerateArray());
        Assert.Equal((id, true, true), (keptCredential.GetProperty("id").GetGuid(), keptCredential.GetProperty("hasPassword").GetBoolean(), keptCredential.GetProperty("hasPrivateKey").GetBoolean()));
        Assert.Equal(before.ProtectedPassword, (await StoredCredentialAsync(server, id)).ProtectedPassword);

        var replaced = await UpdateHostAsync(admin, kept, $$"""[{ "id": "{{id}}", "username": "root", "password": "{{NewPassword}}", "clearPrivateKey": true }]""");
        var replacedCredential = replaced.GetProperty("credentials")[0];
        Assert.Equal((true, false), (replacedCredential.GetProperty("hasPassword").GetBoolean(), replacedCredential.GetProperty("hasPrivateKey").GetBoolean()));
        Assert.NotEqual(before.ProtectedPassword, (await StoredCredentialAsync(server, id)).ProtectedPassword);

        var cleared = await UpdateHostAsync(admin, replaced, $$"""[{ "id": "{{id}}", "username": "root", "clearPassword": true }]""");
        Assert.False(cleared.GetProperty("credentials")[0].GetProperty("hasPassword").GetBoolean());
    }

    [Theory]
    [InlineData("""[null]""", "credentials[0]")]
    [InlineData("""[{ "id": "00000000-0000-0000-0000-000000000001" }]""", "credentials[0].id")]
    [InlineData("""[{ "privateKey": "not a key" }]""", "credentials[0].privateKey")]
    [InlineData("""[{ "password": "x", "clearPassword": true }]""", "credentials[0]")]
    [InlineData("""[{ "serviceIndexes": [2] }]""", "credentials[0].serviceIndexes")]
    [InlineData("""[{ "serviceIndexes": [-1] }]""", "credentials[0].serviceIndexes")]
    [InlineData("""[{ "serviceIndexes": [0, 0] }]""", "credentials[0].serviceIndexes")]
    public async Task An_invalid_credential_reports_the_field(string credentials, string field)
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();

        using var response = await SendRawAsync(admin, HttpMethod.Post, "/api/hosts", HostBody(credentials));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, (await response.ProblemAsync()).GetProperty("errors").EnumerateObject().Select(p => p.Name));
    }

    [Theory]
    [InlineData("label", 101)]
    [InlineData("username", 257)]
    [InlineData("domain", 257)]
    [InlineData("password", 1025)]
    [InlineData("notes", 2001)]
    public async Task A_credential_field_over_its_limit_is_refused(string name, int length)
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();

        using var response = await SendRawAsync(admin, HttpMethod.Post, "/api/hosts", HostBody($$"""[{ "{{name}}": "{{new string('a', length)}}" }]"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"credentials[0].{name}", (await response.ProblemAsync()).GetProperty("errors").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task A_scope_left_without_its_services_is_empty_not_all()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, """[{ "username": "root", "serviceIndexes": [1] }]""");
        var removed = ServiceIds(host).Second;

        // The credential's only service goes, without the credential being edited.
        using (var scope = server.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<CoreDbContext>().HostServices.Where(s => s.Id == removed).ExecuteDeleteAsync(Token);
        }

        var read = await admin.GetFromJsonAsync<JsonElement>($"/api/hosts/{Id(host)}", Token);
        Assert.Empty(read.GetProperty("credentials")[0].GetProperty("serviceIndexes").EnumerateArray());
    }

    [Fact]
    public async Task A_ciphertext_only_decrypts_for_its_own_credential()
    {
        await using var server = await ServerAsync();
        using (server.Client())
        {
        }

        var protector = server.Services.GetRequiredService<CredentialProtector>();
        var ciphertext = protector.Protect(Guid.NewGuid(), Password);

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(Guid.NewGuid(), ciphertext));
    }

    [Fact]
    public void A_secret_prints_as_stars_refuses_serialization_and_clears_when_disposed()
    {
        var secret = new Secret(Password);

        Assert.Equal("***", secret.ToString());
        Assert.Equal("***", $"{secret}");
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(new { secret }));
        Assert.Equal(Password, secret.Reveal());
        secret.Dispose();
        Assert.Throws<ObjectDisposedException>(secret.Reveal);
    }

    // ---- The broker and handles (§7.6) ----

    [Fact]
    public async Task A_plugin_resolves_a_saved_credential_for_a_service_it_is_used_with()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, $$"""
            [{ "username": "root", "password": "{{Password}}", "serviceIndexes": [1] }, { "username": "any", "domain": "CORP", "privateKey": {{JsonSerializer.Serialize(PrivateKey)}} }]
            """);
        var (first, second) = ServiceIds(host);
        var scoped = CredentialId(host, 0);
        var anyService = CredentialId(host, 1);

        Assert.Equal("Saved root  True False", await ProbeAsync(admin, host, new { serviceId = second, credentialId = scoped }));
        Assert.Equal("Saved any CORP False True", await ProbeAsync(admin, host, new { serviceId = first, credentialId = anyService }));
        Assert.Equal("denied NotForService", await ProbeAsync(admin, host, new { serviceId = first, credentialId = scoped }));
        Assert.Equal("denied NotFound", await ProbeAsync(admin, host, new { serviceId = first, credentialId = Guid.NewGuid() }));
        Assert.Equal("denied NotForService", await ProbeAsync(admin, host, new { serviceId = Guid.NewGuid(), credentialId = anyService }));
    }

    [Fact]
    public async Task A_handle_is_refused_outside_its_bindings_and_every_refusal_is_audited()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, $$"""[{ "username": "root", "password": "{{Password}}" }]""");
        var (first, second) = ServiceIds(host);
        var credential = CredentialId(host, 0);

        // Another service than it was resolved for, in the same request.
        Assert.Equal("denied HandleInvalid", await ProbeAsync(admin, host, new { serviceId = first, credentialId = credential, describeServiceId = second }));

        // A host context the plugin made up.
        Assert.Equal("denied NotAllowed", await ProbeAsync(admin, host, new { serviceId = first, credentialId = credential, forged = true }));

        // A later request: the handle ended with the request that obtained it.
        Assert.Equal("Saved root  True False", await ProbeAsync(admin, host, new { serviceId = first, credentialId = credential }));
        Assert.Equal("denied HandleInvalid", await ProbeAsync(admin, host, new { serviceId = first, stashed = true }));

        Assert.Equal(0, server.Services.GetRequiredService<CredentialHandleStore>().Count);
        var denials = await AuditAsync(server, AuditActions.CredentialDenied);
        Assert.Equal(3, denials.Count);
        Assert.All(denials, d => Assert.Equal(Id(host).ToString(), d.Target));
        Assert.Contains(denials, d => d.Detail.StartsWith("HandleInvalid", StringComparison.Ordinal) && d.Detail.EndsWith("plugin hello", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_handle_cant_be_serialized()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, $$"""[{ "username": "root", "password": "{{Password}}" }]""");

        Assert.Equal("unserializable CredentialHandle(***)", await ProbeAsync(admin, host, new { serviceId = ServiceIds(host).First, credentialId = CredentialId(host, 0), serialize = true }));
    }

    [Fact]
    public async Task A_use_only_user_sees_credentials_without_secrets_and_can_resolve_them()
    {
        await using var server = await ServerAsync();
        Guid hostId;
        JsonElement host;
        using (var admin = await server.SignedInClientAsync())
        {
            host = await CreateHostAsync(admin, $$"""[{ "username": "root", "password": "{{Password}}" }]""");
            hostId = Id(host);
            var bobId = await server.CreateUserAsync("bob", BobPassword);
            (await admin.SendWithTokenAsync(HttpMethod.Put, $"/api/users/{bobId}/grants", new[] { new { hostId, level = "use" } })).EnsureSuccessStatusCode();
        }

        using var bob = await SignInAsync(server, "bob");
        var seen = await bob.GetStringAsync(new Uri($"/api/hosts/{hostId}", UriKind.Relative), Token);

        AssertNoSecrets(seen);
        Assert.True(JsonDocument.Parse(seen).RootElement.GetProperty("credentials")[0].GetProperty("hasPassword").GetBoolean());
        Assert.Equal("Saved root  True False", await ProbeAsync(bob, host, new { serviceId = ServiceIds(host).First, credentialId = CredentialId(host, 0) }));
    }

    // ---- The core prompt (§7.6) ----

    [Fact]
    public async Task A_typed_credential_reaches_the_plugin_only_as_a_handle_and_its_reference_works_once()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, "[]");
        var (first, second) = ServiceIds(host);

        var reference = await PromptAsync(admin, host, first);
        Assert.Equal("Prompt alice CORP True False", await ProbeAsync(admin, host, new { serviceId = first, promptReference = reference }));
        Assert.Equal("denied PromptInvalid", await ProbeAsync(admin, host, new { serviceId = first, promptReference = reference }));

        // A reference for one service doesn't work for another, and is used up by trying.
        var other = await PromptAsync(admin, host, first);
        Assert.Equal("denied PromptInvalid", await ProbeAsync(admin, host, new { serviceId = second, promptReference = other }));
        Assert.Equal("denied PromptInvalid", await ProbeAsync(admin, host, new { serviceId = first, promptReference = other }));
        Assert.Equal("denied PromptInvalid", await ProbeAsync(admin, host, new { serviceId = first, promptReference = "made-up" }));
    }

    [Fact]
    public async Task A_prompt_reference_is_bound_to_its_auth_session()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, "[]");
        var first = ServiceIds(host).First;
        var reference = await PromptAsync(admin, host, first);

        // The same user, signed in again elsewhere: another session.
        using var elsewhere = await server.SignedInClientAsync();

        Assert.Equal("denied PromptInvalid", await ProbeAsync(elsewhere, host, new { serviceId = first, promptReference = reference }));
    }

    [Fact]
    public async Task A_prompt_reference_expires_after_two_minutes()
    {
        var time = new AdjustableTime();
        await using var server = await ServerAsync(services => services.AddSingleton<TimeProvider>(time));
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, "[]");
        var first = ServiceIds(host).First;

        var reference = await PromptAsync(admin, host, first);
        time.Advance(CredentialPromptStore.Lifetime);

        Assert.Equal("denied PromptInvalid", await ProbeAsync(admin, host, new { serviceId = first, promptReference = reference }));
    }

    [Fact]
    public async Task Signing_out_drops_the_sessions_unused_prompt_references()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, "[]");
        await PromptAsync(admin, host, ServiceIds(host).First);
        var store = server.Services.GetRequiredService<CredentialPromptStore>();
        Assert.Equal(1, store.Count);

        (await admin.LogoutAsync()).EnsureSuccessStatusCode();

        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task A_session_keeps_at_most_ten_prompt_references()
    {
        await using var server = await ServerAsync();
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, "[]");
        var first = ServiceIds(host).First;
        var oldest = await PromptAsync(admin, host, first);
        for (var i = 0; i < CredentialPromptStore.MaxPerSession; i++)
        {
            await PromptAsync(admin, host, first);
        }

        Assert.Equal(CredentialPromptStore.MaxPerSession, server.Services.GetRequiredService<CredentialPromptStore>().Count);
        Assert.Equal("denied PromptInvalid", await ProbeAsync(admin, host, new { serviceId = first, promptReference = oldest }));
    }

    [Fact]
    public async Task The_prompt_hides_hosts_the_user_cant_see_and_services_the_host_doesnt_have()
    {
        await using var server = await ServerAsync();
        JsonElement host;
        using (var admin = await server.SignedInClientAsync())
        {
            host = await CreateHostAsync(admin, "[]");
            using var noService = await admin.SendWithTokenAsync(HttpMethod.Post, $"/api/hosts/{Id(host)}/services/{Guid.NewGuid()}/credential-prompt", new { password = TypedPassword });
            Assert.Equal(HttpStatusCode.NotFound, noService.StatusCode);
        }

        await server.CreateUserAsync("bob", BobPassword);
        using var bob = await SignInAsync(server, "bob");
        using var hidden = await bob.SendWithTokenAsync(HttpMethod.Post, $"/api/hosts/{Id(host)}/services/{ServiceIds(host).First}/credential-prompt", new { password = TypedPassword });

        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
    }

    // ---- No secret leaks (plugin-security.md §12.5) ----

    [Fact]
    public async Task No_secret_appears_in_the_logs_or_the_audit_trail()
    {
        var logs = new ListLoggerProvider();
        await using var server = await ServerAsync(services => services.AddSingleton<ILoggerProvider>(logs));
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin, $$"""[{ "username": "root", "password": "{{Password}}", "privateKey": {{JsonSerializer.Serialize(PrivateKey)}} }]""");
        await UpdateHostAsync(admin, host, $$"""[{ "id": "{{CredentialId(host, 0)}}", "username": "root", "password": "{{NewPassword}}" }]""");
        var first = ServiceIds(host).First;
        await ProbeAsync(admin, host, new { serviceId = first, credentialId = CredentialId(host, 0) });
        await ProbeAsync(admin, host, new { serviceId = first, credentialId = CredentialId(host, 0), describeServiceId = Guid.NewGuid() });
        var reference = await PromptAsync(admin, host, first);
        await ProbeAsync(admin, host, new { serviceId = first, promptReference = reference });

        Assert.NotEmpty(logs.Messages);
        Assert.All(logs.Messages, AssertNoSecrets);
        Assert.All(await AuditAsync(server, null), e => AssertNoSecrets($"{e.Actor} {e.Action} {e.Target} {e.Detail}"));
    }

    // ---- Helpers ----

    private static void AssertNoSecrets(string text)
    {
        foreach (var secret in Secrets)
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }
    }

    private static bool Contains(byte[] haystack, string needle) =>
        haystack.AsSpan().IndexOf(Encoding.UTF8.GetBytes(needle)) >= 0;

    private static Guid Id(JsonElement host) => host.GetProperty("id").GetGuid();

    private static Guid CredentialId(JsonElement host, int index) => host.GetProperty("credentials")[index].GetProperty("id").GetGuid();

    private static (Guid First, Guid Second) ServiceIds(JsonElement host)
    {
        var services = host.GetProperty("services");
        return (services[0].GetProperty("id").GetGuid(), services[1].GetProperty("id").GetGuid());
    }

    private const string TwoServices = """[{ "type": "hello", "settings": { "greeting": "a" } }, { "type": "hello", "settings": { "greeting": "b" } }]""";

    private static string HostBody(string credentials, string services = TwoServices, uint? version = null) =>
        $$"""{ "name": "web01", "ipAddress": "192.0.2.10", "services": {{services}}, "credentials": {{credentials}}{{(version is { } v ? $", \"version\": {v}" : "")}} }""";

    private static async Task<JsonElement> CreateHostAsync(HttpClient admin, string credentials)
    {
        using var response = await SendRawAsync(admin, HttpMethod.Post, "/api/hosts", HostBody(credentials));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Token));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    /// <summary>Sends the host's services back as they are (or <paramref name="services"/>), with new credentials.</summary>
    private static async Task<JsonElement> UpdateHostAsync(HttpClient admin, JsonElement host, string credentials, string? services = null)
    {
        services ??= JsonSerializer.Serialize(host.GetProperty("services").EnumerateArray().Select(s => new
        {
            id = s.GetProperty("id").GetGuid(),
            type = s.GetProperty("type").GetString(),
            settings = s.GetProperty("settings"),
        }));
        using var response = await SendRawAsync(admin, HttpMethod.Put, $"/api/hosts/{Id(host)}", HostBody(credentials, services, host.GetProperty("version").GetUInt32()));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Token));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    private static async Task<string> PromptAsync(HttpClient client, JsonElement host, Guid serviceId)
    {
        using var response = await client.SendWithTokenAsync(
            HttpMethod.Post,
            $"/api/hosts/{Id(host)}/services/{serviceId}/credential-prompt",
            new { username = "alice", domain = "CORP", password = TypedPassword });
        var body = await response.Content.ReadAsStringAsync(Token);
        Assert.True(response.IsSuccessStatusCode, body);
        AssertNoSecrets(body);
        return JsonDocument.Parse(body).RootElement.GetProperty("reference").GetString()!;
    }

    /// <summary>Calls the test plugin's credential endpoint (HelloPlugin) and returns what it answered.</summary>
    private static async Task<string> ProbeAsync(HttpClient client, JsonElement host, object probe)
    {
        using var response = await client.SendWithTokenAsync(HttpMethod.Post, $"/api/p/hello/hosts/{Id(host)}/credential", probe);
        var body = await response.Content.ReadAsStringAsync(Token);
        AssertNoSecrets(body);
        return body;
    }

    private static async Task<HostCredential> StoredCredentialAsync(ServerScenario server, Guid id)
    {
        using var scope = server.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CoreDbContext>().HostCredentials.AsNoTracking().SingleAsync(c => c.Id == id, Token);
    }

    private static async Task<List<AuditEvent>> AuditAsync(ServerScenario server, string? action)
    {
        using var scope = server.Services.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<CoreDbContext>().AuditEvents.AsNoTracking();
        return await (action is null ? events : events.Where(e => e.Action == action)).ToListAsync(Token);
    }

    private static async Task<HttpResponseMessage> SendRawAsync(HttpClient client, HttpMethod method, string path, string body)
    {
        var (header, token) = await client.AntiforgeryAsync();
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(header, token);
        return await client.SendAsync(request, Token);
    }

    private static async Task<HttpClient> SignInAsync(ServerScenario server, string userName)
    {
        var client = server.Client();
        using var response = await client.LoginAsync(userName, BobPassword);
        response.EnsureSuccessStatusCode();
        return client;
    }

    private async Task<ServerScenario> ServerAsync(Action<IServiceCollection>? services = null)
    {
        var server = new ServerScenario(await postgres.CreateDatabaseAsync()) { TestServices = services };
        server.Install(TestPlugins.Package(server.DevelopmentKey));
        return server;
    }

    /// <summary>A clock the test moves forward.</summary>
    private sealed class AdjustableTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>Captures every log message, formatted, with its exception.</summary>
    private sealed class ListLoggerProvider : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages;

        public ILogger CreateLogger(string categoryName) => new ListLogger(_messages);

        public void Dispose()
        {
        }

        private sealed class ListLogger(System.Collections.Concurrent.ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue($"{formatter(state, exception)} {exception}");
        }
    }
}
