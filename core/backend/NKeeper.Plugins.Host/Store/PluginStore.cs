using System.Text.Json;
using System.Text.Json.Serialization;
using NKeeper.Plugins.Packaging;

namespace NKeeper.Plugins.Host.Store;

/// <summary>What happened to one plugin an install or upgrade asked for.</summary>
public sealed record InstallOutcome(string Id, string? Version, InstallResult Result, string Message);

[JsonConverter(typeof(JsonStringEnumConverter<InstallResult>))]
public enum InstallResult
{
    Installed,
    Upgraded,
    UpToDate,
    Failed,
    Removed,
    Trusted,
    Untrusted,
    Restarting,
}

/// <summary>
/// Root's side of the catalog (docs/architecture.md §9.1): refreshing it, and installing and upgrading from it into the
/// plugin directory. It trusts nothing the service wrote: it fetches the catalog and packages itself, and verifies them
/// with root's own trust configuration.
/// </summary>
public sealed partial class PluginStore(string pluginDirectory, Uri catalogUrl, IPluginReleaseSource source, VerificationContext context)
{
    /// <summary>The most requests one run of the helper applies; any more are refused.</summary>
    public const int MaxRequestsPerRun = 10;

    public const string RequestExtension = ".json";
    public const string ResultExtension = ".result";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private Catalog? _catalog;
    private CatalogResolver? _resolver;
    private readonly Dictionary<string, CatalogPluginStatus> _resolved = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _upgradingFor = new(StringComparer.Ordinal);

    public async Task<Catalog> CatalogAsync(CancellationToken cancellationToken)
    {
        if (_catalog is null)
        {
            byte[] file;
            try
            {
                file = await source.DownloadAsync(catalogUrl, Catalog.MaxBytes, cancellationToken);
            }
            catch (ReleaseSourceException ex)
            {
                throw new CatalogException($"Couldn't read the catalog: {ex.Message}", ex);
            }

            _catalog = Catalog.Parse(file);
        }

        return _catalog;
    }

    /// <summary>The installed plugins' versions, from the package names in the plugin directory (<c>&lt;id&gt;-&lt;version&gt;.nkp</c>).</summary>
    public IReadOnlyDictionary<string, SemanticVersion> Installed() => InstalledIn(pluginDirectory);

    /// <summary>The versions of the plugins whose packages are in <paramref name="pluginDirectory"/>.</summary>
    public static IReadOnlyDictionary<string, SemanticVersion> InstalledIn(string pluginDirectory)
    {
        var installed = new Dictionary<string, SemanticVersion>(StringComparer.Ordinal);
        if (!Directory.Exists(pluginDirectory))
        {
            return installed;
        }

        foreach (var name in Directory.EnumerateFiles(pluginDirectory, "*" + PackageFormat.Extension).Select(Path.GetFileName).OfType<string>())
        {
            if (ParsePackageName(name) is var (id, version) && (!installed.TryGetValue(id, out var other) || version > other))
            {
                installed[id] = version;
            }
        }

        return installed;
    }

    /// <summary>Refreshes the catalog (<c>update</c>): what each catalogued plugin could be installed or upgraded to.</summary>
    public async Task<IReadOnlyList<CatalogPluginStatus>> UpdateAsync(CancellationToken cancellationToken)
    {
        var catalog = await CatalogAsync(cancellationToken);
        var statuses = new List<CatalogPluginStatus>();
        foreach (var entry in catalog.Entries)
        {
            statuses.Add(await ResolveAsync(entry, cancellationToken));
        }

        return CatalogCompatibility.Apply(statuses, InstalledManifests().ToDictionary(m => m.Id, StringComparer.Ordinal));
    }

    /// <summary>Installs catalogued plugins by ID (<c>install</c>), each with the plugins it depends on that aren't installed.</summary>
    public async Task<IReadOnlyList<InstallOutcome>> InstallAsync(IEnumerable<string> ids, CancellationToken cancellationToken)
    {
        var outcomes = new List<InstallOutcome>();
        foreach (var id in ids)
        {
            await InstallAsync(id, null, outcomes, [], cancellationToken);
        }

        return outcomes;
    }

    /// <summary>Upgrades installed plugins (<c>upgrade</c>): those named, or every one the catalog has a newer installable release of.</summary>
    public async Task<IReadOnlyList<InstallOutcome>> UpgradeAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        var catalog = await CatalogAsync(cancellationToken);
        var installed = Installed();
        var outcomes = new List<InstallOutcome>();
        // Dependants first: each brings the shared plugin it needs along, which alone their old ranges would refuse (§9.2).
        var dependants = InstalledManifests().Where(m => m.DependsOn.Count > 0).Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var targets = (ids.Count > 0 ? ids : catalog.Entries.Select(e => e.Id).Where(installed.ContainsKey))
            .OrderBy(id => dependants.Contains(id) ? 0 : 1).ToList();
        foreach (var id in targets)
        {
            if (!installed.ContainsKey(id))
            {
                outcomes.Add(new InstallOutcome(id, null, InstallResult.Failed, $"{id} isn't installed; install it instead."));
                continue;
            }

            await InstallAsync(id, null, outcomes, [], cancellationToken);
        }

        return outcomes;
    }

    /// <summary>
    /// Applies the plugins page's requests (<c>apply-requests</c>): at most <see cref="MaxRequestsPerRun"/>, each checked
    /// again from the catalog, and installed only if the catalog still offers exactly the version and package the
    /// administrator saw. Each request is replaced by its result. Returns whether anything was installed.
    /// </summary>
    public async Task<IReadOnlyList<InstallOutcome>> ApplyRequestsAsync(string requestDirectory, CancellationToken cancellationToken)
    {
        var outcomes = new List<InstallOutcome>();
        if (!Directory.Exists(requestDirectory))
        {
            return outcomes;
        }

        var files = new DirectoryInfo(requestDirectory).EnumerateFiles("*" + RequestExtension)
            .OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal).ToList();
        foreach (var (file, index) in files.Select((f, i) => (f, i)))
        {
            InstallOutcome outcome;
            var request = ReadRequest(file);
            if (index >= MaxRequestsPerRun)
            {
                outcome = new InstallOutcome(request?.Id ?? file.Name, request?.Version, InstallResult.Failed, "Too many requests at once; ask again.");
            }
            else if (request is null)
            {
                outcome = new InstallOutcome(file.Name, null, InstallResult.Failed, "The request isn't one this helper reads.");
            }
            else
            {
                outcome = await ApplyAsync(file, request, outcomes, cancellationToken);
            }

            WriteResult(file, request, outcome);
            if (index >= MaxRequestsPerRun || request is null)
            {
                outcomes.Add(outcome);
            }
        }

        return outcomes;
    }

    /// <summary>Whether any outcome put a package in the plugin directory, so NKeeper needs a restart.</summary>
    public static bool Changed(IEnumerable<InstallOutcome> outcomes) =>
        outcomes.Any(o => o.Result is InstallResult.Installed or InstallResult.Upgraded or InstallResult.Removed or InstallResult.Trusted or InstallResult.Untrusted or InstallResult.Restarting);

    private async Task InstallAsync(string id, PluginRequest? request, List<InstallOutcome> outcomes, HashSet<string> visiting, CancellationToken cancellationToken)
    {
        if (outcomes.Any(o => o.Id == id) || !visiting.Add(id))
        {
            return;
        }

        var catalog = await CatalogAsync(cancellationToken);
        if (catalog.Find(id) is not { } entry)
        {
            outcomes.Add(new InstallOutcome(id, request?.Version, InstallResult.Failed, $"{id} isn't in the catalog."));
            return;
        }

        var status = await ResolveAsync(entry, cancellationToken);
        if (status.Available is not { } candidate)
        {
            outcomes.Add(status.ReasonCode is null
                ? new InstallOutcome(id, status.InstalledVersion, request is null ? InstallResult.UpToDate : InstallResult.Failed, $"{id} {status.InstalledVersion} is the newest release this server can load.")
                : new InstallOutcome(id, request?.Version, InstallResult.Failed, $"{status.Message} ({status.ReasonCode})"));
            return;
        }

        if (request is not null && (candidate.Version != request.Version || !string.Equals(candidate.PackageSha256, request.PackageSha256, StringComparison.OrdinalIgnoreCase)))
        {
            outcomes.Add(new InstallOutcome(id, request.Version, InstallResult.Failed, $"The catalog now offers {id} {candidate.Version}, not the package that was asked for; check for updates and ask again."));
            return;
        }

        var installed = Installed();
        var outOfRange = candidate.Ranges.Where(r => installed.TryGetValue(r.Key, out var v) && VersionRange.TryParse(r.Value, out var range) && !range.Includes(v)).Select(r => r.Key);
        foreach (var dependency in candidate.DependsOn.Where(d => !installed.ContainsKey(d)).Concat(outOfRange).Distinct(StringComparer.Ordinal))
        {
            // Upgraded for this plugin, which is installed right after it, so this plugin's old range doesn't hold it back.
            _upgradingFor[dependency] = id;
            await InstallAsync(dependency, null, outcomes, visiting, cancellationToken);
            if (outcomes.LastOrDefault(o => o.Id == dependency) is not { Result: InstallResult.Installed or InstallResult.Upgraded })
            {
                outcomes.Add(new InstallOutcome(id, candidate.Version, InstallResult.Failed, $"{id} needs {dependency}, which couldn't be installed."));
                return;
            }
        }

        // §9.2: never move a shared plugin out of an installed dependant's range, except the one it's upgraded for.
        var newVersion = SemanticVersion.Parse(candidate.Version);
        _upgradingFor.TryGetValue(id, out var upgradingFor);
        var followUps = new List<string>();
        if (installed.ContainsKey(id))
        {
            foreach (var blocking in InstalledManifests().Where(m => m.Id != id && m.Id != upgradingFor && !CatalogCompatibility.Accepts(m, id, newVersion)))
            {
                // Upgraded in the same run when it has a release that accepts the new version; otherwise nothing moves.
                if (upgradingFor is not null && catalog.Find(blocking.Id) is { } blockingEntry
                    && (await ResolveAsync(blockingEntry, cancellationToken)).Available is { } fix
                    && (!fix.Ranges.TryGetValue(id, out var range) || (VersionRange.TryParse(range, out var parsed) && parsed.Includes(newVersion))))
                {
                    followUps.Add(blocking.Id);
                    continue;
                }

                outcomes.Add(new InstallOutcome(id, candidate.Version, InstallResult.Failed, $"{blocking.Id} {blocking.Version.Text} doesn't accept {id} {candidate.Version}; upgrade {blocking.Id} first."));
                return;
            }
        }

        var upgraded = installed.ContainsKey(id);
        try
        {
            WritePackage(id, candidate.Version, _resolver!.Packages[id]);
        }
        catch (IOException ex)
        {
            outcomes.Add(new InstallOutcome(id, candidate.Version, InstallResult.Failed, $"Couldn't write the package: {ex.Message}"));
            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            outcomes.Add(new InstallOutcome(id, candidate.Version, InstallResult.Failed, $"Couldn't write the package (run this as root): {ex.Message}"));
            return;
        }

        outcomes.Add(new InstallOutcome(
            id,
            candidate.Version,
            upgraded ? InstallResult.Upgraded : InstallResult.Installed,
            upgraded ? $"{id} upgraded from {status.InstalledVersion} to {candidate.Version}." : $"{id} {candidate.Version} installed."));
        foreach (var followUp in followUps)
        {
            await InstallAsync(followUp, null, outcomes, visiting, cancellationToken);
        }
    }

    private async Task<CatalogPluginStatus> ResolveAsync(CatalogEntry entry, CancellationToken cancellationToken)
    {
        if (!_resolved.TryGetValue(entry.Id, out var status))
        {
            _resolver ??= new CatalogResolver(source, context);
            Installed().TryGetValue(entry.Id, out var installed);
            status = await _resolver.ResolveAsync(entry, installed, cancellationToken);
            _resolved[entry.Id] = status;
        }

        return status;
    }

    /// <summary>
    /// Puts a package in the plugin directory as <c>&lt;id&gt;-&lt;version&gt;.nkp</c>, readable by all and written by
    /// its owner only, with an atomic rename; then removes the plugin's other packages.
    /// </summary>
    private void WritePackage(string id, string version, byte[] package)
    {
        var name = PackageFormat.FileName(id, version);
        var path = Path.Combine(pluginDirectory, name);
        var part = Path.Combine(pluginDirectory, $".{name}.part");
        File.Delete(part);
        using (var stream = CreateExclusive(part))
        {
            stream.Write(package);
            stream.Flush(flushToDisk: true);
        }

        File.Move(part, path, overwrite: true);
        MarkRemoved(id, removed: false);
        foreach (var other in Directory.EnumerateFiles(pluginDirectory, "*" + PackageFormat.Extension))
        {
            if (Path.GetFileName(other) != name && ParsePackageName(Path.GetFileName(other)) is var (otherId, _) && otherId == id)
            {
                File.Delete(other);
            }
        }
    }

    /// <summary>
    /// A new file, created exclusively (<c>O_EXCL</c>, so never through a link), <c>0644</c> on Unix: readable by the
    /// service, written by root only.
    /// </summary>
    private static FileStream CreateExclusive(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        }

        return new FileStream(path, options);
    }

    /// <summary>A package's ID and version from its name, <c>&lt;id&gt;-&lt;version&gt;.nkp</c>; null for any other name.</summary>
    internal static (string Id, SemanticVersion Version)? ParsePackageName(string fileName)
    {
        if (!fileName.EndsWith(PackageFormat.Extension, StringComparison.Ordinal))
        {
            return null;
        }

        var stem = fileName[..^PackageFormat.Extension.Length];
        for (var dash = stem.IndexOf('-', StringComparison.Ordinal); dash > 0; dash = stem.IndexOf('-', dash + 1))
        {
            if (dash + 1 < stem.Length && char.IsAsciiDigit(stem[dash + 1]) && PluginId.IsValid(stem[..dash]) && SemanticVersion.TryParse(stem[(dash + 1)..], out var version))
            {
                return (stem[..dash], version);
            }
        }

        return null;
    }

    /// <summary>
    /// Replaces a request with its result. The result is created exclusively, so a link the service put in its place
    /// is never written through; the request is removed either way.
    /// </summary>
    private static void WriteResult(FileInfo file, PluginRequest? request, InstallOutcome outcome)
    {
        var result = Path.ChangeExtension(file.FullName, ResultExtension);
        try
        {
            File.Delete(result);
            using var stream = CreateExclusive(result);
            JsonSerializer.Serialize(stream, new { outcome.Id, outcome.Version, outcome.Result, outcome.Message, RequestedBy = request?.RequestedBy, At = DateTimeOffset.UtcNow }, Json);
        }
        catch (IOException)
        {
            // Someone else's file in the way: the outcome still reaches the helper's log.
        }
        finally
        {
            File.Delete(file.FullName);
        }
    }
}
