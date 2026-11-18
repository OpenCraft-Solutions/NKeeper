using System.Diagnostics;
using NKeeper.Abstractions;
using NKeeper.Plugins.Host;
using NKeeper.Plugins.Host.Store;
using NKeeper.Plugins.Packaging;

namespace NKeeper.Server;

/// <summary>
/// The server binary's <c>plugin</c> command (docs/plugin-security.md §9; architecture.md §9.1), which
/// <c>/usr/bin/nkeeper</c> runs: the catalog's <c>update</c>, <c>upgrade</c>, <c>install</c> and <c>apply-requests</c>,
/// and <c>list</c>. It doesn't start the web server. Writing the plugin directory needs root.
/// </summary>
internal static class PluginCommand
{
    private const string Usage = """
        Usage: nkeeper plugin <command>

          list                   the installed plugin packages
          update                 refreshes the catalog: what can be installed or upgraded
          upgrade [<id>...]      upgrades every installed plugin with a newer release, or those named
          install <id>...        installs catalogued plugins, with the plugins they depend on
          apply-requests         applies the plugins page's requests (run by nkeeper-plugin-requests.service)

        install, upgrade and apply-requests need root. Add --restart to restart NKeeper after a change.
        """;

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
            .AddEnvironmentVariables()
            .Build();
        var environment = configuration["ASPNETCORE_ENVIRONMENT"] ?? configuration["DOTNET_ENVIRONMENT"] ?? "Production";
        var isDevelopment = string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase);
        var options = configuration.GetSection(PluginHostOptions.SectionName).Get<PluginHostOptions>() ?? new PluginHostOptions();
        var catalogOptions = configuration.GetSection(PluginCatalogOptions.SectionName).Get<PluginCatalogOptions>() ?? new PluginCatalogOptions();

        var command = args.FirstOrDefault();
        var restart = args.Contains("--restart");
        var ids = args.Skip(1).Where(a => a != "--restart").ToList();
        if (command is null or "help" or "--help" or "-h")
        {
            Console.WriteLine(Usage);
            return command is null ? 2 : 0;
        }

        if (command == "list")
        {
            foreach (var (id, version) in PluginStore.InstalledIn(options.Directory).OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                Console.WriteLine($"{id} {version.Text}");
            }

            return 0;
        }

        if (command is not ("update" or "upgrade" or "install" or "apply-requests") || (command == "install" && ids.Count == 0))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        if (catalogOptions.CatalogUri is null)
        {
            Console.Error.WriteLine("The plugin catalog is off: Plugins:Catalog:Url isn't an https:// URL.");
            return 1;
        }

        if (command != "update" && !isDevelopment && OperatingSystem.IsLinux() && new LinuxFileSystem().EffectiveUserId != 0)
        {
            Console.Error.WriteLine("Only root can install plugins (docs/plugin-security.md §1). Run it with sudo.");
            return 1;
        }

        TrustStore trust;
        try
        {
            trust = TrustLoader.Load(OfficialKeys.All, OfficialKeys.Revocations, options.TrustDirectory, options.DevelopmentTrustDirectory, isDevelopment);
        }
        catch (TrustConfigurationException ex)
        {
            Console.Error.WriteLine($"The trust directory can't be used: {ex.Message}");
            return 1;
        }

        var context = new VerificationContext(trust, isDevelopment, SemanticVersion.Parse(SdkVersion.Current), SharedAssemblies.OfCurrentProcess(), TimeProvider.System);
        using var http = GitHubReleases.CreateClient();
        var store = new PluginStore(options.Directory, catalogOptions.CatalogUri, new GitHubReleases(http), context);
        try
        {
            var catalog = await store.CatalogAsync(cancellationToken);
            foreach (var warning in catalog.Warnings)
            {
                Console.Error.WriteLine($"Catalog: {warning}");
            }

            if (command == "update")
            {
                foreach (var status in await store.UpdateAsync(cancellationToken))
                {
                    Console.WriteLine(Describe(status));
                }

                return 0;
            }

            var outcomes = command switch
            {
                "install" => await store.InstallAsync(ids, cancellationToken),
                "upgrade" => await store.UpgradeAsync(ids, cancellationToken),
                _ => await store.ApplyRequestsAsync(catalogOptions.RequestDirectory, cancellationToken),
            };
            foreach (var outcome in outcomes)
            {
                Console.WriteLine($"{outcome.Id}: {outcome.Message}");
            }

            if (PluginStore.Changed(outcomes))
            {
                if (restart || command == "apply-requests")
                {
                    await RestartAsync(cancellationToken);
                }
                else
                {
                    Console.WriteLine("Restart NKeeper to load it: systemctl restart nkeeper");
                }
            }

            // The helper's failures are in each request's result; a failed unit would only make systemd stop starting it.
            return command != "apply-requests" && outcomes.Any(o => o.Result == InstallResult.Failed) ? 1 : 0;
        }
        catch (CatalogException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string Describe(CatalogPluginStatus status) => status switch
    {
        { Available: { } a, InstalledVersion: { } installed } => $"{status.Id}: {installed} -> {a.Version} (upgrade)",
        { Available: { } a } => $"{status.Id}: {a.Version} available (install){(status.Summary is null ? string.Empty : $" - {status.Summary}")}",
        { ReasonCode: { } reason } => $"{status.Id}: {(status.InstalledVersion is null ? "not installed" : status.InstalledVersion)}; nothing installable: {status.Message} ({reason})",
        { InstalledVersion: { } installed } => $"{status.Id}: {installed}, up to date",
        _ => $"{status.Id}: not installed; no release yet",
    };

    private static async Task RestartAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("Restarting NKeeper.");
        using var process = Process.Start(new ProcessStartInfo("systemctl", ["try-restart", "nkeeper.service"]) { UseShellExecute = false })
            ?? throw new InvalidOperationException("Couldn't run systemctl.");
        await process.WaitForExitAsync(cancellationToken);
    }
}
