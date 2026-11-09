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
    public const string EntryAssembly = "backend/NKeeper.TestPlugins.Hello.dll";

    /// <summary>The compiled test plugin, read from its build output.</summary>
    public static byte[] HelloAssembly { get; } = File.ReadAllBytes(
        typeof(TestPlugins).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "HelloPluginPath").Value!);

    /// <summary>A signed package of the test plugin.</summary>
    public static BuiltPackage Package(
        PluginSigningKey key,
        string id = "hello",
        string version = "1.0.0",
        bool builtIn = true,
        string entryType = HelloType,
        string sdk = ">=0.1.0 <0.2.0",
        params (string Id, string Range)[] dependsOn)
    {
        var dependencies = dependsOn.Length == 0
            ? string.Empty
            : $", \"dependsOn\": [{string.Join(", ", dependsOn.Select(d => $"{{ \"id\": \"{d.Id}\", \"version\": \"{d.Range}\" }}"))}]";
        var manifest = $$"""
            { "formatVersion": 1, "id": "{{id}}", "name": "Hello", "version": "{{version}}", "sdkVersion": "{{sdk}}",
              "builtIn": {{(builtIn ? "true" : "false")}}{{dependencies}},
              "backend": { "entryAssembly": "{{EntryAssembly}}", "entryType": "{{entryType}}" } }
            """;
        return PackageBuilder.Build(Encoding.UTF8.GetBytes(manifest), new Dictionary<string, byte[]> { [EntryAssembly] = HelloAssembly }, key);
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
