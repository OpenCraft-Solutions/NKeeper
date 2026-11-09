using NKeeper.Plugins.Packaging;

namespace NKeeper.Plugins.Host;

/// <summary>
/// Builds the trust store (docs/plugin-security.md §6): the official keys embedded in the server,
/// the trust directory's key files and revocation list, and, in Development only, the development
/// trust directory. The trust directory is all or nothing: any unusable file is a
/// <see cref="TrustConfigurationException"/>.
/// </summary>
public static class TrustLoader
{
    public const string RevocationFileName = "revoked.json";

    public static TrustStore Load(
        IEnumerable<TrustedKey> officialKeys,
        Revocations embeddedRevocations,
        string trustDirectory,
        string? developmentTrustDirectory,
        bool isDevelopment)
    {
        var keys = new List<TrustedKey>(officialKeys);
        var revocations = embeddedRevocations;

        if (Directory.Exists(trustDirectory))
        {
            revocations = revocations.Union(ReadDirectory(trustDirectory, keys, allowDevelopmentKeys: false));
        }

        if (isDevelopment && developmentTrustDirectory is not null && Directory.Exists(developmentTrustDirectory))
        {
            revocations = revocations.Union(ReadDirectory(developmentTrustDirectory, keys, allowDevelopmentKeys: true));
        }

        return new TrustStore(keys, revocations);
    }

    private static Revocations ReadDirectory(string directory, List<TrustedKey> keys, bool allowDevelopmentKeys)
    {
        var revocations = Revocations.None;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                if (Path.GetFileName(file) == RevocationFileName)
                {
                    revocations = revocations.Union(Revocations.FromJson(File.ReadAllBytes(file)));
                    continue;
                }

                var key = TrustedKey.FromKeyFile(File.ReadAllBytes(file));
                if (key.Kind == TrustedKeyKind.Development && !allowDevelopmentKeys)
                {
                    // Development keys belong in the development trust directory, which only Development reads.
                    throw new TrustConfigurationException("Development keys can't be installed in the production trust directory.");
                }

                keys.Add(key);
            }
            catch (TrustConfigurationException ex)
            {
                throw new TrustConfigurationException($"'{file}': {ex.Message}", ex);
            }
        }

        return revocations;
    }
}
