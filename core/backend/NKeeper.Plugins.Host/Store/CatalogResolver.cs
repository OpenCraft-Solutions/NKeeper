using System.Security.Cryptography;
using System.Text;
using NKeeper.Plugins.Packaging;

namespace NKeeper.Plugins.Host.Store;

/// <summary>A catalogued plugin's newest installable release, verified as it would be at load.</summary>
/// <param name="Ranges">The range of each plugin it depends on, by ID (docs/architecture.md §9.2).</param>
public sealed record CatalogCandidate(
    string Id,
    string Name,
    string Version,
    string PackageSha256,
    string KeyId,
    bool BuiltIn,
    IReadOnlyList<string> DependsOn,
    Uri Package,
    IReadOnlyDictionary<string, string> Ranges);

/// <summary>What a refresh found for one catalogued plugin (docs/architecture.md §9.1).</summary>
/// <param name="Available">The newest installable release newer than the installed one; null when there is none.</param>
/// <param name="ReasonCode">Why there's nothing to install although a newer release exists: a verification reason code, or one of <see cref="CatalogReasonCodes"/>.</param>
public sealed record CatalogPluginStatus(
    string Id,
    string Repository,
    string? Summary,
    string? InstalledVersion,
    CatalogCandidate? Available,
    string? ReasonCode,
    string? Message);

/// <summary>Reasons a refresh couldn't offer a release, beyond the verifier's own (plugin-security.md §5).</summary>
public static class CatalogReasonCodes
{
    /// <summary>GitHub, or a download from it, failed.</summary>
    public const string DownloadFailed = "download_failed";

    /// <summary>The package didn't match its published SHA-256.</summary>
    public const string ChecksumMismatch = "checksum_mismatch";

    /// <summary>The package is another plugin, or another version, than its release says.</summary>
    public const string WrongPackage = "wrong_package";
}

/// <summary>
/// Finds each catalogued plugin's newest release this server could load (docs/architecture.md §9.1): newer than the
/// installed version, with this server's SDK in its range, and verified exactly as at load. Installs nothing.
/// </summary>
public sealed class CatalogResolver(IPluginReleaseSource source, VerificationContext context)
{
    /// <summary>How many releases back a plugin is searched for one this server's SDK can load.</summary>
    public const int MaxReleasesTried = 10;

    private const int MaxChecksumBytes = 1024;

    /// <summary>A tag with no package published under it: passed over, never reported.</summary>
    private const string NotReleased = "not_released";

    private readonly Dictionary<string, byte[]> _packages = new(StringComparer.Ordinal);

    /// <summary>The verified bytes of each candidate found, by plugin ID: what root's side installs.</summary>
    public IReadOnlyDictionary<string, byte[]> Packages => _packages;

    public async Task<IReadOnlyList<CatalogPluginStatus>> ResolveAsync(
        Catalog catalog,
        IReadOnlyDictionary<string, SemanticVersion> installed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(installed);
        var statuses = new List<CatalogPluginStatus>();
        foreach (var entry in catalog.Entries)
        {
            installed.TryGetValue(entry.Id, out var current);
            statuses.Add(await ResolveAsync(entry, current, cancellationToken));
        }

        return statuses;
    }

    /// <summary>One catalogued plugin's newest installable release newer than <paramref name="installed"/>.</summary>
    public async Task<CatalogPluginStatus> ResolveAsync(CatalogEntry entry, SemanticVersion? installed, CancellationToken cancellationToken)
    {
        CatalogPluginStatus Status(CatalogCandidate? candidate, string? reason = null, string? message = null) =>
            new(entry.Id, entry.Repository, entry.Summary, installed?.Text, candidate, reason, message);

        IReadOnlyList<PluginRelease> releases;
        try
        {
            releases = await source.ListAsync(entry, cancellationToken);
        }
        catch (ReleaseSourceException ex)
        {
            return Status(null, CatalogReasonCodes.DownloadFailed, ex.Message);
        }

        (string Reason, string Message)? firstRefusal = null;
        foreach (var release in releases.Where(r => installed is null || r.Version > installed).Take(MaxReleasesTried))
        {
            var (candidate, refusal) = await TryAsync(entry, release, cancellationToken);
            if (candidate is not null)
            {
                return Status(candidate);
            }

            if (refusal!.Value.Reason == NotReleased)
            {
                // Not a release: the next older one may be.
                continue;
            }

            firstRefusal ??= refusal;
            if (refusal.Value.Reason != ReasonCodes.SdkIncompatible)
            {
                // Only an SDK this server doesn't have sends it back to an older release; anything else is reported.
                break;
            }
        }

        return firstRefusal is { } r ? Status(null, r.Reason, r.Message) : Status(null);
    }

    private async Task<(CatalogCandidate?, (string Reason, string Message)?)> TryAsync(CatalogEntry entry, PluginRelease release, CancellationToken cancellationToken)
    {
        byte[] package;
        try
        {
            package = await source.DownloadAsync(release.Package, context.Limits.MaxPackageBytes, cancellationToken);
            if (release.Sha256 is not null)
            {
                var published = Encoding.ASCII.GetString(await source.DownloadAsync(release.Sha256, MaxChecksumBytes, cancellationToken))
                    .Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.Equals(published, Convert.ToHexStringLower(SHA256.HashData(package)), StringComparison.OrdinalIgnoreCase))
                {
                    return (null, (CatalogReasonCodes.ChecksumMismatch, $"{release.PackageName} doesn't match its published SHA-256."));
                }
            }
        }
        catch (ReleaseSourceException ex) when (ex.NotFound)
        {
            // The feed lists tags, and a tag whose release isn't published yet (it waits for signing) has no package.
            return (null, (NotReleased, ex.Message));
        }
        catch (ReleaseSourceException ex)
        {
            return (null, (CatalogReasonCodes.DownloadFailed, ex.Message));
        }

        var verification = PackageVerifier.Verify(package, release.PackageName, context);
        if (!verification.IsVerified)
        {
            return (null, (verification.ReasonCode!, $"{entry.Id} {release.Version.Text}: {verification.Message}"));
        }

        var manifest = verification.Manifest!;
        if (manifest.Id != entry.Id || manifest.Version != release.Version)
        {
            return (null, (CatalogReasonCodes.WrongPackage, $"{entry.Repository}'s release {release.Version.Text} carries {manifest.Id} {manifest.Version.Text}."));
        }

        _packages[entry.Id] = package;
        return (new CatalogCandidate(
            manifest.Id,
            manifest.Name,
            manifest.Version.Text,
            verification.PackageSha256!,
            verification.KeyId!,
            manifest.BuiltIn,
            [.. manifest.DependsOn.Select(d => d.Id)],
            release.Package,
            manifest.DependsOn.GroupBy(d => d.Id).ToDictionary(g => g.Key, g => string.Join(' ', g.Select(d => d.Version.Text)), StringComparer.Ordinal)), null);
    }
}
