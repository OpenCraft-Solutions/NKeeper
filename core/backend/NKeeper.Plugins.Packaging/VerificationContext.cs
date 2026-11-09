namespace NKeeper.Plugins.Packaging;

/// <summary>Everything about the verifying server that a verification depends on.</summary>
/// <param name="Trust">The trusted keys and revocations.</param>
/// <param name="IsDevelopment">Whether the server runs in Development, the only place development keys count.</param>
/// <param name="SdkVersion">The plugin SDK version the server implements.</param>
/// <param name="SharedAssemblies">Simple names of assemblies the server shares with plugins (§7.1), compared case-insensitively.</param>
/// <param name="Time">The clock that key expiry is judged by.</param>
public sealed record VerificationContext(
    TrustStore Trust,
    bool IsDevelopment,
    SemanticVersion SdkVersion,
    IReadOnlySet<string> SharedAssemblies,
    TimeProvider Time)
{
    public PackageLimits Limits { get; init; } = PackageLimits.Default;
}

/// <summary>The simple names of the assemblies a process's default load context provides.</summary>
public static class SharedAssemblies
{
    /// <summary>
    /// The trusted platform assemblies of the current process: the .NET runtime, any shared frameworks
    /// (such as ASP.NET Core) and the application's own dependencies (§7.1).
    /// </summary>
    public static IReadOnlySet<string> OfCurrentProcess()
    {
        var paths = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return paths.Select(Path.GetFileNameWithoutExtension).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
