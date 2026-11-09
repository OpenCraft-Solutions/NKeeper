using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NKeeper.Plugins.Packaging;

/// <summary>One file in the signed list: its path, size and SHA-256 (lowercase hex).</summary>
public sealed record FileListEntry(string Path, long Size, string Sha256);

/// <summary>
/// <c>files.json</c>, the document the package signature covers (docs/plugin-security.md §3): the
/// plugin's ID and version, and every other file in the package with its size and SHA-256.
/// </summary>
public sealed partial record FileList(int FormatVersion, string Id, string Version, IReadOnlyList<FileListEntry> Files)
{
    /// <summary>Builds the list for a set of files.</summary>
    public static FileList Create(string id, string version, IReadOnlyDictionary<string, byte[]> files) =>
        new(
            PackageFormat.Version,
            id,
            version,
            [.. files
                .OrderBy(f => f.Key, StringComparer.Ordinal)
                .Select(f => new FileListEntry(f.Key, f.Value.LongLength, Convert.ToHexStringLower(SHA256.HashData(f.Value))))]);

    /// <summary>
    /// The canonical bytes: compact UTF-8 JSON, properties in a fixed order, files sorted by path.
    /// These exact bytes are what gets signed.
    /// </summary>
    public byte[] ToCanonicalJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", FormatVersion);
            writer.WriteString("id", Id);
            writer.WriteString("version", Version);
            writer.WriteStartArray("files");
            foreach (var file in Files.OrderBy(f => f.Path, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("path", file.Path);
                writer.WriteNumber("size", file.Size);
                writer.WriteString("sha256", file.Sha256);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>Parses and validates a file list. Only call this on bytes whose signature has been verified.</summary>
    public static bool TryParse(ReadOnlySpan<byte> json, [NotNullWhen(true)] out FileList? list, [NotNullWhen(false)] out string? error)
    {
        list = null;
        if (!PackageJson.TryDeserialize<FileListDocument>(json, out var doc, out error))
        {
            return false;
        }

        if (doc.FormatVersion != PackageFormat.Version)
        {
            error = $"File list format version {doc.FormatVersion} isn't supported.";
            return false;
        }

        if (!PluginId.IsValid(doc.Id) || !SemanticVersion.TryParse(doc.Version, out _))
        {
            error = "The file list's ID or version is invalid.";
            return false;
        }

        string? previous = null;
        foreach (var file in doc.Files)
        {
            // Nullable annotations aren't enforced on collection elements, so check for null entries.
            if (file is null
                || !PackagePath.IsValid(file.Path)
                || file.Path is PackageFormat.FileListPath or PackageFormat.SignaturePath
                || file.Size < 0
                || !Sha256Pattern().IsMatch(file.Sha256))
            {
                error = $"File list entry '{file?.Path}' is invalid.";
                return false;
            }

            if (previous is not null && string.CompareOrdinal(previous, file.Path) >= 0)
            {
                error = "File list entries must be unique and sorted by path.";
                return false;
            }

            previous = file.Path;
        }

        list = new FileList(doc.FormatVersion, doc.Id, doc.Version, doc.Files);
        return true;
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    private sealed record FileListDocument(int FormatVersion, string Id, string Version, IReadOnlyList<FileListEntry> Files);
}
