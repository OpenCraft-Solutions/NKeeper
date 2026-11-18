using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using NKeeper.Plugins.Packaging;

namespace NKeeper.Plugins.Host.Store;

/// <summary>One release of a catalogued plugin: its version (from the tag), its package, and its SHA-256 file.</summary>
public sealed record PluginRelease(SemanticVersion Version, Uri Package, string PackageName, Uri? Sha256);

/// <summary>Where the catalog, releases and packages come from: GitHub, or a test's stand-in.</summary>
public interface IPluginReleaseSource
{
    /// <summary>A repository's releases that carry the plugin's package, newest first. Drafts and pre-releases aren't.</summary>
    Task<IReadOnlyList<PluginRelease>> ListAsync(CatalogEntry entry, CancellationToken cancellationToken);

    /// <summary>Downloads a file over HTTPS, refusing one larger than <paramref name="maxBytes"/>.</summary>
    Task<byte[]> DownloadAsync(Uri uri, long maxBytes, CancellationToken cancellationToken);
}

/// <summary>A download or a GitHub request failed.</summary>
public sealed class ReleaseSourceException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// GitHub's releases, through its REST API without a token (docs/architecture.md §9.1). Each repository's listing is
/// kept with its ETag, so asking again about an unchanged repository costs nothing against the rate limit.
/// </summary>
public sealed class GitHubReleases(HttpClient http) : IPluginReleaseSource
{
    public const int MaxListingBytes = 4 * 1024 * 1024;
    private const int PageSize = 30;

    private readonly ConcurrentDictionary<string, (string ETag, byte[] Body)> _listings = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The HTTP client GitHub expects: a User-Agent, its JSON media type, and a bounded timeout.</summary>
    public static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = TimeSpan.FromMinutes(2),
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NKeeper", "2"));
        return client;
    }

    public async Task<IReadOnlyList<PluginRelease>> ListAsync(CatalogEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var uri = new Uri($"https://api.github.com/repos/{entry.Repository}/releases?per_page={PageSize}");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        _listings.TryGetValue(entry.Repository, out var cached);
        if (cached.ETag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);
        }

        byte[] body;
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotModified && cached.Body is not null)
            {
                body = cached.Body;
            }
            else if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new ReleaseSourceException($"GitHub has no repository {entry.Repository}.");
            }
            else if (!response.IsSuccessStatusCode)
            {
                throw new ReleaseSourceException(
                    response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                        ? "GitHub's rate limit for this server's address is used up; try again in an hour."
                        : $"GitHub answered {(int)response.StatusCode} for {entry.Repository}'s releases.");
            }
            else
            {
                body = await ReadAsync(response, MaxListingBytes, cancellationToken);
                if (response.Headers.ETag is { } etag)
                {
                    _listings[entry.Repository] = (etag.ToString(), body);
                }
            }
        }
        catch (HttpRequestException ex)
        {
            throw new ReleaseSourceException($"Couldn't reach GitHub: {ex.Message}", ex);
        }

        return ParseReleases(entry.Id, body);
    }

    public async Task<byte[]> DownloadAsync(Uri uri, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ReleaseSourceException($"Only HTTPS downloads are allowed, not {uri}.");
        }

        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.RequestMessage?.RequestUri is { } final && final.Scheme != Uri.UriSchemeHttps)
            {
                throw new ReleaseSourceException($"{uri} redirected away from HTTPS.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ReleaseSourceException($"{uri} answered {(int)response.StatusCode}.");
            }

            return await ReadAsync(response, maxBytes, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new ReleaseSourceException($"Couldn't download {uri}: {ex.Message}", ex);
        }
    }

    /// <summary>GitHub's releases listing, as the plugin's releases: tag <c>v&lt;version&gt;</c> with <c>&lt;id&gt;-&lt;version&gt;.nkp</c>.</summary>
    internal static IReadOnlyList<PluginRelease> ParseReleases(string id, ReadOnlySpan<byte> json)
    {
        var releases = new List<PluginRelease>();
        try
        {
            using var document = JsonDocument.Parse(json.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new ReleaseSourceException("GitHub's releases listing isn't a list.");
            }

            foreach (var release in document.RootElement.EnumerateArray())
            {
                if (Bool(release, "draft") || Bool(release, "prerelease")
                    || String(release, "tag_name") is not { } tag || !tag.StartsWith('v')
                    || !SemanticVersion.TryParse(tag[1..], out var version)
                    || !release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var name = PackageFormat.FileName(id, version.Text);
                Uri? package = null, sha = null;
                foreach (var asset in assets.EnumerateArray())
                {
                    var assetName = String(asset, "name");
                    if (String(asset, "browser_download_url") is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                    {
                        if (assetName == name)
                        {
                            package = uri;
                        }
                        else if (assetName == name + ".sha256")
                        {
                            sha = uri;
                        }
                    }
                }

                if (package is not null)
                {
                    releases.Add(new PluginRelease(version, package, name, sha));
                }
            }
        }
        catch (JsonException ex)
        {
            throw new ReleaseSourceException("GitHub's releases listing isn't JSON.", ex);
        }

        return [.. releases.OrderByDescending(r => r.Version)];
    }

    private static bool Bool(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? String(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static async Task<byte[]> ReadAsync(HttpResponseMessage response, long maxBytes, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > maxBytes)
        {
            throw new ReleaseSourceException($"{response.RequestMessage?.RequestUri} is larger than {maxBytes} bytes.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new ReleaseSourceException($"{response.RequestMessage?.RequestUri} is larger than {maxBytes} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
