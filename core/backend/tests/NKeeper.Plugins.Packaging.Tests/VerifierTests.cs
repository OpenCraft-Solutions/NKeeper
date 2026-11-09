using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using NKeeper.Plugins.Packaging.Zip;

namespace NKeeper.Plugins.Packaging.Tests;

/// <summary>
/// One test per refusal path of docs/plugin-security.md §5 and §6, each building a real package
/// and expecting that exact reason code, plus the paths that must succeed.
/// </summary>
public sealed class VerifierTests : IDisposable
{
    private readonly TestPackages _packages = new();

    public void Dispose() => _packages.Dispose();

    // ---- Verified packages ----

    [Fact]
    public void A_package_built_by_the_tool_verifies()
    {
        var package = _packages.Build();

        var result = PackageVerifier.Verify(package.Content, package.FileName, _packages.Context());

        Assert.True(result.IsVerified, result.ToString());
        Assert.Equal("sample-1.2.0.nkp", package.FileName);
        Assert.Equal(TestPackages.Id, result.Manifest!.Id);
        Assert.Equal(_packages.OfficialKey.KeyId, result.KeyId);
        Assert.Equal(64, result.PackageSha256!.Length);
        Assert.Equal(TestPackages.PluginAssembly, result.Files[TestPackages.EntryAssembly].ToArray());
        Assert.Contains(PackageFormat.ManifestPath, result.Files.Keys);
        Assert.DoesNotContain(PackageFormat.SignaturePath, result.Files.Keys);
    }

    [Fact]
    public void Building_is_deterministic_apart_from_the_signature()
    {
        var first = Entries(_packages.Build().Content);
        var second = Entries(_packages.Build().Content);

        Assert.Equal(first.Keys, second.Keys);
        foreach (var (name, data) in first.Where(e => e.Key != PackageFormat.SignaturePath))
        {
            Assert.Equal(data, second[name]);
        }
    }

    [Fact]
    public void A_third_party_key_may_sign_ids_in_its_namespace()
    {
        var package = _packages.Build(_packages.ThirdPartyKey, TestPackages.Manifest(id: "com.example.tool"));

        Assert.True(PackageVerifier.Verify(package.Content, package.FileName, _packages.Context()).IsVerified);
    }

    [Fact]
    public void A_development_key_may_sign_an_official_id_in_Development()
    {
        var package = _packages.Build(_packages.DevelopmentKey);

        Assert.True(PackageVerifier.Verify(package.Content, package.FileName, _packages.Context(development: true)).IsVerified);
    }

    [Fact]
    public void A_plugin_without_a_frontend_verifies()
    {
        var files = TestPackages.Files();
        files.Remove(TestPackages.FrontendEntry);
        var package = _packages.Build(manifest: TestPackages.Manifest(frontend: null), files: files);

        Assert.True(PackageVerifier.Verify(package.Content, package.FileName, _packages.Context()).IsVerified);
    }

    // ---- §5.1 Read ----

    [Theory]
    [InlineData("Sample-1.2.0.nkp")]
    [InlineData("sample-1.2.0.zip")]
    [InlineData("plugins/sample-1.2.0.nkp")]
    [InlineData("-sample.nkp")]
    public void A_badly_named_file_is_refused(string fileName)
    {
        AssertRefused(ReasonCodes.BadName, _packages.Build().Content, fileName);
    }

    [Fact]
    public void A_package_over_the_size_limit_is_refused()
    {
        var package = _packages.Build();

        AssertRefused(ReasonCodes.TooLarge, package.Content, limits: new PackageLimits { MaxPackageBytes = package.Content.Length - 1 });
    }

    // ---- §5.2 Structure ----

    [Fact]
    public void A_file_that_is_not_a_zip_is_refused()
    {
        AssertRefused(ReasonCodes.BadZip, Encoding.UTF8.GetBytes("this is not a package"));
    }

    [Fact]
    public void A_truncated_package_is_refused()
    {
        var content = _packages.Build().Content;

        AssertRefused(ReasonCodes.BadZip, content[..(content.Length - 10)]);
    }

    [Fact]
    public void Data_appended_after_the_archive_is_refused()
    {
        AssertRefused(ReasonCodes.BadZip, [.. _packages.Build().Content, .. "trailing"u8.ToArray()]);
    }

    [Fact]
    public void A_local_header_that_disagrees_with_the_central_directory_is_refused()
    {
        var content = _packages.Build().Content;
        content[8] ^= 0x08; // The first local header's compression method.

        AssertRefused(ReasonCodes.BadZip, content);
    }

    [Fact]
    public void A_ZIP64_archive_is_refused()
    {
        var content = _packages.Build().Content;
        var end = content.Length - 22;
        BinaryPrimitives.WriteUInt16LittleEndian(content.AsSpan(end + 8), 0xFFFF);
        BinaryPrimitives.WriteUInt16LittleEndian(content.AsSpan(end + 10), 0xFFFF);

        AssertRefused(ReasonCodes.BadZip, content);
    }

    [Fact]
    public void Entries_that_share_data_are_refused()
    {
        // Point the second central directory entry at the first entry's local header.
        var content = _packages.Build().Content;
        var central = (int)BinaryPrimitives.ReadUInt32LittleEndian(content.AsSpan(content.Length - 22 + 16));
        var firstNameLength = BinaryPrimitives.ReadUInt16LittleEndian(content.AsSpan(central + 28));
        var second = central + 46 + firstNameLength;
        BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(second + 42), 0);

        AssertRefused(ReasonCodes.BadZip, content);
    }

    [Fact]
    public void An_entry_that_fails_its_CRC_is_refused()
    {
        AssertRefused(ReasonCodes.BadZip, AssembleWith(e => e.Name == TestPackages.FrontendEntry ? e with { CrcOverride = 1 } : e));
    }

    [Fact]
    public void A_stored_entry_with_the_wrong_size_is_refused()
    {
        AssertRefused(ReasonCodes.BadZip, AssembleWith(e => e.Name == TestPackages.FrontendEntry
            ? e with { ForceStored = true, UncompressedSizeOverride = (uint)e.Data.Length + 1 }
            : e));
    }

    [Fact]
    public void A_deflated_entry_shorter_than_declared_is_refused()
    {
        AssertRefused(ReasonCodes.BadZip, AssembleWith(e => e.Name == TestPackages.EntryAssembly
            ? e with { UncompressedSizeOverride = (uint)e.Data.Length + 10 }
            : e));
    }

    [Fact]
    public void Invalid_deflate_data_is_refused()
    {
        AssertRefused(ReasonCodes.BadZip, AssembleWith(e => e.Name == TestPackages.FrontendEntry
            ? e with { RawDataOverride = [0xFF, 0xFF, 0xFF, 0xFF], MethodOverride = ZipEntryInfo.MethodDeflate }
            : e));
    }

    // ---- §5.2 Entries ----

    [Theory]
    [InlineData("../escape.js")]
    [InlineData("/etc/passwd")]
    [InlineData("frontend\\index2.js")]
    [InlineData("C:frontend/a.js")]
    [InlineData("frontend//a.js")]
    [InlineData("./frontend/a.js")]
    [InlineData("frontend/../a.js")]
    [InlineData("frontend/a\u0001.js")]
    [InlineData("frontend/")]
    public void An_entry_with_a_forbidden_path_is_refused(string path)
    {
        AssertRefused(ReasonCodes.BadEntry, AddEntry(new ZipWriteEntry(path, [1, 2, 3])));
    }

    [Fact]
    public void Paths_that_differ_only_in_case_are_refused()
    {
        AssertRefused(ReasonCodes.BadEntry, AddEntry(new ZipWriteEntry("Frontend/index.js", [1, 2, 3])));
    }

    [Fact]
    public void A_symlink_entry_is_refused()
    {
        AssertRefused(ReasonCodes.BadEntry, AssembleWith(e => e.Name == TestPackages.FrontendEntry ? e with { ExternalAttributesOverride = 0xA1FFu << 16 } : e));
    }

    [Fact]
    public void An_entry_from_an_unknown_host_system_is_refused()
    {
        AssertRefused(ReasonCodes.BadEntry, AssembleWith(e => e.Name == TestPackages.FrontendEntry ? e with { VersionMadeByOverride = (ushort)((10 << 8) | 30) } : e));
    }

    [Theory]
    [InlineData((ushort)(ZipEntryInfo.FlagUtf8 | ZipEntryInfo.FlagEncrypted))]
    [InlineData((ushort)(ZipEntryInfo.FlagUtf8 | ZipEntryInfo.FlagStrongEncryption))]
    [InlineData((ushort)(ZipEntryInfo.FlagUtf8 | ZipEntryInfo.FlagDataDescriptor))]
    public void Encrypted_entries_and_data_descriptors_are_refused(ushort flags)
    {
        AssertRefused(ReasonCodes.BadEntry, AssembleWith(e => e.Name == TestPackages.FrontendEntry ? e with { FlagsOverride = flags } : e));
    }

    [Fact]
    public void A_ZIP64_extra_field_is_refused()
    {
        AssertRefused(ReasonCodes.BadEntry, AssembleWith(e => e.Name == TestPackages.FrontendEntry ? e with { ExtraField = [0x01, 0x00, 0x00, 0x00] } : e));
    }

    [Fact]
    public void An_unsupported_compression_method_is_refused()
    {
        AssertRefused(ReasonCodes.BadEntry, AssembleWith(e => e.Name == TestPackages.FrontendEntry ? e with { ForceStored = true, MethodOverride = 12 } : e));
    }

    [Fact]
    public void A_non_UTF8_name_is_refused()
    {
        AssertRefused(ReasonCodes.BadEntry, AddEntry(new ZipWriteEntry("frontend/café.js", [1]) { FlagsOverride = 0 }));
    }

    // ---- §5.2 Limits ----

    [Fact]
    public void Too_many_entries_are_refused()
    {
        AssertRefused(ReasonCodes.Limits, _packages.Build().Content, limits: new PackageLimits { MaxEntries = 4 });
    }

    [Fact]
    public void Too_much_uncompressed_data_is_refused()
    {
        AssertRefused(ReasonCodes.Limits, _packages.Build().Content, limits: new PackageLimits { MaxTotalUncompressedBytes = 1000 });
    }

    [Fact]
    public void A_decompression_bomb_in_a_data_file_is_refused()
    {
        var files = TestPackages.FilesWithManifest();
        files["frontend/bomb.js"] = new byte[2_000_000];

        AssertRefused(ReasonCodes.Limits, TestPackages.Assemble(files, _packages.OfficialKey));
    }

    [Theory]
    [InlineData(PackageFormat.ManifestPath)]
    [InlineData(PackageFormat.FileListPath)]
    [InlineData(PackageFormat.SignaturePath)]
    public void A_decompression_bomb_in_a_metadata_file_is_refused_before_the_signature_is_checked(string path)
    {
        // A signed package whose metadata entry is swapped for 3 MB of zeros: refused for its size,
        // not for its (now broken) signature.
        var content = AssembleWith(e => e.Name == path ? e with { Data = new byte[3_000_000] } : e);

        AssertRefused(ReasonCodes.Limits, content, limits: new PackageLimits { MaxCompressionRatio = 100_000 });
    }

    [Fact]
    public void An_entry_that_decompresses_beyond_its_declared_size_is_refused()
    {
        AssertRefused(ReasonCodes.Limits, AssembleWith(e => e.Name == TestPackages.EntryAssembly
            ? e with { UncompressedSizeOverride = (uint)e.Data.Length - 10 }
            : e));
    }

    // ---- §5.3 Signature ----

    [Theory]
    [InlineData(PackageFormat.FileListPath)]
    [InlineData(PackageFormat.SignaturePath)]
    public void A_package_without_its_signed_list_is_refused(string missing)
    {
        AssertRefused(ReasonCodes.NotSigned, AssembleWith(e => e, drop: missing));
    }

    [Fact]
    public void A_malformed_signature_file_is_refused()
    {
        AssertRefused(ReasonCodes.BadSignature, TestPackages.Assemble(TestPackages.FilesWithManifest(), _packages.OfficialKey, signature: "{ not json"u8.ToArray()));
    }

    [Fact]
    public void An_unsupported_signature_algorithm_is_refused()
    {
        var key = _packages.OfficialKey;
        var list = FileList.Create(TestPackages.Id, TestPackages.Version, TestPackages.FilesWithManifest()).ToCanonicalJson();
        var signature = new PackageSignature(key.KeyId, "RSA-SHA1", Convert.ToBase64String(key.Sign(list))).ToJson();

        AssertRefused(ReasonCodes.BadSignature, TestPackages.Assemble(TestPackages.FilesWithManifest(), key, fileList: list, signature: signature));
    }

    [Fact]
    public void A_package_signed_by_an_unknown_key_is_refused()
    {
        AssertRefused(ReasonCodes.UntrustedKey, _packages.Build(_packages.UnknownKey).Content);
    }

    [Fact]
    public void A_development_key_is_refused_outright_outside_Development()
    {
        AssertRefused(ReasonCodes.UntrustedKey, _packages.Build(_packages.DevelopmentKey).Content);
    }

    [Fact]
    public void A_development_key_is_refused_outside_Development_even_for_a_third_party_id()
    {
        var package = _packages.Build(_packages.DevelopmentKey, TestPackages.Manifest(id: "com.example.tool"));

        AssertRefused(ReasonCodes.UntrustedKey, package.Content, package.FileName);
    }

    [Fact]
    public void A_revoked_key_is_refused()
    {
        var revocations = new Revocations(new HashSet<string> { _packages.OfficialKey.KeyId }, new HashSet<string>());

        AssertRefused(ReasonCodes.RevokedKey, _packages.Build().Content, trust: _packages.Trust(revocations));
    }

    [Fact]
    public void An_expired_key_is_refused()
    {
        var package = _packages.Build(_packages.ThirdPartyKey, TestPackages.Manifest(id: "com.example.tool"));
        var trust = _packages.Trust(thirdPartyNotAfter: DateOnly.FromDateTime(TestPackages.Now.UtcDateTime).AddDays(-1));

        AssertRefused(ReasonCodes.ExpiredKey, package.Content, package.FileName, trust: trust);
    }

    [Fact]
    public void A_file_list_changed_after_signing_is_refused()
    {
        var key = _packages.OfficialKey;
        var list = FileList.Create(TestPackages.Id, TestPackages.Version, TestPackages.FilesWithManifest()).ToCanonicalJson();
        var signature = new PackageSignature(key.KeyId, PackageFormat.SignatureAlgorithm, Convert.ToBase64String(key.Sign(list))).ToJson();
        var tampered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(list).Replace("1.2.0", "1.2.1", StringComparison.Ordinal));

        AssertRefused(ReasonCodes.BadSignature, TestPackages.Assemble(TestPackages.FilesWithManifest(), key, fileList: tampered, signature: signature));
    }

    [Fact]
    public void A_signature_by_another_key_under_a_trusted_key_id_is_refused()
    {
        var list = FileList.Create(TestPackages.Id, TestPackages.Version, TestPackages.FilesWithManifest()).ToCanonicalJson();
        var forged = new PackageSignature(_packages.OfficialKey.KeyId, PackageFormat.SignatureAlgorithm, Convert.ToBase64String(_packages.UnknownKey.Sign(list))).ToJson();

        AssertRefused(ReasonCodes.BadSignature, TestPackages.Assemble(TestPackages.FilesWithManifest(), _packages.OfficialKey, fileList: list, signature: forged));
    }

    [Fact]
    public void A_signed_but_malformed_file_list_is_refused()
    {
        var unsorted = Encoding.UTF8.GetBytes(
            """{"formatVersion":1,"id":"sample","version":"1.2.0","files":[{"path":"b","size":1,"sha256":"00"},{"path":"a","size":1,"sha256":"00"}]}""");

        AssertRefused(ReasonCodes.BadFileList, TestPackages.Assemble(TestPackages.FilesWithManifest(), _packages.OfficialKey, fileList: unsorted));
    }

    [Fact]
    public void A_third_party_key_may_not_sign_an_official_id()
    {
        AssertRefused(ReasonCodes.KeyScope, _packages.Build(_packages.ThirdPartyKey).Content);
    }

    [Fact]
    public void A_third_party_key_may_not_sign_another_publishers_id()
    {
        var package = _packages.Build(_packages.ThirdPartyKey, TestPackages.Manifest(id: "org.other.tool"));

        AssertRefused(ReasonCodes.KeyScope, package.Content, package.FileName);
    }

    [Fact]
    public void A_package_renamed_to_another_version_is_refused()
    {
        AssertRefused(ReasonCodes.IdentityMismatch, _packages.Build().Content, "sample-9.9.9.nkp");
    }

    [Fact]
    public void A_manifest_that_disagrees_with_the_signed_list_is_refused()
    {
        var files = TestPackages.FilesWithManifest(TestPackages.Manifest(version: "1.3.0"));

        AssertRefused(ReasonCodes.IdentityMismatch, TestPackages.Assemble(files, _packages.OfficialKey));
    }

    // ---- §5.4 Contents ----

    [Fact]
    public void A_missing_listed_file_is_refused()
    {
        AssertRefused(ReasonCodes.FileSet, AssembleWith(e => e, drop: TestPackages.FrontendEntry));
    }

    [Fact]
    public void An_unlisted_extra_file_is_refused()
    {
        AssertRefused(ReasonCodes.FileSet, AddEntry(new ZipWriteEntry("frontend/extra.js", [1, 2, 3])));
    }

    [Fact]
    public void A_tampered_file_is_refused()
    {
        AssertRefused(ReasonCodes.HashMismatch, AssembleWith(e => e.Name == TestPackages.FrontendEntry ? e with { Data = "alert('changed')"u8.ToArray() } : e));
    }

    // ---- §5.5 Manifest and compatibility ----

    [Fact]
    public void A_package_without_a_manifest_is_refused()
    {
        AssertRefused(ReasonCodes.BadManifest, TestPackages.Assemble(TestPackages.Files(), _packages.OfficialKey));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "formatVersion": 2, "id": "sample", "name": "Sample", "version": "1.2.0", "sdkVersion": ">=0.1.0", "backend": { "entryAssembly": "backend/Sample.Plugin.dll", "entryType": "Sample.Plugin" } }""")]
    [InlineData("""{ "formatVersion": 1, "id": "sample", "name": "Sample", "version": "1.2.0", "sdkVersion": ">=0.1.0", "backend": { "entryAssembly": "backend/Sample.Plugin.dll", "entryType": "Sample.Plugin" }, "unexpected": true }""")]
    [InlineData("""{ "formatVersion": 1, "id": "sample", "name": "Sample", "version": "1.2.0", "sdkVersion": ">=0.1.0" }""")]
    [InlineData("""{ "formatVersion": 1, "id": "sample", "name": "Sample", "version": "1.2.0", "sdkVersion": "~0.1", "backend": { "entryAssembly": "backend/Sample.Plugin.dll", "entryType": "Sample.Plugin" } }""")]
    [InlineData("""{ "formatVersion": 1, "id": "sample", "name": "Sample", "version": "1.2.0", "sdkVersion": ">=0.1.0", "backend": { "entryAssembly": "backend/Missing.dll", "entryType": "Sample.Plugin" } }""")]
    public void An_invalid_manifest_is_refused(string manifest)
    {
        AssertRefused(ReasonCodes.BadManifest, TestPackages.Assemble(TestPackages.FilesWithManifest(Encoding.UTF8.GetBytes(manifest)), _packages.OfficialKey));
    }

    [Fact]
    public void A_plugin_for_another_SDK_version_is_refused()
    {
        AssertRefused(ReasonCodes.SdkIncompatible, _packages.Build(manifest: TestPackages.Manifest(sdk: ">=0.2.0 <0.3.0")).Content);
    }

    [Theory]
    [InlineData("backend/libnative.so")]
    [InlineData("backend/libnative.dylib")]
    [InlineData("backend/tool.exe")]
    [InlineData("backend/Native.dll")]
    public void Native_code_is_refused(string path)
    {
        var files = TestPackages.Files();
        files[path] = [0x7F, 0x45, 0x4C, 0x46, 1, 2, 3, 4];

        AssertRefused(ReasonCodes.NativeLibrary, _packages.Build(files: files).Content);
    }

    [Fact]
    public void A_bundled_copy_of_a_shared_assembly_is_refused_whatever_its_file_name()
    {
        var files = TestPackages.Files();
        files["backend/Innocent.dll"] = File.ReadAllBytes(typeof(JsonSerializer).Assembly.Location);

        AssertRefused(ReasonCodes.SharedAssembly, _packages.Build(files: files).Content);
    }

    [Fact]
    public void A_revoked_package_is_refused()
    {
        var package = _packages.Build();
        var hash = PackageVerifier.Verify(package.Content, package.FileName, _packages.Context()).PackageSha256!;
        var revocations = new Revocations(new HashSet<string>(), new HashSet<string> { hash });

        AssertRefused(ReasonCodes.RevokedPackage, package.Content, trust: _packages.Trust(revocations));
    }

    // ---- Helpers ----

    private static Dictionary<string, byte[]> Entries(byte[] archive) =>
        ZipReader.ReadDirectory(archive, int.MaxValue).ToDictionary(e => e.Name, e => ZipReader.ReadData(archive, e));

    private byte[] AssembleWith(Func<ZipWriteEntry, ZipWriteEntry> change, string? drop = null) =>
        TestPackages.Assemble(TestPackages.FilesWithManifest(), _packages.OfficialKey, mutate: entries => entries.Where(e => e.Name != drop).Select(change));

    private byte[] AddEntry(ZipWriteEntry extra) =>
        TestPackages.Assemble(TestPackages.FilesWithManifest(), _packages.OfficialKey, mutate: entries => entries.Append(extra));

    private void AssertRefused(string reasonCode, byte[] content, string? fileName = null, TrustStore? trust = null, PackageLimits? limits = null)
    {
        var result = PackageVerifier.Verify(content, fileName ?? TestPackages.FileName, _packages.Context(trust: trust, limits: limits));

        Assert.False(result.IsVerified, "The package should have been refused.");
        Assert.True(reasonCode == result.ReasonCode, $"Expected {reasonCode}, got {result}");
        Assert.Null(result.Manifest);
        Assert.Empty(result.Files);
    }
}
