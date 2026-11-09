using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Plugins.Host;
using NKeeper.Plugins.Packaging;
using NKeeper.Testing;

namespace NKeeper.Server.Tests;

/// <summary>
/// A real server process in Development, with its own database and plugin directories and a
/// development key, ready to have packages installed before it starts.
/// </summary>
internal sealed class ServerScenario : IAsyncDisposable
{
    private readonly string _root;
    private WebApplicationFactory<Program>? _factory;

    public ServerScenario(string connectionString)
    {
        ConnectionString = connectionString;

        // Inside the build output, not /tmp: on Linux the location checks run, and /tmp is world-writable.
        _root = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "scenarios", Guid.NewGuid().ToString("N"))).FullName;
        PluginDirectory = Directory.CreateDirectory(Path.Combine(_root, "plugins")).FullName;
        TrustDirectory = Directory.CreateDirectory(Path.Combine(_root, "trust")).FullName;
        DevelopmentTrustDirectory = Directory.CreateDirectory(Path.Combine(_root, "dev-trust")).FullName;
        File.WriteAllBytes(Path.Combine(DevelopmentTrustDirectory, "dev.json"), TrustedKey.Development(DevelopmentKey.PublicKey.Span).ToKeyFile());
        if (OperatingSystem.IsLinux())
        {
            foreach (var directory in new[] { _root, PluginDirectory, TrustDirectory, DevelopmentTrustDirectory })
            {
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    public string ConnectionString { get; }

    public string PluginDirectory { get; }

    public string TrustDirectory { get; }

    public string DevelopmentTrustDirectory { get; }

    public PluginSigningKey DevelopmentKey { get; } = PluginSigningKey.Generate();

    /// <summary>A built shell for the server to serve (Frontend:Root); none for an API-only server.</summary>
    public string? FrontendRoot { get; set; }

    /// <summary>A shell directory under this scenario, with the given index.html and a hashed asset.</summary>
    public string CreateShell(string indexHtml)
    {
        var root = Directory.CreateDirectory(Path.Combine(_root, "shell")).FullName;
        Directory.CreateDirectory(Path.Combine(root, "assets"));
        File.WriteAllText(Path.Combine(root, "index.html"), indexHtml);
        File.WriteAllText(Path.Combine(root, "assets", "app-1a2b3c.js"), "console.log('shell')");
        FrontendRoot = root;
        return root;
    }

    public string Install(BuiltPackage package)
    {
        var path = package.WriteTo(PluginDirectory);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return path;
    }

    public const string AdminUserName = "admin";
    public const string AdminEmail = "admin@example.com";
    public const string AdminPassword = "Admin-Passw0rd-1";

    /// <summary>Extra settings for the server, applied before it starts.</summary>
    public Dictionary<string, string?> Settings { get; } = new(StringComparer.Ordinal);

    /// <summary>Starts the server (once) and returns a client for it, which keeps cookies unless told not to.</summary>
    public HttpClient Client(bool handleCookies = true)
    {
        _factory ??= new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Development");
            web.UseSetting("ConnectionStrings:NKeeper", ConnectionString);

            // The test server is plain http, so the session cookie can't require HTTPS.
            web.UseSetting("Authentication:RequireSecureCookies", "false");
            web.UseSetting("BootstrapAdmin:Enabled", "true");
            web.UseSetting("BootstrapAdmin:UserName", AdminUserName);
            web.UseSetting("BootstrapAdmin:Email", AdminEmail);
            web.UseSetting("BootstrapAdmin:Password", AdminPassword);
            foreach (var (key, value) in Settings)
            {
                web.UseSetting(key, value);
            }
            web.UseSetting("Plugins:Directory", PluginDirectory);
            web.UseSetting("Plugins:TrustDirectory", TrustDirectory);
            web.UseSetting("Plugins:DevelopmentTrustDirectory", DevelopmentTrustDirectory);
            if (FrontendRoot is not null)
            {
                web.UseSetting("Frontend:Root", FrontendRoot);
            }

            if (OperatingSystem.IsLinux())
            {
                web.UseSetting("Plugins:DevelopmentOwnerUid", new LinuxFileSystem().EffectiveUserId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        });
        return _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = handleCookies, AllowAutoRedirect = false });
    }

    /// <summary>A client signed in as the bootstrap administrator.</summary>
    public async Task<HttpClient> SignedInClientAsync()
    {
        var client = Client();
        using var response = await client.LoginAsync(AdminUserName, AdminPassword);
        response.EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>Creates an account directly, as an administrator would.</summary>
    public async Task<Guid> CreateUserAsync(string userName, string password, Action<NKeeper.Core.Persistence.ApplicationUser>? configure = null)
    {
        Client().Dispose();
        await using var scope = Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<NKeeper.Core.Persistence.ApplicationUser>>();
        var user = new NKeeper.Core.Persistence.ApplicationUser { UserName = userName, Email = $"{userName}@example.com", EmailConfirmed = true };
        configure?.Invoke(user);
        var result = await users.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join(" ", result.Errors.Select(e => e.Description)));
        return user.Id;
    }

    /// <summary>Makes an account an administrator directly.</summary>
    public async Task AddToAdministratorsAsync(string userName)
    {
        await using var scope = Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<NKeeper.Core.Persistence.ApplicationUser>>();
        var user = await users.FindByNameAsync(userName) ?? throw new InvalidOperationException($"No user {userName}.");
        Assert.True((await users.AddToRoleAsync(user, NKeeper.Core.Persistence.SystemRoles.Administrator)).Succeeded);
    }

    /// <summary>Changes an account directly, as an administrator would.</summary>
    public async Task UpdateUserAsync(string userName, Action<NKeeper.Core.Persistence.ApplicationUser> change)
    {
        await using var scope = Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<NKeeper.Core.Persistence.ApplicationUser>>();
        var user = await users.FindByNameAsync(userName) ?? throw new InvalidOperationException($"No user {userName}.");
        change(user);
        Assert.True((await users.UpdateAsync(user)).Succeeded);
    }

    public IServiceProvider Services => _factory?.Services ?? throw new InvalidOperationException("The server hasn't started.");

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        DevelopmentKey.Dispose();
        Directory.Delete(_root, recursive: true);
    }
}
