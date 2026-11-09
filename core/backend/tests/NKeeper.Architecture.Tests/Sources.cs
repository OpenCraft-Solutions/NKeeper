using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace NKeeper.Architecture.Tests;

/// <summary>
/// Just enough reading of sources for the rules: string literals, decoded as the language decodes them,
/// and relative imports. Comments are skipped: they may name a plugin as an example ("e.g. ssh"); only
/// code counts.
/// </summary>
public static partial class Sources
{
    /// <summary>
    /// The value of every string literal in a C# source, read by the C# compiler's own parser: regular,
    /// verbatim, raw and UTF-8 strings, the text of interpolated strings, and literals inside interpolations.
    /// </summary>
    public static IEnumerable<string> CSharpLiterals(string source) =>
        CSharpSyntaxTree.ParseText(source).GetRoot().DescendantTokens()
            .Where(t => t.IsKind(SyntaxKind.StringLiteralToken)
                || t.IsKind(SyntaxKind.Utf8StringLiteralToken)
                || t.IsKind(SyntaxKind.SingleLineRawStringLiteralToken)
                || t.IsKind(SyntaxKind.MultiLineRawStringLiteralToken)
                || t.IsKind(SyntaxKind.Utf8SingleLineRawStringLiteralToken)
                || t.IsKind(SyntaxKind.Utf8MultiLineRawStringLiteralToken)
                || t.IsKind(SyntaxKind.InterpolatedStringTextToken))
            .Select(t => t.ValueText);

    /// <summary>
    /// The value of every string literal in a JavaScript or TypeScript source, including each text part of
    /// a template literal and literals inside its <c>${…}</c> substitutions.
    /// </summary>
    public static IEnumerable<string> ScriptLiterals(string source) => ScriptStrings(source).Select(s => s.Value);

    /// <summary>
    /// Module specifiers starting with <c>.</c>, from <c>import … from</c>, <c>export … from</c>,
    /// <c>import()</c> and <c>require()</c>, even with a comment between the keyword and the specifier.
    /// </summary>
    public static IEnumerable<string> RelativeImports(string source) =>
        ScriptStrings(source).Where(s => s.Value.StartsWith('.') && ImportLead().IsMatch(s.Lead)).Select(s => s.Value);

    /// <summary>A string in a script, and the code just before it (comments removed) that says how it's used.</summary>
    private sealed record ScriptString(string Value, string Lead);

    private static List<ScriptString> ScriptStrings(string source)
    {
        var strings = new List<ScriptString>();
        var position = 0;
        new ScriptScanner(source, strings).Scan(ref position, insideSubstitution: false);
        return strings;
    }

    /// <summary>
    /// A character scanner for JavaScript and TypeScript: comments, quoted strings and template literals,
    /// recursing into substitutions. Regular expression literals aren't recognised; core's sources don't
    /// put quotes in them.
    /// </summary>
    private sealed class ScriptScanner(string source, List<ScriptString> strings)
    {
        private const char LineFeed = (char)0x0A;
        private const char LineSeparator = (char)0x2028;
        private const char ParagraphSeparator = (char)0x2029;

        public void Scan(ref int i, bool insideSubstitution)
        {
            var code = new StringBuilder();
            var depth = 0;
            while (i < source.Length)
            {
                var c = source[i];
                var next = i + 1 < source.Length ? source[i + 1] : '\0';
                if (c == '/' && next == '/')
                {
                    while (i < source.Length && source[i] != '\n')
                    {
                        i++;
                    }

                    code.Append(' ');
                }
                else if (c == '/' && next == '*')
                {
                    var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? source.Length : end + 2;
                    code.Append(' ');
                }
                else if (c is '"' or '\'')
                {
                    var value = ReadQuoted(ref i, c);
                    strings.Add(new ScriptString(value, Tail(code)));
                    code.Append('0');
                }
                else if (c == '`')
                {
                    var lead = Tail(code);
                    ReadTemplate(ref i, lead);
                    code.Append('0');
                }
                else
                {
                    if (insideSubstitution && c == '{')
                    {
                        depth++;
                    }
                    else if (insideSubstitution && c == '}')
                    {
                        if (depth == 0)
                        {
                            i++;
                            return;
                        }

                        depth--;
                    }

                    code.Append(c);
                    i++;
                }
            }
        }

        private string ReadQuoted(ref int i, char quote)
        {
            var value = new StringBuilder();
            i++;
            while (i < source.Length && source[i] != quote && source[i] != '\n')
            {
                if (source[i] == '\\')
                {
                    ReadEscape(ref i, value);
                }
                else
                {
                    value.Append(source[i++]);
                }
            }

            i++;
            return value.ToString();
        }

        private void ReadTemplate(ref int i, string lead)
        {
            var text = new StringBuilder();
            i++;
            while (i < source.Length && source[i] != '`')
            {
                if (source[i] == '\\')
                {
                    ReadEscape(ref i, text);
                }
                else if (source[i] == '$' && i + 1 < source.Length && source[i + 1] == '{')
                {
                    strings.Add(new ScriptString(text.ToString(), lead));
                    text.Clear();
                    i += 2;
                    Scan(ref i, insideSubstitution: true);
                }
                else
                {
                    text.Append(source[i++]);
                }
            }

            i++;
            strings.Add(new ScriptString(text.ToString(), lead));
        }

        /// <summary>Decodes one escape sequence at <paramref name="i"/> (which is on the backslash).</summary>
        private void ReadEscape(ref int i, StringBuilder value)
        {
            i++;
            if (i >= source.Length)
            {
                return;
            }

            var c = source[i++];
            switch (c)
            {
                case 'n': value.Append('\n'); break;
                case 'r': value.Append('\r'); break;
                case 't': value.Append('\t'); break;
                case 'b': value.Append('\b'); break;
                case 'f': value.Append('\f'); break;
                case 'v': value.Append('\v'); break;
                case '0' when i >= source.Length || !char.IsAsciiDigit(source[i]): value.Append('\0'); break;
                case '\r':
                    if (i < source.Length && source[i] == '\n')
                    {
                        i++;
                    }

                    break;
                case LineFeed or LineSeparator or ParagraphSeparator: break; // a line continuation
                case 'x': value.Append(Hex(ref i, 2)); break;
                case 'u' when i < source.Length && source[i] == '{':
                    var close = source.IndexOf('}', i);
                    value.Append(char.ConvertFromUtf32(int.Parse(source.AsSpan(i + 1, close - i - 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
                    i = close + 1;
                    break;
                case 'u': value.Append(Hex(ref i, 4)); break;
                default: value.Append(c); break;
            }
        }

        private string Hex(ref int i, int digits)
        {
            var code = int.Parse(source.AsSpan(i, digits), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            i += digits;
            return ((char)code).ToString();
        }

        private static string Tail(StringBuilder code) => code.ToString(Math.Max(0, code.Length - 40), Math.Min(40, code.Length));
    }

    [GeneratedRegex(@"(?:\bfrom|\bimport|\bimport\s*\(|\brequire\s*\()\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ImportLead();
}
