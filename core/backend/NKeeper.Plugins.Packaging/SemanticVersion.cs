using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NKeeper.Plugins.Packaging;

/// <summary>A SemVer 2.0 version, compared by SemVer precedence (build metadata is ignored).</summary>
public sealed partial class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    private readonly string[] _prerelease;

    private SemanticVersion(string text, long major, long minor, long patch, string[] prerelease)
    {
        Text = text;
        Major = major;
        Minor = minor;
        Patch = patch;
        _prerelease = prerelease;
    }

    /// <summary>The version exactly as written.</summary>
    public string Text { get; }

    public long Major { get; }

    public long Minor { get; }

    public long Patch { get; }

    /// <summary>Whether this is a pre-release (it has a <c>-</c> part).</summary>
    public bool IsPrerelease => _prerelease.Length > 0;

    public static SemanticVersion Parse(string text) =>
        TryParse(text, out var version) ? version : throw new FormatException($"'{text}' is not a SemVer 2.0 version.");

    public static bool TryParse(string? text, [NotNullWhen(true)] out SemanticVersion? version)
    {
        version = null;
        if (text is null || text.Length > 128)
        {
            return false;
        }

        var match = VersionPattern().Match(text);
        if (!match.Success
            || !long.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !long.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !long.TryParse(match.Groups["patch"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        var prerelease = match.Groups["pre"].Success ? match.Groups["pre"].Value.Split('.') : [];
        version = new SemanticVersion(text, major, minor, patch, prerelease);
        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var core = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (core != 0)
        {
            return core;
        }

        // A pre-release has lower precedence than the release itself.
        if (_prerelease.Length == 0 || other._prerelease.Length == 0)
        {
            return other._prerelease.Length.CompareTo(_prerelease.Length);
        }

        for (var i = 0; i < Math.Min(_prerelease.Length, other._prerelease.Length); i++)
        {
            var result = CompareIdentifiers(_prerelease[i], other._prerelease[i]);
            if (result != 0)
            {
                return result;
            }
        }

        return _prerelease.Length.CompareTo(other._prerelease.Length);
    }

    public bool Equals(SemanticVersion? other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, string.Join('.', _prerelease));

    public override string ToString() => Text;

    public static bool operator ==(SemanticVersion? left, SemanticVersion? right) => left is null ? right is null : left.Equals(right);

    public static bool operator !=(SemanticVersion? left, SemanticVersion? right) => !(left == right);

    public static bool operator <(SemanticVersion? left, SemanticVersion? right) => Compare(left, right) < 0;

    public static bool operator <=(SemanticVersion? left, SemanticVersion? right) => Compare(left, right) <= 0;

    public static bool operator >(SemanticVersion? left, SemanticVersion? right) => Compare(left, right) > 0;

    public static bool operator >=(SemanticVersion? left, SemanticVersion? right) => Compare(left, right) >= 0;

    private static int Compare(SemanticVersion? left, SemanticVersion? right) =>
        left is null ? (right is null ? 0 : -1) : left.CompareTo(right);

    /// <summary>
    /// Numeric identifiers compare numerically and rank below alphanumeric ones. They can be longer
    /// than any integer type, and have no leading zeros, so they compare by length, then digits.
    /// </summary>
    private static int CompareIdentifiers(string left, string right)
    {
        var leftNumeric = left.All(char.IsAsciiDigit);
        var rightNumeric = right.All(char.IsAsciiDigit);
        return (leftNumeric, rightNumeric) switch
        {
            (true, true) => left.Length != right.Length ? left.Length.CompareTo(right.Length) : string.CompareOrdinal(left, right),
            (true, false) => -1,
            (false, true) => 1,
            _ => string.CompareOrdinal(left, right),
        };
    }

    // The official SemVer 2.0 grammar: no leading zeros in numbers, dot-separated identifiers.
    [GeneratedRegex(
        @"^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)"
        + @"(?:-(?<pre>(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?"
        + @"(?:\+(?<build>[0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
