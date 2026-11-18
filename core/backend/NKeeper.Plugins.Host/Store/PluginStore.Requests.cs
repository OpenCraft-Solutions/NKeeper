using System.Security.Cryptography;
using System.Text.Json;
using NKeeper.Plugins.Packaging;

namespace NKeeper.Plugins.Host.Store;

/// <summary>What the plugins page can ask root's helper for (docs/architecture.md §9.1–§9.3).</summary>
public static class PluginRequestActions
{
    /// <summary>Install or upgrade a plugin from a catalog: NKeeper's own, or the one the request names.</summary>
    public const string Install = "install";

    /// <summary>Install a package an administrator uploaded, left beside the request as <c>&lt;request&gt;.nkp</c>.</summary>
    public const string Upload = "upload";

    /// <summary>Remove a plugin's package; its data stays.</summary>
    public const string Remove = "remove";

    /// <summary>Restart NKeeper, so enabling and disabling take effect.</summary>
    public const string Restart = "restart";

    /// <summary>Trust a third-party key: only where root allowed web trust.</summary>
    public const string Trust = "trust";

    /// <summary>Stop trusting a third-party key: only where root allowed web trust.</summary>
    public const string Untrust = "untrust";
}

/// <summary>A request from the plugins page, which root's helper checks again from scratch before applying it.</summary>
/// <param name="Catalog">For <c>install</c>, the catalog the plugin comes from, when it isn't NKeeper's own (§9.3).</param>
/// <param name="KeyFile">For <c>trust</c>, the publisher's key file (plugin-security.md §6.1).</param>
/// <param name="KeyId">For <c>untrust</c>, the key to stop trusting.</param>
public sealed record PluginRequest(
    string RequestedBy,
    string Action = PluginRequestActions.Install,
    string? Id = null,
    string? Version = null,
    string? PackageSha256 = null,
    string? Catalog = null,
    string? KeyFile = null,
    string? KeyId = null);

public sealed partial class PluginStore
{
    /// <summary>The file in the plugin directory that remembers which plugins were removed, so the installer doesn't bring them back.</summary>
    public const string RemovedFileName = ".removed";

    private const int MaxRequestBytes = 16 * 1024;

    /// <summary>The trust directory, for trusting and untrusting keys (plugin-security.md §4).</summary>
    public string? TrustDirectory { get; init; }

    /// <summary>Whether root allowed the plugins page to trust keys on this server (<c>--allow-web-trust</c>, §9.3).</summary>
    public bool AllowWebTrust { get; init; }

    /// <summary>A store over another catalog, for a request that names one (§9.3).</summary>
    public Func<Uri, PluginStore>? OtherCatalog { get; init; }

    /// <summary>The installed plugins' manifests, from the packages in the plugin directory that verify with root's trust.</summary>
    public IReadOnlyList<PluginManifest> InstalledManifests() =>
        [.. (Directory.Exists(pluginDirectory) ? Directory.EnumerateFiles(pluginDirectory, "*" + PackageFormat.Extension) : [])
            .Select(path => PackageVerifier.Verify(File.ReadAllBytes(path), Path.GetFileName(path), context))
            .Where(v => v.IsVerified)
            .Select(v => v.Manifest!)];

    /// <summary>
    /// Removes a plugin's package (<c>remove</c>), unless an installed plugin depends on it. Its data stays, and the
    /// installer won't bring it back (§9.2).
    /// </summary>
    public InstallOutcome Remove(string id)
    {
        if (!Installed().TryGetValue(id, out var version))
        {
            return new InstallOutcome(id, null, InstallResult.Failed, $"{id} isn't installed.");
        }

        var dependants = InstalledManifests().Where(m => m.DependsOn.Any(d => d.Id == id)).Select(m => m.Id).Order(StringComparer.Ordinal).ToList();
        if (dependants.Count > 0)
        {
            return new InstallOutcome(id, version.Text, InstallResult.Failed, $"{string.Join(", ", dependants)} depend{(dependants.Count == 1 ? "s" : string.Empty)} on {id}; remove {(dependants.Count == 1 ? "it" : "them")} first.");
        }

        foreach (var path in Directory.EnumerateFiles(pluginDirectory, "*" + PackageFormat.Extension))
        {
            if (ParsePackageName(Path.GetFileName(path)) is var (packageId, _) && packageId == id)
            {
                File.Delete(path);
            }
        }

        MarkRemoved(id, removed: true);
        return new InstallOutcome(id, version.Text, InstallResult.Removed, $"{id} {version.Text} removed. Its data is kept.");
    }

    /// <summary>Trusts a third-party key (<c>trust add</c>): never an official, install or development key (plugin-security.md §6).</summary>
    public InstallOutcome Trust(string keyFile)
    {
        TrustedKey key;
        try
        {
            key = TrustedKey.FromKeyFile(System.Text.Encoding.UTF8.GetBytes(keyFile));
        }
        catch (TrustConfigurationException ex)
        {
            return new InstallOutcome("key", null, InstallResult.Failed, $"That isn't a key file NKeeper can trust: {ex.Message}");
        }

        if (key.Kind != TrustedKeyKind.ThirdParty)
        {
            return new InstallOutcome(key.KeyId, null, InstallResult.Failed, "Only a third-party key can be trusted this way: official keys are in the server, and install and development keys can't be added.");
        }

        if (TrustDirectory is null)
        {
            return new InstallOutcome(key.KeyId, null, InstallResult.Failed, "No trust directory is configured.");
        }

        Directory.CreateDirectory(TrustDirectory);
        var path = Path.Combine(TrustDirectory, $"{key.KeyId}.json");
        var part = Path.Combine(TrustDirectory, $".{key.KeyId}.part");
        File.Delete(part);
        using (var stream = CreateExclusive(part))
        {
            stream.Write(key.ToKeyFile());
        }

        File.Move(part, path, overwrite: true);
        return new InstallOutcome(key.KeyId, null, InstallResult.Trusted, $"Key {key.KeyId} trusted to sign {string.Join(", ", key.AllowedIds)}.");
    }

    /// <summary>Stops trusting a third-party key (<c>trust remove</c>). Packages it signed are refused from the next restart.</summary>
    public InstallOutcome Untrust(string keyId)
    {
        foreach (var (path, key) in TrustedKeys())
        {
            if (key.KeyId == keyId)
            {
                if (key.Kind != TrustedKeyKind.ThirdParty)
                {
                    return new InstallOutcome(keyId, null, InstallResult.Failed, "Only third-party keys can be untrusted this way.");
                }

                File.Delete(path);
                return new InstallOutcome(keyId, null, InstallResult.Untrusted, $"Key {keyId} is no longer trusted. Plugins it signed are refused from the next restart.");
            }
        }

        return new InstallOutcome(keyId, null, InstallResult.Failed, $"Key {keyId} isn't in the trust directory.");
    }

    /// <summary>The key files in the trust directory, with the keys they hold (<c>trust list</c>).</summary>
    public IReadOnlyList<(string Path, TrustedKey Key)> TrustedKeys() => TrustedKeysIn(TrustDirectory);

    /// <summary>The key files in a trust directory, with the keys they hold.</summary>
    public static IReadOnlyList<(string Path, TrustedKey Key)> TrustedKeysIn(string? trustDirectory)
    {
        var keys = new List<(string, TrustedKey)>();
        if (trustDirectory is null || !Directory.Exists(trustDirectory))
        {
            return keys;
        }

        foreach (var path in Directory.EnumerateFiles(trustDirectory, "*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                keys.Add((path, TrustedKey.FromKeyFile(File.ReadAllBytes(path))));
            }
            catch (TrustConfigurationException)
            {
                // revoked.json, or a file the server itself refuses at start.
            }
        }

        return keys;
    }

    /// <summary>Applies one request from the plugins page.</summary>
    private async Task<InstallOutcome> ApplyAsync(FileInfo file, PluginRequest request, List<InstallOutcome> outcomes, CancellationToken cancellationToken)
    {
        InstallOutcome outcome;
        switch (request.Action)
        {
            case PluginRequestActions.Install:
                var store = request.Catalog is null || request.Catalog == catalogUrl.ToString() ? this : OtherCatalog?.Invoke(new Uri(request.Catalog));
                if (store is null)
                {
                    outcome = new InstallOutcome(request.Id!, request.Version, InstallResult.Failed, "This helper can't read other catalogs.");
                    break;
                }

                var these = new List<InstallOutcome>();
                await store.InstallAsync(request.Id!, request, these, [], cancellationToken);
                outcomes.AddRange(these);
                outcome = these.LastOrDefault(o => o.Id == request.Id) ?? new InstallOutcome(request.Id!, request.Version, InstallResult.Failed, "Nothing was installed.");
                if (these.Count > 1)
                {
                    var dependencies = string.Join(", ", these.Where(o => o.Id != request.Id).Select(o => $"{o.Id} {o.Version}: {o.Result}"));
                    outcome = outcome with { Message = $"{outcome.Message} Dependencies: {dependencies}." };
                }

                return outcome;
            case PluginRequestActions.Upload:
                outcome = InstallUpload(file, request);
                break;
            case PluginRequestActions.Remove:
                outcome = Remove(request.Id!);
                break;
            case PluginRequestActions.Restart:
                outcome = new InstallOutcome("nkeeper", null, InstallResult.Restarting, "NKeeper restarted.");
                break;
            case PluginRequestActions.Trust or PluginRequestActions.Untrust when !AllowWebTrust:
                outcome = new InstallOutcome(request.KeyId ?? "key", null, InstallResult.Failed, "This server doesn't allow trusting keys from the plugins page. Root can: sudo nkeeper plugin trust add <key file>.");
                break;
            case PluginRequestActions.Trust:
                outcome = Trust(request.KeyFile!);
                break;
            default:
                outcome = Untrust(request.KeyId!);
                break;
        }

        outcomes.Add(outcome);
        return outcome;
    }

    /// <summary>
    /// Installs an uploaded package: the one beside the request, if it is exactly the package the administrator saw,
    /// verifies with root's trust, is newer than the installed version, and has its dependencies installed (§9.3).
    /// </summary>
    private InstallOutcome InstallUpload(FileInfo file, PluginRequest request)
    {
        var package = new FileInfo(Path.ChangeExtension(file.FullName, PackageFormat.Extension));
        try
        {
            if (!package.Exists || package.LinkTarget is not null || package.Length > context.Limits.MaxPackageBytes)
            {
                return new InstallOutcome(request.Id!, request.Version, InstallResult.Failed, "The uploaded package isn't there, or is too large.");
            }

            var bytes = File.ReadAllBytes(package.FullName);
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), request.PackageSha256, StringComparison.OrdinalIgnoreCase))
            {
                return new InstallOutcome(request.Id!, request.Version, InstallResult.Failed, "The uploaded package isn't the one that was checked.");
            }

            var verification = PackageVerifier.Verify(bytes, PackageFormat.FileName(request.Id!, request.Version!), context);
            if (!verification.IsVerified)
            {
                return new InstallOutcome(request.Id!, request.Version, InstallResult.Failed, $"{verification.Message} ({verification.ReasonCode})");
            }

            var manifest = verification.Manifest!;
            var installed = Installed();
            if (installed.TryGetValue(manifest.Id, out var current) && manifest.Version <= current)
            {
                return new InstallOutcome(manifest.Id, manifest.Version.Text, InstallResult.Failed, $"{manifest.Id} {current.Text} is installed; only a newer version can be.");
            }

            if (manifest.DependsOn.FirstOrDefault(d => !installed.TryGetValue(d.Id, out var v) || !d.Version.Includes(v)) is { } missing)
            {
                return new InstallOutcome(manifest.Id, manifest.Version.Text, InstallResult.Failed, $"{manifest.Id} needs {missing.Id} {missing.Version}; install it first.");
            }

            WritePackage(manifest.Id, manifest.Version.Text, bytes);
            return new InstallOutcome(
                manifest.Id,
                manifest.Version.Text,
                current is null ? InstallResult.Installed : InstallResult.Upgraded,
                current is null ? $"{manifest.Id} {manifest.Version.Text} installed from an upload." : $"{manifest.Id} upgraded from {current.Text} to {manifest.Version.Text}, from an upload.");
        }
        finally
        {
            File.Delete(package.FullName);
        }
    }

    /// <summary>Records (or forgets) that a plugin was removed, in <see cref="RemovedFileName"/> in the plugin directory.</summary>
    private void MarkRemoved(string id, bool removed)
    {
        var path = Path.Combine(pluginDirectory, RemovedFileName);
        var ids = File.Exists(path) ? File.ReadAllLines(path).Where(PluginId.IsValid).ToHashSet(StringComparer.Ordinal) : [];
        if (!(removed ? ids.Add(id) : ids.Remove(id)))
        {
            return;
        }

        var part = Path.Combine(pluginDirectory, $"{RemovedFileName}.part");
        File.Delete(part);
        using (var stream = CreateExclusive(part))
        using (var writer = new StreamWriter(stream))
        {
            foreach (var line in ids.Order(StringComparer.Ordinal))
            {
                writer.WriteLine(line);
            }
        }

        File.Move(part, path, overwrite: true);
    }

    /// <summary>
    /// A request as the service wrote it: a regular file (never a link), small, with the fields its action needs.
    /// Anything else is treated as no request.
    /// </summary>
    private static PluginRequest? ReadRequest(FileInfo file)
    {
        try
        {
            if (file.LinkTarget is not null || file.Length > MaxRequestBytes)
            {
                return null;
            }

            var request = JsonSerializer.Deserialize<PluginRequest>(File.ReadAllBytes(file.FullName), Json);
            if (request?.RequestedBy is null)
            {
                return null;
            }

            static bool Package(PluginRequest r) =>
                PluginId.IsValid(r.Id) && SemanticVersion.TryParse(r.Version, out _) && r.PackageSha256 is { Length: 64 } sha && sha.All(char.IsAsciiHexDigit);

            return request.Action switch
            {
                PluginRequestActions.Install => Package(request) && (request.Catalog is null || (Uri.TryCreate(request.Catalog, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)) ? request : null,
                PluginRequestActions.Upload => Package(request) ? request : null,
                PluginRequestActions.Remove => PluginId.IsValid(request.Id) ? request : null,
                PluginRequestActions.Restart => request,
                PluginRequestActions.Trust => request.KeyFile is { Length: > 0 } ? request : null,
                PluginRequestActions.Untrust => SigningKeys.IsKeyId(request.KeyId) ? request : null,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
