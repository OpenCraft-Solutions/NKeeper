namespace NKeeper.Core.Connectivity.Guacamole;

/// <summary>
/// Decides which browser instructions reach guacd: input, display and text clipboard only.
/// File uploads, audio input, argument changes and anything unknown are dropped. Stream data
/// (blob/end) is accepted solely for clipboard streams the browser opened, up to a size cap.
/// One instance per session (it tracks open clipboard streams).
/// </summary>
public sealed class GuacamoleInstructionFilter
{
    public const int MaxClipboardChars = 1024 * 1024;

    private static readonly HashSet<string> AllowedOpcodes =
        ["key", "mouse", "touch", "size", "sync", "ack", "nop", "clipboard", "blob", "end", "disconnect"];

    private readonly Dictionary<string, int> _clipboardStreams = [];

    public bool IsAllowed(GuacamoleInstruction instruction)
    {
        if (!AllowedOpcodes.Contains(instruction.Opcode))
        {
            return false;
        }

        switch (instruction.Opcode)
        {
            case "clipboard":
                if (instruction.Args is [var stream, "text/plain"] && _clipboardStreams.Count < 16)
                {
                    _clipboardStreams[stream] = 0;
                    return true;
                }

                return false;
            case "blob":
                if (instruction.Args is [var blobStream, var data]
                    && _clipboardStreams.TryGetValue(blobStream, out var size)
                    && size + data.Length <= MaxClipboardChars)
                {
                    _clipboardStreams[blobStream] = size + data.Length;
                    return true;
                }

                return false;
            case "end":
                return instruction.Args is [var endStream] && _clipboardStreams.Remove(endStream);
            default:
                return true;
        }
    }
}
