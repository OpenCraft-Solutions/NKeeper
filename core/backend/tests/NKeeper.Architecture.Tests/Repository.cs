using System.Reflection;
using System.Xml.Linq;

namespace NKeeper.Architecture.Tests;

/// <summary>What a project is, by where it lives (docs/architecture.md §1).</summary>
public enum ProjectKind
{
    /// <summary><c>core/backend/*</c> (not its tests) and <c>tools/*</c>.</summary>
    Core,

    /// <summary><c>plugins/*</c> and <c>testing/plugins/*</c>.</summary>
    Plugin,

    /// <summary>Everything else: tests and test helpers.</summary>
    Test,
}

/// <summary>One <c>ProjectReference</c>, with the metadata the rules look at.</summary>
public sealed record ProjectReference(string Include, string FullPath, string? Private, string? ExcludeAssets);

/// <summary>A project file, read as XML.</summary>
public sealed record Project(string FullPath, string RelativePath, ProjectKind Kind, string AssemblyName, IReadOnlyList<ProjectReference> References)
{
    public string Directory => Path.GetDirectoryName(FullPath)!;

    /// <summary>The built assembly, for the configuration and framework these tests were built with.</summary>
    public string OutputAssembly => Path.Combine(Directory, "bin", Repository.Configuration, Repository.TargetFramework, AssemblyName + ".dll");

    public override string ToString() => RelativePath;
}

/// <summary>The repository these tests were built from.</summary>
public static class Repository
{
    private static readonly string[] SkippedFolders = ["bin", "obj", "node_modules", "dist", ".git", "generated"];

    public static string Root { get; } = Metadata("RepositoryRoot");

    public static string Configuration { get; } = Metadata("Configuration");

    public static string TargetFramework { get; } = Metadata("TargetFramework");

    /// <summary>Every project file in the repository.</summary>
    public static IReadOnlyList<Project> Projects { get; } = Files("", "*.csproj").Select(Read).ToList();

    public static IEnumerable<Project> OfKind(ProjectKind kind) => Projects.Where(p => p.Kind == kind);

    /// <summary>A path relative to the root, with forward slashes.</summary>
    public static string Relative(string fullPath) => Path.GetRelativePath(Root, fullPath).Replace('\\', '/');

    /// <summary>Files under <paramref name="folder"/> (relative to the root) matching any pattern, skipping build output and dependencies.</summary>
    public static IEnumerable<string> Files(string folder, params string[] patterns)
    {
        var start = Path.Combine(Root, folder);
        if (!System.IO.Directory.Exists(start))
        {
            return [];
        }

        return Walk(start).Where(f => patterns.Any(p => FileSystemName.Matches(p, Path.GetFileName(f)))).Order(StringComparer.Ordinal);

        static IEnumerable<string> Walk(string directory)
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(directory))
            {
                yield return file;
            }

            foreach (var child in System.IO.Directory.EnumerateDirectories(directory))
            {
                if (!SkippedFolders.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase))
                {
                    foreach (var file in Walk(child))
                    {
                        yield return file;
                    }
                }
            }
        }
    }

    /// <summary>The project at a full path. Fails the test if it isn't one of the repository's projects.</summary>
    public static Project At(string fullPath) =>
        Projects.SingleOrDefault(p => string.Equals(p.FullPath, Path.GetFullPath(fullPath), StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"'{Relative(fullPath)}' is referenced but isn't a project in the repository.");

    private static Project Read(string path)
    {
        var relative = Relative(path);
        var document = XDocument.Load(path);
        var references = document.Descendants("ProjectReference")
            .Select(r =>
            {
                var include = (string?)r.Attribute("Include") ?? string.Empty;
                return new ProjectReference(
                    include,
                    Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, include.Replace('\\', Path.DirectorySeparatorChar))),
                    (string?)r.Attribute("Private") ?? (string?)r.Element("Private"),
                    (string?)r.Attribute("ExcludeAssets") ?? (string?)r.Element("ExcludeAssets"));
            })
            .ToList();
        var assemblyName = document.Descendants("AssemblyName").Select(e => e.Value).LastOrDefault() ?? Path.GetFileNameWithoutExtension(path);
        return new Project(Path.GetFullPath(path), relative, KindOf(relative), assemblyName, references);
    }

    private static ProjectKind KindOf(string relative) =>
        relative.StartsWith("plugins/", StringComparison.Ordinal) || relative.StartsWith("testing/plugins/", StringComparison.Ordinal) ? ProjectKind.Plugin
        : relative.StartsWith("core/backend/tests/", StringComparison.Ordinal) ? ProjectKind.Test
        : relative.StartsWith("core/", StringComparison.Ordinal) || relative.StartsWith("tools/", StringComparison.Ordinal) ? ProjectKind.Core
        : ProjectKind.Test;

    private static string Metadata(string key) =>
        typeof(Repository).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;
}

/// <summary>Shell-style wildcard matching of file names (<c>*</c> only).</summary>
internal static class FileSystemName
{
    public static bool Matches(string pattern, string name) =>
        System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true);
}
