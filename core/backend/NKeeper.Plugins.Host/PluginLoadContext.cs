using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using NKeeper.Plugins.Packaging;

namespace NKeeper.Plugins.Host;

/// <summary>
/// One plugin's load context (docs/plugin-security.md §7.1). Its assemblies are loaded only from the
/// verified in-memory bytes, never from a path. Shared assemblies always come from the server's
/// default context, and a dependency's exported assemblies from that dependency's context. This
/// separates versions; it is not a security boundary.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly Dictionary<string, ReadOnlyMemory<byte>> _assemblies;
    private readonly IReadOnlySet<string> _shared;
    private readonly IReadOnlyList<PluginLoadContext> _dependencies;

    public PluginLoadContext(string pluginId, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files, IReadOnlySet<string> exportedPaths, IReadOnlySet<string> shared, IReadOnlyList<PluginLoadContext> dependencies)
        : base($"plugin:{pluginId}", isCollectible: false)
    {
        _shared = shared;
        _dependencies = dependencies;
        _assemblies = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.OrdinalIgnoreCase);
        Exported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, data) in files)
        {
            if (path.StartsWith(PackageFormat.BackendFolder, StringComparison.Ordinal)
                && path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                && AssemblyNameOf(data) is { } name)
            {
                _assemblies[name] = data;
                if (exportedPaths.Contains(path))
                {
                    Exported.Add(name);
                }
            }
        }
    }

    /// <summary>Simple names of the assemblies dependants may load from this context.</summary>
    public HashSet<string> Exported { get; }

    /// <summary>The simple name of the assembly at a package path, if it is one of this plugin's.</summary>
    public static string? AssemblyNameOf(ReadOnlyMemory<byte> data)
    {
        using var stream = new MemoryStream(data.ToArray(), writable: false);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        return metadata.IsAssembly ? metadata.GetString(metadata.GetAssemblyDefinition().Name) : null;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (name is null || _shared.Contains(name))
        {
            // Shared: resolved by the default context, so the plugin and the server agree on its types.
            return null;
        }

        if (_assemblies.TryGetValue(name, out var data))
        {
            return LoadFromStream(new MemoryStream(data.ToArray(), writable: false));
        }

        foreach (var dependency in _dependencies)
        {
            if (dependency.Exported.Contains(name))
            {
                return dependency.LoadFromAssemblyName(assemblyName);
            }
        }

        return null;
    }
}
