using System.Net;
using System.Security.Cryptography;
using System.Text;
using NKeeper.Abstractions;
using NKeeper.Plugins.Host.Store;
using NKeeper.Plugins.Packaging;
using NKeeper.Testing;

namespace NKeeper.Plugins.Host.Tests;

/// <summary>The plugin catalog (docs/architecture.md §9.1): reading it, finding installable releases, and root's installs.</summary>
public sealed class PluginStoreTests : IDisposable
{
    private static readonly Uri CatalogUrl = new("https://catalog.test/catalog.json");

    private readonly HostScenario _scenario = new();
    private readonly FakeReleases _releases = new();
    private readonly string _requests = Directory.CreateTempSubdirectory("nkeeper-requests-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _scenario.Dispose();
        Directory.Delete(_requests, recursive: true);
    }

    [Fact]
    public void A_catalog_lists_plugins_by_repository_and_skips_entries_it_cant_read()
    {
        var catalog = Catalog.Parse("""
            { "formatVersion": 1, "future": true, "plugins": [
              { "id": "kvm", "repository": "OpenCraft-Solutions/NKeeper-Plugin-KVM", "summary": "KVM\nguests", "tags": ["x"] },
              { "id": "Bad Id", "repository": "a/b" },
              { "id": "zabbix", "repository": "https://github.com/a/b" },
              { "id": "kvm", "repository": "someone/else" },
              "nonsense"
            ] }
            """u8);

        var entry = Assert.Single(catalog.Entries);
        Assert.Equal(new CatalogEntry("kvm", "OpenCraft-Solutions/NKeeper-Plugin-KVM", "KVM guests"), entry);
        Assert.Equal(4, catalog.Warnings.Count);
        Assert.Throws<CatalogException>(() => Catalog.Parse("""{ "formatVersion": 2, "plugins": [] }"""u8));
        Assert.Throws<CatalogException>(() => Catalog.Parse("not json"u8));
    }

    [Fact]
    public void GitHubs_releases_are_read_newest_first_without_drafts_prereleases_or_releases_lacking_the_package()
    {
        var json = """
            [
              { "tag_name": "v1.0.0", "draft": false, "prerelease": false, "assets": [
                { "name": "kvm-1.0.0.nkp", "browser_download_url": "https://dl.test/kvm-1.0.0.nkp" },
                { "name": "kvm-1.0.0.nkp.sha256", "browser_download_url": "https://dl.test/kvm-1.0.0.nkp.sha256" } ] },
              { "tag_name": "v1.2.0", "draft": false, "prerelease": false, "assets": [
                { "name": "kvm-1.2.0.nkp", "browser_download_url": "https://dl.test/kvm-1.2.0.nkp" } ] },
              { "tag_name": "v1.3.0", "draft": true, "assets": [ { "name": "kvm-1.3.0.nkp", "browser_download_url": "https://dl.test/a" } ] },
              { "tag_name": "v1.4.0", "prerelease": true, "assets": [ { "name": "kvm-1.4.0.nkp", "browser_download_url": "https://dl.test/b" } ] },
              { "tag_name": "v1.5.0", "assets": [ { "name": "other-1.5.0.nkp", "browser_download_url": "https://dl.test/c" } ] },
              { "tag_name": "v1.6.0", "assets": [ { "name": "kvm-1.6.0.nkp", "browser_download_url": "http://dl.test/d" } ] },
              { "tag_name": "latest", "assets": [] }
            ]
            """u8;

        var releases = GitHubReleases.ParseReleases("kvm", json);

        Assert.Equal(["1.2.0", "1.0.0"], releases.Select(r => r.Version.Text));
        Assert.Null(releases[0].Sha256);
        Assert.Equal(new Uri("https://dl.test/kvm-1.0.0.nkp.sha256"), releases[1].Sha256);
    }

    [Fact]
    public void A_release_feed_is_read_newest_first_without_prereleases_or_other_repositories_tags()
    {
        var atom = """
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry><link rel="alternate" type="text/html" href="https://github.com/o/kvm/releases/tag/v1.0.0"/><title>kvm 1.0.0</title></entry>
              <entry><link rel="alternate" type="text/html" href="https://github.com/o/kvm/releases/tag/v1.2.0"/><title>kvm 1.2.0</title></entry>
              <entry><link rel="alternate" type="text/html" href="https://github.com/o/kvm/releases/tag/v1.3.0-beta.1"/></entry>
              <entry><link rel="alternate" type="text/html" href="https://github.com/elsewhere/kvm/releases/tag/v9.0.0"/></entry>
              <entry><link rel="alternate" type="text/html" href="https://github.com/o/kvm/releases/tag/latest"/></entry>
            </feed>
            """u8.ToArray();

        var releases = GitHubReleases.ParseFeed("kvm", "o/kvm", atom);

        Assert.Equal(["1.2.0", "1.0.0"], releases.Select(r => r.Version.Text));
        Assert.Equal(new Uri("https://github.com/o/kvm/releases/download/v1.2.0/kvm-1.2.0.nkp"), releases[0].Package);
        Assert.Equal(new Uri("https://github.com/o/kvm/releases/download/v1.2.0/kvm-1.2.0.nkp.sha256"), releases[0].Sha256);
        Assert.Throws<ReleaseSourceException>(() => GitHubReleases.ParseFeed("kvm", "o/kvm", "<!DOCTYPE x [<!ENTITY e 'x'>]><feed/>"u8.ToArray()));
    }

    [Fact]
    public async Task When_the_API_allowance_is_used_up_the_release_feed_is_read_instead()
    {
        using var http = new HttpClient(new RateLimitedGitHub());
        var releases = await new GitHubReleases(http).ListAsync(new CatalogEntry("kvm", "o/kvm", null), Ct);

        Assert.Equal(["1.1.0"], releases.Select(r => r.Version.Text));
    }

    /// <summary>GitHub with this address's API allowance used up: the API answers 403, the release feed still answers.</summary>
    private sealed class RateLimitedGitHub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(request.RequestUri!.Host == "api.github.com"
                ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{ "message": "API rate limit exceeded" }""") }
                : request.RequestUri.AbsoluteUri == "https://github.com/o/kvm/releases.atom"
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""<feed xmlns="http://www.w3.org/2005/Atom"><entry><link href="https://github.com/o/kvm/releases/tag/v1.1.0"/></entry></feed>"""),
                    }
                    : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    [Fact]
    public async Task A_refresh_offers_the_newest_verified_release_newer_than_the_installed_one()
    {
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey, version: "1.0.0"));
        Publish(TestPlugins.Package(_scenario.OfficialKey, version: "1.1.0"));
        Publish(TestPlugins.Package(_scenario.OfficialKey, version: "1.0.0"));
        PublishCatalog(("hello", "o/hello"));

        var status = Assert.Single(await Store().UpdateAsync(Ct));

        Assert.Equal("1.0.0", status.InstalledVersion);
        Assert.Equal("1.1.0", status.Available?.Version);
        Assert.Null(status.ReasonCode);
        Assert.True(status.Available!.BuiltIn);
    }

    [Fact]
    public async Task A_release_for_a_newer_SDK_falls_back_to_the_newest_one_this_server_loads()
    {
        Publish(TestPlugins.Package(_scenario.OfficialKey, version: "2.0.0", sdk: ">=99.0.0"));
        Publish(TestPlugins.Package(_scenario.OfficialKey, version: "1.5.0"));
        PublishCatalog(("hello", "o/hello"));

        Assert.Equal("1.5.0", Assert.Single(await Store().UpdateAsync(Ct)).Available?.Version);
    }

    [Fact]
    public async Task Packages_that_wouldnt_load_or_dont_match_their_release_are_reported_not_offered()
    {
        Publish(TestPlugins.Package(_scenario.ThirdPartyKey, id: "hello", version: "1.0.0")); // an official ID, signed by a third party
        Publish(TestPlugins.Package(_scenario.ThirdPartyKey, id: "com.example.hello", version: "1.0.0", builtIn: false));
        var other = TestPlugins.Package(_scenario.OfficialKey, id: "other", version: "1.0.0");
        _releases.Add("o/wrong", new PluginRelease(SemanticVersion.Parse("1.0.0"), new Uri("https://dl.test/o/wrong.nkp"), "wrong-1.0.0.nkp", null), other.Content);
        Publish(TestPlugins.Package(_scenario.OfficialKey, id: "badsum", version: "1.0.0"), checksum: new string('0', 64));
        PublishCatalog(("hello", "o/hello"), ("com.example.hello", "o/com.example.hello"), ("wrong", "o/wrong"), ("badsum", "o/badsum"), ("gone", "o/gone"));

        var statuses = (await Store().UpdateAsync(Ct)).ToDictionary(s => s.Id);

        Assert.Equal(ReasonCodes.KeyScope, statuses["hello"].ReasonCode);
        Assert.Equal("1.0.0", statuses["com.example.hello"].Available?.Version);
        Assert.NotNull(statuses["wrong"].ReasonCode);
        Assert.Equal(CatalogReasonCodes.ChecksumMismatch, statuses["badsum"].ReasonCode);
        Assert.Equal(CatalogReasonCodes.DownloadFailed, statuses["gone"].ReasonCode);
        Assert.All(statuses.Values.Where(s => s.ReasonCode is not null), s => Assert.Null(s.Available));
    }

    [Fact]
    public async Task Installing_brings_the_plugins_it_depends_on_and_upgrading_replaces_the_older_package()
    {
        Publish(TestPlugins.Package(_scenario.OfficialKey, id: "virt-core", version: "1.0.0"));
        Publish(TestPlugins.Package(_scenario.OfficialKey, id: "kvm", version: "1.0.0", dependsOn: [("virt-core", ">=1.0.0 <2.0.0")]));
        PublishCatalog(("kvm", "o/kvm"), ("virt-core", "o/virt-core"));

        var outcomes = await Store().InstallAsync(["kvm"], Ct);

        Assert.Equal([("virt-core", InstallResult.Installed), ("kvm", InstallResult.Installed)], outcomes.Select(o => (o.Id, o.Result)));
        Assert.True(File.Exists(Path.Combine(_scenario.Options.Directory, "kvm-1.0.0.nkp")));
        Assert.Equal(InstallResult.UpToDate, Assert.Single(await Store().UpgradeAsync([], Ct), o => o.Id == "kvm").Result);

        Publish(TestPlugins.Package(_scenario.OfficialKey, id: "kvm", version: "1.1.0", dependsOn: [("virt-core", ">=1.0.0 <2.0.0")]));
        var upgraded = Assert.Single(await Store().UpgradeAsync(["kvm"], Ct));

        Assert.Equal(InstallResult.Upgraded, upgraded.Result);
        Assert.Equal(["kvm-1.1.0.nkp", "virt-core-1.0.0.nkp"], Directory.GetFiles(_scenario.Options.Directory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        var loaded = await _scenario.LoadAsync();
        Assert.Contains(loaded.Statuses, s => s.Id == "kvm" && s.Version == "1.1.0" && s.State == PluginState.Loaded);
    }

    [Fact]
    public async Task A_plugin_outside_the_catalog_or_with_nothing_installable_isnt_installed()
    {
        Publish(TestPlugins.Package(_scenario.OfficialKey, id: "kvm", version: "1.0.0", dependsOn: [("virt-core", ">=1.0.0 <2.0.0")]));
        PublishCatalog(("kvm", "o/kvm"));

        var outcomes = await Store().InstallAsync(["kvm", "zabbix"], Ct);

        Assert.All(outcomes, o => Assert.Equal(InstallResult.Failed, o.Result));
        Assert.Contains(outcomes, o => o.Id == "kvm" && o.Message.Contains("virt-core", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(_scenario.Options.Directory));
    }

    [Fact]
    public async Task The_pages_requests_install_only_what_the_administrator_saw_and_each_leaves_a_result()
    {
        var package = TestPlugins.Package(_scenario.OfficialKey, id: "kvm", version: "1.0.0");
        Publish(package);
        PublishCatalog(("kvm", "o/kvm"), ("ssh", "o/ssh"));
        Request("a", "kvm", "1.0.0", Sha(package.Content));
        Request("b", "ssh", "1.0.0", new string('a', 64));
        File.WriteAllText(Path.Combine(_requests, "c.json"), "{ \"id\": \"kvm\" }");

        var outcomes = await Store().ApplyRequestsAsync(_requests, Ct);

        Assert.True(PluginStore.Changed(outcomes));
        Assert.True(File.Exists(Path.Combine(_scenario.Options.Directory, "kvm-1.0.0.nkp")));
        Assert.Empty(Directory.GetFiles(_requests, "*.json"));
        Assert.Contains("\"installed\"", File.ReadAllText(Path.Combine(_requests, "a.result")), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"failed\"", File.ReadAllText(Path.Combine(_requests, "b.result")), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"failed\"", File.ReadAllText(Path.Combine(_requests, "c.result")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_request_for_a_package_the_catalog_no_longer_offers_is_refused()
    {
        Publish(TestPlugins.Package(_scenario.OfficialKey, id: "kvm", version: "1.0.0"));
        PublishCatalog(("kvm", "o/kvm"));
        Request("a", "kvm", "1.0.0", new string('b', 64));

        var outcome = Assert.Single(await Store().ApplyRequestsAsync(_requests, Ct));

        Assert.Equal(InstallResult.Failed, outcome.Result);
        Assert.Empty(Directory.GetFiles(_scenario.Options.Directory));
    }

    [Fact]
    public async Task At_most_ten_requests_are_applied_in_one_run()
    {
        PublishCatalog(("kvm", "o/kvm"));
        for (var i = 0; i < PluginStore.MaxRequestsPerRun + 2; i++)
        {
            Request($"r{i:00}", "kvm", "1.0.0", new string('c', 64));
            File.SetLastWriteTimeUtc(Path.Combine(_requests, $"r{i:00}.json"), DateTime.UtcNow.AddMinutes(i));
        }

        var outcomes = await Store().ApplyRequestsAsync(_requests, Ct);

        Assert.Equal(2, outcomes.Count(o => o.Message.StartsWith("Too many", StringComparison.Ordinal)));
        Assert.Empty(Directory.GetFiles(_requests, "*.json"));
    }

    [Fact]
    public async Task Removing_a_plugin_keeps_the_plugins_that_need_it_and_remembers_the_removal()
    {
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey, id: "db-core"));
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey, id: "mssql", dependsOn: [("db-core", ">=1.0.0 <2.0.0")]));
        File.WriteAllText(Path.Combine(_requests, "a.json"), "{ \"action\": \"remove\", \"id\": \"db-core\", \"requestedBy\": \"admin\" }");

        var refused = Assert.Single(await Store().ApplyRequestsAsync(_requests, Ct));

        Assert.Equal(InstallResult.Failed, refused.Result);
        Assert.Contains("mssql depends on db-core", refused.Message, StringComparison.Ordinal);
        Assert.Equal(InstallResult.Removed, Store().Remove("mssql").Result);
        Assert.Equal(InstallResult.Removed, Store().Remove("db-core").Result);
        Assert.Empty(Directory.GetFiles(_scenario.Options.Directory, "*.nkp"));
        Assert.Equal(["db-core", "mssql"], File.ReadAllLines(Path.Combine(_scenario.Options.Directory, PluginStore.RemovedFileName)));

        Publish(TestPlugins.Package(_scenario.OfficialKey, id: "db-core"));
        PublishCatalog(("db-core", "o/db-core"));
        await Store().InstallAsync(["db-core"], Ct);
        Assert.Equal(["mssql"], File.ReadAllLines(Path.Combine(_scenario.Options.Directory, PluginStore.RemovedFileName)));
    }

    [Fact]
    public async Task An_uploaded_package_installs_only_if_its_the_one_checked_and_verifies_with_roots_trust()
    {
        var good = TestPlugins.Package(_scenario.ThirdPartyKey, id: "com.example.netbox", builtIn: false);
        var stranger = TestPlugins.Package(PluginSigningKey.Generate(), id: "com.other.thing", builtIn: false);
        Upload("a", good, Sha(good.Content));
        Upload("b", good, new string('d', 64));
        Upload("c", stranger, Sha(stranger.Content));

        var outcomes = await Store().ApplyRequestsAsync(_requests, Ct);

        Assert.Equal([InstallResult.Installed, InstallResult.Failed, InstallResult.Failed], outcomes.Select(o => o.Result));
        Assert.Contains(ReasonCodes.UntrustedKey, outcomes[2].Message, StringComparison.Ordinal);
        Assert.Equal(["com.example.netbox-1.0.0.nkp"], Directory.GetFiles(_scenario.Options.Directory, "*.nkp").Select(Path.GetFileName));
        Assert.Empty(Directory.GetFiles(_requests, "*.nkp"));
    }

    [Fact]
    public async Task Keys_are_trusted_from_the_page_only_where_root_allowed_it_and_only_third_party_ones()
    {
        using var publisher = PluginSigningKey.Generate();
        var keyFile = Encoding.UTF8.GetString(TrustedKey.ThirdParty(publisher.PublicKey.Span, ["org.publisher.*"]).ToKeyFile());
        var development = Encoding.UTF8.GetString(TrustedKey.Development(publisher.PublicKey.Span).ToKeyFile());
        void Ask(string name, string keyFile) => File.WriteAllText(
            Path.Combine(_requests, name + ".json"),
            System.Text.Json.JsonSerializer.Serialize(new { action = "trust", keyFile, requestedBy = "admin" }));

        Ask("a", keyFile);
        Assert.Contains("doesn't allow", Assert.Single(await Store().ApplyRequestsAsync(_requests, Ct)).Message, StringComparison.Ordinal);
        Ask("b", keyFile);
        Ask("c", development);
        var outcomes = await Store(webTrust: true).ApplyRequestsAsync(_requests, Ct);

        Assert.Equal([InstallResult.Trusted, InstallResult.Failed], outcomes.Select(o => o.Result));
        var trusted = Assert.Single(Store().TrustedKeys(), k => k.Key.KeyId == SigningKeys.ComputeKeyId(publisher.PublicKey.Span)).Key;
        Assert.Equal(["org.publisher.*"], trusted.AllowedIds);
        Assert.Equal(InstallResult.Untrusted, Store().Untrust(trusted.KeyId).Result);
        Assert.DoesNotContain(Store().TrustedKeys(), k => k.Key.KeyId == trusted.KeyId);
    }

    [Fact]
    public async Task Enabling_brings_the_shared_plugin_and_disabling_a_shared_plugin_takes_its_dependants()
    {
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey, id: "db-core"));
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey, id: "mssql", dependsOn: [("db-core", ">=1.0.0 <2.0.0")]));
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey, id: "oracle", dependsOn: [("db-core", ">=1.0.0 <2.0.0")]));
        var loaded = await _scenario.LoadAsync();
        var switches = new PluginSwitches(_scenario.Store, _scenario.Audit, TimeProvider.System);

        Assert.Equal(["mssql", "oracle", "db-core"], await switches.SwitchAsync("db-core", enable: false, "admin", loaded.Manifests, Ct));
        Assert.Empty((await _scenario.LoadAsync()).Loaded);
        Assert.Equal(["db-core", "mssql"], await switches.SwitchAsync("mssql", enable: true, "admin", loaded.Manifests, Ct));

        var result = await _scenario.LoadAsync();
        Assert.Equal(["db-core", "mssql"], result.Loaded.Select(p => p.Manifest.Id));
        Assert.Equal(PluginState.Disabled, Assert.Single(result.Statuses, s => s.Id == "oracle").State);
        Assert.Contains(_scenario.Audit.Events, e => e.Action == "plugin.disabled" && e.Target == "oracle" && e.Detail.Contains("which needs db-core", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_shared_plugins_new_major_waits_for_its_dependants_and_comes_along_with_them()
    {
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey, id: "db-core"));
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey, id: "mssql", dependsOn: [("db-core", ">=1.0.0 <2.0.0")]));
        _scenario.Install(TestPlugins.Package(_scenario.OfficialKey, id: "oracle", dependsOn: [("db-core", ">=1.0.0 <2.0.0")]));
        Publish(TestPlugins.Package(_scenario.OfficialKey, id: "db-core", version: "2.0.0"));
        Publish(TestPlugins.Package(_scenario.OfficialKey, id: "mssql", version: "2.0.0", dependsOn: [("db-core", ">=2.0.0 <3.0.0")]));
        PublishCatalog(("db-core", "o/db-core"), ("mssql", "o/mssql"), ("oracle", "o/oracle"));

        var statuses = (await Store().UpdateAsync(Ct)).ToDictionary(s => s.Id);

        // oracle has no release for db-core 2 yet: neither db-core 2 nor mssql 2 can be installed without breaking it.
        Assert.Equal(CatalogCompatibility.DependantsIncompatible, statuses["db-core"].ReasonCode);
        Assert.Contains("oracle", statuses["db-core"].Message, StringComparison.Ordinal);
        Assert.Equal(CatalogCompatibility.DependencyUnavailable, statuses["mssql"].ReasonCode);
        Assert.Contains("oracle doesn't accept", statuses["mssql"].Message, StringComparison.Ordinal);
        Assert.Equal(InstallResult.Failed, Assert.Single(await Store().UpgradeAsync(["db-core"], Ct), o => o.Id == "db-core").Result);

        Publish(TestPlugins.Package(_scenario.OfficialKey, id: "oracle", version: "2.0.0", dependsOn: [("db-core", ">=2.0.0 <3.0.0")]));
        statuses = (await Store().UpdateAsync(Ct)).ToDictionary(s => s.Id);
        Assert.Contains("Upgrade mssql to 2.0.0", statuses["db-core"].Message, StringComparison.Ordinal);
        Assert.Equal("2.0.0", statuses["mssql"].Available?.Version);

        // Upgrading mssql alone brings db-core 2, and oracle 2 with it, in one run.
        var outcomes = await Store().UpgradeAsync(["mssql"], Ct);

        Assert.All(outcomes, o => Assert.Equal(InstallResult.Upgraded, o.Result));
        Assert.Equal(
            ["db-core-2.0.0.nkp", "mssql-2.0.0.nkp", "oracle-2.0.0.nkp"],
            Directory.GetFiles(_scenario.Options.Directory, "*.nkp").Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(["db-core", "mssql", "oracle"], (await _scenario.LoadAsync()).Loaded.Select(p => p.Manifest.Id));
    }

    [Theory]
    [InlineData("kvm-1.0.0.nkp", "kvm", "1.0.0")]
    [InlineData("virt-core-1.2.0.nkp", "virt-core", "1.2.0")]
    [InlineData("com.example.x-2.0.0-beta.1.nkp", "com.example.x", "2.0.0-beta.1")]
    [InlineData("kvm.nkp", null, null)]
    [InlineData("kvm-x.nkp", null, null)]
    public void Package_names_give_the_plugin_and_its_version(string name, string? id, string? version)
    {
        var parsed = PluginStore.ParsePackageName(name);
        Assert.Equal(id, parsed?.Id);
        Assert.Equal(version, parsed?.Version.Text);
    }

    private PluginStore Store(bool webTrust = false) => new(
        _scenario.Options.Directory,
        CatalogUrl,
        _releases,
        new VerificationContext(
            new TrustStore([TrustedKey.Official(_scenario.OfficialKey.PublicKey.Span), TrustedKey.ThirdParty(_scenario.ThirdPartyKey.PublicKey.Span, ["com.example.*"])], Revocations.None),
            IsDevelopment: false,
            SemanticVersion.Parse(SdkVersion.Current),
            SharedAssemblies.OfCurrentProcess(),
            TimeProvider.System))
    {
        TrustDirectory = _scenario.Options.TrustDirectory,
        AllowWebTrust = webTrust,
    };

    private void Publish(BuiltPackage package, string? checksum = null)
    {
        var (id, version) = PluginStore.ParsePackageName(package.FileName)!.Value;
        var uri = new Uri($"https://dl.test/{id}/{package.FileName}");
        var sha = new Uri(uri + ".sha256");
        _releases.Files[sha] = Encoding.ASCII.GetBytes($"{checksum ?? Sha(package.Content)}  {package.FileName}\n");
        _releases.Add($"o/{id}", new PluginRelease(version, uri, package.FileName, sha), package.Content);
    }

    private void PublishCatalog(params (string Id, string Repository)[] plugins) =>
        _releases.Files[CatalogUrl] = Encoding.UTF8.GetBytes(
            "{ \"formatVersion\": 1, \"plugins\": [" + string.Join(", ", plugins.Select(p => $"{{ \"id\": \"{p.Id}\", \"repository\": \"{p.Repository}\" }}")) + "] }");

    private void Request(string name, string id, string version, string sha) =>
        File.WriteAllText(Path.Combine(_requests, name + ".json"), $"{{ \"id\": \"{id}\", \"version\": \"{version}\", \"packageSha256\": \"{sha}\", \"requestedBy\": \"admin\" }}");

    private void Upload(string name, BuiltPackage package, string sha)
    {
        var (id, version) = PluginStore.ParsePackageName(package.FileName)!.Value;
        File.WriteAllBytes(Path.Combine(_requests, name + ".nkp"), package.Content);
        File.WriteAllText(Path.Combine(_requests, name + ".json"), $"{{ \"action\": \"upload\", \"id\": \"{id}\", \"version\": \"{version.Text}\", \"packageSha256\": \"{sha}\", \"requestedBy\": \"admin\" }}");
        File.SetLastWriteTimeUtc(Path.Combine(_requests, name + ".json"), DateTime.UtcNow.AddSeconds(name[0]));
    }

    private static string Sha(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    private sealed class FakeReleases : IPluginReleaseSource
    {
        private readonly Dictionary<string, List<PluginRelease>> _releases = new(StringComparer.Ordinal);

        public Dictionary<Uri, byte[]> Files { get; } = [];

        public void Add(string repository, PluginRelease release, byte[] package)
        {
            if (!_releases.TryGetValue(repository, out var list))
            {
                _releases[repository] = list = [];
            }

            list.RemoveAll(r => r.Version == release.Version);
            list.Add(release);
            Files[release.Package] = package;
        }

        public Task<IReadOnlyList<PluginRelease>> ListAsync(CatalogEntry entry, CancellationToken cancellationToken) =>
            _releases.TryGetValue(entry.Repository, out var list)
                ? Task.FromResult<IReadOnlyList<PluginRelease>>([.. list.OrderByDescending(r => r.Version)])
                : throw new ReleaseSourceException($"GitHub has no repository {entry.Repository}.");

        public Task<byte[]> DownloadAsync(Uri uri, long maxBytes, CancellationToken cancellationToken) =>
            Files.TryGetValue(uri, out var file) ? Task.FromResult(file) : throw new ReleaseSourceException($"{uri} answered 404.");
    }
}
