using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Core.Persistence;
using NKeeper.Identity;
using NKeeper.Testing;

namespace NKeeper.Server.Tests;

/// <summary>docs/architecture.md §6.3: Data Protection keys encrypted with a certificate kept outside the database. Ported from 1.x.</summary>
public sealed class KeyRingTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IDisposable
{
    private const string Purpose = "NKeeper.Tests.KeyRing";
    private readonly List<string> _files = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task With_a_certificate_new_keys_are_stored_encrypted()
    {
        await using var server = WithCertificate(await postgres.CreateDatabaseAsync(), CreateCertificateFile());
        server.Client().Dispose();

        var secret = Protect(server, "root-secret");
        var keys = await StoredKeysAsync(server.ConnectionString);

        Assert.NotEmpty(keys);
        Assert.All(keys, AssertEncrypted);
        Assert.Equal("root-secret", Unprotect(server, secret));
    }

    [Fact]
    public async Task A_certificate_added_later_encrypts_the_stored_keys_and_secrets_stay_readable()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        byte[] secret;
        await using (var before = new ServerScenario(connectionString))
        {
            before.Client().Dispose();
            secret = Protect(before, "root-secret");
            Assert.All(await StoredKeysAsync(connectionString), key => Assert.Contains("<masterKey", key, StringComparison.Ordinal));
        }

        await using var after = WithCertificate(connectionString, CreateCertificateFile());
        after.Client().Dispose();

        Assert.All(await StoredKeysAsync(connectionString), AssertEncrypted);
        Assert.Equal("root-secret", Unprotect(after, secret));
    }

    [Fact]
    public async Task Keys_encrypted_with_a_certificate_stop_startup_without_it_or_with_another()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using (var first = WithCertificate(connectionString, CreateCertificateFile()))
        {
            first.Client().Dispose();
            Protect(first, "root-secret");
        }

        await using var withoutCertificate = new ServerScenario(connectionString);
        await using var withAnotherCertificate = WithCertificate(connectionString, CreateCertificateFile());

        Assert.Contains(KeyRingCertificate.PathSetting, StartupError(withoutCertificate), StringComparison.Ordinal);
        Assert.Contains("different certificate", StartupError(withAnotherCertificate), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_certificate_file_stops_startup()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"nkeeper-missing-{Guid.NewGuid():N}.pem");
        await using var server = WithCertificate(await postgres.CreateDatabaseAsync(), missing);

        Assert.Contains(KeyRingCertificate.PathSetting, StartupError(server), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        foreach (var file in _files)
        {
            File.Delete(file);
        }
    }

    private static ServerScenario WithCertificate(string connectionString, string certificatePath)
    {
        var server = new ServerScenario(connectionString);
        server.Settings[KeyRingCertificate.PathSetting] = certificatePath;
        return server;
    }

    private string CreateCertificateFile()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=NKeeper test key ring", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var path = Path.Combine(Path.GetTempPath(), $"nkeeper-keyring-{Guid.NewGuid():N}.pem");
        File.WriteAllText(path, certificate.ExportCertificatePem() + "\n" + rsa.ExportPkcs8PrivateKeyPem());
        _files.Add(path);
        return path;
    }

    private static void AssertEncrypted(string key)
    {
        Assert.Contains("<encryptedSecret", key, StringComparison.Ordinal);
        Assert.DoesNotContain("<masterKey", key, StringComparison.Ordinal);
    }

    private static string StartupError(ServerScenario server)
    {
        var error = Assert.ThrowsAny<Exception>(() => server.Client());
        var messages = new List<string>();
        for (Exception? e = error; e is not null; e = e.InnerException)
        {
            messages.Add(e.Message);
        }

        return string.Join(" | ", messages);
    }

    private static byte[] Protect(ServerScenario server, string secret) =>
        server.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Protect(System.Text.Encoding.UTF8.GetBytes(secret));

    private static string Unprotect(ServerScenario server, byte[] secret) =>
        System.Text.Encoding.UTF8.GetString(server.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Unprotect(secret));

    private static async Task<List<string>> StoredKeysAsync(string connectionString)
    {
        await using var db = new CoreDbContext(CoreDbContext.CreateOptions(connectionString));
        return await db.DataProtectionKeys.AsNoTracking().Select(k => k.Xml!).ToListAsync(Token);
    }
}
