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

    private PluginStore Store() => new(
        _scenario.Options.Directory,
        CatalogUrl,
        _releases,
        new VerificationContext(
            new TrustStore([TrustedKey.Official(_scenario.OfficialKey.PublicKey.Span), TrustedKey.ThirdParty(_scenario.ThirdPartyKey.PublicKey.Span, ["com.example.*"])], Revocations.None),
            IsDevelopment: false,
            SemanticVersion.Parse(SdkVersion.Current),
            SharedAssemblies.OfCurrentProcess(),
            TimeProvider.System));

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
