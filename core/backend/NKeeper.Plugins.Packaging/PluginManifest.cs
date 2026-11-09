using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace NKeeper.Plugins.Packaging;

/// <summary>A dependency on another plugin.</summary>
public sealed record PluginDependency(string Id, VersionRange Version);

/// <summary>The backend part of a plugin.</summary>
/// <param name="EntryAssembly">The package path of the assembly holding the plugin's entry type.</param>
/// <param name="EntryType">The full name of the type implementing the plugin interface.</param>
/// <param name="ExportedAssemblies">Package paths of contract assemblies dependants may use.</param>
public sealed record PluginBackend(string EntryAssembly, string EntryType, IReadOnlyList<string> ExportedAssemblies);

/// <summary>The frontend part of a plugin.</summary>
/// <param name="Entry">The package path of the ES module the shell imports.</param>
public sealed record PluginFrontend(string Entry);

/// <summary>A plugin's manifest, <c>nkeeper-plugin.json</c> (docs/plugin-security.md §3), validated.</summary>
/// <remarks>
/// <see cref="BuiltIn"/> is only what the package claims. The plugin host honours it only for
/// official signers (or development keys in Development).
/// </remarks>
public sealed partial record PluginManifest(
    int FormatVersion,
    string Id,
    string Name,
    SemanticVersion Version,
    VersionRange SdkVersion,
    IReadOnlyList<PluginDependency> DependsOn,
    IReadOnlyList<string> Requirements,
    PluginBackend Backend,
    PluginFrontend? Frontend,
    bool BuiltIn = false)
{
    /// <summary>
    /// Parses and validates a manifest. When <paramref name="packageFiles"/> is given, the entry
    /// points must be files in that set.
    /// </summary>
    public static bool TryParse(
        ReadOnlySpan<byte> json,
        IReadOnlySet<string>? packageFiles,
        [NotNullWhen(true)] out PluginManifest? manifest,
        [NotNullWhen(false)] out string? error)
    {
        manifest = null;
        if (!PackageJson.TryDeserialize<ManifestDocument>(json, out var doc, out error))
        {
            return false;
        }

        error = Validate(doc, packageFiles, out manifest);
        return error is null;
    }

    private static string? Validate(ManifestDocument doc, IReadOnlySet<string>? files, out PluginManifest? manifest)
    {
        manifest = null;
        if (doc.FormatVersion != PackageFormat.Version)
        {
            return $"Manifest format version {doc.FormatVersion} isn't supported; this server reads version {PackageFormat.Version}.";
        }

        if (!PluginId.IsValid(doc.Id))
        {
            return $"'{doc.Id}' is not a valid plugin ID.";
        }

        if (string.IsNullOrWhiteSpace(doc.Name) || doc.Name.Length > 100)
        {
            return "The name must be 1 to 100 characters.";
        }

        if (!SemanticVersion.TryParse(doc.Version, out var version))
        {
            return $"'{doc.Version}' is not a SemVer 2.0 version.";
        }

        if (!VersionRange.TryParse(doc.SdkVersion, out var sdk))
        {
            return $"'{doc.SdkVersion}' is not a version range.";
        }

        // Nullable annotations aren't enforced on collection elements, so check for null entries.
        if (doc.DependsOn?.Any(d => d is null) == true
            || doc.Requirements?.Any(r => r is null) == true
            || doc.Backend.ExportedAssemblies?.Any(a => a is null) == true)
        {
            return "Lists may not contain null entries.";
        }

        var dependencies = new List<PluginDependency>();
        foreach (var dependency in doc.DependsOn ?? [])
        {
            if (!PluginId.IsValid(dependency.Id) || dependency.Id == doc.Id || dependencies.Any(d => d.Id == dependency.Id))
            {
                return $"'{dependency.Id}' is not a valid, distinct dependency.";
            }

            if (!VersionRange.TryParse(dependency.Version, out var range))
            {
                return $"The version range of dependency '{dependency.Id}' is invalid.";
            }

            dependencies.Add(new PluginDependency(dependency.Id, range));
        }

        var requirements = doc.Requirements ?? [];
        if (requirements.Any(r => !RequirementPattern().IsMatch(r)) || requirements.Distinct(StringComparer.Ordinal).Count() != requirements.Count)
        {
            return "Requirements must be distinct lowercase names such as 'guacd'.";
        }

        var backend = doc.Backend;
        var exported = backend.ExportedAssemblies ?? [];
        if (!IsBackendAssembly(backend.EntryAssembly, files) || exported.Any(a => !IsBackendAssembly(a, files)))
        {
            return "The entry and exported assemblies must be .dll files under backend/ in the package.";
        }

        if (string.IsNullOrWhiteSpace(backend.EntryType) || backend.EntryType.Length > 512)
        {
            return "The entry type must be a type name.";
        }

        if (doc.Frontend is { } frontend
            && (!PackagePath.IsValid(frontend.Entry)
                || !frontend.Entry.StartsWith(PackageFormat.FrontendFolder, StringComparison.Ordinal)
                || !frontend.Entry.EndsWith(".js", StringComparison.Ordinal)
                || (files is not null && !files.Contains(frontend.Entry))))
        {
            return "The frontend entry must be a .js file under frontend/ in the package.";
        }

        manifest = new PluginManifest(
            doc.FormatVersion,
            doc.Id,
            doc.Name,
            version,
            sdk,
            dependencies,
            requirements,
            new PluginBackend(backend.EntryAssembly, backend.EntryType, exported),
            doc.Frontend is { } f ? new PluginFrontend(f.Entry) : null,
            doc.BuiltIn ?? false);
        return null;
    }

    private static bool IsBackendAssembly(string path, IReadOnlySet<string>? files) =>
        PackagePath.IsValid(path)
        && path.StartsWith(PackageFormat.BackendFolder, StringComparison.Ordinal)
        && path.EndsWith(".dll", StringComparison.Ordinal)
        && (files is null || files.Contains(path));

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex RequirementPattern();

    private sealed record ManifestDocument(
        int FormatVersion,
        string Id,
        string Name,
        string Version,
        string SdkVersion,
        BackendDocument Backend,
        IReadOnlyList<DependencyDocument>? DependsOn = null,
        IReadOnlyList<string>? Requirements = null,
        FrontendDocument? Frontend = null,
        bool? BuiltIn = null);

    private sealed record DependencyDocument(string Id, string Version);

    private sealed record BackendDocument(string EntryAssembly, string EntryType, IReadOnlyList<string>? ExportedAssemblies = null);

    private sealed record FrontendDocument(string Entry);
}
