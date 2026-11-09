using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

namespace NKeeper.Architecture.Tests;

/// <summary>docs/architecture.md §2, rules 1–4: who may reference whom.</summary>
public sealed class DependencyTests
{
    private const string Sdk = "NKeeper.Abstractions";

    public static TheoryData<string> PluginProjects() => [.. Repository.OfKind(ProjectKind.Plugin).Select(p => p.RelativePath)];

    public static TheoryData<string> CoreProjects() => [.. Repository.OfKind(ProjectKind.Core).Select(p => p.RelativePath)];

    [Fact]
    public void The_layout_is_what_the_rules_expect()
    {
        // Guards the rules below against passing vacuously after a move or rename.
        Assert.Contains(Repository.Projects, p => p.AssemblyName == Sdk && p.Kind == ProjectKind.Core);
        Assert.Contains(Repository.Projects, p => p.AssemblyName == "NKeeper.Server" && p.Kind == ProjectKind.Core);
        Assert.NotEmpty(Repository.OfKind(ProjectKind.Plugin));
    }

    [Fact]
    public void Every_project_is_in_the_solution()
    {
        // The real <Project Path> entries: a commented-out entry doesn't count.
        var inSolution = System.Xml.Linq.XDocument.Load(Path.Combine(Repository.Root, "NKeeper.slnx"))
            .Descendants("Project")
            .Select(p => ((string?)p.Attribute("Path") ?? string.Empty).Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);

        var missing = Repository.Projects.Where(p => !inSolution.Contains(p.RelativePath)).ToList();

        Assert.True(missing.Count == 0, $"Not in NKeeper.slnx, so CI wouldn't build or test them: {string.Join(", ", missing)}");
    }

    // ---- Rule 1: a plugin references only the SDK ----

    [Theory]
    [MemberData(nameof(PluginProjects))]
    public void A_plugin_project_references_only_the_sdk_without_copying_it(string relativePath)
    {
        var plugin = Repository.Projects.Single(p => p.RelativePath == relativePath);

        foreach (var reference in plugin.References)
        {
            var target = Repository.At(reference.FullPath);
            Assert.True(target.AssemblyName == Sdk, $"{plugin} references {target}. A plugin may reference only {Sdk}.");
            Assert.True(
                string.Equals(reference.Private, "false", StringComparison.OrdinalIgnoreCase)
                    && (reference.ExcludeAssets ?? string.Empty).Contains("runtime", StringComparison.OrdinalIgnoreCase),
                $"{plugin} must reference {Sdk} with Private=\"false\" ExcludeAssets=\"runtime\": the SDK is shared from the server, never shipped in a package.");
        }
    }

    [Theory]
    [MemberData(nameof(PluginProjects))]
    public void A_built_plugin_references_no_core_assembly_and_ships_no_sdk(string relativePath)
    {
        var plugin = Repository.Projects.Single(p => p.RelativePath == relativePath);

        var nkeeper = ReferencedAssemblies(plugin.OutputAssembly).Where(IsNKeeper).ToList();

        Assert.All(nkeeper, name => Assert.Equal(Sdk, name));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(plugin.OutputAssembly)!, Sdk + ".dll")), $"{plugin}'s output contains {Sdk}.dll.");
    }

    // ---- Rule 2: core never references a plugin ----

    [Theory]
    [MemberData(nameof(CoreProjects))]
    public void A_core_project_reaches_no_plugin_through_its_references(string relativePath)
    {
        var start = Repository.Projects.Single(p => p.RelativePath == relativePath);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<(Project Project, string Path)>([(start, start.RelativePath)]);

        while (pending.TryPop(out var current))
        {
            foreach (var reference in current.Project.References)
            {
                var target = Repository.At(reference.FullPath);
                var path = $"{current.Path} -> {target.RelativePath}";
                Assert.True(target.Kind != ProjectKind.Plugin, $"Core references a plugin: {path}");
                Assert.True(target.Kind != ProjectKind.Test, $"Core references a test project: {path}");
                if (seen.Add(target.FullPath))
                {
                    pending.Push((target, path));
                }
            }
        }
    }

    [Fact]
    public void The_built_server_depends_on_no_plugin()
    {
        var server = Repository.Projects.Single(p => p.AssemblyName == "NKeeper.Server");
        var depsFile = Path.ChangeExtension(server.OutputAssembly, ".deps.json");
        var plugins = Repository.OfKind(ProjectKind.Plugin).Select(p => p.AssemblyName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        using var deps = JsonDocument.Parse(File.ReadAllText(depsFile));
        var libraries = deps.RootElement.GetProperty("libraries").EnumerateObject().Select(l => l.Name.Split('/')[0]).ToList();

        Assert.Contains(Sdk, libraries);
        Assert.DoesNotContain(libraries, plugins.Contains);
    }

    // ---- Rule 3: the SDK stands alone ----

    [Fact]
    public void The_sdk_references_no_other_nkeeper_project_or_assembly()
    {
        var sdk = Repository.Projects.Single(p => p.AssemblyName == Sdk);

        Assert.Empty(sdk.References);
        Assert.DoesNotContain(ReferencedAssemblies(sdk.OutputAssembly), IsNKeeper);
    }

    // ---- Rule 4: frontend packages ----

    [Fact]
    public void Core_frontend_packages_depend_on_no_plugin_package()
    {
        var pluginPackages = Packages("plugins").Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var package in Packages("core/frontend"))
        {
            foreach (var (name, version) in package.Dependencies)
            {
                Assert.False(
                    pluginPackages.Contains(name) || name.StartsWith("@nkeeper/plugin-", StringComparison.Ordinal) || version.Contains("plugins/", StringComparison.Ordinal),
                    $"{package.Path} depends on the plugin package {name}.");
            }
        }
    }

    [Fact]
    public void A_plugin_frontend_depends_on_no_nkeeper_package_but_the_sdk()
    {
        foreach (var package in Packages("plugins"))
        {
            var nkeeper = package.Dependencies.Select(d => d.Name).Where(n => n.StartsWith("@nkeeper/", StringComparison.Ordinal));
            Assert.All(nkeeper, name => Assert.True(name == "@nkeeper/sdk", $"{package.Path} depends on {name}; a plugin may use only @nkeeper/sdk."));
        }
    }

    [Fact]
    public void Core_frontend_sources_import_nothing_from_outside_core()
    {
        var core = Path.Combine(Repository.Root, "core") + Path.DirectorySeparatorChar;

        foreach (var file in Repository.Files("core/frontend", "*.ts", "*.tsx", "*.js", "*.mjs"))
        {
            foreach (var specifier in Sources.RelativeImports(File.ReadAllText(file)))
            {
                var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, specifier));
                Assert.True(target.StartsWith(core, StringComparison.OrdinalIgnoreCase), $"{Repository.Relative(file)} imports '{specifier}', outside core/.");
            }
        }
    }

    private static bool IsNKeeper(string assemblyName) =>
        assemblyName.Equals("NKeeper", StringComparison.OrdinalIgnoreCase) || assemblyName.StartsWith("NKeeper.", StringComparison.OrdinalIgnoreCase);

    private static List<string> ReferencedAssemblies(string path)
    {
        Assert.True(File.Exists(path), $"'{Repository.Relative(path)}' hasn't been built.");
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        return metadata.AssemblyReferences.Select(h => metadata.GetString(metadata.GetAssemblyReference(h).Name)).ToList();
    }

    private sealed record Package(string Path, string Name, IReadOnlyList<(string Name, string Version)> Dependencies);

    private static List<Package> Packages(string folder) =>
        Repository.Files(folder, "package.json").Select(file =>
        {
            using var json = JsonDocument.Parse(File.ReadAllText(file));
            var dependencies = new List<(string, string)>();
            foreach (var section in new[] { "dependencies", "devDependencies", "peerDependencies", "optionalDependencies" })
            {
                if (json.RootElement.TryGetProperty(section, out var entries))
                {
                    dependencies.AddRange(entries.EnumerateObject().Select(e => (e.Name, e.Value.GetString() ?? string.Empty)));
                }
            }

            var name = json.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
            return new Package(Repository.Relative(file), name, dependencies);
        }).ToList();
}
