using System.Globalization;

// Formatting and comparisons never follow the server's locale.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

// Liveness only: the process is up and serving requests. Readiness (database, plugins) comes with them.
app.MapGet("/health/live", () => Results.Text("Healthy"));

await app.RunAsync();

/// <summary>Entry point marker for <c>WebApplicationFactory</c> in the integration tests.</summary>
public partial class Program;
