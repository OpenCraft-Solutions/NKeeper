using System.Security.Claims;
using System.Text.Json;
using NKeeper.Abstractions;
using NKeeper.Core.Audit;
using NKeeper.Core.Persistence;
using NKeeper.Plugins.Host;
using NKeeper.Plugins.Host.Store;
using NKeeper.Plugins.Packaging;

namespace NKeeper.Server;

/// <summary>What the plugins page shows of the catalog (docs/architecture.md §9.1).</summary>
/// <param name="Enabled">Whether the store is on: a catalog URL is set and the trust configuration could be read.</param>
/// <param name="CanRequest">Whether root's helper takes requests from the page; if not, the page shows the command instead.</param>
/// <param name="Pending">Plugins with a request the helper hasn't applied yet.</param>
public sealed record CatalogResponse(
    bool Enabled,
    bool CanRequest,
    string? CatalogUrl,
    DateTimeOffset? RefreshedAt,
    string? Error,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<CatalogPluginStatus> Plugins,
    IReadOnlyList<string> Pending,
    IReadOnlyList<CatalogRequestResult> Results);

/// <summary>What root's helper did with a request.</summary>
public sealed record CatalogRequestResult(string Id, string? Version, string Result, string Message, string? RequestedBy, DateTimeOffset At);

public sealed record CatalogInstallRequest(string Id);

/// <summary>
/// The server's side of the catalog (docs/architecture.md §9.1): it refreshes it shortly after starting, then daily and
/// on demand, verifying each offered package as at load, and leaves the page's install requests for root's helper. It
/// installs nothing itself.
/// </summary>
internal sealed class PluginCatalogService(
    PluginCatalogOptions options,
    PluginHostResult plugins,
    VerificationContext? context,
    IServiceScopeFactory scopes,
    ILogger<PluginCatalogService> logger,
    TimeProvider time) : BackgroundService
{
    private static readonly TimeSpan FirstRefresh = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(1);
    private static readonly TimeSpan ResultsInterval = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _refreshing = new(1, 1);
    private readonly HttpClient _http = GitHubReleases.CreateClient();
    private readonly List<CatalogRequestResult> _results = [];
    private GitHubReleases? _releases;
    private CatalogSnapshot _snapshot = new(null, null, [], []);

    private sealed record CatalogSnapshot(DateTimeOffset? RefreshedAt, string? Error, IReadOnlyList<string> Warnings, IReadOnlyList<CatalogPluginStatus> Plugins);

    public bool Enabled => options.CatalogUri is not null && context is not null;

    public bool CanRequest => Enabled && Directory.Exists(options.RequestDirectory);

    public CatalogResponse Snapshot()
    {
        var snapshot = _snapshot;
        IReadOnlyList<CatalogRequestResult> results;
        lock (_results)
        {
            results = [.. _results];
        }

        return new CatalogResponse(Enabled, CanRequest, options.CatalogUri?.ToString(), snapshot.RefreshedAt, snapshot.Error, snapshot.Warnings, snapshot.Plugins, Pending(), results);
    }

    /// <summary>Refreshes now (<c>update</c>), or waits for the refresh already running.</summary>
    public async Task<CatalogResponse> RefreshAsync(CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return Snapshot();
        }

        await _refreshing.WaitAsync(cancellationToken);
        try
        {
            _releases ??= new GitHubReleases(_http);
            var store = new PluginStore(string.Empty, options.CatalogUri!, _releases, context!);
            try
            {
                var catalog = await store.CatalogAsync(cancellationToken);
                var resolver = new CatalogResolver(_releases, context!);
                var statuses = await resolver.ResolveAsync(catalog, Installed(), cancellationToken);
                _snapshot = new CatalogSnapshot(time.GetUtcNow(), null, catalog.Warnings, statuses);
                logger.LogInformation("Refreshed the plugin catalog: {Count} plugins, {Offered} installable.", statuses.Count, statuses.Count(s => s.Available is not null));
            }
            catch (CatalogException ex)
            {
                // The last result stays, with when it was taken.
                _snapshot = _snapshot with { Error = ex.Message };
                logger.LogWarning("Couldn't refresh the plugin catalog: {Message}", ex.Message);
            }
        }
        finally
        {
            _refreshing.Release();
        }

        return Snapshot();
    }

    /// <summary>
    /// Leaves a request for root's helper: the version and package the last refresh offered, so the helper installs
    /// exactly what the administrator saw, or nothing.
    /// </summary>
    public async Task<IResult> RequestAsync(string id, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (!CanRequest)
        {
            return Results.Problem("This server's installer didn't set up install requests. Run nkeeper plugin install as root instead.", statusCode: StatusCodes.Status409Conflict);
        }

        if (!PluginId.IsValid(id) || _snapshot.Plugins.FirstOrDefault(p => p.Id == id)?.Available is not { } candidate)
        {
            return Results.Problem("The catalog offers nothing to install for that plugin. Check for updates first.", statusCode: StatusCodes.Status404NotFound);
        }

        var name = user.Identity?.Name ?? "administrator";
        var request = new InstallRequest(candidate.Id, candidate.Version, candidate.PackageSha256, name);
        var path = Path.Combine(options.RequestDirectory, candidate.Id + PluginStore.RequestExtension);
        var part = Path.Combine(options.RequestDirectory, $".{candidate.Id}.part");
        File.Delete(part);
        await File.WriteAllBytesAsync(part, JsonSerializer.SerializeToUtf8Bytes(request, Json), cancellationToken);
        // Renamed into place, so the helper, which starts when a request appears, never reads half of one.
        File.Move(part, path, overwrite: true);

        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IAuditLog>().RecordAsync(
            new AuditEntry(AuditActions.PluginInstallRequested, candidate.Id, $"{candidate.Version}, package {candidate.PackageSha256}"),
            cancellationToken);
        return Results.Accepted(value: Snapshot());
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextRefresh = time.GetUtcNow() + FirstRefresh;
        while (!stoppingToken.IsCancellationRequested)
        {
            await CollectResultsAsync(stoppingToken);
            if (Enabled && time.GetUtcNow() >= nextRefresh)
            {
                await RefreshAsync(stoppingToken);
                nextRefresh = time.GetUtcNow() + RefreshInterval;
            }

            try
            {
                await Task.Delay(ResultsInterval, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public override void Dispose()
    {
        _http.Dispose();
        _refreshing.Dispose();
        base.Dispose();
    }

    private Dictionary<string, SemanticVersion> Installed() =>
        plugins.Statuses
            .Where(s => s.State != PluginState.Missing && s.Version is not null && SemanticVersion.TryParse(s.Version, out _))
            .GroupBy(s => s.Id)
            .ToDictionary(g => g.Key, g => g.Select(s => SemanticVersion.Parse(s.Version!)).Max()!, StringComparer.Ordinal);

    private IReadOnlyList<string> Pending() =>
        CanRequest
            ? [.. Directory.EnumerateFiles(options.RequestDirectory, "*" + PluginStore.RequestExtension).Select(Path.GetFileNameWithoutExtension).OfType<string>().Order(StringComparer.Ordinal)]
            : [];

    /// <summary>Takes the helper's results: each is audited once, kept for the page, and removed.</summary>
    private async Task CollectResultsAsync(CancellationToken cancellationToken)
    {
        if (!CanRequest)
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(options.RequestDirectory, "*" + PluginStore.ResultExtension))
        {
            CatalogRequestResult? result = null;
            try
            {
                var info = new FileInfo(file);
                if (info.LinkTarget is null && info.Length < 16 * 1024)
                {
                    result = JsonSerializer.Deserialize<CatalogRequestResult>(await File.ReadAllBytesAsync(file, cancellationToken), Json);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Couldn't read the plugin helper's result {File}: {Message}", Path.GetFileName(file), ex.Message);
            }

            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Couldn't remove the plugin helper's result {File}: {Message}", Path.GetFileName(file), ex.Message);
                continue;
            }

            if (result is null || !PluginId.IsValid(result.Id))
            {
                continue;
            }

            lock (_results)
            {
                _results.Insert(0, result);
                if (_results.Count > 20)
                {
                    _results.RemoveAt(_results.Count - 1);
                }
            }

            var action = result.Result.ToLowerInvariant() switch
            {
                "installed" => AuditActions.PluginInstalled,
                "upgraded" => AuditActions.PluginUpgraded,
                _ => AuditActions.PluginInstallFailed,
            };
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IAuditLog>().RecordAsync(
                new AuditEntry(action, result.Id, $"{result.Version}: {result.Message}", UserName: result.RequestedBy is null ? null : $"{result.RequestedBy} (via root's helper)"),
                cancellationToken);
        }
    }
}

internal static class PluginCatalogEndpoints
{
    public static void MapPluginCatalog(this IEndpointRouteBuilder api)
    {
        var catalog = api.MapGroup("/plugins/catalog").RequireAuthorization(policy => policy.RequireRole(SystemRoles.Administrator));
        catalog.MapGet("", (PluginCatalogService service) => service.Snapshot());
        catalog.MapPost("/refresh", (PluginCatalogService service, CancellationToken cancellationToken) => service.RefreshAsync(cancellationToken));
        catalog.MapPost("/requests", (CatalogInstallRequest request, PluginCatalogService service, ClaimsPrincipal user, CancellationToken cancellationToken) =>
            service.RequestAsync(request.Id, user, cancellationToken));
    }
}
