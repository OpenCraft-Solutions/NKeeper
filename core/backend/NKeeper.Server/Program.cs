using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using NKeeper.Abstractions;
using NKeeper.Core.Persistence;
using NKeeper.Identity;
using NKeeper.Plugins.Host;
using NKeeper.Plugins.Packaging;
using NKeeper.Server;

// Formatting and comparisons never follow the server's locale.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

var builder = WebApplication.CreateBuilder(args);
var isDevelopment = builder.Environment.IsDevelopment();
var fileSystem = OperatingSystem.IsLinux() ? new LinuxFileSystem() : null;

// docs/plugin-security.md §4: the service never runs as root in production.
if (!isDevelopment && fileSystem?.EffectiveUserId == 0)
{
    throw new InvalidOperationException("NKeeper refuses to run as root. Run it as its own unprivileged user (the installer creates 'nkeeper').");
}

using var startupLogging = LoggerFactory.Create(logging => logging
    .AddConfiguration(builder.Configuration.GetSection("Logging"))
    .AddConsole());
var pluginLogger = startupLogging.CreateLogger("NKeeper.Plugins");

// The shell, if this server serves one, is checked before anything else (§7.3): an index.html with an
// inline script other than its import map stops the server here.
var frontendRoot = builder.Configuration.GetSection(FrontendOptions.SectionName).Get<FrontendOptions>()?.Root;
var shell = string.IsNullOrWhiteSpace(frontendRoot)
    ? null
    : ShellDocument.Load(Path.Combine(builder.Environment.ContentRootPath, frontendRoot));

// Startup order (§9): the core schema first, so plugin records can be read and written…
var connectionString = builder.Configuration.GetConnectionString("NKeeper")
    ?? throw new InvalidOperationException("ConnectionStrings:NKeeper is required.");
var database = CoreDbContext.CreateOptions(connectionString);
await using (var db = new CoreDbContext(database))
{
    await db.Database.MigrateAsync();
}

// …and the key ring brought in line with its certificate, before anything is encrypted with it
// (architecture.md §6.3)…
var keyRing = KeyRingCertificate.Load(builder.Configuration);
await new KeyRingEncryption(database, keyRing, startupLogging).SecureStoredKeysAsync(CancellationToken.None);

// …then plugins are verified and loaded, and register their services…
var pluginHost = new PluginHost(
    builder.Configuration.GetSection(PluginHostOptions.SectionName).Get<PluginHostOptions>() ?? new PluginHostOptions(),
    new PluginHostEnvironment(
        isDevelopment,
        OperatingSystem.IsLinux(),
        SemanticVersion.Parse(SdkVersion.Current),
        SharedAssemblies.OfCurrentProcess(),
        OfficialKeys.All,
        OfficialKeys.Revocations),
    fileSystem,
    new DbPluginStateStore(database),
    new DbPluginAuditSink(database, TimeProvider.System),
    pluginLogger,
    TimeProvider.System);
var plugins = await pluginHost.LoadAsync(CancellationToken.None);
await pluginHost.ConfigureServicesAsync(plugins, builder.Services, builder.Configuration, CancellationToken.None);

// Core's services are registered after the plugins', so a plugin can't replace them.
builder.Services.AddNKeeperIdentity(builder.Configuration, connectionString, keyRing);
builder.Services.AddSingleton(plugins);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false)));

// …and only then is the web application built.
var app = builder.Build();
await app.InitializeIdentityAsync(CancellationToken.None);

app.UseSecurityHeaders();
if (shell is not null)
{
    // The shell's own files need no sign-in, so they're served before it's checked.
    app.UseShell(shell);
}

app.UseNKeeperIdentity();

// Liveness only: the process is up and serving requests.
app.MapGet("/health/live", () => Results.Text("Healthy")).AllowAnonymous();

// Everything under /api needs a sign-in unless it opts out, plugin routes included (architecture.md §6.4).
var api = app.MapGroup("/api");
api.MapAuthEndpoints();
api.MapUserEndpoints();
api.MapPluginStatus();
app.MapPluginFrontends();
PluginHost.MapEndpoints(plugins, api, builder.Configuration);
if (shell is not null)
{
    app.MapShellFallback(shell);
}

await app.RunAsync();

/// <summary>Entry point marker for <c>WebApplicationFactory</c> in the integration tests.</summary>
public partial class Program;
