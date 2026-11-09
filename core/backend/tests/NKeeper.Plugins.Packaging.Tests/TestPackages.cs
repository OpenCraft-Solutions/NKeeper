using System.Text;
using NKeeper.Plugins.Packaging.Zip;

namespace NKeeper.Plugins.Packaging.Tests;

/// <summary>A clock that always says the same time.</summary>
internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>Keys, contexts and package builders for the verifier tests.</summary>
internal sealed class TestPackages : IDisposable
{
    public const string Id = "sample";
    public const string Version = "1.2.0";
    public const string EntryAssembly = "backend/Sample.Plugin.dll";
    public const string FrontendEntry = "frontend/index.js";

    public static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A real managed assembly to stand in for a plugin's code (this test assembly).</summary>
    public static readonly byte[] PluginAssembly = File.ReadAllBytes(typeof(TestPackages).Assembly.Location);

    /// <summary>
    /// What a server shares with plugins: this process's trusted platform assemblies, except the test
    /// assembly itself, which the tests use as a plugin's own code.
    /// </summary>
    public static readonly IReadOnlySet<string> Shared = SharedAssemblies.OfCurrentProcess()
        .Where(name => name != typeof(TestPackages).Assembly.GetName().Name)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public PluginSigningKey OfficialKey { get; } = PluginSigningKey.Generate();

    public PluginSigningKey ThirdPartyKey { get; } = PluginSigningKey.Generate();

    public PluginSigningKey DevelopmentKey { get; } = PluginSigningKey.Generate();

    public PluginSigningKey UnknownKey { get; } = PluginSigningKey.Generate();

    public TrustStore Trust(Revocations? revocations = null, DateOnly? thirdPartyNotAfter = null) => new(
        [
            TrustedKey.Official(OfficialKey.PublicKey.Span, "test official"),
            TrustedKey.ThirdParty(ThirdPartyKey.PublicKey.Span, ["com.example.*"], "test third party", thirdPartyNotAfter),
            TrustedKey.Development(DevelopmentKey.PublicKey.Span, "test development"),
        ],
        revocations);

    public VerificationContext Context(bool development = false, string sdk = "0.1.0", TrustStore? trust = null, PackageLimits? limits = null) =>
        new(trust ?? Trust(), development, SemanticVersion.Parse(sdk), Shared, new FixedTime(Now))
        {
            Limits = limits ?? PackageLimits.Default,
        };

    public static byte[] Manifest(
        string id = Id,
        string version = Version,
        string sdk = ">=0.1.0 <0.2.0",
        string entryAssembly = EntryAssembly,
        string? frontend = FrontendEntry,
        string extra = "") =>
        Encoding.UTF8.GetBytes(
            $$"""
            { "formatVersion": 1, "id": "{{id}}", "name": "Sample", "version": "{{version}}", "sdkVersion": "{{sdk}}",
              "backend": { "entryAssembly": "{{entryAssembly}}", "entryType": "Sample.Plugin" }
              {{(frontend is null ? "" : $", \"frontend\": {{ \"entry\": \"{frontend}\" }}")}}{{extra}} }
            """);

    public static Dictionary<string, byte[]> Files() => new(StringComparer.Ordinal)
    {
        [EntryAssembly] = PluginAssembly,
        [FrontendEntry] = Encoding.UTF8.GetBytes("export default { id: 'sample' }\n"),
    };

    /// <summary>A valid package built the way the packaging tool builds one.</summary>
    public BuiltPackage Build(PluginSigningKey? key = null, byte[]? manifest = null, Dictionary<string, byte[]>? files = null) =>
        PackageBuilder.Build(manifest ?? Manifest(), files ?? Files(), key ?? OfficialKey);

    /// <summary>
    /// Assembles a package by hand, so tests can break exactly one thing: the files (manifest included)
    /// are listed and signed as given, then <paramref name="mutate"/> may change the archive's entries.
    /// </summary>
    public static byte[] Assemble(
        Dictionary<string, byte[]> files,
        PluginSigningKey key,
        string id = Id,
        string version = Version,
        byte[]? fileList = null,
        byte[]? signature = null,
        Func<List<ZipWriteEntry>, IEnumerable<ZipWriteEntry>>? mutate = null)
    {
        fileList ??= FileList.Create(id, version, files).ToCanonicalJson();
        signature ??= new PackageSignature(key.KeyId, PackageFormat.SignatureAlgorithm, Convert.ToBase64String(key.Sign(fileList))).ToJson();
        var entries = files.Select(f => new ZipWriteEntry(f.Key, f.Value)).ToList();
        entries.Add(new ZipWriteEntry(PackageFormat.FileListPath, fileList));
        entries.Add(new ZipWriteEntry(PackageFormat.SignaturePath, signature));
        return ZipWriter.Write(mutate is null ? entries : mutate(entries));
    }

    /// <summary>The default files plus the default manifest.</summary>
    public static Dictionary<string, byte[]> FilesWithManifest(byte[]? manifest = null)
    {
        var files = Files();
        files[PackageFormat.ManifestPath] = manifest ?? Manifest();
        return files;
    }

    public static string FileName => PackageFormat.FileName(Id, Version);

    public void Dispose()
    {
        OfficialKey.Dispose();
        ThirdPartyKey.Dispose();
        DevelopmentKey.Dispose();
        UnknownKey.Dispose();
    }
}
