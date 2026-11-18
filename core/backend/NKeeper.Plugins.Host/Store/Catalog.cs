using System.Text.Json;
using System.Text.RegularExpressions;
using NKeeper.Plugins.Packaging;

namespace NKeeper.Plugins.Host.Store;

/// <summary>One plugin the catalog recognises: its ID and the GitHub repository whose releases carry it.</summary>
public sealed record CatalogEntry(string Id, string Repository, string? Summary);

/// <summary>
/// The catalog file (docs/architecture.md §9.1): which plugins a server can get, by repository. It names no versions;
/// they come from each repository's releases. Being in it isn't being trusted: packages still verify (plugin-security.md §5).
/// </summary>
public sealed partial class Catalog
{
    public const int FormatVersion = 1;

    /// <summary>The largest catalog file read.</summary>
    public const int MaxBytes = 1024 * 1024;

    private const int MaxSummaryLength = 200;

    private Catalog(IReadOnlyList<CatalogEntry> entries, IReadOnlyList<string> warnings)
    {
        Entries = entries;
        Warnings = warnings;
    }

    public IReadOnlyList<CatalogEntry> Entries { get; }

    /// <summary>Entries that were skipped, and why.</summary>
    public IReadOnlyList<string> Warnings { get; }

    public CatalogEntry? Find(string id) => Entries.FirstOrDefault(e => e.Id == id);

    /// <summary>
    /// Reads a catalog file. A file that isn't a catalog is a <see cref="CatalogException"/>; an entry that doesn't
    /// parse is skipped with a warning, not the whole file. Unknown fields are ignored, so the format can grow.
    /// </summary>
    public static Catalog Parse(ReadOnlySpan<byte> json)
    {
        if (json.Length > MaxBytes)
        {
            throw new CatalogException($"The catalog is larger than {MaxBytes} bytes.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (JsonException ex)
        {
            throw new CatalogException($"The catalog isn't JSON: {ex.Message}", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("formatVersion", out var format) || format.ValueKind != JsonValueKind.Number
                || !format.TryGetInt32(out var version))
            {
                throw new CatalogException("The catalog has no formatVersion.");
            }

            if (version != FormatVersion)
            {
                throw new CatalogException($"The catalog's format is version {version}; this server reads version {FormatVersion}.");
            }

            if (!root.TryGetProperty("plugins", out var plugins) || plugins.ValueKind != JsonValueKind.Array)
            {
                throw new CatalogException("The catalog has no plugins list.");
            }

            var entries = new List<CatalogEntry>();
            var warnings = new List<string>();
            var index = 0;
            foreach (var item in plugins.EnumerateArray())
            {
                index++;
                var id = Text(item, "id");
                var repository = Text(item, "repository");
                var summary = Text(item, "summary");
                if (!PluginId.IsValid(id))
                {
                    warnings.Add($"Entry {index} has no valid plugin ID; skipped.");
                }
                else if (repository is null || !RepositoryPattern().IsMatch(repository))
                {
                    warnings.Add($"'{id}' has no valid GitHub repository (owner/name); skipped.");
                }
                else if (entries.Any(e => e.Id == id))
                {
                    warnings.Add($"'{id}' is listed twice; the first is kept.");
                }
                else
                {
                    summary = summary?.ReplaceLineEndings(" ").Trim();
                    entries.Add(new CatalogEntry(id!, repository, summary is { Length: > MaxSummaryLength } ? summary[..MaxSummaryLength] : summary));
                }
            }

            return new Catalog(entries, warnings);
        }
    }

    private static string? Text(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>A GitHub <c>owner/name</c>, as GitHub allows them.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex RepositoryPattern();
}

/// <summary>The catalog couldn't be read, or isn't a catalog.</summary>
public sealed class CatalogException : Exception
{
    public CatalogException(string message)
        : base(message)
    {
    }

    public CatalogException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
