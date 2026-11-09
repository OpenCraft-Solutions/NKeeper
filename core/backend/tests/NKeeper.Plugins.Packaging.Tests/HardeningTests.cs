using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using NKeeper.Plugins.Packaging.Zip;

namespace NKeeper.Plugins.Packaging.Tests;

/// <summary>Hostile and unusual inputs found in review: each must be a clean refusal or a correct answer, never an exception.</summary>
public sealed class HardeningTests : IDisposable
{
    private readonly TestPackages _packages = new();

    public void Dispose() => _packages.Dispose();

    [Fact]
    public void A_mixed_mode_assembly_counts_as_native_code()
    {
        // Clear the IL-only flag of a real assembly: what a C++/CLI image looks like to the verifier.
        var image = TestPackages.PluginAssembly.ToArray();
        using (var pe = new PEReader(new MemoryStream(TestPackages.PluginAssembly)))
        {
            var flags = pe.PEHeaders.CorHeaderStartOffset + 16;
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(flags), BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(flags)) & ~1u);
        }

        var files = TestPackages.Files();
        files["backend/Mixed.dll"] = image;

        AssertRefused(ReasonCodes.NativeLibrary, _packages.Build(files: files).Content);
    }

    [Fact]
    public void A_null_entry_in_the_signed_file_list_is_refused()
    {
        var list = """{"formatVersion":1,"id":"sample","version":"1.2.0","files":[null]}"""u8.ToArray();

        AssertRefused(ReasonCodes.BadFileList, TestPackages.Assemble(TestPackages.FilesWithManifest(), _packages.OfficialKey, fileList: list));
    }

    [Theory]
    [InlineData(""", "dependsOn": [null]""")]
    [InlineData(""", "requirements": [null]""")]
    public void A_null_list_entry_in_the_manifest_is_refused(string extra)
    {
        var files = TestPackages.FilesWithManifest(TestPackages.Manifest(extra: extra));

        AssertRefused(ReasonCodes.BadManifest, TestPackages.Assemble(files, _packages.OfficialKey));
    }

    [Fact]
    public void A_null_exported_assembly_is_refused()
    {
        var manifest = """
            { "formatVersion": 1, "id": "sample", "name": "Sample", "version": "1.2.0", "sdkVersion": ">=0.1.0",
              "backend": { "entryAssembly": "backend/Sample.Plugin.dll", "entryType": "Sample.Plugin", "exportedAssemblies": [null] } }
            """u8.ToArray();

        AssertRefused(ReasonCodes.BadManifest, TestPackages.Assemble(TestPackages.FilesWithManifest(manifest), _packages.OfficialKey));
    }

    [Theory]
    [InlineData("1.2.0-rc.A")]
    [InlineData("1.2.0+Build.7")]
    public void Versions_with_uppercase_prerelease_or_build_parts_verify(string version)
    {
        var package = _packages.Build(manifest: TestPackages.Manifest(version: version));

        var result = PackageVerifier.Verify(package.Content, package.FileName, _packages.Context());

        Assert.True(result.IsVerified, result.ToString());
        Assert.Equal($"sample-{version}.nkp", package.FileName);
    }

    [Theory]
    [InlineData(new byte[] { 0x99, 0x99, 0x08, 0x00 })]
    [InlineData(new byte[] { 0x99, 0x99, 0x00 })]
    public void A_truncated_extra_field_is_refused(byte[] extra)
    {
        var content = TestPackages.Assemble(TestPackages.FilesWithManifest(), _packages.OfficialKey,
            mutate: entries => entries.Select(e => e.Name == TestPackages.FrontendEntry ? e with { ExtraField = extra } : e));

        AssertRefused(ReasonCodes.BadZip, content);
    }

    [Fact]
    public void Numeric_prerelease_identifiers_compare_numerically_at_any_length()
    {
        Assert.True(SemanticVersion.Parse("1.0.0-9223372036854775808") < SemanticVersion.Parse("1.0.0-alpha"));
        Assert.True(SemanticVersion.Parse("1.0.0-9223372036854775807") < SemanticVersion.Parse("1.0.0-9223372036854775808"));
        Assert.True(SemanticVersion.Parse("1.0.0-99999999999999999999") > SemanticVersion.Parse("1.0.0-9223372036854775808"));
    }

    [Fact]
    public void A_256_bit_key_on_another_curve_cant_sign()
    {
        string pem;
        try
        {
            using var other = ECDsa.Create(ECCurve.CreateFromFriendlyName("secP256k1"));
            pem = other.ExportPkcs8PrivateKeyPem();
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or CryptographicException)
        {
            Assert.Skip("This platform's cryptography doesn't provide secp256k1.");
            return;
        }

        Assert.Throws<CryptographicException>(() => PluginSigningKey.FromPem(pem));
    }

    [Theory]
    [InlineData("FILES.JSON")]
    [InlineData("Files.Json.Sig")]
    [InlineData("NKeeper-Plugin.json")]
    public void The_builder_refuses_reserved_names_in_any_case(string path)
    {
        var files = TestPackages.Files();
        files[path] = [1];

        Assert.Throws<ArgumentException>(() => PackageBuilder.Build(TestPackages.Manifest(), files, _packages.OfficialKey));
    }

    [Fact]
    public void A_null_revoked_hash_is_a_configuration_error()
    {
        Assert.Throws<TrustConfigurationException>(() => Revocations.FromJson("""{ "revokedPackageSha256": [null] }"""u8));
    }

    [Fact]
    public void A_null_key_scope_is_a_configuration_error()
    {
        var json = $$"""{ "keyId": "{{_packages.ThirdPartyKey.KeyId}}", "publicKey": "{{Convert.ToBase64String(_packages.ThirdPartyKey.PublicKey.Span)}}", "allowedIds": [null] }""";

        Assert.Throws<TrustConfigurationException>(() => TrustedKey.FromKeyFile(Encoding.UTF8.GetBytes(json)));
    }

    private void AssertRefused(string reasonCode, byte[] content)
    {
        var result = PackageVerifier.Verify(content, TestPackages.FileName, _packages.Context());

        Assert.True(reasonCode == result.ReasonCode, $"Expected {reasonCode}, got {result}");
    }
}
