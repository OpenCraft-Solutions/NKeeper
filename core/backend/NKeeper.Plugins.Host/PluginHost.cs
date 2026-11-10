using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NKeeper.Abstractions;
using NKeeper.Plugins.Packaging;

namespace NKeeper.Plugins.Host;

/// <summary>Facts about the running server that the host's decisions depend on.</summary>
/// <param name="OfficialKeys">Official keys embedded in this server build (§6.1); empty until the first release (D12).</param>
/// <param name="EmbeddedRevocations">Revocations embedded in this server build (§6.3).</param>
public sealed record PluginHostEnvironment(
    bool IsDevelopment,
    bool IsLinux,
    SemanticVersion SdkVersion,
    IReadOnlySet<string> SharedAssemblies,
    IReadOnlyList<TrustedKey> OfficialKeys,
    Revocations EmbeddedRevocations);

/// <summary>The outcome of startup: every plugin's status, the loaded plugins in dependency order, and alerts.</summary>
public sealed class PluginHostResult
{
    private readonly List<PluginStatus> _statuses = [];
    private readonly List<LoadedPlugin> _loaded = [];
    private readonly List<string> _alerts = [];
    private readonly List<ContributedServiceType> _serviceTypes = [];
    private readonly List<ContributedSessionType> _sessionTypes = [];

    public IReadOnlyList<PluginStatus> Statuses => _statuses;

    /// <summary>The service types the loaded plugins contributed, in load order (docs/architecture.md §7.4).</summary>
    public IReadOnlyList<ContributedServiceType> ServiceTypes =>
        [.. _serviceTypes.Where(t => _loaded.Exists(p => p.Manifest.Id == t.PluginId))];

    /// <summary>The session types the loaded plugins contributed, in load order (docs/architecture.md §7.10).</summary>
    public IReadOnlyList<ContributedSessionType> SessionTypes =>
        [.. _sessionTypes.Where(t => _loaded.Exists(p => p.Manifest.Id == t.PluginId))];

    /// <summary>Loaded plugins, dependencies before dependants.</summary>
    public IReadOnlyList<LoadedPlugin> Loaded => _loaded;

    /// <summary>Security alerts for administrators (§9): every refusal and every blocked startup step.</summary>
    public IReadOnlyList<string> Alerts => _alerts;

    /// <summary>Plugins that should load: enabled or built in, by record or by this start.</summary>
    internal HashSet<string> EnabledIds { get; } = new(StringComparer.Ordinal);

    internal HashSet<string> BuiltInIds { get; } = new(StringComparer.Ordinal);

    internal void Add(PluginStatus status) => _statuses.Add(status);

    internal void AddLoaded(LoadedPlugin plugin) => _loaded.Add(plugin);

    internal void Alert(string message) => _alerts.Add(message);

    internal void AddServiceTypes(string pluginId, IEnumerable<ServiceType> types) =>
        _serviceTypes.AddRange(types.Select(t => new ContributedServiceType(pluginId, t)));

    internal void AddSessionTypes(string pluginId, IEnumerable<SessionType> types) =>
        _sessionTypes.AddRange(types.Select(t => new ContributedSessionType(pluginId, t)));

    /// <summary>Marks a loaded plugin as refused, with every loaded plugin that depends on it.</summary>
    internal IReadOnlyList<string> Fail(string id, string reasonCode, string message)
    {
        var failed = new List<string>();
        var pending = new Queue<(string Id, string Code, string Message)>([(id, reasonCode, message)]);
        while (pending.TryDequeue(out var next))
        {
            var plugin = _loaded.FirstOrDefault(p => p.Manifest.Id == next.Id);
            if (plugin is null)
            {
                continue;
            }

            _loaded.Remove(plugin);
            var index = _statuses.FindIndex(s => s.Id == next.Id);
            var refused = new PluginStatus(next.Id, plugin.Manifest.Version.Text, PluginState.Refused, next.Code, next.Message, null, null, BuiltInIds.Contains(next.Id), null);
            if (index < 0)
            {
                _statuses.Add(refused);
            }
            else
            {
                _statuses[index] = _statuses[index] with { State = PluginState.Refused, ReasonCode = next.Code, Message = next.Message };
            }

            _alerts.Add($"Plugin {next.Id} refused ({next.Code}): {next.Message}");
            failed.Add(next.Id);
            foreach (var dependant in _loaded.Where(p => p.Manifest.DependsOn.Any(d => d.Id == next.Id)))
            {
                pending.Enqueue((dependant.Manifest.Id, HostReasonCodes.Dependency, $"It depends on {next.Id}, which was refused."));
            }
        }

        return failed;
    }
}

/// <summary>
/// Verifies and loads plugins at startup, in the order of docs/plugin-security.md §4 to §8. It never
/// names a plugin: what loads is decided only by packages, signatures, records and settings.
/// </summary>
public sealed class PluginHost(
    PluginHostOptions options,
    PluginHostEnvironment environment,
    IFileSystemInspector? fileSystem,
    IPluginStateStore store,
    IPluginAuditSink audit,
    ILogger logger,
    TimeProvider time)
{
    /// <summary>The marker recorded as who enabled a built-in plugin.</summary>
    public const string BuiltInEnabler = "built-in";

    public async Task<PluginHostResult> LoadAsync(CancellationToken cancellationToken)
    {
        var result = new PluginHostResult();
        var records = new Dictionary<string, PluginRecord>(await store.LoadAsync(cancellationToken), StringComparer.Ordinal);
        result.EnabledIds.UnionWith(records.Values.Where(r => r.Enabled || r.BuiltIn).Select(r => r.Id));
        var packages = Directory.Exists(options.Directory)
            ? Directory.EnumerateFiles(options.Directory, "*" + PackageFormat.Extension).Order(StringComparer.Ordinal).ToList()
            : [];

        // §4, §5.7: conditions that stop every plugin.
        var (blockCode, blockMessage, trust) = CheckServer();
        if (blockCode is not null)
        {
            await BlockAllAsync(result, records.Values, packages, blockCode, blockMessage!, cancellationToken);
            return result;
        }

        var context = new VerificationContext(trust!, environment.IsDevelopment, environment.SdkVersion, environment.SharedAssemblies, time);
        var verified = await VerifyPackagesAsync(result, packages, context, records, cancellationToken);
        var candidates = await ReconcileAsync(result, verified, records, trust!, cancellationToken);
        var order = await ResolveDependenciesAsync(result, candidates, cancellationToken);
        await LoadCodeAsync(result, order, candidates, cancellationToken);

        foreach (var record in records.Values.Where(r => !result.Statuses.Any(s => s.Id == r.Id)))
        {
            result.Add(new PluginStatus(record.Id, record.Version, PluginState.Missing, null, "Recorded before, but no package is installed. Its data is kept.", record.PackageSha256, record.KeyId, record.BuiltIn, null));
        }

        RequireAllEnabledLoaded(result);
        return result;
    }

    /// <summary>
    /// Calls each loaded plugin's <see cref="IPlugin.Contribute"/> and checks what it declares, then its
    /// <see cref="IPlugin.ConfigureServices"/>. A plugin whose contributions are refused (<c>contribution_invalid</c>),
    /// or that throws (<c>load_failed</c>), is refused with its dependants; the registrations it made are removed.
    /// </summary>
    public async Task ConfigureServicesAsync(PluginHostResult result, IServiceCollection services, IConfiguration configuration, CancellationToken cancellationToken)
    {
        await ContributeAsync(result, configuration, cancellationToken);
        foreach (var plugin in result.Loaded.ToList())
        {
            if (!result.Loaded.Contains(plugin))
            {
                continue;
            }

            var before = services.Count;
            try
            {
                plugin.Instance.ConfigureServices(services, ContextFor(plugin, configuration));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                while (services.Count > before)
                {
                    services.RemoveAt(services.Count - 1);
                }

                logger.LogError(ex, "Plugin {PluginId} failed in ConfigureServices", plugin.Manifest.Id);
                foreach (var id in result.Fail(plugin.Manifest.Id, HostReasonCodes.LoadFailed, $"ConfigureServices threw {ex.GetType().Name}: {ex.Message}"))
                {
                    await audit.WriteAsync("plugin.refused", id, HostReasonCodes.LoadFailed, cancellationToken);
                }
            }
        }

        RequireAllEnabledLoaded(result);
    }

    /// <summary>
    /// Gives each loaded plugin its route group at <c>p/&lt;id&gt;/</c> under <paramref name="api"/>, core's <c>/api</c> group, so core's policies apply to it. Endpoints can't be unmapped,
    /// so a plugin that throws here stops the server from starting rather than leaving it half mapped.
    /// </summary>
    /// <param name="configureGroup">Lets core mark each plugin's group, before the plugin maps into it, with metadata a plugin can't make itself.</param>
    public static void MapEndpoints(PluginHostResult result, IEndpointRouteBuilder api, IConfiguration configuration, Action<RouteGroupBuilder, string>? configureGroup = null)
    {
        foreach (var plugin in result.Loaded)
        {
            var group = api.MapGroup($"/p/{plugin.Manifest.Id}");
            configureGroup?.Invoke(group, plugin.Manifest.Id);
            try
            {
                plugin.Instance.MapEndpoints(group, ContextFor(plugin, configuration));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new PluginHostException($"Plugin {plugin.Manifest.Id} failed while mapping its endpoints; the server won't start half configured.", ex);
            }
        }
    }

    /// <summary>In load order, so when two plugins contribute one ID, the one that loads first keeps it.</summary>
    private async Task ContributeAsync(PluginHostResult result, IConfiguration configuration, CancellationToken cancellationToken)
    {
        foreach (var plugin in result.Loaded.ToList())
        {
            if (!result.Loaded.Contains(plugin))
            {
                continue;
            }

            var id = plugin.Manifest.Id;
            var collected = new ContributionCollector();
            string? code = null, problem = null;
            try
            {
                plugin.Instance.Contribute(collected, ContextFor(plugin, configuration));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogError(ex, "Plugin {PluginId} failed in Contribute", id);
                (code, problem) = (HostReasonCodes.LoadFailed, $"Contribute threw {ex.GetType().Name}: {ex.Message}");
            }

            if (code is null)
            {
                var taken = result.ServiceTypes.ToDictionary(t => t.Type.Id, t => t.PluginId, StringComparer.Ordinal);
                foreach (var type in collected.ServiceTypes)
                {
                    problem = ContributionRules.Check(type)
                        ?? (taken.TryGetValue(type.Id, out var owner)
                            ? $"The service type '{type.Id}' is already contributed by plugin {owner}."
                            : null);
                    if (problem is not null)
                    {
                        code = HostReasonCodes.ContributionInvalid;
                        break;
                    }

                    taken[type.Id] = id;
                }
            }

            if (code is null)
            {
                var taken = result.SessionTypes.ToDictionary(t => t.Type.Id, t => t.PluginId, StringComparer.Ordinal);
                foreach (var type in collected.SessionTypes)
                {
                    problem = ContributionRules.Check(type)
                        ?? (taken.TryGetValue(type.Id, out var owner)
                            ? $"The session type '{type.Id}' is already contributed by plugin {owner}."
                            : null);
                    if (problem is not null)
                    {
                        code = HostReasonCodes.ContributionInvalid;
                        break;
                    }

                    taken[type.Id] = id;
                }
            }

            if (code is null)
            {
                result.AddServiceTypes(id, collected.ServiceTypes);
                result.AddSessionTypes(id, collected.SessionTypes);
                continue;
            }

            foreach (var refused in result.Fail(id, code, problem!))
            {
                await audit.WriteAsync("plugin.refused", refused, code, cancellationToken);
            }
        }
    }

    private static PluginContext ContextFor(LoadedPlugin plugin, IConfiguration configuration) =>
        new(plugin.Manifest.Id, plugin.Manifest.Version.Text, configuration.GetSection($"{PluginHostOptions.SectionName}:Settings:{plugin.Manifest.Id}"));

    private (string? Code, string? Message, TrustStore? Trust) CheckServer()
    {
        if (!environment.IsLinux && !environment.IsDevelopment)
        {
            return (HostReasonCodes.UnsupportedPlatform, "Plugins load only on Linux outside Development, where their location can be checked.", null);
        }

        if (environment.IsLinux)
        {
            var fs = fileSystem ?? throw new InvalidOperationException("A file system inspector is required on Linux.");
            var owner = environment.IsDevelopment && options.DevelopmentOwnerUid is { } uid ? uid : 0u;
            foreach (var directory in new[] { options.Directory, options.TrustDirectory }.Where(Directory.Exists))
            {
                if (LocationChecks.Check(directory, owner, fs) is { } problem)
                {
                    return (HostReasonCodes.UnsafeLocation, problem, null);
                }
            }
        }
        else
        {
            logger.LogWarning("Development on a non-Linux system: plugin directory ownership and mode checks are skipped. Signatures and hashes are still checked.");
        }

        try
        {
            return (null, null, TrustLoader.Load(environment.OfficialKeys, environment.EmbeddedRevocations, options.TrustDirectory, options.DevelopmentTrustDirectory, environment.IsDevelopment));
        }
        catch (TrustConfigurationException ex)
        {
            return (HostReasonCodes.BadTrustConfig, ex.Message, null);
        }
    }

    private async Task BlockAllAsync(PluginHostResult result, IEnumerable<PluginRecord> records, IEnumerable<string> packages, string code, string message, CancellationToken cancellationToken)
    {
        logger.LogCritical("No plugin will load ({ReasonCode}): {Message}", code, message);
        result.Alert($"No plugin loaded ({code}): {message}");
        await audit.WriteAsync("plugin.refused", "*", $"{code}: {message}", cancellationToken);

        var recorded = records.ToList();
        foreach (var record in recorded)
        {
            result.Add(new PluginStatus(record.Id, record.Version, PluginState.Refused, code, message, record.PackageSha256, record.KeyId, record.BuiltIn, null));
        }

        foreach (var package in packages.Select(Path.GetFileName).OfType<string>().Where(p => !recorded.Any(r => AttributeId(p, [r]) == r.Id)))
        {
            result.Add(new PluginStatus(package, null, PluginState.Refused, code, message, null, null, false, package));
        }

        if (options.RequireAllEnabledLoaded && result.EnabledIds.Count > 0)
        {
            throw new PluginHostException($"Plugins:RequireAllEnabledLoaded is set and no plugin can load ({code}): {message}");
        }
    }

    private async Task<List<PackageVerification>> VerifyPackagesAsync(PluginHostResult result, List<string> packages, VerificationContext context, Dictionary<string, PluginRecord> records, CancellationToken cancellationToken)
    {
        var verified = new List<PackageVerification>();
        foreach (var path in packages)
        {
            var fileName = Path.GetFileName(path);
            PackageVerification verification;
            if (new FileInfo(path).Length > context.Limits.MaxPackageBytes)
            {
                // Refused before reading it into memory.
                await RefuseAsync(result, AttributeId(fileName, records), null, ReasonCodes.TooLarge, "The package is larger than the limit.", null, null, fileName, cancellationToken);
                continue;
            }

            verification = PackageVerifier.Verify(await File.ReadAllBytesAsync(path, cancellationToken), fileName, context);
            if (!verification.IsVerified)
            {
                await RefuseAsync(result, AttributeId(fileName, records), null, verification.ReasonCode!, verification.Message, verification.PackageSha256, null, fileName, cancellationToken);
                continue;
            }

            verified.Add(verification);
        }

        // §5.7: two packages with one ID — neither is trusted to be the right one.
        var duplicates = verified.GroupBy(v => v.Manifest!.Id).Where(g => g.Count() > 1).ToList();
        foreach (var group in duplicates)
        {
            foreach (var v in group)
            {
                await RefuseAsync(result, v.Manifest!.Id, v.Manifest.Version.Text, HostReasonCodes.DuplicateId, $"{group.Count()} packages carry plugin ID '{group.Key}'.", v.PackageSha256, v.KeyId, PackageFormat.FileName(v.Manifest.Id, v.Manifest.Version.Text), cancellationToken);
                verified.Remove(v);
            }
        }

        return verified;
    }

    private async Task<Dictionary<string, PackageVerification>> ReconcileAsync(
        PluginHostResult result, List<PackageVerification> verified, Dictionary<string, PluginRecord> records, TrustStore trust, CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<string, PackageVerification>(StringComparer.Ordinal);
        var now = time.GetUtcNow();
        foreach (var v in verified)
        {
            var manifest = v.Manifest!;
            var version = manifest.Version.Text;
            var fileName = PackageFormat.FileName(manifest.Id, version);
            var kind = trust.Find(v.KeyId!)!.Kind;

            // §3: builtIn counts only from an official key, or a development key in Development.
            var builtIn = manifest.BuiltIn && (kind == TrustedKeyKind.Official || (kind == TrustedKeyKind.Development && environment.IsDevelopment));

            if (!records.TryGetValue(manifest.Id, out var record))
            {
                record = new PluginRecord(manifest.Id, version, v.PackageSha256!, v.KeyId!, builtIn, builtIn, builtIn ? BuiltInEnabler : null, builtIn ? now : null, now, now);
                await SaveAsync(records, record, cancellationToken);
                await audit.WriteAsync("plugin.discovered", manifest.Id, $"{version}, sha256 {v.PackageSha256}, key {v.KeyId}{(builtIn ? ", built in" : "")}", cancellationToken);
            }
            else
            {
                var comparison = manifest.Version.CompareTo(SemanticVersion.Parse(record.Version));
                if (comparison == 0 && v.PackageSha256 != record.PackageSha256)
                {
                    await RefuseAsync(result, manifest.Id, version, HostReasonCodes.PinMismatch, $"Version {version} was recorded with package {record.PackageSha256}; this package is {v.PackageSha256}.", v.PackageSha256, v.KeyId, fileName, cancellationToken);
                    continue;
                }

                if (comparison < 0)
                {
                    await RefuseAsync(result, manifest.Id, version, HostReasonCodes.Downgrade, $"Version {record.Version} is recorded; {version} is older.", v.PackageSha256, v.KeyId, fileName, cancellationToken);
                    continue;
                }

                if (comparison > 0 && v.KeyId != record.KeyId)
                {
                    result.Add(new PluginStatus(manifest.Id, version, PluginState.AwaitingSignerApproval, null, $"Signed by key {v.KeyId}; version {record.Version} was signed by {record.KeyId}. An administrator must approve the new signer.", v.PackageSha256, v.KeyId, builtIn, fileName));
                    result.Alert($"Plugin {manifest.Id} {version} is signed by a new key and awaits approval.");
                    await audit.WriteAsync("plugin.awaiting_signer", manifest.Id, $"{version}, key {v.KeyId} (was {record.KeyId})", cancellationToken);
                    continue;
                }

                if (comparison > 0)
                {
                    await audit.WriteAsync("plugin.updated", manifest.Id, $"{record.Version} -> {version}, sha256 {v.PackageSha256}", cancellationToken);
                }

                var updated = record with
                {
                    Version = version,
                    PackageSha256 = v.PackageSha256!,
                    BuiltIn = builtIn,
                    Enabled = record.Enabled || builtIn,
                    EnabledBy = record.Enabled ? record.EnabledBy : builtIn ? BuiltInEnabler : null,
                    EnabledAtUtc = record.Enabled ? record.EnabledAtUtc : builtIn ? now : null,
                    UpdatedAtUtc = now,
                };
                if (updated != record)
                {
                    record = updated;
                    await SaveAsync(records, record, cancellationToken);
                }
            }

            if (record.BuiltIn)
            {
                result.BuiltInIds.Add(manifest.Id);
            }

            if (record.Enabled)
            {
                result.EnabledIds.Add(manifest.Id);
            }

            if (!record.Enabled)
            {
                result.Add(new PluginStatus(manifest.Id, version, PluginState.Disabled, null, "Installed and verified, but not enabled.", v.PackageSha256, v.KeyId, builtIn, fileName));
                continue;
            }

            candidates[manifest.Id] = v;
        }

        return candidates;
    }

    /// <summary>§5.6: refuses plugins whose dependencies can't load, and orders the rest dependencies first.</summary>
    private async Task<List<string>> ResolveDependenciesAsync(PluginHostResult result, Dictionary<string, PackageVerification> candidates, CancellationToken cancellationToken)
    {
        var refused = new HashSet<string>(StringComparer.Ordinal);
        bool changed;
        do
        {
            changed = false;
            foreach (var (id, v) in candidates.Where(c => !refused.Contains(c.Key)))
            {
                var missing = v.Manifest!.DependsOn.FirstOrDefault(d =>
                    !candidates.TryGetValue(d.Id, out var dependency) || refused.Contains(d.Id) || !d.Version.Includes(dependency.Manifest!.Version));
                if (missing is not null)
                {
                    refused.Add(id);
                    changed = true;
                    await RefuseAsync(result, id, v.Manifest.Version.Text, HostReasonCodes.Dependency, $"It needs {missing.Id} {missing.Version}, which isn't installed, enabled and verified in that range.", v.PackageSha256, v.KeyId, null, cancellationToken);
                }
            }
        }
        while (changed);

        // Kahn's algorithm: whatever can't be ordered is in, or depends on, a cycle.
        var remaining = candidates.Keys.Where(id => !refused.Contains(id)).ToHashSet(StringComparer.Ordinal);
        var order = new List<string>();
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(id => candidates[id].Manifest!.DependsOn.All(d => !remaining.Contains(d.Id))).Order(StringComparer.Ordinal).ToList();
            if (ready.Count == 0)
            {
                break;
            }

            order.AddRange(ready);
            remaining.ExceptWith(ready);
        }

        foreach (var id in remaining.Order(StringComparer.Ordinal))
        {
            var v = candidates[id];
            var inCycle = Reaches(id, id, candidates, remaining, []);
            await RefuseAsync(result, id, v.Manifest!.Version.Text, inCycle ? HostReasonCodes.DependencyCycle : HostReasonCodes.Dependency,
                inCycle ? "It is part of a dependency cycle." : "It depends on a plugin in a dependency cycle.", v.PackageSha256, v.KeyId, null, cancellationToken);
        }

        return order;
    }

    private static bool Reaches(string from, string target, Dictionary<string, PackageVerification> candidates, HashSet<string> remaining, HashSet<string> seen)
    {
        foreach (var dependency in candidates[from].Manifest!.DependsOn.Select(d => d.Id).Where(remaining.Contains))
        {
            if (dependency == target || (seen.Add(dependency) && Reaches(dependency, target, candidates, remaining, seen)))
            {
                return true;
            }
        }

        return false;
    }

    private async Task LoadCodeAsync(PluginHostResult result, List<string> order, Dictionary<string, PackageVerification> candidates, CancellationToken cancellationToken)
    {
        var contexts = new Dictionary<string, PluginLoadContext>(StringComparer.Ordinal);
        foreach (var id in order)
        {
            var v = candidates[id];
            var manifest = v.Manifest!;
            var fileName = PackageFormat.FileName(id, manifest.Version.Text);
            var dependencies = manifest.DependsOn.Select(d => contexts.GetValueOrDefault(d.Id)).ToList();
            if (dependencies.Any(d => d is null))
            {
                await RefuseAsync(result, id, manifest.Version.Text, HostReasonCodes.Dependency, "A plugin it depends on failed to load.", v.PackageSha256, v.KeyId, fileName, cancellationToken);
                continue;
            }

            IPlugin instance;
            PluginLoadContext context;
            try
            {
                context = new PluginLoadContext(id, v.Files, manifest.Backend.ExportedAssemblies.ToHashSet(StringComparer.Ordinal), environment.SharedAssemblies, dependencies!);
                var entryName = PluginLoadContext.AssemblyNameOf(v.Files[manifest.Backend.EntryAssembly])
                    ?? throw new BadImageFormatException("The entry assembly isn't an assembly.");
                var type = context.LoadFromAssemblyName(new AssemblyName(entryName)).GetType(manifest.Backend.EntryType, throwOnError: false)
                    ?? throw new TypeLoadException($"Type '{manifest.Backend.EntryType}' isn't in {entryName}.");
                if (!typeof(IPlugin).IsAssignableFrom(type) || type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is null)
                {
                    throw new TypeLoadException($"Type '{type.FullName}' doesn't implement {nameof(IPlugin)} with a public parameterless constructor.");
                }

                instance = (IPlugin)Activator.CreateInstance(type)!;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                var detail = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
                logger.LogError(detail, "Plugin {PluginId} failed to load", id);
                await RefuseAsync(result, id, manifest.Version.Text, HostReasonCodes.LoadFailed, $"{detail.GetType().Name}: {detail.Message}", v.PackageSha256, v.KeyId, fileName, cancellationToken);
                continue;
            }

            contexts[id] = context;
            result.Add(new PluginStatus(id, manifest.Version.Text, PluginState.Loaded, null, v.Message, v.PackageSha256, v.KeyId, result.BuiltInIds.Contains(id), fileName));
            result.AddLoaded(new LoadedPlugin(manifest, instance, v.Files));
            await audit.WriteAsync("plugin.loaded", id, $"{manifest.Version}, sha256 {v.PackageSha256}, key {v.KeyId}", cancellationToken);
            logger.LogInformation("Loaded plugin {PluginId} {Version}", id, manifest.Version);
        }
    }

    /// <summary>
    /// §9: with the setting on, every enabled plugin must have loaded. Judged by ID rather than by refusal,
    /// because a tampered package is refused before its ID is known.
    /// </summary>
    private void RequireAllEnabledLoaded(PluginHostResult result)
    {
        if (!options.RequireAllEnabledLoaded)
        {
            return;
        }

        var loaded = result.Loaded.Select(p => p.Manifest.Id).ToHashSet(StringComparer.Ordinal);
        var notLoaded = result.EnabledIds.Where(id => !loaded.Contains(id)).Order(StringComparer.Ordinal).ToList();
        if (notLoaded.Count > 0)
        {
            throw new PluginHostException($"Plugins:RequireAllEnabledLoaded is set and enabled plugins didn't load: {string.Join(", ", notLoaded)}.");
        }
    }

    /// <summary>
    /// The plugin a refused file belongs to, for display only: a recorded plugin when the file is named
    /// exactly <c>&lt;id&gt;-&lt;version&gt;.nkp</c> for its ID, otherwise the file name. A refused package's own
    /// contents can't be trusted to say which plugin it is, but showing an enabled plugin as refused
    /// rather than missing is what an administrator needs to see.
    /// </summary>
    private static string AttributeId(string fileName, IEnumerable<PluginRecord> records)
    {
        foreach (var record in records.OrderByDescending(r => r.Id.Length))
        {
            var prefix = record.Id + "-";
            if (fileName.StartsWith(prefix, StringComparison.Ordinal)
                && fileName.EndsWith(PackageFormat.Extension, StringComparison.Ordinal)
                && SemanticVersion.TryParse(fileName[prefix.Length..^PackageFormat.Extension.Length], out _))
            {
                return record.Id;
            }
        }

        return fileName;
    }

    private static string AttributeId(string fileName, Dictionary<string, PluginRecord> records) => AttributeId(fileName, records.Values);

    private async Task SaveAsync(Dictionary<string, PluginRecord> records, PluginRecord record, CancellationToken cancellationToken)
    {
        records[record.Id] = record;
        await store.SaveAsync(record, cancellationToken);
    }

    private async Task RefuseAsync(PluginHostResult result, string id, string? version, string code, string message, string? sha256, string? keyId, string? fileName, CancellationToken cancellationToken)
    {
        logger.LogWarning("Plugin {PluginId} refused ({ReasonCode}): {Message}", id, code, message);
        result.Add(new PluginStatus(id, version, PluginState.Refused, code, message, sha256, keyId, false, fileName));
        result.Alert($"Plugin {id} refused ({code}): {message}");
        await audit.WriteAsync("plugin.refused", id, $"{code}: {message}", cancellationToken);
    }
}
