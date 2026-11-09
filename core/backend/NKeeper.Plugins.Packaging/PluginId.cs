using System.Text.RegularExpressions;

namespace NKeeper.Plugins.Packaging;

/// <summary>
/// Plugin ID rules (docs/plugin-security.md §6.2): lowercase <c>[a-z0-9-]</c> segments separated by
/// dots. IDs without a dot are reserved for official plugins; everyone else uses a reverse-domain ID.
/// </summary>
public static partial class PluginId
{
    /// <summary>The longest ID accepted.</summary>
    public const int MaxLength = 100;

    /// <summary>Whether <paramref name="id"/> is a well-formed plugin ID.</summary>
    public static bool IsValid(string? id) =>
        id is { Length: > 0 and <= MaxLength } && IdPattern().IsMatch(id);

    /// <summary>Whether a valid ID is in the official namespace (it has no dot).</summary>
    public static bool IsOfficial(string id) => !id.Contains('.', StringComparison.Ordinal);

    [GeneratedRegex(@"^[a-z0-9]+(?:-[a-z0-9]+)*(?:\.[a-z0-9]+(?:-[a-z0-9]+)*)*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}
