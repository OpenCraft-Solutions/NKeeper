using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace NKeeper.Testing;

/// <summary>What a <see cref="TestServer"/> starts with.</summary>
public sealed class TestServerOptions
{
    /// <summary>The PostgreSQL database to use (see <see cref="PostgresFixture.CreateDatabaseAsync"/>). Required.</summary>
    public required string ConnectionString { get; init; }

    /// <summary>Plugin packages (.nkp) to install before the server starts, for example the ones a plugin's build wrote.</summary>
    public IList<string> PluginPackages { get; } = [];

    /// <summary>
    /// Trust files (<c>*.trust.json</c>) of the development keys the packages are signed with. By default, the ones in
    /// the nearest <c>.dev/keys</c> above the test assemblies, where NKeeper.Plugin.Build puts the key it creates.
    /// </summary>
    public IList<string>? DevelopmentTrustFiles { get; init; }

    /// <summary>Extra settings, by configuration key (<c>Guacd:Address</c>), applied before the server starts.</summary>
    public IDictionary<string, string?> Settings { get; } = new Dictionary<string, string?>(StringComparer.Ordinal);
}

/// <summary>
/// A real NKeeper server in its own process, in Development, with its own plugin and trust directories, ready for
/// plugin tests (docs/architecture.md §9). The package carries a published server, so a plugin repository needs
/// nothing of core's source to use it. Start it with <see cref="StartAsync"/> after installing the plugins to test:
/// <code>
/// var options = new TestServerOptions { ConnectionString = await postgres.CreateDatabaseAsync() };
/// foreach (var package in DevelopmentKeys.BuiltPackages()) options.PluginPackages.Add(package);
/// await using var server = await TestServer.StartAsync(options);
/// using var client = await server.SignedInClientAsync();
/// </code>
/// </summary>
public sealed class TestServer : IAsyncDisposable
{
    public const string AdminUserName = "admin";
    public const string AdminEmail = "admin@example.com";
    public const string AdminPassword = "Admin-Passw0rd-1";

    private const string ServerSetting = "NKeeper.TestServer";
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(90);

    private readonly string _root;
    private readonly Process _process;
    private readonly ConcurrentQueue<string> _log = new();

    private TestServer(string root, Process process, Uri baseAddress)
    {
        _root = root;
        _process = process;
        BaseAddress = baseAddress;
    }

    public Uri BaseAddress { get; }

    /// <summary>What the server wrote to its output so far (the last lines), for a failure message.</summary>
    public string Log => string.Join(Environment.NewLine, _log);

    /// <summary>Installs the packages, starts the server and waits until it answers.</summary>
    public static async Task<TestServer> StartAsync(TestServerOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var serverDll = LocateServer();

        // Inside the build output, not /tmp: on Linux the location checks run, and /tmp is world-writable.
        var root = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "test-servers", Guid.NewGuid().ToString("N"))).FullName;
        var plugins = Directory.CreateDirectory(Path.Combine(root, "plugins")).FullName;
        var trust = Directory.CreateDirectory(Path.Combine(root, "trust")).FullName;
        var developmentTrust = Directory.CreateDirectory(Path.Combine(root, "dev-trust")).FullName;
        try
        {
            foreach (var package in options.PluginPackages)
            {
                var path = Path.Combine(plugins, Path.GetFileName(package));
                File.Copy(package, path);
                RestrictToOwner(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            var trustFiles = options.DevelopmentTrustFiles ?? DevelopmentKeys.TrustFiles();
            if (trustFiles.Count == 0)
            {
                throw new InvalidOperationException("No development trust file: the plugin packages can't be verified. Build the plugin first (it creates .dev/keys), or set DevelopmentTrustFiles.");
            }

            foreach (var file in trustFiles)
            {
                File.Copy(file, Path.Combine(developmentTrust, Path.GetFileName(file)));
            }

            foreach (var directory in new[] { root, plugins, trust, developmentTrust })
            {
                RestrictToOwner(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var port = FreeTcpPort();
            var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ConnectionStrings:NKeeper"] = options.ConnectionString,
                ["Authentication:RequireSecureCookies"] = "false",
                ["BootstrapAdmin:Enabled"] = "true",
                ["BootstrapAdmin:UserName"] = AdminUserName,
                ["BootstrapAdmin:Email"] = AdminEmail,
                ["BootstrapAdmin:Password"] = AdminPassword,
                ["Plugins:Directory"] = plugins,
                ["Plugins:TrustDirectory"] = trust,
                ["Plugins:DevelopmentTrustDirectory"] = developmentTrust,

                // Tests check health on demand, so nothing probes in the background.
                ["Health:Enabled"] = "false",
            };
            if (OperatingSystem.IsLinux())
            {
                settings["Plugins:DevelopmentOwnerUid"] = GetEffectiveUserId().ToString(CultureInfo.InvariantCulture);
            }

            foreach (var (key, value) in options.Settings)
            {
                settings[key] = value;
            }

            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = Path.GetDirectoryName(serverDll)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            };
            start.ArgumentList.Add(serverDll);
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}";
            foreach (var (key, value) in settings)
            {
                start.Environment[key.Replace(":", "__", StringComparison.Ordinal)] = value;
            }

            var process = new Process { StartInfo = start };
            var server = new TestServer(root, process, new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/"));
            process.OutputDataReceived += (_, e) => server.Append(e.Data);
            process.ErrorDataReceived += (_, e) => server.Append(e.Data);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try
            {
                await server.WaitUntilReadyAsync(cancellationToken);
            }
            catch
            {
                await server.DisposeAsync();
                throw;
            }

            return server;
        }
        catch
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }

            throw;
        }
    }

    /// <summary>A client with its own cookies, not signed in.</summary>
    public TestServerClient CreateClient() => new(BaseAddress);

    /// <summary>A client signed in as the first administrator.</summary>
    public async Task<TestServerClient> SignedInClientAsync(CancellationToken cancellationToken = default)
    {
        var client = CreateClient();
        try
        {
            using var response = await client.SendAsync(
                HttpMethod.Post, "/api/auth/login", new { userNameOrEmail = AdminUserName, password = AdminPassword, rememberMe = false }, cancellationToken);
            response.EnsureSuccessStatusCode();
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }

        _process.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void Append(string? line)
    {
        if (line is null)
        {
            return;
        }

        _log.Enqueue(line);
        while (_log.Count > 200)
        {
            _log.TryDequeue(out _);
        }
    }

    private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        using var client = new HttpClient { BaseAddress = BaseAddress, Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow + StartTimeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_process.HasExited)
            {
                throw new InvalidOperationException($"The test server exited with code {_process.ExitCode}:{Environment.NewLine}{Log}");
            }

            try
            {
                using var response = await client.GetAsync(new Uri("/api/auth/antiforgery", UriKind.Relative), cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Slow to answer.
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"The test server didn't start in {StartTimeout.TotalSeconds:0} seconds:{Environment.NewLine}{Log}");
            }

            await Task.Delay(250, cancellationToken);
        }
    }

    /// <summary>
    /// The server to run: a published copy, which the NKeeper.Testing package's build targets name in the test
    /// project's runtime configuration, or, inside NKeeper's own repository, its build output.
    /// </summary>
    private static string LocateServer()
    {
        var path = AppContext.GetData(ServerSetting) as string
            ?? typeof(TestServer).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "NKeeperTestServer")?.Value;
        return path is not null && File.Exists(path)
            ? path
            : throw new InvalidOperationException($"The test server isn't at '{path}'. Is the NKeeper.Testing package restored, and its build targets imported?");
    }

    private static void RestrictToOwner(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path, mode);
        }
    }

    private static int FreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
}

/// <summary>An HTTP client for a <see cref="TestServer"/>, with cookies, that sends the antiforgery token the API requires on unsafe requests.</summary>
public sealed class TestServerClient : IDisposable
{
    private readonly HttpClient _http;

    internal TestServerClient(Uri baseAddress) =>
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true }) { BaseAddress = baseAddress };

    /// <summary>The underlying client, for requests that need no token (GET).</summary>
    public HttpClient Http => _http;

    /// <summary>Sends a request the way the frontend SDK does: an unsafe method carries a fresh antiforgery token, and a body is JSON.</summary>
    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(method);
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (method != HttpMethod.Get && method != HttpMethod.Head && method != HttpMethod.Options)
        {
            using var tokenResponse = await _http.GetAsync(new Uri("/api/auth/antiforgery", UriKind.Relative), cancellationToken);
            tokenResponse.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(cancellationToken));
            request.Headers.Add(json.RootElement.GetProperty("headerName").GetString()!, json.RootElement.GetProperty("token").GetString()!);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _http.SendAsync(request, cancellationToken);
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Where NKeeper.Plugin.Build puts what a plugin's tests need, found from the test assemblies.</summary>
public static class DevelopmentKeys
{
    /// <summary>The development keys' trust files in the nearest <c>.dev/keys</c> above the test assemblies.</summary>
    public static IList<string> TrustFiles() =>
        FindUpward(Path.Combine(".dev", "keys")) is { } directory ? [.. Directory.EnumerateFiles(directory, "*.trust.json")] : [];

    /// <summary>The packages in the nearest <c>artifacts/plugins</c> above the test assemblies, where NKeeper.Plugin.Build writes them.</summary>
    public static IList<string> BuiltPackages() =>
        FindUpward(Path.Combine("artifacts", "plugins")) is { } directory ? [.. Directory.EnumerateFiles(directory, "*.nkp")] : [];

    private static string? FindUpward(string relative)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
