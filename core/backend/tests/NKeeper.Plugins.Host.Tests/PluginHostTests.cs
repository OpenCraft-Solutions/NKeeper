using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Plugins.Packaging;
using NKeeper.Testing;

namespace NKeeper.Plugins.Host.Tests;

/// <summary>docs/plugin-security.md §5.6 to §8: what loads, what doesn't, and why.</summary>
public sealed class PluginHostTests : IDisposable
{
    private readonly HostScenario _scenario = new();

    public void Dispose() => _scenario.Dispose();

    // ---- First sight and built-in plugins ----

    [Fact]
    public async Task An_official_built_in_plugin_loads_and_is_recorded_enabled()
    {
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey));

        var result = await _scenario.LoadAsync();

        var status = Assert.Single(result.Statuses);
        Assert.Equal(PluginState.Loaded, status.State);
        Assert.True(status.BuiltIn);
        Assert.Equal("NKeeper.TestPlugins.Hello.HelloPlugin", Assert.Single(result.Loaded).Instance.GetType().FullName);
        var record = _scenario.Store.Records["hello"];
        Assert.True(record.Enabled);
        Assert.Equal(PluginHost.BuiltInEnabler, record.EnabledBy);
        Assert.Equal(["plugin.discovered", "plugin.loaded"], _scenario.Audit.Events.Select(e => e.Action));
    }

    [Fact]
    public async Task A_plugin_loads_its_code_in_its_own_context()
    {
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey));

        var result = await _scenario.LoadAsync();

        var type = Assert.Single(result.Loaded).Instance.GetType();
        Assert.Equal("plugin:hello", System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(type.Assembly)!.Name);
    }

    [Fact]
    public async Task A_plugin_that_isnt_built_in_starts_disabled()
    {
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey, builtIn: false));

        var result = await _scenario.LoadAsync();

        Assert.Equal(PluginState.Disabled, Assert.Single(result.Statuses).State);
        Assert.Empty(result.Loaded);
        Assert.False(_scenario.Store.Records["hello"].Enabled);
    }

    [Fact]
    public async Task A_third_party_plugin_cant_make_itself_built_in()
    {
        _scenario.Install(TestPlugins.Package(_scenario.ThirdPartyKey, id: "com.example.hello", builtIn: true));

        var result = await _scenario.LoadAsync();

        var status = Assert.Single(result.Statuses);
        Assert.Equal(PluginState.Disabled, status.State);
        Assert.False(status.BuiltIn);
        Assert.False(_scenario.Store.Records["com.example.hello"].BuiltIn);
    }

    [Fact]
    public async Task A_development_key_counts_as_built_in_in_Development()
    {
        _scenario.IsDevelopment = true;
        _scenario.Install(TestPlugins.Package(_scenario.DevelopmentKey));

        var result = await _scenario.LoadAsync();

        Assert.Equal(PluginState.Loaded, Assert.Single(result.Statuses).State);
    }

    [Fact]
    public async Task Development_keys_are_ignored_outside_Development()
    {
        _scenario.Install(TestPlugins.Package(_scenario.DevelopmentKey));

        var result = await _scenario.LoadAsync();

        var status = Assert.Single(result.Statuses);
        Assert.Equal(PluginState.Refused, status.State);
        Assert.Equal(ReasonCodes.UntrustedKey, status.ReasonCode);
    }

    [Fact]
    public async Task An_enabled_plugin_loads_on_later_starts()
    {
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey, builtIn: false));
        await _scenario.LoadAsync();
        _scenario.Store.Records["hello"] = _scenario.Store.Records["hello"] with { Enabled = true, EnabledBy = "admin" };

        var result = await _scenario.LoadAsync();

        Assert.Equal(PluginState.Loaded, Assert.Single(result.Statuses).State);
    }

    // ---- Pinning (§8) ----

    [Fact]
    public async Task The_same_version_with_a_different_package_is_refused_as_tampering()
    {
        var path = _scenario.Install(TestPlugins.Package(_scenario.OfficialKey));
        await _scenario.LoadAsync();
        File.Delete(path);
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey)); // Re-signed: a different signature, so a different hash.

        var result = await _scenario.LoadAsync();

        AssertRefused(result, "hello", HostReasonCodes.PinMismatch);
    }

    [Fact]
    public async Task A_newer_version_from_the_same_signer_is_an_update()
    {
        var path = _scenario.Install(TestPlugins.Package(_scenario.OfficialKey));
        await _scenario.LoadAsync();
        File.Delete(path);
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey, version: "1.1.0"));

        var result = await _scenario.LoadAsync();

        Assert.Equal(PluginState.Loaded, Assert.Single(result.Statuses).State);
        Assert.Equal("1.1.0", _scenario.Store.Records["hello"].Version);
        Assert.Contains(_scenario.Audit.Events, e => e is ("plugin.updated", "hello", _));
    }

    [Fact]
    public async Task A_reinstall_signed_by_a_new_install_key_updates_the_record_and_an_earlier_key_stops_verifying()
    {
        using var first = PluginSigningKey.Generate();
        using var second = PluginSigningKey.Generate();
        var trustFile = Path.Combine(_scenario.Options.TrustDirectory, "install.json");
        File.WriteAllBytes(trustFile, TrustedKey.Install(first.PublicKey.Span, ["hello"]).ToKeyFile());
        var path = _scenario.Install(TestPlugins.Package(first));

        var installed = await _scenario.LoadAsync();
        Assert.Equal(PluginState.Loaded, Assert.Single(installed.Statuses).State);
        Assert.True(_scenario.Store.Records["hello"].BuiltIn);

        // The installer's next run: a new key, the same version rebuilt.
        File.WriteAllBytes(trustFile, TrustedKey.Install(second.PublicKey.Span, ["hello"]).ToKeyFile());
        File.Delete(path);
        path = _scenario.Install(TestPlugins.Package(second));
        var reinstalled = await _scenario.LoadAsync();
        Assert.Equal(PluginState.Loaded, Assert.Single(reinstalled.Statuses).State);
        Assert.Equal(second.KeyId, _scenario.Store.Records["hello"].KeyId);
        Assert.Contains(_scenario.Audit.Events, e => e is ("plugin.updated", "hello", _));

        // A package signed with the earlier install key no longer verifies.
        File.Delete(path);
        path = _scenario.Install(TestPlugins.Package(first));
        AssertRefused(await _scenario.LoadAsync(), "hello", ReasonCodes.UntrustedKey);

        // Nor does a downgrade, even from the current install key.
        File.Delete(path);
        _scenario.Install(TestPlugins.Package(second, version: "0.9.0"));
        AssertRefused(await _scenario.LoadAsync(), "hello", HostReasonCodes.Downgrade);
    }

    [Fact]
    public async Task A_newer_version_from_a_new_signer_awaits_approval()
    {
        _scenario.IsDevelopment = true;
        var path = _scenario.Install(TestPlugins.Package(_scenario.OfficialKey));
        await _scenario.LoadAsync();
        File.Delete(path);
        _scenario.Install(TestPlugins.Package(_scenario.DevelopmentKey, version: "1.1.0"));

        var result = await _scenario.LoadAsync();

        Assert.Equal(PluginState.AwaitingSignerApproval, Assert.Single(result.Statuses).State);
        Assert.Empty(result.Loaded);
        Assert.Equal("1.0.0", _scenario.Store.Records["hello"].Version);
    }

    [Fact]
    public async Task An_older_version_is_refused()
    {
        var path = _scenario.Install(TestPlugins.Package(_scenario.OfficialKey, version: "2.0.0"));
        await _scenario.LoadAsync();
        File.Delete(path);
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey, version: "1.0.0"));

        AssertRefused(await _scenario.LoadAsync(), "hello", HostReasonCodes.Downgrade);
    }

    [Fact]
    public async Task A_recorded_plugin_without_a_package_is_missing()
    {
        var path = _scenario.Install(TestPlugins.Package(_scenario.OfficialKey));
        await _scenario.LoadAsync();
        File.Delete(path);

        var result = await _scenario.LoadAsync();

        Assert.Equal(PluginState.Missing, Assert.Single(result.Statuses).State);
        Assert.True(_scenario.Store.Records.ContainsKey("hello"));
    }

    // ---- Refusals by the host (§5.7) ----

    [Fact]
    public async Task Two_packages_with_one_id_are_both_refused()
    {
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey, version: "1.0.0"));
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey, version: "1.1.0"));

        var result = await _scenario.LoadAsync();

        Assert.Equal(2, result.Statuses.Count(s => s.ReasonCode == HostReasonCodes.DuplicateId));
        Assert.Empty(result.Loaded);
    }

    [Fact]
    public async Task A_tampered_package_is_refused_by_the_verifier()
    {
        var path = _scenario.Install(TestPlugins.Package(_scenario.OfficialKey));
        var bytes = File.ReadAllBytes(path);
        bytes[^30] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        var result = await _scenario.LoadAsync();

        Assert.Equal(PluginState.Refused, Assert.Single(result.Statuses).State);
        Assert.Contains(_scenario.Audit.Events, e => e.Action == "plugin.refused");
        Assert.NotEmpty(result.Alerts);
    }

    [Fact]
    public async Task A_tampered_enabled_plugin_shows_as_refused_not_missing()
    {
        var path = _scenario.Install(TestPlugins.Package(_scenario.OfficialKey));
        await _scenario.LoadAsync();
        var bytes = File.ReadAllBytes(path);
        bytes[^30] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        var result = await _scenario.LoadAsync();

        var status = Assert.Single(result.Statuses);
        Assert.Equal(("hello", PluginState.Refused, "hello-1.0.0.nkp"), (status.Id, status.State, status.FileName));
    }

    [Fact]
    public async Task No_plugin_loads_outside_Development_on_other_systems()
    {
        _scenario.IsLinux = false;
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey));

        AssertRefused(await _scenario.LoadAsync(), "hello-1.0.0.nkp", HostReasonCodes.UnsupportedPlatform);
    }

    [Fact]
    public async Task An_unsafe_plugin_directory_stops_every_plugin()
    {
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey));
        _scenario.FileSystem.Overrides[Path.GetFullPath(_scenario.Options.Directory)] = new FileStatus(0, 0x41FF); // drwxrwxrwx

        var result = await _scenario.LoadAsync();

        AssertRefused(result, "hello-1.0.0.nkp", HostReasonCodes.UnsafeLocation);
        Assert.Empty(result.Loaded);
    }

    [Fact]
    public async Task An_unsafe_trust_directory_stops_every_plugin()
    {
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey));
        var keyFile = Path.GetFullPath(Path.Combine(_scenario.Options.TrustDirectory, "acme.json"));
        _scenario.FileSystem.Overrides[keyFile] = new FileStatus(1000, 0x81A4); // owned by an ordinary user

        AssertRefused(await _scenario.LoadAsync(), "hello-1.0.0.nkp", HostReasonCodes.UnsafeLocation);
    }

    [Theory]
    [InlineData("broken.json", "{ not json")]
    [InlineData("revoked.json", """{ "revokedKeyIds": ["not-a-key"] }""")]
    public async Task Unusable_trust_configuration_stops_every_plugin(string file, string content)
    {
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey));
        File.WriteAllText(Path.Combine(_scenario.Options.TrustDirectory, file), content);

        AssertRefused(await _scenario.LoadAsync(), "hello-1.0.0.nkp", HostReasonCodes.BadTrustConfig);
    }

    [Fact]
    public async Task A_development_key_in_the_production_trust_directory_is_unusable_configuration()
    {
        File.WriteAllBytes(Path.Combine(_scenario.Options.TrustDirectory, "dev.json"), TrustedKey.Development(_scenario.DevelopmentKey.PublicKey.Span).ToKeyFile());
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey));

        AssertRefused(await _scenario.LoadAsync(), "hello-1.0.0.nkp", HostReasonCodes.BadTrustConfig);
    }

    [Fact]
    public async Task A_local_revocation_applies_to_official_keys()
    {
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey));
        File.WriteAllText(Path.Combine(_scenario.Options.TrustDirectory, "revoked.json"), $$"""{ "revokedKeyIds": ["{{_scenario.OfficialKey.KeyId}}"] }""");

        AssertRefused(await _scenario.LoadAsync(), "hello-1.0.0.nkp", ReasonCodes.RevokedKey);
    }

    // ---- Loading code ----

    [Theory]
    [InlineData("NKeeper.TestPlugins.Hello.DoesNotExist")]
    [InlineData(TestPlugins.NotAPluginType)]
    [InlineData(TestPlugins.ThrowingConstructorType)]
    public async Task Code_that_cant_be_loaded_is_refused(string entryType)
    {
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey, entryType: entryType));

        AssertRefused(await _scenario.LoadAsync(), "hello", HostReasonCodes.LoadFailed);
    }

    [Fact]
    public async Task A_plugin_that_fails_to_configure_is_refused_and_its_registrations_removed()
    {
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey, entryType: TestPlugins.ThrowingConfigureType));
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey, id: "hello-dependant", dependsOn: ("hello", ">=1.0.0")));
        var host = _scenario.Host();
        var result = await host.LoadAsync(TestContext.Current.CancellationToken);
        var services = new ServiceCollection();

        await host.ConfigureServicesAsync(result, services, new ConfigurationBuilder().Build(), TestContext.Current.CancellationToken);

        Assert.Empty(services);
        Assert.Empty(result.Loaded);
        AssertRefused(result, "hello", HostReasonCodes.LoadFailed);
        AssertRefused(result, "hello-dependant", HostReasonCodes.Dependency);
    }

    // ---- Contributions (architecture.md §7.4) ----

    [Fact]
    public async Task The_service_types_of_loaded_plugins_are_collected()
    {
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey));

        var (result, _) = await ConfigureAsync();

        var type = Assert.Single(result.ServiceTypes);
        Assert.Equal(("hello", "hello", 7, 2), (type.PluginId, type.Type.Id, type.Type.DefaultPort, type.Type.SettingsVersion));
    }

    [Fact]
    public async Task A_plugin_contributing_a_taken_service_type_is_refused_with_its_dependants()
    {
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey));
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey, id: "copycat", entryType: TestPlugins.DuplicateServiceTypeType, dependsOn: ("hello", ">=1.0.0")));
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey, id: "copycat-dependant", dependsOn: ("copycat", ">=1.0.0")));

        var (result, services) = await ConfigureAsync();

        AssertRefused(result, "copycat", HostReasonCodes.ContributionInvalid);
        AssertRefused(result, "copycat-dependant", HostReasonCodes.Dependency);
        Assert.Equal(["hello"], result.ServiceTypes.Select(t => t.PluginId));
        Assert.DoesNotContain(services, s => s.ImplementationInstance?.GetType().GetProperty("Text")?.GetValue(s.ImplementationInstance) is "must not be registered");
        Assert.Contains(_scenario.Audit.Events, e => e.Action == "plugin.refused" && e.Target == "copycat" && e.Detail == HostReasonCodes.ContributionInvalid);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("label")]
    [InlineData("port")]
    [InlineData("version")]
    [InlineData("key")]
    [InlineData("twice")]
    [InlineData("length")]
    [InlineData("range")]
    [InlineData("choices")]
    [InlineData("null-settings")]
    [InlineData("id-newline")]
    [InlineData("key-newline")]
    public async Task A_plugin_contributing_an_invalid_service_type_is_refused_and_registers_nothing(string rule)
    {
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey, id: "broken", entryType: TestPlugins.InvalidServiceTypeType));

        var (result, services) = await ConfigureAsync(("Plugins:Settings:broken:Break", rule));

        AssertRefused(result, "broken", HostReasonCodes.ContributionInvalid);
        Assert.Empty(result.ServiceTypes);
        Assert.Empty(services);
    }

    [Theory]
    [InlineData("session-id")]
    [InlineData("session-label")]
    [InlineData("session-handler")]
    [InlineData("session-interface")]
    [InlineData("session-none")]
    [InlineData("session-service")]
    public async Task A_plugin_contributing_an_invalid_session_type_is_refused_and_registers_nothing(string rule)
    {
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey, id: "broken", entryType: TestPlugins.InvalidServiceTypeType));

        var (result, services) = await ConfigureAsync(("Plugins:Settings:broken:Break", rule));

        AssertRefused(result, "broken", HostReasonCodes.ContributionInvalid);
        Assert.Empty(result.ServiceTypes);
        Assert.Empty(result.SessionTypes);
        Assert.Empty(services);
    }

    [Fact]
    public async Task A_plugin_contributing_a_taken_session_type_is_refused()
    {
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey));
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey, id: "broken", entryType: TestPlugins.InvalidServiceTypeType, dependsOn: ("hello", ">=1.0.0")));

        var (result, _) = await ConfigureAsync(("Plugins:Settings:broken:Break", "session-taken"));

        AssertRefused(result, "broken", HostReasonCodes.ContributionInvalid);
        Assert.Equal([("hello", "hello")], result.SessionTypes.Select(t => (t.PluginId, t.Type.Id)));
    }

    [Theory]
    [InlineData("action-id")]
    [InlineData("action-label")]
    [InlineData("action-session")]
    [InlineData("action-credential")]
    [InlineData("action-both")]
    [InlineData("action-neither")]
    [InlineData("action-endpoint")]
    [InlineData("action-run-services")]
    [InlineData("action-session-services")]
    public async Task A_plugin_contributing_an_invalid_host_action_is_refused_and_registers_nothing(string rule)
    {
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey, id: "broken", entryType: TestPlugins.InvalidServiceTypeType));

        var (result, services) = await ConfigureAsync(("Plugins:Settings:broken:Break", rule));

        AssertRefused(result, "broken", HostReasonCodes.ContributionInvalid);
        Assert.Empty(result.ServiceTypes);
        Assert.Empty(result.SessionTypes);
        Assert.Empty(result.HostActions);
        Assert.Empty(services);
    }

    [Fact]
    public async Task A_host_action_ID_taken_by_an_earlier_plugin_is_refused()
    {
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey));
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey, id: "broken", entryType: TestPlugins.InvalidServiceTypeType, dependsOn: ("hello", ">=1.0.0")));

        var (result, _) = await ConfigureAsync(("Plugins:Settings:broken:Break", "action-taken"));

        AssertRefused(result, "broken", HostReasonCodes.ContributionInvalid);
        Assert.Equal([("hello", "hello.open", "hello")], result.HostActions.Select(a => (a.PluginId, a.Action.Id, a.Action.SessionType)));
    }

    [Fact]
    public async Task A_plugin_whose_contribute_throws_is_refused_and_registers_nothing()
    {
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey, entryType: TestPlugins.ThrowingContributeType));

        var (result, services) = await ConfigureAsync();

        AssertRefused(result, "hello", HostReasonCodes.LoadFailed);
        Assert.Empty(services);
    }

    private async Task<(PluginHostResult Result, ServiceCollection Services)> ConfigureAsync(params (string Key, string Value)[] settings)
    {
        var host = _scenario.Host();
        var result = await host.LoadAsync(TestContext.Current.CancellationToken);
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        await host.ConfigureServicesAsync(result, services, configuration, TestContext.Current.CancellationToken);
        return (result, services);
    }

    // ---- Dependencies (§5.6) ----

    [Fact]
    public async Task Dependencies_load_first()
    {
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey, id: "a-dependant", dependsOn: ("z-base", ">=1.0.0 <2.0.0")));
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey, id: "z-base"));

        var result = await _scenario.LoadAsync();

        Assert.Equal(["z-base", "a-dependant"], result.Loaded.Select(p => p.Manifest.Id));
    }

    [Theory]
    [InlineData("missing", ">=1.0.0")]
    [InlineData("base", ">=2.0.0")]
    public async Task A_missing_or_out_of_range_dependency_is_refused(string dependency, string range)
    {
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey, id: "base"));
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey, id: "dependant", dependsOn: (dependency, range)));

        var result = await _scenario.LoadAsync();

        AssertRefused(result, "dependant", HostReasonCodes.Dependency);
        Assert.Equal(["base"], result.Loaded.Select(p => p.Manifest.Id));
    }

    [Fact]
    public async Task A_disabled_dependency_refuses_its_dependant()
    {
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey, id: "base", builtIn: false));
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey, id: "dependant", dependsOn: ("base", ">=1.0.0")));

        AssertRefused(await _scenario.LoadAsync(), "dependant", HostReasonCodes.Dependency);
    }

    [Fact]
    public async Task A_dependency_cycle_is_refused()
    {
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey, id: "first", dependsOn: ("second", ">=1.0.0")));
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey, id: "second", dependsOn: ("first", ">=1.0.0")));
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey, id: "third", dependsOn: ("first", ">=1.0.0")));

        var result = await _scenario.LoadAsync();

        AssertRefused(result, "first", HostReasonCodes.DependencyCycle);
        AssertRefused(result, "second", HostReasonCodes.DependencyCycle);
        AssertRefused(result, "third", HostReasonCodes.Dependency);
    }

    // ---- RequireAllEnabledLoaded (§9) ----

    [Fact]
    public async Task With_RequireAllEnabledLoaded_a_tampered_enabled_plugin_stops_startup()
    {
        var path = _scenario.Install(TestPlugins.Package(_scenario.OfficialKey));
        await _scenario.LoadAsync();
        var bytes = File.ReadAllBytes(path);
        bytes[^30] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
        _scenario.Options.RequireAllEnabledLoaded = true;

        await Assert.ThrowsAsync<PluginHostException>(_scenario.LoadAsync);
    }

    [Fact]
    public async Task With_RequireAllEnabledLoaded_a_disabled_refused_plugin_doesnt_stop_startup()
    {
        File.WriteAllBytes(Path.Combine(_scenario.Options.Directory, "stray-1.0.0.nkp"), [1, 2, 3]);
        _scenario.Options.RequireAllEnabledLoaded = true;

        var result = await _scenario.LoadAsync();

        Assert.Equal(PluginState.Refused, Assert.Single(result.Statuses).State);
    }

    private static void AssertRefused(PluginHostResult result, string id, string reasonCode)
    {
        var status = result.Statuses.FirstOrDefault(s => s.Id == id);
        Assert.True(status is not null, $"No status for '{id}': {string.Join("; ", result.Statuses)}");
        Assert.True(status.State == PluginState.Refused && status.ReasonCode == reasonCode, $"Expected {id} refused ({reasonCode}), got {status}");
        Assert.DoesNotContain(result.Loaded, p => p.Manifest.Id == id);
    }
}
