namespace NKeeper.Architecture.Tests;

/// <summary>docs/architecture.md §2, rule 5: core never names a service type or plugin ID.</summary>
public sealed class NamingTests
{
    /// <summary>
    /// The plugins and service types the plan names (docs/v2-plan.md §6), so the rule holds before they
    /// are written. Folder names under <c>plugins/</c> are added to these automatically.
    /// </summary>
    private static readonly string[] PlannedIds =
    [
        // service types
        "ssh", "telnet", "rdp", "vnc", "http", "https", "rpc", "mssql", "oracle", "ase", "idrac", "proxmox",
        // plugins
        "desktop", "web", "linux", "storage", "windows", "virt-core", "kvm", "migration", "db-core",
        "db-mssql", "db-oracle", "db-ase", "zabbix", "import-mremoteng", "network-scan", "wake-on-lan", "reports-ip",
    ];

    /// <summary>
    /// Literals core may use although they equal an ID, as "path in core: literal" with the reason.
    /// Each must be about something other than the plugin, such as a trust-store scope.
    /// </summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        // guacd's own protocol name in the Guacamole handshake ("select rdp"), not the desktop plugin's service type,
        // which happens to share it (docs/architecture.md §7.20).
        ["core/backend/NKeeper.Core/Connectivity/DesktopConnector.cs: rdp"] = "guacd's protocol name in the handshake",
    };

    public static IReadOnlySet<string> Ids { get; } = PlannedIds
        .Concat(Directory.Exists(Path.Combine(Repository.Root, "plugins"))
            ? Directory.EnumerateDirectories(Path.Combine(Repository.Root, "plugins")).Select(d => Path.GetFileName(d)!)
            : [])
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Core's source files: backend, frontend and tooling, without tests or generated code.</summary>
    public static IEnumerable<string> CoreSources() =>
        Repository.Files("core", "*.cs", "*.ts", "*.tsx", "*.js", "*.mjs")
            .Concat(Repository.Files("tools", "*.cs"))
            .Select(Repository.Relative)
            .Where(f => !f.StartsWith("core/backend/tests/", StringComparison.Ordinal)
                && !f.Contains("/Migrations/", StringComparison.Ordinal)
                && !f.Contains(".test.", StringComparison.Ordinal));

    [Fact]
    public void Core_sources_name_no_service_type_or_plugin_id()
    {
        var sources = CoreSources().ToList();
        var found = new List<string>();

        foreach (var file in sources)
        {
            var text = File.ReadAllText(Path.Combine(Repository.Root, file));
            var literals = file.EndsWith(".cs", StringComparison.Ordinal) ? Sources.CSharpLiterals(text) : Sources.ScriptLiterals(text);
            found.AddRange(literals
                .Where(l => Ids.Contains(l.Trim()))
                .Select(l => $"{file}: {l.Trim()}")
                .Where(entry => !Allowed.ContainsKey(entry)));
        }

        Assert.Contains(sources, f => f.StartsWith("core/backend/", StringComparison.Ordinal));
        Assert.Contains(sources, f => f.StartsWith("core/frontend/", StringComparison.Ordinal));
        Assert.True(found.Count == 0, "Core names a plugin or service type:\n" + string.Join("\n", found.Distinct()));
    }

    [Fact]
    public void Every_allowed_literal_is_still_used()
    {
        foreach (var entry in Allowed.Keys)
        {
            var (file, literal) = (entry[..entry.IndexOf(": ", StringComparison.Ordinal)], entry[(entry.IndexOf(": ", StringComparison.Ordinal) + 2)..]);
            var path = Path.Combine(Repository.Root, file);
            Assert.True(File.Exists(path), $"Allowed literal in a file that no longer exists: {entry}");
            var text = File.ReadAllText(path);
            var literals = file.EndsWith(".cs", StringComparison.Ordinal) ? Sources.CSharpLiterals(text) : Sources.ScriptLiterals(text);
            Assert.True(literals.Any(l => l.Trim() == literal), $"Allowed literal no longer used; remove it from the list: {entry}");
        }
    }
}
