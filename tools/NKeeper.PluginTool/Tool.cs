using System.Globalization;
using NKeeper.Abstractions;
using NKeeper.Plugins.Packaging;

namespace NKeeper.PluginTool;

/// <summary>The <c>nkeeper-plugin</c> command line: keygen, pack and verify (docs/plugin-security.md §10).</summary>
internal static class Tool
{
    public const int Ok = 0;
    public const int Refused = 1;
    public const int UsageError = 2;

    private const string Usage = """
        nkeeper-plugin: build, sign and verify NKeeper plugin packages.

          keygen --out <dir> --name <name> [--allow <pattern>]... [--not-after <yyyy-mm-dd>] [--comment <text>]
              A new signing key. Writes <name>.private.pem (owner-only) and <name>.trust.json.
              Without --allow it is a development key, trusted only by servers in Development.
              With --allow it is a third-party key limited to those IDs (e.g. com.example.*).
          keygen --install --out <dir> --name <name> --allow <id>... [--comment <text>]
              A one-time install key (plugin-security.md §6.1) for the built-in plugins a native install
              built: it may sign exactly the IDs given, official ones included. Sign the packages, then
              delete the private key.
          keygen --release --out <dir> --name <name>
              The official release key (decision D12). Writes <name>.private.pem and
              <name>.public.txt (key ID and public key, to embed in the server). Keep exactly two
              copies of the private key: the protected release environment and an offline backup.

          pack --manifest <nkeeper-plugin.json> --backend <dir> [--frontend <dir>] --key <private.pem> --out <dir>
               [--shared-list <file>]
              Builds <id>-<version>.nkp from the manifest and the backend/frontend folders, signs it,
              and checks the result with the verifier.

          verify <package.nkp> [--trust <dir>] [--official <public.txt>]... [--development] [--sdk <version>]
                 [--shared-list <file>]
              Runs the server's checks and prints the result. Exits 0 if verified, 1 if refused.

        Shared assemblies: the tool can't see a server's exact set. It assumes the .NET runtime and the
        ASP.NET Core shared framework; --shared-list adds names, one per line (e.g. exported from the
        target server). The server's own check when it loads the plugin is the one that counts.
        """;

    /// <param name="sharedAssemblies">
    /// What counts as shared with the server (§7.1). By default, the .NET runtime and the ASP.NET Core
    /// shared framework; <c>--shared-list</c> adds to it either way.
    /// </param>
    public static int Run(string[] args, TextWriter output, TextWriter error, IReadOnlySet<string>? sharedAssemblies = null)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                output.WriteLine(Usage);
                return args.Length == 0 ? UsageError : Ok;
            }

            var options = Options.Parse(args[1..]);
            var shared = new HashSet<string>(sharedAssemblies ?? DefaultSharedAssemblies(), StringComparer.OrdinalIgnoreCase);
            if (options.Get("shared-list") is { } list)
            {
                shared.UnionWith(File.ReadLines(list).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')));
            }

            return args[0] switch
            {
                "keygen" => KeyGen(options, output),
                "pack" => Pack(options, shared, output, error),
                "verify" => Verify(options, shared, output),
                _ => throw new UsageException($"Unknown command '{args[0]}'."),
            };
        }
        catch (UsageException ex)
        {
            error.WriteLine(ex.Message);
            error.WriteLine("Run 'nkeeper-plugin --help' for usage.");
            return UsageError;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or FormatException or TrustConfigurationException or System.Security.Cryptography.CryptographicException)
        {
            error.WriteLine($"error: {ex.Message}");
            return UsageError;
        }
    }

    private static int KeyGen(Options options, TextWriter output)
    {
        var directory = options.Required("out");
        var name = options.Required("name");
        Directory.CreateDirectory(directory);
        using var key = PluginSigningKey.Generate();
        var privatePath = Path.Combine(directory, $"{name}.private.pem");
        WriteNew(privatePath, System.Text.Encoding.ASCII.GetBytes(key.ExportPrivateKeyPem()), ownerOnly: true);

        if (options.Has("release"))
        {
            var publicPath = Path.Combine(directory, $"{name}.public.txt");
            WriteNew(publicPath, System.Text.Encoding.ASCII.GetBytes($"keyId: {key.KeyId}\npublicKey: {Convert.ToBase64String(key.PublicKey.Span)}\n"), ownerOnly: false);
            output.WriteLine($"Release key {key.KeyId}:");
            output.WriteLine($"  private: {privatePath}");
            output.WriteLine($"  public:  {publicPath}");
            output.WriteLine("Keep exactly two copies of the private key: the protected release environment and an offline backup. Never commit it.");
            return Ok;
        }

        var allowed = options.All("allow");
        DateOnly? notAfter = options.Get("not-after") is { } date
            ? DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;
        var trusted = options.Has("install")
            ? notAfter is null && allowed.Count > 0
                ? TrustedKey.Install(key.PublicKey.Span, allowed, options.Get("comment") ?? "install key")
                : throw new UsageException("An install key needs --allow with the exact IDs it may sign, and no --not-after.")
            : allowed.Count > 0
            ? TrustedKey.ThirdParty(key.PublicKey.Span, allowed, options.Get("comment"), notAfter)
            : notAfter is null
                ? TrustedKey.Development(key.PublicKey.Span, options.Get("comment") ?? "development key")
                : throw new UsageException("--not-after applies to third-party keys (with --allow) only.");
        var trustPath = Path.Combine(directory, $"{name}.trust.json");
        WriteNew(trustPath, trusted.ToKeyFile(), ownerOnly: false);

        output.WriteLine($"{trusted.Kind} key {key.KeyId}:");
        output.WriteLine($"  private: {privatePath}");
        output.WriteLine($"  trust:   {trustPath}");
        return Ok;
    }

    private static int Pack(Options options, IReadOnlySet<string> shared, TextWriter output, TextWriter error)
    {
        var manifest = File.ReadAllBytes(options.Required("manifest"));
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        AddFolder(files, options.Required("backend"), PackageFormat.BackendFolder);
        if (options.Get("frontend") is { } frontend)
        {
            AddFolder(files, frontend, PackageFormat.FrontendFolder);
        }

        using var key = PluginSigningKey.FromPem(File.ReadAllText(options.Required("key")));
        var package = PackageBuilder.Build(manifest, files, key);

        // Check the result with the server's verifier, trusting only the key that signed it.
        var check = PackageVerifier.Verify(
            package.Content,
            package.FileName,
            new VerificationContext(
                new TrustStore([TrustedKey.Development(key.PublicKey.Span)]),
                IsDevelopment: true,
                SemanticVersion.Parse(options.Get("sdk") ?? SdkVersion.Current),
                shared,
                TimeProvider.System));
        if (!check.IsVerified)
        {
            error.WriteLine($"The built package doesn't verify: {check}");
            return Refused;
        }

        var directory = options.Required("out");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, package.FileName);
        WriteNew(path, package.Content, ownerOnly: false, overwrite: options.Has("force"));
        output.WriteLine($"{path}");
        output.WriteLine($"  {check.Manifest!.Id} {check.Manifest.Version}, {files.Count + 1} files, signed by {key.KeyId}, sha256 {check.PackageSha256}");
        return Ok;
    }

    private static int Verify(Options options, IReadOnlySet<string> shared, TextWriter output)
    {
        var path = options.Positional ?? throw new UsageException("verify needs a package file.");
        var keys = new List<TrustedKey>();
        var revocations = Revocations.None;
        if (options.Get("trust") is { } trustDirectory)
        {
            foreach (var file in Directory.EnumerateFiles(trustDirectory, "*.json").Order(StringComparer.Ordinal))
            {
                if (Path.GetFileName(file) == "revoked.json")
                {
                    revocations = revocations.Union(Revocations.FromJson(File.ReadAllBytes(file)));
                }
                else
                {
                    keys.Add(TrustedKey.FromKeyFile(File.ReadAllBytes(file)));
                }
            }
        }

        foreach (var official in options.All("official"))
        {
            keys.Add(TrustedKey.Official(ReadPublicKey(official)));
        }

        var result = PackageVerifier.Verify(
            File.ReadAllBytes(path),
            Path.GetFileName(path),
            new VerificationContext(
                new TrustStore(keys, revocations),
                options.Has("development"),
                SemanticVersion.Parse(options.Get("sdk") ?? SdkVersion.Current),
                shared,
                TimeProvider.System));

        output.WriteLine(result.IsVerified ? $"verified: {result.Message}" : $"refused ({result.ReasonCode}): {result.Message}");
        if (result.PackageSha256 is { } hash)
        {
            output.WriteLine($"  sha256 {hash}");
        }

        return result.IsVerified ? Ok : Refused;
    }

    private static byte[] ReadPublicKey(string path)
    {
        var line = File.ReadLines(path).FirstOrDefault(l => l.StartsWith("publicKey:", StringComparison.Ordinal))
            ?? throw new UsageException($"'{path}' has no 'publicKey:' line.");
        return Convert.FromBase64String(line["publicKey:".Length..].Trim());
    }

    private static void AddFolder(Dictionary<string, byte[]> files, string folder, string prefix)
    {
        if (!Directory.Exists(folder))
        {
            throw new UsageException($"Folder '{folder}' doesn't exist.");
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
            files[prefix + relative] = File.ReadAllBytes(file);
        }
    }

    /// <summary>
    /// The .NET runtime this tool runs on, plus the ASP.NET Core shared framework of the same major
    /// version, which every NKeeper server also provides to plugins.
    /// </summary>
    private static HashSet<string> DefaultSharedAssemblies()
    {
        var names = new HashSet<string>(SharedAssemblies.OfCurrentProcess(), StringComparer.OrdinalIgnoreCase);

        // The runtime lives in <dotnet>/shared/Microsoft.NETCore.App/<version>/; ASP.NET Core beside it.
        var runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var aspNetCore = Path.GetFullPath(Path.Combine(runtime, "..", "..", "Microsoft.AspNetCore.App"));
        if (Directory.Exists(aspNetCore))
        {
            var latest = Directory.GetDirectories(aspNetCore)
                .Select(d => (Path: d, Version: Version.TryParse(Path.GetFileName(d), out var v) ? v : null))
                .Where(d => d.Version?.Major == Environment.Version.Major)
                .MaxBy(d => d.Version);
            if (latest.Path is not null)
            {
                names.UnionWith(Directory.EnumerateFiles(latest.Path, "*.dll").Select(Path.GetFileNameWithoutExtension).OfType<string>());
            }
        }

        return names;
    }

    private static void WriteNew(string path, byte[] content, bool ownerOnly, bool overwrite = false)
    {
        var mode = overwrite ? FileMode.Create : FileMode.CreateNew;
        if (ownerOnly && OperatingSystem.IsWindows())
        {
            // Windows has no Unix mode: create the file with an ACL that grants only the current user
            // access and inherits nothing from the folder, before any key material is written.
            var security = new System.Security.AccessControl.FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var user = System.Security.Principal.WindowsIdentity.GetCurrent().User
                ?? throw new UnauthorizedAccessException("Can't determine the current Windows user to restrict the key file to.");
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                user, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            using var restricted = System.IO.FileSystemAclExtensions.Create(
                new FileInfo(path), mode, System.Security.AccessControl.FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, security);
            restricted.Write(content);
            return;
        }

        var options = new FileStreamOptions
        {
            Mode = mode,
            Access = FileAccess.Write,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = ownerOnly
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite
                : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        }

        using var stream = new FileStream(path, options);
        stream.Write(content);
    }
}

/// <summary>A command-line mistake: printed with a pointer to the usage text.</summary>
internal sealed class UsageException(string message) : Exception(message);

/// <summary><c>--name value</c> options, repeatable ones, flags, and one positional argument.</summary>
internal sealed class Options
{
    private static readonly HashSet<string> Flags = ["release", "development", "force", "install"];
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);

    public string? Positional { get; private set; }

    public static Options Parse(string[] args)
    {
        var options = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                options.Positional = options.Positional is null ? arg : throw new UsageException($"Unexpected argument '{arg}'.");
                continue;
            }

            var name = arg[2..];
            var value = Flags.Contains(name) ? "true"
                : i + 1 < args.Length ? args[++i]
                : throw new UsageException($"{arg} needs a value.");
            if (!options._values.TryGetValue(name, out var list))
            {
                options._values[name] = list = [];
            }

            list.Add(value);
        }

        return options;
    }

    public bool Has(string name) => _values.ContainsKey(name);

    public string? Get(string name) => _values.TryGetValue(name, out var list) ? list[^1] : null;

    public IReadOnlyList<string> All(string name) => _values.TryGetValue(name, out var list) ? list : [];

    public string Required(string name) => Get(name) ?? throw new UsageException($"--{name} is required.");
}
