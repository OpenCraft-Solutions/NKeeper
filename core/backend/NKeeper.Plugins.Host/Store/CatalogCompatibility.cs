using NKeeper.Plugins.Packaging;

namespace NKeeper.Plugins.Host.Store;

/// <summary>
/// Keeps shared plugins and the plugins built on them in step (docs/architecture.md §9.2). A shared plugin's new
/// version is offered only if every installed dependant accepts it; otherwise the dependant to upgrade first is named.
/// A dependant's new version that needs a newer shared plugin brings it along, if every other dependant accepts it.
/// </summary>
public static class CatalogCompatibility
{
    /// <summary>A shared plugin's new version that an installed dependant doesn't accept.</summary>
    public const string DependantsIncompatible = "dependants_incompatible";

    /// <summary>A new version that needs a version of a shared plugin that can't be installed with it.</summary>
    public const string DependencyUnavailable = "dependency_unavailable";

    /// <summary>The statuses, with offers that would break an installed plugin withdrawn and explained.</summary>
    /// <param name="installed">The installed plugins' manifests, by ID.</param>
    public static IReadOnlyList<CatalogPluginStatus> Apply(IReadOnlyList<CatalogPluginStatus> statuses, IReadOnlyDictionary<string, PluginManifest> installed)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        ArgumentNullException.ThrowIfNull(installed);
        var offers = statuses.Where(s => s.Available is not null).ToDictionary(s => s.Id, s => s.Available!, StringComparer.Ordinal);
        return [.. statuses.Select(s => s.Available is { } candidate ? Check(s, candidate, offers, installed) : s)];
    }

    /// <summary>Whether a dependant accepts a shared plugin's version: its candidate's range if it's upgraded with it, else its installed one's.</summary>
    public static bool Accepts(PluginManifest dependant, string sharedId, SemanticVersion version) =>
        dependant.DependsOn.Where(d => d.Id == sharedId).All(d => d.Version.Includes(version));

    private static CatalogPluginStatus Check(CatalogPluginStatus status, CatalogCandidate candidate, Dictionary<string, CatalogCandidate> offers, IReadOnlyDictionary<string, PluginManifest> installed)
    {
        var version = SemanticVersion.Parse(candidate.Version);

        // As a shared plugin: every installed dependant must accept the new version, or be upgraded first.
        var refusing = installed.Values.Where(m => m.Id != status.Id && !Accepts(m, status.Id, version)).OrderBy(m => m.Id, StringComparer.Ordinal).ToList();
        if (refusing.Count > 0)
        {
            var needs = string.Join("; ", refusing.Select(m => $"{m.Id} {m.Version.Text} needs {status.Id} {Range(m, status.Id)}"));
            var fixes = string.Join(" ", refusing.Select(m => offers.TryGetValue(m.Id, out var upgrade) && Includes(upgrade, status.Id, version)
                ? $"Upgrade {m.Id} to {upgrade.Version}, which brings {status.Id} {candidate.Version} with it."
                : $"Wait for a release of {m.Id} that accepts it, or remove {m.Id}."));
            return Withdraw(status, DependantsIncompatible, $"{needs}, which {candidate.Version} isn't in. {fixes}");
        }

        // As a dependant: each shared plugin it needs, in range, installed or brought along.
        foreach (var (sharedId, range) in candidate.Ranges)
        {
            if (!installed.TryGetValue(sharedId, out var shared) || !VersionRange.TryParse(range, out var parsed) || parsed.Includes(shared.Version))
            {
                continue;
            }

            if (offers.TryGetValue(sharedId, out var sharedOffer) && parsed.Includes(SemanticVersion.Parse(sharedOffer.Version)))
            {
                var sharedVersion = SemanticVersion.Parse(sharedOffer.Version);
                // Other dependants that don't accept it are upgraded in the same run, if they have a release that does.
                var others = installed.Values
                    .Where(m => m.Id != status.Id && m.Id != sharedId && !Accepts(m, sharedId, sharedVersion)
                        && !(offers.TryGetValue(m.Id, out var upgrade) && Includes(upgrade, sharedId, sharedVersion)))
                    .Select(m => m.Id).ToList();
                if (others.Count == 0)
                {
                    continue;
                }

                return Withdraw(status, DependencyUnavailable, $"{status.Id} {candidate.Version} needs {sharedId} {range}, which {string.Join(", ", others)} doesn't accept yet.");
            }

            return Withdraw(status, DependencyUnavailable, $"{status.Id} {candidate.Version} needs {sharedId} {range}; {shared.Version.Text} is installed and no release in range can be.");
        }

        return status;
    }

    private static bool Includes(CatalogCandidate candidate, string sharedId, SemanticVersion version) =>
        !candidate.Ranges.TryGetValue(sharedId, out var range) || (VersionRange.TryParse(range, out var parsed) && parsed.Includes(version));

    private static string Range(PluginManifest dependant, string sharedId) =>
        string.Join(" and ", dependant.DependsOn.Where(d => d.Id == sharedId).Select(d => d.Version.Text));

    private static CatalogPluginStatus Withdraw(CatalogPluginStatus status, string reason, string message) =>
        status with { Available = null, ReasonCode = reason, Message = message };
}
