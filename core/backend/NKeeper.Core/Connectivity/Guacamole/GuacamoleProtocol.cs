using System.Globalization;
using System.Text;

namespace NKeeper.Core.Connectivity.Guacamole;

/// <summary>One Guacamole protocol instruction, e.g. <c>4.size,4.1024,3.768;</c>.</summary>
public sealed record GuacamoleInstruction(string Opcode, IReadOnlyList<string> Args, string Raw)
{
    public static string Encode(string opcode, params IEnumerable<string> args)
    {
        var builder = new StringBuilder();
        AppendElement(builder, opcode);
        foreach (var arg in args)
        {
            builder.Append(',');
            AppendElement(builder, arg);
        }

        return builder.Append(';').ToString();
    }

    private static void AppendElement(StringBuilder builder, string value) =>
        builder.Append(CodePoints(value).ToString(CultureInfo.InvariantCulture)).Append('.').Append(value);

    /// <summary>Element lengths count Unicode code points (a surrogate pair is one).</summary>
    private static int CodePoints(string value)
    {
        var count = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
            }

            count++;
        }

        return count;
    }
}

/// <summary>
/// Incremental parser: feed it text as it arrives and take out complete instructions.
/// Rejects malformed input and instructions larger than <paramref name="maxInstructionLength"/>.
/// </summary>
public sealed class GuacamoleParser(int maxInstructionLength)
{
    private readonly StringBuilder _buffer = new();

    public int BufferedLength => _buffer.Length;

    public void Append(ReadOnlySpan<char> text) => _buffer.Append(text);

    public bool TryRead(out GuacamoleInstruction instruction)
    {
        instruction = null!;
        var elements = new List<string>();
        var position = 0;

        while (true)
        {
            // Length prefix: decimal digits followed by '.'.
            var length = 0;
            var digits = 0;
            while (true)
            {
                if (position >= _buffer.Length)
                {
                    return Incomplete();
                }

                var c = _buffer[position++];
                if (c == '.' && digits > 0)
                {
                    break;
                }

                if (c is < '0' or > '9' || ++digits > 9)
                {
                    throw new FormatException("Malformed Guacamole instruction length.");
                }

                length = (length * 10) + (c - '0');
            }

            // Value: exactly `length` code points.
            var start = position;
            for (var remaining = length; remaining > 0; remaining--)
            {
                if (position >= _buffer.Length)
                {
                    return Incomplete();
                }

                if (char.IsHighSurrogate(_buffer[position]))
                {
                    if (position + 1 >= _buffer.Length)
                    {
                        return Incomplete();
                    }

                    position++;
                }

                position++;
            }

            elements.Add(_buffer.ToString(start, position - start));

            if (position >= _buffer.Length)
            {
                return Incomplete();
            }

            var terminator = _buffer[position++];
            if (terminator == ';')
            {
                var raw = _buffer.ToString(0, position);
                _buffer.Remove(0, position);
                instruction = new GuacamoleInstruction(elements[0], elements.Skip(1).ToList(), raw);
                return true;
            }

            if (terminator != ',')
            {
                throw new FormatException("Malformed Guacamole instruction separator.");
            }
        }

        bool Incomplete() =>
            _buffer.Length > maxInstructionLength ? throw new FormatException("Guacamole instruction too large.") : false;
    }
}
