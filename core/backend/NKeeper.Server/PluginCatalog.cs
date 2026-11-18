using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using NKeeper.Abstractions;
using NKeeper.Core.Audit;
using NKeeper.Core.Persistence;
using NKeeper.Plugins.Host;
using NKeeper.Plugins.Host.Store;
using NKeeper.Plugins.Packaging;
using TrustedKeyKind = NKeeper.Plugins.Packaging.TrustedKeyKind;

namespace NKeeper.Server;

/// <summary>One catalogued plugin, with the catalog it comes from (docs/architecture.md §9.1, §9.3).</summary>
/// <param name="Official">Whether it comes from NKeeper's own catalog.</param>
public sealed record CatalogListing(string Catalog, bool Official, CatalogPluginStatus Status);

/// <summary>A catalog the server reads: NKeeper's own, or one an administrator added (§9.3).</summary>
public sealed record CatalogSource(string Url, bool Official, string? Error, IReadOnlyList<string> Warnings);

/// <summary>A third-party key in the trust directory.</summary>
public sealed record TrustedKeyResponse(string KeyId, IReadOnlyList<string> AllowedIds, string? Comment, DateOnly? NotAfter);

/// <summary>What the plugins page shows of plugin administration (docs/architecture.md §9.1–§9.3).</summary>
/// <param name="Enabled">Whether the catalog is on: a catalog URL is set and the trust configuration could be read.</param>
/// <param name="CanRequest">Whether root's helper takes requests from the page; if not, the page shows commands instead.</param>
/// <param name="CanTrust">Whether root allowed the page to trust keys (<c>--allow-web-trust</c>).</param>
/// <param name="Switches">Each recorded plugin's enabled state, which takes effect at the next restart.</param>
/// <param name="Pending">Requests root's helper hasn't applied yet.</param>
public sealed record CatalogResponse(
    bool Enabled,
    bool CanRequest,
    bool CanTrust,
    DateTimeOffset? RefreshedAt,
    IReadOnlyList<CatalogSource> Catalogs,
    IReadOnlyList<CatalogListing> Plugins,
    IReadOnlyList<string> Conflicts,
    IReadOnlyDictionary<string, bool> Switches,
    IReadOnlyList<TrustedKeyResponse> Keys,
    IReadOnlyList<string> Pending,
    IReadOnlyList<CatalogRequestResult> Results);

/// <summary>What root's helper did with a request.</summary>
public sealed record CatalogRequestResult(string Id, string? Version, string Result, string Message, string? RequestedBy, DateTimeOffset At);

public sealed record CatalogInstallRequest(string Id);

public sealed record CatalogAddRequest(string Url);

public sealed record TrustKeyRequest(string KeyFile, string Password);

public sealed record UntrustKeyRequest(string KeyId, string Password);

/// <summary>What an upload turned out to be, and whether root's helper was asked to install it.</summary>
public sealed record UploadResponse(string? Id, string? Version, string? KeyId, bool Requested, string? ReasonCode, string Message);

/// <summary>
/// Plugin administration on the server's side (docs/architecture.md §9.1–§9.3): it refreshes the catalogs shortly after
/// starting, then daily and on demand, verifying each offered package as at load; records enabling and disabling; and
/// leaves requests for root's helper. It installs, removes and trusts nothing itself.
/// </summary>
internal sealed class PluginCatalogService(
    PluginCatalogOptions options,
    PluginHostOptions hostOptions,
    PluginHostResult plugins,
    VerificationContext? context,
    IServiceScopeFactory scopes,
    ILogger<PluginCatalogService> logger,
    TimeProvider time) : BackgroundService
{
    private const int MaxCatalogs = 20;
    private static readonly TimeSpan FirstRefresh = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(1);
    private static readonly TimeSpan ResultsInterval = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _refreshing = new(1, 1);
    private readonly SemaphoreSlim _catalogsFile = new(1, 1);
    private readonly HttpClient _http = GitHubReleases.CreateClient();
    private readonly List<CatalogRequestResult> _results = [];
    private GitHubReleases? _releases;
    private Snapshot _snapshot = new(null, [], [], []);

    private sealed record Snapshot(DateTimeOffset? RefreshedAt, IReadOnlyList<CatalogSource> Catalogs, IReadOnlyList<CatalogListing> Plugins, IReadOnlyList<string> Conflicts);

    public bool Enabled => options.CatalogUri is not null && context is not null;

    public bool CanRequest => context is not null && Directory.Exists(options.RequestDirectory);

    public bool CanTrust => CanRequest && options.AllowWebTrust;

    public async Task<CatalogResponse> ResponseAsync(CancellationToken cancellationToken)
    {
        var snapshot = _snapshot;
        IReadOnlyList<CatalogRequestResult> results;
        lock (_results)
        {
            results = [.. _results];
        }

        await using var scope = scopes.CreateAsyncScope();
        var records = await scope.ServiceProvider.GetRequiredService<IPluginStateStore>().LoadAsync(cancellationToken);
        return new CatalogResponse(
            Enabled,
            CanRequest,
            CanTrust,
            snapshot.RefreshedAt,
            snapshot.Catalogs,
            snapshot.Plugins,
            snapshot.Conflicts,
            records.ToDictionary(r => r.Key, r => r.Value.Enabled, StringComparer.Ordinal),
            TrustedKeys(),
            Pending(),
            results);
    }

    /// <summary>Refreshes every catalog now (<c>update</c>), or waits for the refresh already running.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (context is null)
        {
            return;
        }

        await _refreshing.WaitAsync(cancellationToken);
        try
        {
            _releases ??= new GitHubReleases(_http);
            var sources = new List<CatalogSource>();
            var listings = new List<CatalogListing>();
            var conflicts = new List<string>();
            var urls = (options.CatalogUri is { } official ? [(official, true)] : new List<(Uri, bool)>())
                .Concat((await AddedCatalogsAsync(cancellationToken)).Select(u => (new Uri(u), false)));
            foreach (var (url, isOfficial) in urls)
            {
                var store = new PluginStore(string.Empty, url, _releases, context);
                try
                {
                    var catalog = await store.CatalogAsync(cancellationToken);
                    sources.Add(new CatalogSource(url.ToString(), isOfficial, null, catalog.Warnings));
                    var resolver = new CatalogResolver(_releases, context);
                    var installed = Installed();
                    foreach (var entry in catalog.Entries)
                    {
                        if (listings.FirstOrDefault(l => l.Status.Id == entry.Id) is { } earlier)
                        {
                            // NKeeper's own catalog first, then catalogs in the order they were added (§9.3).
                            conflicts.Add($"{entry.Id} is in {url} too; it comes from {earlier.Catalog}.");
                            continue;
                        }

                        installed.TryGetValue(entry.Id, out var current);
                        listings.Add(new CatalogListing(url.ToString(), isOfficial, await resolver.ResolveAsync(entry, current, cancellationToken)));
                    }
                }
                catch (CatalogException ex)
                {
                    sources.Add(new CatalogSource(url.ToString(), isOfficial, ex.Message, []));
                    logger.LogWarning("Couldn't refresh the plugin catalog {Url}: {Message}", url, ex.Message);
                }
            }

            _snapshot = new Snapshot(time.GetUtcNow(), sources, listings, conflicts);
            logger.LogInformation("Refreshed {Catalogs} plugin catalog(s): {Count} plugins, {Offered} installable.", sources.Count, listings.Count, listings.Count(l => l.Status.Available is not null));
        }
        finally
        {
            _refreshing.Release();
        }
    }

    /// <summary>Adds a publisher's catalog (§9.3). It grants nothing by itself: its packages still need a trusted key.</summary>
    public async Task<IResult> AddCatalogAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || url.Length > 500)
        {
            return Results.Problem("A catalog's address is an https:// URL.", statusCode: StatusCodes.Status400BadRequest);
        }

        await _catalogsFile.WaitAsync(cancellationToken);
        try
        {
            var catalogs = (await AddedCatalogsAsync(cancellationToken)).ToList();
            if (uri == options.CatalogUri || catalogs.Contains(uri.ToString()))
            {
                return Results.Problem("That catalog is already added.", statusCode: StatusCodes.Status409Conflict);
            }

            if (catalogs.Count >= MaxCatalogs)
            {
                return Results.Problem($"At most {MaxCatalogs} catalogs can be added.", statusCode: StatusCodes.Status409Conflict);
            }

            catalogs.Add(uri.ToString());
            await SaveCatalogsAsync(catalogs, cancellationToken);
        }
        finally
        {
            _catalogsFile.Release();
        }

        await AuditAsync(new AuditEntry(AuditActions.PluginCatalogAdded, uri.ToString()), cancellationToken);
        await RefreshAsync(cancellationToken);
        return Results.Ok(await ResponseAsync(cancellationToken));
    }

    public async Task<IResult> RemoveCatalogAsync(string url, CancellationToken cancellationToken)
    {
        await _catalogsFile.WaitAsync(cancellationToken);
        try
        {
            var catalogs = (await AddedCatalogsAsync(cancellationToken)).ToList();
            if (!catalogs.Remove(url))
            {
                return Results.NotFound();
            }

            await SaveCatalogsAsync(catalogs, cancellationToken);
        }
        finally
        {
            _catalogsFile.Release();
        }

        await AuditAsync(new AuditEntry(AuditActions.PluginCatalogRemoved, url), cancellationToken);
        await RefreshAsync(cancellationToken);
        return Results.Ok(await ResponseAsync(cancellationToken));
    }

    /// <summary>
    /// Asks root's helper to install or upgrade a catalogued plugin: the version and package the last refresh offered,
    /// so the helper installs exactly what the administrator saw, or nothing.
    /// </summary>
    public async Task<IResult> RequestInstallAsync(string id, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (!PluginId.IsValid(id) || _snapshot.Plugins.FirstOrDefault(p => p.Status.Id == id) is not { Status.Available: { } candidate } listing)
        {
            return Results.Problem("The catalog offers nothing to install for that plugin. Check for updates first.", statusCode: StatusCodes.Status404NotFound);
        }

        var request = new PluginRequest(Name(user), PluginRequestActions.Install, candidate.Id, candidate.Version, candidate.PackageSha256, listing.Official ? null : listing.Catalog);
        return await RequestAsync(candidate.Id, request, null, new AuditEntry(AuditActions.PluginInstallRequested, candidate.Id, $"{candidate.Version}, package {candidate.PackageSha256}, from {listing.Catalog}"), cancellationToken);
    }

    /// <summary>Verifies an uploaded package as at load and, if it verifies, asks root's helper to install it (§9.3).</summary>
    public async Task<IResult> UploadAsync(HttpRequest http, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (context is null || !CanRequest)
        {
            return Results.Problem("This server's installer didn't set up requests to root's helper.", statusCode: StatusCodes.Status409Conflict);
        }

        var limit = context.Limits.MaxPackageBytes;
        if (!http.HasFormContentType)
        {
            return Results.Problem("Upload the package as a form's file, \"package\".", statusCode: StatusCodes.Status400BadRequest);
        }

        var form = await http.ReadFormAsync(new Microsoft.AspNetCore.Http.Features.FormOptions { MultipartBodyLengthLimit = limit + (64 * 1024) }, cancellationToken);
        if (form.Files.GetFile("package") is not { } file)
        {
            return Results.Problem("Upload the package as a form's file, \"package\".", statusCode: StatusCodes.Status400BadRequest);
        }

        if (file.Length > limit)
        {
            return Results.Problem($"A package is at most {limit} bytes.", statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        // The name is the package's own claim; the verifier checks it against the signed manifest.
        var name = file.FileName;
        var verification = PackageVerifier.Verify(bytes, Path.GetFileName(name), context);
        await AuditAsync(new AuditEntry(AuditActions.PluginUploaded, verification.Manifest?.Id ?? Path.GetFileName(name), $"{bytes.Length} bytes, sha256 {sha}: {verification}"), cancellationToken);
        if (!verification.IsVerified)
        {
            return Results.Json(new UploadResponse(null, null, null, false, verification.ReasonCode, verification.Message), statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var manifest = verification.Manifest!;
        var request = new PluginRequest(Name(user), PluginRequestActions.Upload, manifest.Id, manifest.Version.Text, sha);
        var result = await RequestAsync(manifest.Id, request, bytes, new AuditEntry(AuditActions.PluginInstallRequested, manifest.Id, $"{manifest.Version.Text}, uploaded package {sha}"), cancellationToken);
        return result is IStatusCodeHttpResult { StatusCode: StatusCodes.Status202Accepted }
            ? Results.Accepted(value: new UploadResponse(manifest.Id, manifest.Version.Text, verification.KeyId, true, null, $"{manifest.Id} {manifest.Version.Text} verified; the server is installing it."))
            : result;
    }

    public Task<IResult> RequestRemoveAsync(string id, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        !PluginId.IsValid(id)
            ? Task.FromResult(Results.NotFound())
            : RequestAsync(id, new PluginRequest(Name(user), PluginRequestActions.Remove, id), null, new AuditEntry(AuditActions.PluginRemoveRequested, id), cancellationToken);

    public Task<IResult> RequestRestartAsync(ClaimsPrincipal user, CancellationToken cancellationToken) =>
        RequestAsync("restart", new PluginRequest(Name(user), PluginRequestActions.Restart), null, new AuditEntry(AuditActions.PluginRestartRequested, "nkeeper"), cancellationToken);

    /// <summary>Asks root's helper to trust a third-party key: only where root allowed it, and with the administrator's password.</summary>
    public async Task<IResult> RequestTrustAsync(TrustKeyRequest body, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (!CanTrust)
        {
            return Results.Problem("This server doesn't allow trusting keys from the plugins page. Root can: sudo nkeeper plugin trust add <key file>.", statusCode: StatusCodes.Status403Forbidden);
        }

        if (await PasswordProblemAsync(user, body.Password) is { } problem)
        {
            return problem;
        }

        TrustedKey key;
        try
        {
            key = TrustedKey.FromKeyFile(System.Text.Encoding.UTF8.GetBytes(body.KeyFile ?? string.Empty));
        }
        catch (TrustConfigurationException ex)
        {
            return Results.Problem($"That isn't a key file NKeeper can trust: {ex.Message}", statusCode: StatusCodes.Status400BadRequest);
        }

        if (key.Kind != TrustedKeyKind.ThirdParty)
        {
            return Results.Problem("Only a third-party key can be trusted from the plugins page.", statusCode: StatusCodes.Status400BadRequest);
        }

        return await RequestAsync(
            $"trust-{key.KeyId}",
            new PluginRequest(Name(user), PluginRequestActions.Trust, KeyFile: body.KeyFile),
            null,
            new AuditEntry(AuditActions.PluginTrustRequested, key.KeyId, $"may sign {string.Join(", ", key.AllowedIds)}"),
            cancellationToken);
    }

    public async Task<IResult> RequestUntrustAsync(UntrustKeyRequest body, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (!CanTrust)
        {
            return Results.Problem("This server doesn't allow untrusting keys from the plugins page. Root can: sudo nkeeper plugin trust remove <key ID>.", statusCode: StatusCodes.Status403Forbidden);
        }

        if (!SigningKeys.IsKeyId(body.KeyId))
        {
            return Results.NotFound();
        }

        return await PasswordProblemAsync(user, body.Password) ?? await RequestAsync(
            $"untrust-{body.KeyId}",
            new PluginRequest(Name(user), PluginRequestActions.Untrust, KeyId: body.KeyId),
            null,
            new AuditEntry(AuditActions.PluginTrustRequested, body.KeyId, "untrust"),
            cancellationToken);
    }

    /// <summary>Enables or disables a plugin, with those its dependencies bring along (§9.2), at the next restart.</summary>
    public async Task<IResult> SwitchAsync(string id, bool enable, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (!plugins.Manifests.ContainsKey(id))
        {
            return Results.Problem("Only an installed plugin that verified can be turned on or off.", statusCode: StatusCodes.Status404NotFound);
        }

        await using var scope = scopes.CreateAsyncScope();
        var switches = new PluginSwitches(
            scope.ServiceProvider.GetRequiredService<IPluginStateStore>(),
            scope.ServiceProvider.GetRequiredService<IPluginAuditSink>(),
            time);
        var changed = await switches.SwitchAsync(id, enable, Name(user), plugins.Manifests, cancellationToken);
        return Results.Ok(new { changed, catalog = await ResponseAsync(cancellationToken) });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextRefresh = time.GetUtcNow() + FirstRefresh;
        while (!stoppingToken.IsCancellationRequested)
        {
            await CollectResultsAsync(stoppingToken);
            if (context is not null && time.GetUtcNow() >= nextRefresh)
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
        _catalogsFile.Dispose();
        base.Dispose();
    }

    private static string Name(ClaimsPrincipal user) => user.Identity?.Name ?? "administrator";

    private async Task<IResult?> PasswordProblemAsync(ClaimsPrincipal principal, string? password)
    {
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.GetUserAsync(principal);
        return user is not null && !string.IsNullOrEmpty(password) && await users.CheckPasswordAsync(user, password)
            ? null
            : Results.Problem("Your password is needed to change which keys this server trusts.", statusCode: StatusCodes.Status403Forbidden);
    }

    /// <summary>Leaves a request (and an uploaded package beside it) for root's helper, renamed into place so it's never read half-written.</summary>
    private async Task<IResult> RequestAsync(string name, PluginRequest request, byte[]? package, AuditEntry audit, CancellationToken cancellationToken)
    {
        if (!CanRequest)
        {
            return Results.Problem("This server's installer didn't set up requests to root's helper. Run the nkeeper plugin command as root instead.", statusCode: StatusCodes.Status409Conflict);
        }

        var directory = options.RequestDirectory;
        if (package is not null)
        {
            await WriteAtomicallyAsync(Path.Combine(directory, name + PackageFormat.Extension), package, cancellationToken);
        }

        // The request last: it's what starts the helper.
        await WriteAtomicallyAsync(Path.Combine(directory, name + PluginStore.RequestExtension), JsonSerializer.SerializeToUtf8Bytes(request, Json), cancellationToken);
        await AuditAsync(audit, cancellationToken);
        return Results.Accepted(value: await ResponseAsync(cancellationToken));
    }

    private static async Task WriteAtomicallyAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        var part = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.part");
        File.Delete(part);
        await File.WriteAllBytesAsync(part, content, cancellationToken);
        File.Move(part, path, overwrite: true);
    }

    private async Task AuditAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IAuditLog>().RecordAsync(entry, cancellationToken);
    }

    private async Task<IReadOnlyList<string>> AddedCatalogsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return File.Exists(options.CatalogsFile)
                ? JsonSerializer.Deserialize<List<string>>(await File.ReadAllBytesAsync(options.CatalogsFile, cancellationToken), Json)?
                    .Where(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps).Take(MaxCatalogs).ToList() ?? []
                : [];
        }
        catch (JsonException ex)
        {
            logger.LogWarning("Couldn't read the added plugin catalogs: {Message}", ex.Message);
            return [];
        }
    }

    private Task SaveCatalogsAsync(List<string> catalogs, CancellationToken cancellationToken) =>
        WriteAtomicallyAsync(options.CatalogsFile, JsonSerializer.SerializeToUtf8Bytes(catalogs, Json), cancellationToken);

    private IReadOnlyList<TrustedKeyResponse> TrustedKeys() =>
        [.. PluginStore.TrustedKeysIn(hostOptions.TrustDirectory)
            .Where(k => k.Key.Kind == TrustedKeyKind.ThirdParty)
            .Select(k => new TrustedKeyResponse(k.Key.KeyId, k.Key.AllowedIds, k.Key.Comment, k.Key.NotAfter))];

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

            if (result is null)
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
                "removed" => AuditActions.PluginRemoved,
                "trusted" => AuditActions.PluginSignerTrusted,
                "untrusted" => AuditActions.PluginSignerUntrusted,
                "restarting" => AuditActions.PluginRestarted,
                _ => AuditActions.PluginRequestFailed,
            };
            await AuditAsync(
                new AuditEntry(action, result.Id, $"{result.Version}: {result.Message}", UserName: result.RequestedBy is null ? null : $"{result.RequestedBy} (via root's helper)"),
                cancellationToken);
        }
    }
}

internal static class PluginCatalogEndpoints
{
    public static void MapPluginCatalog(this IEndpointRouteBuilder api)
    {
        var admin = api.MapGroup("/plugins").RequireAuthorization(policy => policy.RequireRole(SystemRoles.Administrator));
        admin.MapGet("/catalog", (PluginCatalogService service, CancellationToken ct) => service.ResponseAsync(ct));
        admin.MapPost("/catalog/refresh", async (PluginCatalogService service, CancellationToken ct) =>
        {
            await service.RefreshAsync(ct);
            return await service.ResponseAsync(ct);
        });
        admin.MapPost("/catalog/catalogs", (CatalogAddRequest body, PluginCatalogService service, CancellationToken ct) => service.AddCatalogAsync(body.Url, ct));
        admin.MapDelete("/catalog/catalogs", (string url, PluginCatalogService service, CancellationToken ct) => service.RemoveCatalogAsync(url, ct));
        admin.MapPost("/catalog/requests", (CatalogInstallRequest body, PluginCatalogService service, ClaimsPrincipal user, CancellationToken ct) => service.RequestInstallAsync(body.Id, user, ct));
        admin.MapPost("/upload", (HttpRequest request, PluginCatalogService service, ClaimsPrincipal user, CancellationToken ct) => service.UploadAsync(request, user, ct))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(PackageLimits.Default.MaxPackageBytes));
        admin.MapPost("/{id}/enable", (string id, PluginCatalogService service, ClaimsPrincipal user, CancellationToken ct) => service.SwitchAsync(id, true, user, ct));
        admin.MapPost("/{id}/disable", (string id, PluginCatalogService service, ClaimsPrincipal user, CancellationToken ct) => service.SwitchAsync(id, false, user, ct));
        admin.MapPost("/{id}/remove", (string id, PluginCatalogService service, ClaimsPrincipal user, CancellationToken ct) => service.RequestRemoveAsync(id, user, ct));
        admin.MapPost("/restart", (PluginCatalogService service, ClaimsPrincipal user, CancellationToken ct) => service.RequestRestartAsync(user, ct));
        admin.MapPost("/trust", (TrustKeyRequest body, PluginCatalogService service, ClaimsPrincipal user, CancellationToken ct) => service.RequestTrustAsync(body, user, ct));
        admin.MapPost("/untrust", (UntrustKeyRequest body, PluginCatalogService service, ClaimsPrincipal user, CancellationToken ct) => service.RequestUntrustAsync(body, user, ct));
    }
}
