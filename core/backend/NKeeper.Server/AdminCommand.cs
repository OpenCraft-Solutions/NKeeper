using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using NKeeper.Core.Persistence;
using NKeeper.Identity;
using NKeeper.Plugins.Host;

namespace NKeeper.Server;

/// <summary>
/// <c>nkeeper admin</c> (docs/architecture.md §8): what root can do when nobody can sign in. Today, resetting an
/// account's password, as the Users page does, for a lost administrator password.
/// </summary>
internal static class AdminCommand
{
    private const string Usage = """
        Usage: nkeeper admin <command>

          reset-password <user name>   sets a new random password, printed once; the user must change it at the next sign-in

        Run as root: it reads the server's settings file.
        """;

    /// <summary>Long enough for the policy (12) with room to spare, from letters and digits that read unambiguously.</summary>
    private const int PasswordLength = 20;
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";

    /// <summary>
    /// A random password the policy accepts: it needs a digit and a lower- and upper-case letter, which a purely random
    /// draw sometimes lacks, so one of each is placed, at random positions.
    /// </summary>
    internal static string NewPassword()
    {
        var chars = RandomNumberGenerator.GetString(Alphabet, PasswordLength).ToCharArray();
        string[] required = ["ABCDEFGHJKLMNPQRSTUVWXYZ", "abcdefghijkmnpqrstuvwxyz", "23456789"];
        var positions = Enumerable.Range(0, PasswordLength).ToArray();
        RandomNumberGenerator.Shuffle(positions.AsSpan());
        for (var i = 0; i < required.Length; i++)
        {
            chars[positions[i]] = RandomNumberGenerator.GetString(required[i], 1)[0];
        }

        return new string(chars);
    }

    public static Task<int> RunAsync(string[] args, CancellationToken cancellationToken) =>
        RunAsync(
            args,
            new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true).AddEnvironmentVariables().Build(),
            Console.Out,
            Console.Error,
            cancellationToken);

    internal static async Task<int> RunAsync(string[] args, IConfiguration configuration, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (args is not ["reset-password", var userName] || string.IsNullOrWhiteSpace(userName))
        {
            await error.WriteLineAsync(Usage);
            return 2;
        }

        var isDevelopment = string.Equals(configuration["ASPNETCORE_ENVIRONMENT"] ?? configuration["DOTNET_ENVIRONMENT"], "Development", StringComparison.OrdinalIgnoreCase);
        if (!isDevelopment && OperatingSystem.IsLinux() && new LinuxFileSystem().EffectiveUserId != 0)
        {
            await error.WriteLineAsync("Run it as root: sudo nkeeper admin reset-password <user name>");
            return 1;
        }

        if (configuration.GetConnectionString("NKeeper") is not { Length: > 0 } connectionString)
        {
            await error.WriteLineAsync("ConnectionStrings:NKeeper isn't set: run it as root on the NKeeper server, through /usr/bin/nkeeper.");
            return 1;
        }

        using var keyRing = KeyRingCertificate.Load(configuration);
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddNKeeperIdentity(configuration, connectionString, keyRing);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        if (await users.FindByNameAsync(userName.Trim()) is not { } user)
        {
            await error.WriteLineAsync($"There's no user named {userName.Trim()}.");
            return 1;
        }

        var password = NewPassword();
        await scope.ServiceProvider.GetRequiredService<UserAdministration>()
            .ResetPasswordAsync(user.Id, new ResetPasswordRequest { Password = password, MustChangePassword = true }, Guid.Empty, cancellationToken);
        output.WriteLine($"{user.UserName}'s password is now: {password}");
        output.WriteLine("It's shown once. They must change it at the next sign-in; their sessions have ended.");
        return 0;
    }
}
