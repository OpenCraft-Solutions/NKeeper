using System.Text;

namespace NKeeper.Plugins.Packaging.Tests;

public sealed class FormatTests
{
    // ---- Versions and ranges ----

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("0.1.0")]
    [InlineData("2.0.0-alpha.1")]
    [InlineData("1.0.0+build.5")]
    public void Valid_versions_parse(string text)
    {
        Assert.True(SemanticVersion.TryParse(text, out _));
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-")]
    [InlineData("v1.0.0")]
    [InlineData("1.0.0-01")]
    public void Invalid_versions_are_rejected(string text)
    {
        Assert.False(SemanticVersion.TryParse(text, out _));
    }

    [Theory]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    [InlineData("1.9.0", "1.10.0")]
    public void Versions_follow_SemVer_precedence(string lower, string higher)
    {
        Assert.True(SemanticVersion.Parse(lower) < SemanticVersion.Parse(higher));
    }

    [Fact]
    public void Build_metadata_is_ignored_for_precedence()
    {
        Assert.Equal(SemanticVersion.Parse("1.0.0+a"), SemanticVersion.Parse("1.0.0+b"));
    }

    [Theory]
    [InlineData(">=0.1.0 <0.2.0", "0.1.0", true)]
    [InlineData(">=0.1.0 <0.2.0", "0.1.9", true)]
    [InlineData(">=0.1.0 <0.2.0", "0.2.0", false)]
    [InlineData(">0.1.0", "0.1.0", false)]
    [InlineData("=1.2.3", "1.2.3", true)]
    [InlineData("<=1.0.0", "1.0.0-rc.1", true)]
    public void Ranges_include_the_versions_they_describe(string range, string version, bool included)
    {
        Assert.True(VersionRange.TryParse(range, out var parsed));
        Assert.Equal(included, parsed.Includes(SemanticVersion.Parse(version)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("~1.0.0")]
    [InlineData("^1.0.0")]
    [InlineData(">=1.0")]
    [InlineData("1.0.0 || 2.0.0")]
    public void Unsupported_ranges_are_rejected(string range)
    {
        Assert.False(VersionRange.TryParse(range, out _));
    }

    // ---- Plugin IDs ----

    [Theory]
    [InlineData("ssh", true)]
    [InlineData("db-mssql", true)]
    [InlineData("com.example.netbox", true)]
    [InlineData("SSH", false)]
    [InlineData("db_mssql", false)]
    [InlineData("-ssh", false)]
    [InlineData("com..example", false)]
    [InlineData("com.example.", false)]
    public void Plugin_ids_follow_the_rules(string id, bool valid)
    {
        Assert.Equal(valid, PluginId.IsValid(id));
    }

    [Fact]
    public void Ids_without_a_dot_are_official()
    {
        Assert.True(PluginId.IsOfficial("ssh"));
        Assert.False(PluginId.IsOfficial("com.example.ssh"));
    }

    // ---- Keys and trust ----

    [Fact]
    public void A_key_id_is_derived_from_the_public_key()
    {
        using var key = PluginSigningKey.Generate();

        Assert.True(SigningKeys.IsKeyId(key.KeyId));
        Assert.Equal(SigningKeys.ComputeKeyId(key.PublicKey.Span), key.KeyId);
        Assert.Equal(key.KeyId, TrustedKey.Official(key.PublicKey.Span).KeyId);
    }

    [Fact]
    public void A_private_key_round_trips_through_PEM()
    {
        using var key = PluginSigningKey.Generate();
        using var loaded = PluginSigningKey.FromPem(key.ExportPrivateKeyPem());

        Assert.Equal(key.KeyId, loaded.KeyId);
        Assert.True(TrustedKey.Official(key.PublicKey.Span).Verify("data"u8, loaded.Sign("data"u8)));
    }

    [Fact]
    public void Third_party_and_development_key_files_round_trip()
    {
        using var key = PluginSigningKey.Generate();
        var thirdParty = TrustedKey.ThirdParty(key.PublicKey.Span, ["com.example.*", "org.other.tool"], "Example", new DateOnly(2030, 1, 1));
        var development = TrustedKey.Development(key.PublicKey.Span, "dev");

        var thirdPartyLoaded = TrustedKey.FromKeyFile(thirdParty.ToKeyFile());
        var developmentLoaded = TrustedKey.FromKeyFile(development.ToKeyFile());

        Assert.Equal(TrustedKeyKind.ThirdParty, thirdPartyLoaded.Kind);
        Assert.Equal(["com.example.*", "org.other.tool"], thirdPartyLoaded.AllowedIds);
        Assert.Equal(new DateOnly(2030, 1, 1), thirdPartyLoaded.NotAfter);
        Assert.Equal(TrustedKeyKind.Development, developmentLoaded.Kind);
    }

    [Theory]
    [InlineData("com.*")]
    [InlineData("*")]
    [InlineData("ssh")]
    [InlineData("ssh.*")]
    [InlineData("com.example.*.x")]
    public void Third_party_keys_cant_have_broad_or_official_scopes(string pattern)
    {
        using var key = PluginSigningKey.Generate();

        Assert.Throws<TrustConfigurationException>(() => TrustedKey.ThirdParty(key.PublicKey.Span, [pattern]));
    }

    [Theory]
    [InlineData("com.example.tool", true)]
    [InlineData("com.example.deep.tool", true)]
    [InlineData("com.example", false)]
    [InlineData("com.examples.tool", false)]
    [InlineData("ssh", false)]
    public void A_wildcard_scope_covers_only_its_namespace(string id, bool allowed)
    {
        using var key = PluginSigningKey.Generate();

        Assert.Equal(allowed, TrustedKey.ThirdParty(key.PublicKey.Span, ["com.example.*"]).MaySign(id));
    }

    [Fact]
    public void A_key_file_cant_claim_another_keys_id()
    {
        using var key = PluginSigningKey.Generate();
        var json = Encoding.UTF8.GetString(TrustedKey.Development(key.PublicKey.Span).ToKeyFile())
            .Replace(key.KeyId, new string('0', 32), StringComparison.Ordinal);

        Assert.Throws<TrustConfigurationException>(() => TrustedKey.FromKeyFile(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void A_development_key_file_cant_be_scoped()
    {
        using var key = PluginSigningKey.Generate();
        var json = $$"""{ "keyId": "{{key.KeyId}}", "publicKey": "{{Convert.ToBase64String(key.PublicKey.Span)}}", "allowedIds": ["com.example.*"], "development": true }""";

        Assert.Throws<TrustConfigurationException>(() => TrustedKey.FromKeyFile(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void An_install_key_signs_exactly_the_IDs_it_lists_official_ones_included_and_round_trips()
    {
        using var key = PluginSigningKey.Generate();
        var install = TrustedKey.FromKeyFile(TrustedKey.Install(key.PublicKey.Span, ["ssh", "com.example.tool"]).ToKeyFile());

        Assert.Equal(TrustedKeyKind.Install, install.Kind);
        Assert.Equal(["ssh", "com.example.tool"], install.AllowedIds);
        Assert.True(install.MaySign("ssh"));
        Assert.True(install.MaySign("com.example.tool"));
        Assert.False(install.MaySign("db-mssql"));
        Assert.False(install.MaySign("com.example.other"));
    }

    [Theory]
    [InlineData("""[]""", "")]
    [InlineData("""["com.example.*"]""", "")]
    [InlineData("""["Not An Id"]""", "")]
    [InlineData("""["ssh"]""", ", \"development\": true")]
    [InlineData("""["ssh"]""", ", \"notAfter\": \"2030-01-01\"")]
    public void An_install_key_file_lists_exact_IDs_and_nothing_else(string allowedIds, string extra)
    {
        using var key = PluginSigningKey.Generate();
        var json = $$"""{ "keyId": "{{key.KeyId}}", "publicKey": "{{Convert.ToBase64String(key.PublicKey.Span)}}", "allowedIds": {{allowedIds}}, "install": true{{extra}} }""";

        Assert.Throws<TrustConfigurationException>(() => TrustedKey.FromKeyFile(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void A_key_file_with_unknown_fields_is_refused()
    {
        using var key = PluginSigningKey.Generate();
        var json = $$"""{ "keyId": "{{key.KeyId}}", "publicKey": "{{Convert.ToBase64String(key.PublicKey.Span)}}", "development": true, "official": true }""";

        Assert.Throws<TrustConfigurationException>(() => TrustedKey.FromKeyFile(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void A_trust_store_refuses_a_key_configured_twice()
    {
        using var key = PluginSigningKey.Generate();

        Assert.Throws<TrustConfigurationException>(() => new TrustStore([TrustedKey.Official(key.PublicKey.Span), TrustedKey.Development(key.PublicKey.Span)]));
    }

    [Fact]
    public void Revocation_lists_are_parsed_and_validated()
    {
        var keyId = new string('a', 32);
        var hash = new string('b', 64);

        var revocations = Revocations.FromJson(Encoding.UTF8.GetBytes($$"""{ "revokedKeyIds": ["{{keyId}}"], "revokedPackageSha256": ["{{hash}}"] }"""));

        Assert.Contains(keyId, revocations.KeyIds);
        Assert.Contains(hash, revocations.PackageSha256);
        Assert.Throws<TrustConfigurationException>(() => Revocations.FromJson("""{ "revokedKeyIds": ["not-a-key-id"] }"""u8));
    }

    // ---- Building ----

    [Theory]
    [InlineData(PackageFormat.FileListPath)]
    [InlineData(PackageFormat.SignaturePath)]
    [InlineData(PackageFormat.ManifestPath)]
    [InlineData("../escape.js")]
    public void The_builder_refuses_reserved_and_invalid_paths(string path)
    {
        using var key = PluginSigningKey.Generate();
        var files = TestPackages.Files();
        files[path] = [1];

        Assert.Throws<ArgumentException>(() => PackageBuilder.Build(TestPackages.Manifest(), files, key));
    }

    [Fact]
    public void The_builder_refuses_an_invalid_manifest()
    {
        using var key = PluginSigningKey.Generate();

        Assert.Throws<ArgumentException>(() => PackageBuilder.Build(TestPackages.Manifest(entryAssembly: "backend/Missing.dll"), TestPackages.Files(), key));
    }

    [Fact]
    public void The_file_list_is_canonical()
    {
        var files = new Dictionary<string, byte[]> { ["b.js"] = [2], ["a.js"] = [1] };

        var json = Encoding.UTF8.GetString(FileList.Create("sample", "1.0.0", files).ToCanonicalJson());

        Assert.StartsWith("""{"formatVersion":1,"id":"sample","version":"1.0.0","files":[{"path":"a.js","size":1,""", json, StringComparison.Ordinal);
        Assert.True(FileList.TryParse(Encoding.UTF8.GetBytes(json), out _, out _));
    }
}
