using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NKeeper.Core.Persistence;

namespace NKeeper.Identity;

/// <summary>
/// The certificate that encrypts the Data Protection keys before they are stored in the database
/// (<c>DataProtection:CertificatePath</c>, docs/architecture.md §6.3), so a copy of the database alone
/// can't decrypt what those keys protect. Without one, the keys are stored unencrypted.
/// </summary>
public sealed class KeyRingCertificate : IDisposable
{
    public const string PathSetting = "DataProtection:CertificatePath";

    private KeyRingCertificate(X509Certificate2? certificate, string? path)
    {
        Certificate = certificate;
        Path = path;
    }

    public X509Certificate2? Certificate { get; }

    public string? Path { get; }

    /// <summary>Reads the PEM file (an RSA certificate and its private key) named by <see cref="PathSetting"/>, if set.</summary>
    /// <exception cref="InvalidOperationException">The setting names a file that can't be used.</exception>
    public static KeyRingCertificate Load(IConfiguration configuration)
    {
        var path = configuration[PathSetting];
        if (string.IsNullOrWhiteSpace(path))
        {
            return new KeyRingCertificate(null, null);
        }

        X509Certificate2 certificate;
        try
        {
            certificate = X509Certificate2.CreateFromPemFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        {
            throw new InvalidOperationException($"{PathSetting}: can't read a certificate and its private key from {path}: {ex.Message}", ex);
        }

        using var privateKey = certificate.GetRSAPrivateKey();
        if (privateKey is null)
        {
            certificate.Dispose();
            throw new InvalidOperationException($"{PathSetting}: {path} must hold an RSA certificate and its private key.");
        }

        return new KeyRingCertificate(certificate, path);
    }

    public void Dispose() => Certificate?.Dispose();
}

/// <summary>
/// Brings the stored Data Protection keys in line with <see cref="KeyRingCertificate"/> at startup.
/// It encrypts keys stored before the certificate was configured, and refuses to start when the
/// stored keys need a certificate that isn't configured. Otherwise Data Protection would skip those
/// keys and make new ones, and everything they protect would become unreadable.
/// </summary>
public sealed class KeyRingEncryption(DbContextOptions<CoreDbContext> database, KeyRingCertificate keyRing, ILoggerFactory loggerFactory)
{
    private static readonly XNamespace DataProtection = "http://schemas.asp.net/2015/03/dataProtection";
    private static readonly XName RequiresEncryption = DataProtection + "requiresEncryption";
    // Data Protection writes <encryptedSecret> in its own namespace (xmlns="http://schemas.asp.net/2015/03/dataProtection"),
    // as stored keys show; KeyRingTests check both the lookup and the rewrite against real keys.
    private static readonly XName EncryptedSecret = DataProtection + "encryptedSecret";
    private static readonly XName X509Certificate = XName.Get("X509Certificate", "http://www.w3.org/2000/09/xmldsig#");

    public async Task SecureStoredKeysAsync(CancellationToken cancellationToken)
    {
        await using var db = new CoreDbContext(database);
        var certificate = keyRing.Certificate;
        var keys = await db.DataProtectionKeys.Where(k => k.Xml != null).ToListAsync(cancellationToken);
        var encryptor = certificate is null ? null : new CertificateXmlEncryptor(certificate, loggerFactory);
        var encrypted = 0;

        foreach (var key in keys)
        {
            var element = XElement.Parse(key.Xml!);
            foreach (var thumbprint in EncryptingCertificates(element))
            {
                if (certificate is null)
                {
                    throw new InvalidOperationException(
                        $"The Data Protection keys in the database are encrypted with a certificate (thumbprint {thumbprint}), " +
                        $"but {KeyRingCertificate.PathSetting} isn't set. Point it at that certificate's file: without it, " +
                        "nothing those keys protect can be decrypted.");
                }

                if (!string.Equals(thumbprint, certificate.Thumbprint, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"The Data Protection keys in the database are encrypted with a different certificate (thumbprint {thumbprint}) " +
                        $"from {keyRing.Path} (thumbprint {certificate.Thumbprint}). Restore the original file: without it, " +
                        "nothing those keys protect can be decrypted.");
                }
            }

            if (encryptor is null)
            {
                continue;
            }

            // As Data Protection does for new keys: each secret becomes <encryptedSecret decryptorType="...">.
            var secrets = element.DescendantsAndSelf().Where(e => (bool?)e.Attribute(RequiresEncryption) == true).ToList();
            foreach (var secret in secrets)
            {
                var info = encryptor.Encrypt(new XElement(secret));
                secret.ReplaceWith(new XElement(
                    EncryptedSecret,
                    new XAttribute("decryptorType", info.DecryptorType.AssemblyQualifiedName!),
                    info.EncryptedElement));
            }

            if (secrets.Count > 0)
            {
                key.Xml = element.ToString(SaveOptions.DisableFormatting);
                encrypted++;
            }
        }

        if (encrypted == 0)
        {
            return;
        }

        await db.SaveChangesAsync(cancellationToken);

        // Rewrites the table's files, so the old unencrypted row versions don't linger in them.
#pragma warning disable EF1002 // A fixed statement, no input.
        await db.Database.ExecuteSqlRawAsync($"VACUUM FULL \"{CoreDbContext.Schema}\".\"data_protection_keys\"", cancellationToken);
#pragma warning restore EF1002
        loggerFactory.CreateLogger<KeyRingEncryption>().LogInformation(
            "Encrypted {Count} stored Data Protection keys with the certificate {Thumbprint}.", encrypted, certificate!.Thumbprint);
    }

    /// <summary>The thumbprints of the certificates a key's secrets are encrypted with.</summary>
    private static IEnumerable<string> EncryptingCertificates(XElement key) =>
        key.Descendants(EncryptedSecret)
            .SelectMany(e => e.Descendants(X509Certificate))
            .Select(e =>
            {
                using var certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(e.Value));
                return certificate.Thumbprint;
            })
            .Distinct(StringComparer.OrdinalIgnoreCase);
}
