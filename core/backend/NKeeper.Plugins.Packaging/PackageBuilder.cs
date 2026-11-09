using NKeeper.Plugins.Packaging.Zip;

namespace NKeeper.Plugins.Packaging;

/// <summary>A built package: its file name and bytes.</summary>
public sealed record BuiltPackage(string FileName, byte[] Content);

/// <summary>Builds and signs plugin packages (docs/plugin-security.md §3, §10).</summary>
public static class PackageBuilder
{
    /// <summary>
    /// Builds a signed package from a manifest and the plugin's files (package paths such as
    /// <c>backend/Foo.dll</c> to contents). The output is deterministic for the same input and key.
    /// </summary>
    /// <exception cref="ArgumentException">The manifest is invalid, or a path is invalid or reserved.</exception>
    public static BuiltPackage Build(byte[] manifestJson, IReadOnlyDictionary<string, byte[]> files, PluginSigningKey key)
    {
        ArgumentNullException.ThrowIfNull(manifestJson);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(key);

        var all = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [PackageFormat.ManifestPath] = manifestJson };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { PackageFormat.ManifestPath };
        foreach (var (path, data) in files)
        {
            if (!PackagePath.IsValid(path) || path is PackageFormat.FileListPath or PackageFormat.SignaturePath || !seen.Add(path))
            {
                throw new ArgumentException($"'{path}' is not a valid, unique package path.", nameof(files));
            }

            all[path] = data;
        }

        if (!PluginManifest.TryParse(manifestJson, all.Keys.ToHashSet(StringComparer.Ordinal), out var manifest, out var error))
        {
            throw new ArgumentException($"The manifest is invalid: {error}", nameof(manifestJson));
        }

        var fileList = FileList.Create(manifest.Id, manifest.Version.Text, all).ToCanonicalJson();
        var signature = new PackageSignature(key.KeyId, PackageFormat.SignatureAlgorithm, Convert.ToBase64String(key.Sign(fileList))).ToJson();

        var entries = all.Select(f => new ZipWriteEntry(f.Key, f.Value))
            .Append(new ZipWriteEntry(PackageFormat.FileListPath, fileList))
            .Append(new ZipWriteEntry(PackageFormat.SignaturePath, signature));
        return new BuiltPackage(PackageFormat.FileName(manifest.Id, manifest.Version.Text), ZipWriter.Write(entries));
    }
}
