using System.Reflection;
using System.Text;
using NKeeper.Plugins.Packaging;

namespace NKeeper.Testing;

/// <summary>Signed packages of the test plugin, built the way the packaging tool builds them.</summary>
public static class TestPlugins
{
    public const string HelloType = "NKeeper.TestPlugins.Hello.HelloPlugin";
    public const string NotAPluginType = "NKeeper.TestPlugins.Hello.NotAPlugin";
    public const string ThrowingConstructorType = "NKeeper.TestPlugins.Hello.ThrowingConstructorPlugin";
    public const string ThrowingConfigureType = "NKeeper.TestPlugins.Hello.ThrowingConfigurePlugin";
    public const string ThrowingContributeType = "NKeeper.TestPlugins.Hello.ThrowingContributePlugin";
    public const string DuplicateServiceTypeType = "NKeeper.TestPlugins.Hello.DuplicateServiceTypePlugin";
    public const string InvalidServiceTypeType = "NKeeper.TestPlugins.Hello.InvalidServiceTypePlugin";
    public const string QuietType = "NKeeper.TestPlugins.Hello.QuietPlugin";
    public const string EntryAssembly = "backend/NKeeper.TestPlugins.Hello.dll";
    public const string FrontendEntry = "frontend/index.js";

    /// <summary>The test plugin's frontend: an ES module importing the shared SDK by name, through the import map.</summary>
    public static string HelloFrontend(string id = "hello") =>
        $"import {{ definePlugin }} from '@nkeeper/sdk'\nexport default definePlugin({{ id: '{id}' }})\n";

    /// <summary>The compiled test plugin, embedded in this assembly when it's built.</summary>
    public static byte[] HelloAssembly { get; } = ReadHelloAssembly();

    private static byte[] ReadHelloAssembly()
    {
        using var stream = typeof(TestPlugins).Assembly.GetManifestResourceStream("NKeeper.TestPlugins.Hello.dll")
            ?? throw new InvalidOperationException("The test plugin's assembly isn't embedded in NKeeper.Testing.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>A signed package of the test plugin.</summary>
    public static BuiltPackage Package(
        PluginSigningKey key,
        string id = "hello",
        string version = "1.0.0",
        bool builtIn = true,
        string entryType = HelloType,
        string sdk = ">=1.0.0 <2.0.0",
        IReadOnlyDictionary<string, byte[]>? frontend = null,
        string frontendEntry = FrontendEntry,
        string? credentialRelease = null,
        params (string Id, string Range)[] dependsOn)
    {
        var dependencies = dependsOn.Length == 0
            ? string.Empty
            : $", \"dependsOn\": [{string.Join(", ", dependsOn.Select(d => $"{{ \"id\": \"{d.Id}\", \"version\": \"{d.Range}\" }}"))}]";
        var manifest = $$"""
            { "formatVersion": 1, "id": "{{id}}", "name": "Hello", "version": "{{version}}", "sdkVersion": "{{sdk}}",
              "builtIn": {{(builtIn ? "true" : "false")}}{{dependencies}}{{(credentialRelease is null ? "" : $", \"credentialRelease\": \"{credentialRelease}\"")}}{{(frontend is null ? "" : $", \"frontend\": {{ \"entry\": \"{frontendEntry}\" }}")}},
              "backend": { "entryAssembly": "{{EntryAssembly}}", "entryType": "{{entryType}}" } }
            """;
        var files = new Dictionary<string, byte[]> { [EntryAssembly] = HelloAssembly };
        foreach (var (path, content) in frontend ?? new Dictionary<string, byte[]>())
        {
            files[path] = content;
        }

        return PackageBuilder.Build(Encoding.UTF8.GetBytes(manifest), files, key);
    }

    /// <summary>Writes a package into <paramref name="directory"/> and returns its path.</summary>
    public static string WriteTo(this BuiltPackage package, string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, package.FileName);
        File.WriteAllBytes(path, package.Content);
        return path;
    }
}
