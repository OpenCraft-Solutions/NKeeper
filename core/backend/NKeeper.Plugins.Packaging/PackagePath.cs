namespace NKeeper.Plugins.Packaging;

/// <summary>The rules every path in a package and its file list must follow (docs/plugin-security.md §5.2, step 5).</summary>
public static class PackagePath
{
    /// <summary>The longest path accepted.</summary>
    public const int MaxLength = 512;

    /// <summary>
    /// Whether <paramref name="path"/> is relative, uses <c>/</c>, and has no empty, <c>.</c> or
    /// <c>..</c> segments, drive letters, backslashes or control characters.
    /// </summary>
    public static bool IsValid(string? path)
    {
        if (path is not { Length: > 0 and <= MaxLength } || path[0] == '/')
        {
            return false;
        }

        foreach (var c in path)
        {
            if (c is '\\' or ':' || char.IsControl(c))
            {
                return false;
            }
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment is "" or "." or "..")
            {
                return false;
            }
        }

        return true;
    }
}
