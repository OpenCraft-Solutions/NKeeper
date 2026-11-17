using System.Buffers.Binary;
using System.Text;

namespace NKeeper.Core.Connectivity.Telnet;

/// <summary>
/// Ported from 1.x (`Telnet/TelnetProtocol`). The client side of the Telnet protocol (RFC 854), as a terminal needs it: splits the server's
/// stream into terminal data and commands, and answers option negotiation. NKeeper agrees to send
/// its terminal type (<c>xterm-256color</c>, RFC 1091) and window size (NAWS, RFC 1073), and lets the
/// server echo and suppress go-ahead; every other option is declined. It never offers options
/// itself, so it can't start a negotiation loop. Keeps its state across reads, so a command split
/// between two packets is still understood.
/// </summary>
public sealed class TelnetProtocol
{
    public const byte Iac = 255;
    public const byte Dont = 254;
    public const byte Do = 253;
    public const byte Wont = 252;
    public const byte Will = 251;
    public const byte Sb = 250;
    public const byte Se = 240;

    public const byte Echo = 1;
    public const byte SuppressGoAhead = 3;
    public const byte TerminalType = 24;
    public const byte WindowSize = 31;

    private const byte TerminalTypeSend = 1;
    private const byte TerminalTypeIs = 0;
    private const int MaxSubnegotiation = 512;
    private static readonly byte[] TerminalName = Encoding.ASCII.GetBytes("XTERM-256COLOR");

    /// <summary>Options we perform (the server sent DO and we agreed).</summary>
    private readonly HashSet<byte> _local = [];

    /// <summary>Options the server performs (it sent WILL and we agreed).</summary>
    private readonly HashSet<byte> _remote = [];

    /// <summary>Refusals already sent, so a server repeating itself isn't answered forever.</summary>
    private readonly HashSet<(byte Verb, byte Option)> _refused = [];

    private readonly List<byte> _subnegotiation = [];
    private State _state;
    private byte _verb;
    private ushort _cols = 80;
    private ushort _rows = 24;

    private enum State
    {
        Data,
        Command,
        Option,
        Subnegotiation,
        SubnegotiationIac,
    }

    /// <summary>Whether the server echoes what is typed (so a password prompt's input stays hidden).</summary>
    public bool ServerEchoes => _remote.Contains(Echo);

    /// <summary>
    /// Processes bytes from the server.
    /// </summary>
    /// <param name="data">Receives the terminal data in them.</param>
    /// <param name="replies">Receives the negotiation answers to send back.</param>
    public void Receive(ReadOnlySpan<byte> input, List<byte> data, List<byte> replies)
    {
        foreach (var b in input)
        {
            switch (_state)
            {
                case State.Data:
                    if (b == Iac)
                    {
                        _state = State.Command;
                    }
                    else
                    {
                        data.Add(b);
                    }

                    break;

                case State.Command:
                    switch (b)
                    {
                        case Iac:
                            data.Add(Iac); // An escaped 255 is data.
                            _state = State.Data;
                            break;
                        case Do or Dont or Will or Wont:
                            _verb = b;
                            _state = State.Option;
                            break;
                        case Sb:
                            _subnegotiation.Clear();
                            _state = State.Subnegotiation;
                            break;
                        default:
                            _state = State.Data; // NOP, GA, AYT and the like: nothing to do.
                            break;
                    }

                    break;

                case State.Option:
                    Negotiate(_verb, b, replies);
                    _state = State.Data;
                    break;

                case State.Subnegotiation:
                    if (b == Iac)
                    {
                        _state = State.SubnegotiationIac;
                    }
                    else if (_subnegotiation.Count < MaxSubnegotiation)
                    {
                        _subnegotiation.Add(b);
                    }

                    break;

                case State.SubnegotiationIac:
                    if (b == Se)
                    {
                        Subnegotiate(replies);
                        _state = State.Data;
                    }
                    else
                    {
                        if (b == Iac && _subnegotiation.Count < MaxSubnegotiation)
                        {
                            _subnegotiation.Add(Iac);
                        }

                        _state = State.Subnegotiation;
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// What the user typed, ready to send: 255 is escaped, and Enter (a lone CR, as terminals send it)
    /// becomes CR LF, Telnet's end of line.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<byte> input)
    {
        var output = new List<byte>(input.Length + 8);
        for (var i = 0; i < input.Length; i++)
        {
            var b = input[i];
            output.Add(b);
            if (b == Iac)
            {
                output.Add(Iac);
            }
            else if (b == (byte)'\r' && (i + 1 >= input.Length || input[i + 1] != (byte)'\n'))
            {
                output.Add((byte)'\n');
            }
        }

        return [.. output];
    }

    /// <summary>Records the terminal's size and, once the server asked for it, the NAWS message to send.</summary>
    public byte[] Resize(int cols, int rows)
    {
        _cols = (ushort)Math.Clamp(cols, 1, ushort.MaxValue);
        _rows = (ushort)Math.Clamp(rows, 1, ushort.MaxValue);
        var replies = new List<byte>();
        if (_local.Contains(WindowSize))
        {
            AppendWindowSize(replies);
        }

        return [.. replies];
    }

    private void Negotiate(byte verb, byte option, List<byte> replies)
    {
        switch (verb)
        {
            case Do when option is TerminalType or WindowSize:
                if (_local.Add(option))
                {
                    replies.AddRange([Iac, Will, option]);
                    if (option == WindowSize)
                    {
                        AppendWindowSize(replies);
                    }
                }

                break;
            case Do:
                Refuse(Wont, option, replies);
                break;
            case Dont:
                if (_local.Remove(option))
                {
                    replies.AddRange([Iac, Wont, option]);
                }

                break;
            case Will when option is Echo or SuppressGoAhead:
                if (_remote.Add(option))
                {
                    replies.AddRange([Iac, Do, option]);
                }

                break;
            case Will:
                Refuse(Dont, option, replies);
                break;
            case Wont:
                if (_remote.Remove(option))
                {
                    replies.AddRange([Iac, Dont, option]);
                }

                break;
        }
    }

    private void Refuse(byte verb, byte option, List<byte> replies)
    {
        if (_refused.Add((verb, option)))
        {
            replies.AddRange([Iac, verb, option]);
        }
    }

    private void Subnegotiate(List<byte> replies)
    {
        if (_subnegotiation is [TerminalType, TerminalTypeSend, ..] && _local.Contains(TerminalType))
        {
            replies.AddRange([Iac, Sb, TerminalType, TerminalTypeIs, .. TerminalName, Iac, Se]);
        }
    }

    private void AppendWindowSize(List<byte> replies)
    {
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(size, _cols);
        BinaryPrimitives.WriteUInt16BigEndian(size[2..], _rows);
        replies.AddRange([Iac, Sb, WindowSize]);
        foreach (var b in size)
        {
            replies.Add(b);
            if (b == Iac)
            {
                replies.Add(Iac);
            }
        }

        replies.AddRange([Iac, Se]);
    }
}
