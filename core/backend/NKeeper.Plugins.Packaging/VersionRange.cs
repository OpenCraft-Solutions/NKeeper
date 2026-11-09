using System.Diagnostics.CodeAnalysis;

namespace NKeeper.Plugins.Packaging;

/// <summary>
/// A version range: comparators separated by spaces, all of which must hold, e.g. <c>&gt;=0.1.0 &lt;0.2.0</c>
/// (docs/plugin-security.md §3). Operators: <c>=</c>, <c>&gt;</c>, <c>&gt;=</c>, <c>&lt;</c>, <c>&lt;=</c>.
/// </summary>
public sealed class VersionRange
{
    private readonly IReadOnlyList<(string Operator, SemanticVersion Version)> _comparators;

    private VersionRange(string text, IReadOnlyList<(string, SemanticVersion)> comparators)
    {
        Text = text;
        _comparators = comparators;
    }

    /// <summary>The range exactly as written.</summary>
    public string Text { get; }

    public static bool TryParse(string? text, [NotNullWhen(true)] out VersionRange? range)
    {
        range = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 256)
        {
            return false;
        }

        var comparators = new List<(string, SemanticVersion)>();
        foreach (var part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var op = part.StartsWith(">=", StringComparison.Ordinal) ? ">="
                : part.StartsWith("<=", StringComparison.Ordinal) ? "<="
                : part.StartsWith('>') ? ">"
                : part.StartsWith('<') ? "<"
                : part.StartsWith('=') ? "="
                : null;
            if (op is null || !SemanticVersion.TryParse(part[op.Length..], out var version))
            {
                return false;
            }

            comparators.Add((op, version));
        }

        range = new VersionRange(text, comparators);
        return true;
    }

    /// <summary>Whether <paramref name="version"/> satisfies every comparator.</summary>
    public bool Includes(SemanticVersion version) => _comparators.All(c => c.Operator switch
    {
        ">=" => version >= c.Version,
        "<=" => version <= c.Version,
        ">" => version > c.Version,
        "<" => version < c.Version,
        _ => version == c.Version,
    });

    public override string ToString() => Text;
}
