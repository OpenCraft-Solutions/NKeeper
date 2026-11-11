using System.Text;
using NKeeper.PluginTool;

namespace NKeeper.Plugins.Packaging.Tests;

/// <summary>The command line end to end: keygen, pack and verify against real files.</summary>
public sealed class ToolTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("nkeeper-plugin-tool-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void A_development_key_packs_a_plugin_that_verifies_only_in_Development()
    {
        var keys = Path.Combine(_root, "keys");
        Assert.Equal(Tool.Ok, Run("keygen", "--out", keys, "--name", "dev"));

        var package = PackSample(Path.Combine(keys, "dev.private.pem"));

        Assert.Equal(Tool.Ok, Run("verify", package, "--trust", keys, "--development"));
        var (code, output) = RunWithOutput("verify", package, "--trust", keys);
        Assert.Equal(Tool.Refused, code);
        Assert.Contains("refused (untrusted_key)", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_third_party_key_is_limited_to_its_ids()
    {
        var keys = Path.Combine(_root, "keys");
        Assert.Equal(Tool.Ok, Run("keygen", "--out", keys, "--name", "acme", "--allow", "com.example.*"));

        var inScope = PackSample(Path.Combine(keys, "acme.private.pem"), id: "com.example.sample");
        var outOfScope = PackSample(Path.Combine(keys, "acme.private.pem"), id: "sample");

        Assert.Equal(Tool.Ok, Run("verify", inScope, "--trust", keys));
        var (code, output) = RunWithOutput("verify", outOfScope, "--trust", keys);
        Assert.Equal(Tool.Refused, code);
        Assert.Contains("refused (key_scope)", output, StringComparison.Ordinal);
    }

    [Fact]
    public void An_install_key_signs_the_official_IDs_it_names_outside_development()
    {
        var keys = Path.Combine(_root, "install");
        Assert.Equal(Tool.Ok, Run("keygen", "--install", "--out", keys, "--name", "install", "--allow", "sample"));

        var named = PackSample(Path.Combine(keys, "install.private.pem"), id: "sample");
        var other = PackSample(Path.Combine(keys, "install.private.pem"), id: "other");

        Assert.Equal(Tool.Ok, Run("verify", named, "--trust", keys));
        var (code, output) = RunWithOutput("verify", other, "--trust", keys);
        Assert.Equal(Tool.Refused, code);
        Assert.Contains("refused (key_scope)", output, StringComparison.Ordinal);
        Assert.Equal(Tool.UsageError, Run("keygen", "--install", "--out", Path.Combine(_root, "unscoped"), "--name", "install"));
    }

    [Fact]
    public void A_release_key_is_trusted_as_official()
    {
        var keys = Path.Combine(_root, "release");
        Assert.Equal(Tool.Ok, Run("keygen", "--release", "--out", keys, "--name", "release"));

        var package = PackSample(Path.Combine(keys, "release.private.pem"));

        Assert.Equal(Tool.Ok, Run("verify", package, "--official", Path.Combine(keys, "release.public.txt")));
    }

    [Fact]
    public void A_tampered_package_is_refused()
    {
        var keys = Path.Combine(_root, "keys");
        Run("keygen", "--out", keys, "--name", "dev");
        var package = PackSample(Path.Combine(keys, "dev.private.pem"));
        var bytes = File.ReadAllBytes(package);
        var marker = Encoding.UTF8.GetBytes("sample frontend");
        var at = bytes.AsSpan().IndexOf(marker);
        Assert.True(at >= 0, "The frontend file should be stored uncompressed.");
        bytes[at] = (byte)'S';
        File.WriteAllBytes(package, bytes);

        var (code, output) = RunWithOutput("verify", package, "--trust", keys, "--development");

        Assert.Equal(Tool.Refused, code);
        Assert.Contains("refused (bad_zip)", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Keygen_never_overwrites_a_key()
    {
        var keys = Path.Combine(_root, "keys");
        Assert.Equal(Tool.Ok, Run("keygen", "--out", keys, "--name", "dev"));
        var original = File.ReadAllText(Path.Combine(keys, "dev.private.pem"));

        Assert.Equal(Tool.UsageError, Run("keygen", "--out", keys, "--name", "dev"));
        Assert.Equal(original, File.ReadAllText(Path.Combine(keys, "dev.private.pem")));
    }

    [Fact]
    public void A_private_key_is_readable_by_its_owner_only()
    {
        var keys = Path.Combine(_root, "keys");
        Assert.Equal(Tool.Ok, Run("keygen", "--out", keys, "--name", "dev"));
        var path = Path.Combine(keys, "dev.private.pem");

        if (OperatingSystem.IsWindows())
        {
            // An explicit, non-inherited ACL with a single rule for the current user.
            var security = new FileInfo(path).GetAccessControl();
            var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(System.Security.Principal.SecurityIdentifier))
                .Cast<System.Security.AccessControl.FileSystemAccessRule>()
                .ToList();
            Assert.True(security.AreAccessRulesProtected);
            var rule = Assert.Single(rules);
            Assert.Equal(System.Security.Principal.WindowsIdentity.GetCurrent().User, rule.IdentityReference);
            Assert.False(rule.IsInherited);
        }
        else
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    [Fact]
    public void A_shared_list_from_the_server_is_applied()
    {
        var keys = Path.Combine(_root, "keys");
        Run("keygen", "--out", keys, "--name", "dev");
        var package = PackSample(Path.Combine(keys, "dev.private.pem"));
        var list = Path.Combine(_root, "shared.txt");
        File.WriteAllLines(list, ["# exported from the server", typeof(ToolTests).Assembly.GetName().Name!]);

        var (code, output) = RunWithOutput("verify", package, "--trust", keys, "--development", "--shared-list", list);

        Assert.Equal(Tool.Refused, code);
        Assert.Contains("refused (shared_assembly)", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("pack", "--manifest")]
    [InlineData("keygen", "--out", "x")]
    public void Usage_errors_exit_with_2(params string[] args)
    {
        Assert.Equal(Tool.UsageError, Run(args));
    }

    private string PackSample(string keyPath, string id = TestPackages.Id)
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "src-" + Guid.NewGuid().ToString("N"))).FullName;
        var backend = Directory.CreateDirectory(Path.Combine(source, "backend")).FullName;
        var frontend = Directory.CreateDirectory(Path.Combine(source, "frontend")).FullName;
        File.WriteAllBytes(Path.Combine(backend, "Sample.Plugin.dll"), TestPackages.PluginAssembly);
        File.WriteAllText(Path.Combine(frontend, "index.js"), "/* sample frontend */");
        var manifest = Path.Combine(source, "nkeeper-plugin.json");
        File.WriteAllBytes(manifest, TestPackages.Manifest(id: id));
        var output = Path.Combine(_root, "out");

        var (code, text) = RunWithOutput("pack", "--manifest", manifest, "--backend", backend, "--frontend", frontend, "--key", keyPath, "--out", output, "--force");
        Assert.True(code == Tool.Ok, text);
        return Path.Combine(output, PackageFormat.FileName(id, TestPackages.Version));
    }

    private static int Run(params string[] args) => RunWithOutput(args).Code;

    private static (int Code, string Output) RunWithOutput(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = Tool.Run(args, output, error, TestPackages.Shared);
        return (code, output.ToString() + error);
    }
}
