namespace NKeeper.Plugins.Host.Store;

/// <summary>The plugin catalog's settings, from <c>Plugins:Catalog</c> (docs/architecture.md §9.1).</summary>
public sealed class PluginCatalogOptions
{
    public const string SectionName = "Plugins:Catalog";

    public const string DefaultUrl = "https://raw.githubusercontent.com/OpenCraft-Solutions/NKeeper-Plugins/main/catalog.json";

    /// <summary>The catalog file, over HTTPS. Empty turns the store off.</summary>
    public string? Url { get; set; } = DefaultUrl;

    /// <summary>Where the plugins page leaves requests for root's helper, in the service's own directory.</summary>
    public string RequestDirectory { get; set; } = "/var/lib/nkeeper/plugin-requests";

    /// <summary>The catalogs administrators added on the plugins page (§9.3), in the service's own directory.</summary>
    public string CatalogsFile { get; set; } = "/var/lib/nkeeper/plugin-catalogs.json";

    /// <summary>
    /// Whether root allowed the plugins page to trust third-party keys on this server (<c>setup.sh --allow-web-trust</c>,
    /// §9.3). Read by the server, which offers it, and by root's helper, which refuses trust requests without it.
    /// </summary>
    public bool AllowWebTrust { get; set; }

    /// <summary>The catalog's URL, if the store is on: only an absolute HTTPS URL counts.</summary>
    public Uri? CatalogUri =>
        Uri.TryCreate(Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;
}
