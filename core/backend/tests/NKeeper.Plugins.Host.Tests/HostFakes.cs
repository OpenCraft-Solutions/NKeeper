using Microsoft.Extensions.Logging.Abstractions;
using NKeeper.Abstractions;
using NKeeper.Plugins.Packaging;
using NKeeper.Testing;

namespace NKeeper.Plugins.Host.Tests;

internal sealed class MemoryStateStore : IPluginStateStore
{
    public Dictionary<string, PluginRecord> Records { get; } = new(StringComparer.Ordinal);

    public Task<IReadOnlyDictionary<string, PluginRecord>> LoadAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, PluginRecord>>(new Dictionary<string, PluginRecord>(Records));

    public Task SaveAsync(PluginRecord record, CancellationToken cancellationToken)
    {
        Records[record.Id] = record;
        return Task.CompletedTask;
    }
}

internal sealed class ListAuditSink : IPluginAuditSink
{
    public List<(string Action, string Target, string Detail)> Events { get; } = [];

    public Task WriteAsync(string action, string target, string detail, CancellationToken cancellationToken)
    {
        Events.Add((action, target, detail));
        return Task.CompletedTask;
    }
}

/// <summary>Reports every existing path as root-owned and safe, except the ones a test overrides.</summary>
internal sealed class FakeFileSystem : IFileSystemInspector
{
    public Dictionary<string, FileStatus> Overrides { get; } = new(StringComparer.Ordinal);

    public uint EffectiveUserId { get; set; } = 1000;

    public FileStatus? Stat(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (Overrides.TryGetValue(full, out var status))
        {
            return status;
        }

        return Directory.Exists(full) ? new FileStatus(0, 0x41ED) : File.Exists(full) ? new FileStatus(0, 0x81A4) : null;
    }
}

/// <summary>A plugin host over temporary directories, with generated official, third-party and development keys.</summary>
internal sealed class HostScenario : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("nkeeper-host-").FullName;

    public HostScenario()
    {
        Options = new PluginHostOptions
        {
            Directory = Path.Combine(_root, "plugins"),
            TrustDirectory = Path.Combine(_root, "trust"),
            DevelopmentTrustDirectory = Path.Combine(_root, "dev-trust"),
        };
        Directory.CreateDirectory(Options.Directory);
        Directory.CreateDirectory(Options.TrustDirectory);
        Directory.CreateDirectory(Options.DevelopmentTrustDirectory);
        File.WriteAllBytes(Path.Combine(Options.TrustDirectory, "acme.json"), TrustedKey.ThirdParty(ThirdPartyKey.PublicKey.Span, ["com.example.*"]).ToKeyFile());
        File.WriteAllBytes(Path.Combine(Options.DevelopmentTrustDirectory, "dev.json"), TrustedKey.Development(DevelopmentKey.PublicKey.Span).ToKeyFile());
    }

    public PluginSigningKey OfficialKey { get; } = PluginSigningKey.Generate();

    public PluginSigningKey ThirdPartyKey { get; } = PluginSigningKey.Generate();

    public PluginSigningKey DevelopmentKey { get; } = PluginSigningKey.Generate();

    public PluginHostOptions Options { get; }

    public MemoryStateStore Store { get; } = new();

    public ListAuditSink Audit { get; } = new();

    public FakeFileSystem FileSystem { get; } = new();

    /// <summary>Production on Linux by default, with the fake file system.</summary>
    public bool IsDevelopment { get; set; }

    public bool IsLinux { get; set; } = true;

    public DateTimeOffset Now { get; set; } = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    public string Install(BuiltPackage package) => package.WriteTo(Options.Directory);

    public PluginHost Host() => new(
        Options,
        new PluginHostEnvironment(
            IsDevelopment,
            IsLinux,
            SemanticVersion.Parse(SdkVersion.Current),
            SharedAssemblies.OfCurrentProcess(),
            [TrustedKey.Official(OfficialKey.PublicKey.Span)],
            Revocations.None),
        FileSystem,
        Store,
        Audit,
        NullLogger.Instance,
        new FixedTime(Now));

    public Task<PluginHostResult> LoadAsync() => Host().LoadAsync(TestContext.Current.CancellationToken);

    public void Dispose()
    {
        OfficialKey.Dispose();
        ThirdPartyKey.Dispose();
        DevelopmentKey.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
