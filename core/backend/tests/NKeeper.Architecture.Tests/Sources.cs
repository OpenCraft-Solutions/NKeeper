using System.Text.RegularExpressions;

namespace NKeeper.Architecture.Tests;

/// <summary>
/// Just enough reading of C# and JavaScript sources for the rules: string literals and relative imports.
/// One pass of tokens, so a quote inside a comment, or "//" inside a string, is read as what it is.
/// Comments are skipped: they may name a plugin as an example ("e.g. ssh"); only code counts.
/// </summary>
public static partial class Sources
{
    /// <summary>The contents of every string literal in a C# source.</summary>
    public static IEnumerable<string> CSharpLiterals(string source) =>
        CSharpToken().Matches(source)
            .Select(m => m.Groups["raw"].Success ? m.Groups["raw"].Value
                : m.Groups["verbatim"].Success ? m.Groups["verbatim"].Value.Replace("\"\"", "\"", StringComparison.Ordinal)
                : m.Groups["string"].Success ? Unescape(m.Groups["string"].Value)
                : null)
            .OfType<string>();

    /// <summary>The contents of every string or template literal in a JavaScript or TypeScript source.</summary>
    public static IEnumerable<string> ScriptLiterals(string source) =>
        ScriptToken().Matches(source).Where(m => m.Groups["string"].Success).Select(m => Unescape(m.Groups["string"].Value));

    /// <summary>Module specifiers starting with <c>.</c>, from <c>import … from</c>, <c>export … from</c>, <c>import()</c> and <c>require()</c>.</summary>
    public static IEnumerable<string> RelativeImports(string source)
    {
        var tokens = ScriptToken().Matches(source).ToList();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!tokens[i].Groups["string"].Success || !tokens[i].Groups["string"].Value.StartsWith('.'))
            {
                continue;
            }

            // The code just before the literal (back to the previous token) says how it's used.
            var start = i == 0 ? 0 : tokens[i - 1].Index + tokens[i - 1].Length;
            if (ImportLead().IsMatch(source[start..tokens[i].Index]))
            {
                yield return tokens[i].Groups["string"].Value;
            }
        }
    }

    private static string Unescape(string value) =>
        EscapedCharacter().Replace(value, m => m.Value[1..]);

    [GeneratedRegex(
        @"(?<comment>//[^\r\n]*|/\*[\s\S]*?\*/)"
        + @"|'(?:[^'\\\r\n]|\\[^\r\n])+'"
        + @"|\$*""{3,}(?<raw>[\s\S]*?)""{3,}"
        + @"|\$?@\$?""(?<verbatim>(?:[^""]|"""")*)"""
        + @"|\$?""(?<string>(?:[^""\\\r\n]|\\.)*)""",
        RegexOptions.CultureInvariant)]
    private static partial Regex CSharpToken();

    [GeneratedRegex(
        @"(?<comment>//[^\r\n]*|/\*[\s\S]*?\*/)"
        + @"|(?<q>[""'])(?<string>(?:(?!\k<q>)[^\\\r\n]|\\.)*)\k<q>"
        + @"|`(?<string>(?:[^`\\]|\\[\s\S])*)`",
        RegexOptions.CultureInvariant)]
    private static partial Regex ScriptToken();

    [GeneratedRegex(@"(?:\bfrom|\bimport|\bimport\s*\(|\brequire\s*\()\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ImportLead();

    [GeneratedRegex(@"\\.", RegexOptions.CultureInvariant)]
    private static partial Regex EscapedCharacter();
}
