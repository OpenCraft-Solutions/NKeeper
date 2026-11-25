using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Core.Audit;
using NKeeper.Core.Credentials;
using NKeeper.Core.Persistence;
using NKeeper.Testing;

namespace NKeeper.Server.Tests;

/// <summary>docs/architecture.md §7.45: an instance exported from one server and imported into a fresh one.</summary>
public sealed class TransferTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Passphrase = "correct horse battery staple";
    private const string BobPassword = "Bob-Passw0rd-123";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_export_imported_into_a_fresh_server_brings_the_users_hosts_and_secrets_encrypted_again()
    {
        // The old server: a user with a grant, a host with a credential, and plugin data on it.
        await using var old = new ServerScenario(await postgres.CreateDatabaseAsync());
        old.Install(TestPlugins.Package(old.DevelopmentKey));
        using var oldAdmin = await old.SignedInClientAsync();
        var bobId = await old.CreateUserAsync("bob", BobPassword);
        var host = await CreateHostAsync(oldAdmin);
        var hostId = host.GetProperty("id").GetGuid();
        var credentialId = host.GetProperty("credentials")[0].GetProperty("id").GetGuid();
        (await oldAdmin.SendWithTokenAsync(HttpMethod.Put, $"/api/users/{bobId}/grants", new[] { new { hostId, level = "use" } })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await oldAdmin.SendWithTokenAsync(HttpMethod.Put, $"/api/p/hello/hosts/{hostId}/data", new { linked = 7 })).StatusCode);

        // Export needs the administrator's own password, and a long enough passphrase, twice.
        Assert.Equal(HttpStatusCode.Forbidden, (await oldAdmin.SendWithTokenAsync(HttpMethod.Post, "/api/transfer/export", new { password = "wrong", passphrase = Passphrase, passphraseConfirmation = Passphrase })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await oldAdmin.SendWithTokenAsync(HttpMethod.Post, "/api/transfer/export", new { password = ServerScenario.AdminPassword, passphrase = "short", passphraseConfirmation = "short" })).StatusCode);
        using var exported = await oldAdmin.SendWithTokenAsync(HttpMethod.Post, "/api/transfer/export", new { password = ServerScenario.AdminPassword, passphrase = Passphrase, passphraseConfirmation = Passphrase });
        Assert.True(exported.IsSuccessStatusCode, await exported.Content.ReadAsStringAsync(Token));
        var file = await exported.Content.ReadAsByteArrayAsync(Token);
        Assert.Equal("NKEEPERB"u8.ToArray(), file[..8]);
        // The secret isn't in the file as it was typed.
        Assert.DoesNotContain("s3cret-pass", System.Text.Encoding.UTF8.GetString(file), StringComparison.Ordinal);

        // The new server: fresh, its own key ring, and the hello plugin installed.
        await using var fresh = new ServerScenario(await postgres.CreateDatabaseAsync());
        fresh.Install(TestPlugins.Package(fresh.DevelopmentKey));
        using var newAdmin = await fresh.SignedInClientAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(newAdmin, "/api/transfer/import/preview", file, "not the passphrase at all")).StatusCode);
        using (var previewed = await SendAsync(newAdmin, "/api/transfer/import/preview", file, Passphrase))
        {
            var preview = await previewed.Content.ReadFromJsonAsync<JsonElement>(Token);
            Assert.True(previewed.IsSuccessStatusCode, preview.GetRawText());
            Assert.Equal((2, 1, 1), (preview.GetProperty("users").GetInt32(), preview.GetProperty("hosts").GetInt32(), preview.GetProperty("credentials").GetInt32()));
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(newAdmin, "/api/transfer/import", file, Passphrase)).StatusCode);
        using (var imported = await SendAsync(newAdmin, "/api/transfer/import", file, Passphrase, confirm: true))
        {
            Assert.True(imported.IsSuccessStatusCode, await imported.Content.ReadAsStringAsync(Token));
        }

        // The importer's session ended with their account; everyone signs in as before.
        Assert.Equal(HttpStatusCode.Unauthorized, (await newAdmin.GetAsync("/api/auth/me", Token)).StatusCode);
        using var bob = fresh.Client();
        (await bob.LoginAsync("bob", BobPassword)).EnsureSuccessStatusCode();
        var hosts = await bob.GetFromJsonAsync<JsonElement>("/api/hosts", Token);
        Assert.Equal(("web01", hostId), (hosts[0].GetProperty("name").GetString(), hosts[0].GetProperty("id").GetGuid()));

        using var scope = fresh.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreDbContext>();
        var credential = await db.HostCredentials.AsNoTracking().SingleAsync(c => c.Id == credentialId, Token);
        using (var secret = scope.ServiceProvider.GetRequiredService<CredentialProtector>().Unprotect(credentialId, credential.ProtectedPassword!))
        {
            Assert.Equal("s3cret-pass", secret.Reveal());
        }

        Assert.Equal("""{"linked":7}""", (await db.HostData.AsNoTracking().SingleAsync(Token)).Data.Replace(" ", "", StringComparison.Ordinal));
        Assert.Single(await db.AuditEvents.AsNoTracking().Where(e => e.Action == AuditActions.TransferImport).ToListAsync(Token));
    }

    [Fact]
    public async Task An_import_needs_a_fresh_instance_and_administrators()
    {
        await using var old = new ServerScenario(await postgres.CreateDatabaseAsync());
        using var oldAdmin = await old.SignedInClientAsync();
        using var exported = await oldAdmin.SendWithTokenAsync(HttpMethod.Post, "/api/transfer/export", new { password = ServerScenario.AdminPassword, passphrase = Passphrase, passphraseConfirmation = Passphrase });
        var file = await exported.Content.ReadAsByteArrayAsync(Token);

        await using var used = new ServerScenario(await postgres.CreateDatabaseAsync());
        used.Install(TestPlugins.Package(used.DevelopmentKey));
        using var admin = await used.SignedInClientAsync();
        await CreateHostAsync(admin);
        using (var refused = await SendAsync(admin, "/api/transfer/import", file, Passphrase, confirm: true))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        }

        await used.CreateUserAsync("bob", BobPassword);
        using var bob = used.Client();
        (await bob.LoginAsync("bob", BobPassword)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.SendWithTokenAsync(HttpMethod.Post, "/api/transfer/export", new { password = BobPassword, passphrase = Passphrase, passphraseConfirmation = Passphrase })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(bob, "/api/transfer/import/preview", file, Passphrase)).StatusCode);
    }

    [Fact]
    public async Task A_bundle_that_was_altered_cut_off_or_written_by_1x_is_refused()
    {
        var header = new NKeeper.Core.Transfer.BundleHeader(0, "2", "m", DateTimeOffset.UtcNow, "admin", string.Empty, 0, string.Empty, string.Empty, 0);
        var plaintext = new byte[200_000];
        Random.Shared.NextBytes(plaintext);
        using var written = new MemoryStream();
        await NKeeper.Core.Transfer.BundleFormat.WriteAsync(written, header, Passphrase, plaintext, Token, iterations: 100_000);
        var bundle = written.ToArray();

        Assert.Equal(plaintext, NKeeper.Core.Transfer.BundleFormat.Read(bundle, Passphrase).Plaintext);
        var altered = (byte[])bundle.Clone();
        altered[^100] ^= 1;
        Assert.Throws<NKeeper.Core.Transfer.BundleException>(() => NKeeper.Core.Transfer.BundleFormat.Read(altered, Passphrase));
        // Dropping the last chunk: the one before it isn't marked last, so it's refused rather than read short.
        var lastChunk = plaintext.Length % (64 * 1024) + 4 + 16;
        Assert.Throws<NKeeper.Core.Transfer.BundleException>(() => NKeeper.Core.Transfer.BundleFormat.Read(bundle.AsSpan(0, bundle.Length - lastChunk), Passphrase));

        // 1.x's files are format 1.
        var text = System.Text.Encoding.UTF8.GetString(bundle);
        var oneX = System.Text.Encoding.UTF8.GetBytes(text.Replace("\"format\":2", "\"format\":1", StringComparison.Ordinal));
        var refused = Assert.Throws<NKeeper.Core.Transfer.BundleException>(() => NKeeper.Core.Transfer.BundleFormat.Read(oneX, Passphrase));
        Assert.Contains("format 1", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_export_can_lock_hosts_and_users_against_changes_until_unlocked_while_connecting_still_works()
    {
        await using var server = new ServerScenario(await postgres.CreateDatabaseAsync());
        server.Install(TestPlugins.Package(server.DevelopmentKey));
        using var admin = await server.SignedInClientAsync();
        var host = await CreateHostAsync(admin);
        var id = host.GetProperty("id").GetGuid();
        var serviceId = host.GetProperty("services")[0].GetProperty("id").GetGuid();

        (await admin.SendWithTokenAsync(HttpMethod.Post, "/api/transfer/export", new { password = ServerScenario.AdminPassword, passphrase = Passphrase, passphraseConfirmation = Passphrase, lockAfterExport = true })).EnsureSuccessStatusCode();
        var status = await admin.GetFromJsonAsync<JsonElement>("/api/transfer/status", Token);
        Assert.True(status.GetProperty("locked").GetBoolean());
        Assert.Equal("admin", status.GetProperty("lockedBy").GetString());

        // Hosts and users can't change; reading and connecting can.
        using var edit = await admin.SendWithTokenAsync(HttpMethod.Put, $"/api/hosts/{id}", new { name = "renamed", ipAddress = "127.0.0.1", version = host.GetProperty("version").GetUInt32() });
        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
        Assert.Equal("instance_locked", (await edit.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.SendWithTokenAsync(HttpMethod.Delete, $"/api/hosts/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.SendWithTokenAsync(HttpMethod.Post, "/api/users", new { userName = "carol", password = BobPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/hosts/{id}", Token)).StatusCode);
        Assert.NotEqual(HttpStatusCode.Conflict, (await admin.SendWithTokenAsync(HttpMethod.Post, $"/api/hosts/{id}/services/{serviceId}/credential-prompt", new { username = "root", password = "typed" })).StatusCode);

        (await admin.SendWithTokenAsync(HttpMethod.Post, "/api/transfer/unlock")).EnsureSuccessStatusCode();
        Assert.False((await admin.GetFromJsonAsync<JsonElement>("/api/transfer/status", Token)).GetProperty("locked").GetBoolean());
        (await admin.SendWithTokenAsync(HttpMethod.Put, $"/api/hosts/{id}", new { name = "renamed", ipAddress = "127.0.0.1", version = host.GetProperty("version").GetUInt32() })).EnsureSuccessStatusCode();

        await using var db = new CoreDbContext(CoreDbContext.CreateOptions(server.ConnectionString));
        Assert.Equal(
            [AuditActions.TransferExport, AuditActions.TransferLock, AuditActions.TransferUnlock],
            await db.AuditEvents.Where(e => e.Action.StartsWith("transfer.")).OrderBy(e => e.Id).Select(e => e.Action).ToListAsync(Token));
    }

    private static async Task<JsonElement> CreateHostAsync(HttpClient admin)
    {
        using var response = await admin.SendWithTokenAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = "web01",
            ipAddress = "127.0.0.1",
            services = new[] { new { type = "hello", port = 22, settings = new { greeting = "a" } } },
            credentials = new[] { new { username = "root", password = "s3cret-pass" } },
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Token));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string path, byte[] file, string passphrase, bool confirm = false)
    {
        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "file", "export.nkeeper");
        form.Add(new StringContent(passphrase), "passphrase");
        if (confirm)
        {
            form.Add(new StringContent("true"), "confirm");
        }

        return await client.SendWithTokenAsync(HttpMethod.Post, path, form);
    }
}
