using System.Net;
using System.Net.Http.Headers;
using System.Xml;
using System.Xml.Linq;
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
/// GitHub's releases, from each repository's release feed (docs/architecture.md §9.1), not its REST API: without a
/// token the API allows 60 requests an hour per public address, which every machine behind it shares. The feed and the
/// downloads aren't part of that allowance.
/// </summary>
public sealed class GitHubReleases(HttpClient http) : IPluginReleaseSource
{
    public const int MaxFeedBytes = 2 * 1024 * 1024;
    /// <summary>The HTTP client GitHub expects: a User-Agent, and a bounded timeout.</summary>
    public static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = TimeSpan.FromMinutes(2),
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NKeeper", "2"));
        return client;
    }

    /// <summary>The repository's release feed; the files are at their conventional addresses.</summary>
    public async Task<IReadOnlyList<PluginRelease>> ListAsync(CatalogEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        byte[] feed;
        try
        {
            using var response = await http.GetAsync(new Uri($"https://github.com/{entry.Repository}/releases.atom"), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new ReleaseSourceException($"GitHub has no repository {entry.Repository}.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ReleaseSourceException(
                    response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                        ? "GitHub is refusing this server's requests for now; try again later."
                        : $"GitHub answered {(int)response.StatusCode} for {entry.Repository}'s release feed.");
            }

            feed = await ReadAsync(response, MaxFeedBytes, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new ReleaseSourceException($"Couldn't reach GitHub: {ex.Message}", ex);
        }

        return ParseFeed(entry.Id, entry.Repository, feed);
    }

    /// <summary>
    /// A release feed (Atom), as the plugin's releases: each entry's link to <c>releases/tag/v&lt;version&gt;</c> of this
    /// repository, with the package and its SHA-256 at their conventional download addresses. The feed doesn't say which
    /// are pre-releases, so a version with a pre-release part is left out, as the release workflow marks those.
    /// </summary>
    internal static IReadOnlyList<PluginRelease> ParseFeed(string id, string repository, byte[] atom)
    {
        XDocument document;
        try
        {
            using var stream = new MemoryStream(atom);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new ReleaseSourceException($"{repository}'s release feed isn't a feed.", ex);
        }

        XNamespace atomNs = "http://www.w3.org/2005/Atom";
        var prefix = $"https://github.com/{repository}/releases/tag/";
        var releases = new List<PluginRelease>();
        foreach (var link in document.Descendants(atomNs + "entry").SelectMany(e => e.Elements(atomNs + "link")))
        {
            var href = (string?)link.Attribute("href");
            if (href is null || !href.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var tag = Uri.UnescapeDataString(href[prefix.Length..]);
            if (!tag.StartsWith('v') || !SemanticVersion.TryParse(tag[1..], out var version) || version.IsPrerelease)
            {
                continue;
            }

            var name = PackageFormat.FileName(id, version.Text);
            var download = $"https://github.com/{repository}/releases/download/{Uri.EscapeDataString(tag)}/{name}";
            if (releases.All(r => r.Version != version))
            {
                releases.Add(new PluginRelease(version, new Uri(download), name, new Uri(download + ".sha256")));
            }
        }

        return [.. releases.OrderByDescending(r => r.Version)];
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
