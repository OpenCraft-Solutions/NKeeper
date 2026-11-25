using System.IO.Compression;
using System.Text;
using System.Text.Json;
using NKeeper.Abstractions;

namespace NKeeper.Core.Sessions;

/// <summary>Recording terminal sessions (docs/architecture.md §7.51): the size limit, and how long recordings are kept.</summary>
public sealed class RecordingOptions
{
    public const string Section = "Recordings";

    /// <summary>A recording stops at this size (before compression), marked as cut short.</summary>
    public int MaxMegabytes { get; set; } = 50;

    /// <summary>Recordings older than this are deleted; 0 keeps them.</summary>
    public int RetentionDays { get; set; } = 90;
}

/// <summary>
/// Records what a terminal showed as an asciicast v2 file (the format asciinema plays), ported from 1.x: a JSON header
/// line, then one <c>[seconds, "o", text]</c> line per output and <c>[seconds, "r", "COLSxROWS"]</c> per resize. Only
/// output is recorded, never what was typed. It's compressed as it goes, and stops (marking itself truncated) at a
/// size limit, so a session that floods the terminal can't exhaust memory.
/// </summary>
public sealed class TerminalRecorder : IDisposable
{
    private readonly MemoryStream _compressed = new();
    private readonly GZipStream _gzip;
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly DateTimeOffset _started;
    private readonly long _limit;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private bool _finished;

    public TerminalRecorder(int columns, int rows, string title, long limit, TimeProvider time)
    {
        _time = time;
        _started = time.GetUtcNow();
        _limit = limit;
        _gzip = new GZipStream(_compressed, CompressionLevel.Fastest, leaveOpen: true);
        Write(JsonSerializer.Serialize(new { version = 2, width = columns, height = rows, timestamp = _started.ToUnixTimeSeconds(), title }));
    }

    public DateTimeOffset StartedAtUtc => _started;

    /// <summary>The asciicast's size so far, before compression.</summary>
    public long Size { get; private set; }

    public bool Truncated { get; private set; }

    /// <summary>Whether anything was shown: a session that showed nothing isn't kept.</summary>
    public bool HasOutput { get; private set; }

    /// <summary>Terminal output; multi-byte characters split between frames are joined up.</summary>
    public void Output(ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            var chars = new char[_decoder.GetCharCount(data, flush: false)];
            var count = _decoder.GetChars(data, chars, flush: false);
            if (count > 0)
            {
                HasOutput = true;
                Event("o", new string(chars, 0, count));
            }
        }
    }

    public void Resize(int columns, int rows)
    {
        lock (_lock)
        {
            Event("r", $"{columns}x{rows}");
        }
    }

    /// <summary>Ends the recording: the gzipped asciicast.</summary>
    public byte[] Finish()
    {
        lock (_lock)
        {
            _finished = true;
            _gzip.Dispose();
            return _compressed.ToArray();
        }
    }

    public void Dispose()
    {
        _gzip.Dispose();
        _compressed.Dispose();
    }

    private void Event(string kind, string text)
    {
        var seconds = Math.Round((_time.GetUtcNow() - _started).TotalSeconds, 3);
        Write(JsonSerializer.Serialize(new object[] { seconds, kind, text }));
    }

    private void Write(string line)
    {
        if (Truncated || _finished)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        if (Size + bytes.Length > _limit)
        {
            Truncated = true;
            return;
        }

        Size += bytes.Length;
        _gzip.Write(bytes);
    }
}

/// <summary>
/// The channel a recorded terminal session's plugin sees (§7.51): frames pass as they are, and the plugin's binary frames
/// (what the terminal shows) and the browser's <c>resize</c> messages are recorded on the way. Nothing else the browser
/// sends, what's typed included, is recorded.
/// </summary>
internal sealed class RecordingStreamChannel(IStreamChannel inner, TerminalRecorder recorder) : IStreamChannel
{
    public async Task<StreamFrame?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var frame = await inner.ReceiveAsync(cancellationToken);
        if (frame is { IsText: true } && Resize(frame.Data.Span) is var (columns, rows))
        {
            recorder.Resize(columns, rows);
        }

        return frame;
    }

    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        recorder.Output(data.Span);
        return inner.SendAsync(data, cancellationToken);
    }

    public Task SendTextAsync(string json, CancellationToken cancellationToken) => inner.SendTextAsync(json, cancellationToken);

    /// <summary>The size a <c>{"type":"resize","columns":…,"rows":…}</c> message asks for; null for any other.</summary>
    internal static (int Columns, int Rows)? Resize(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json);
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "resize"
                && root.TryGetProperty("columns", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var columns) && columns is > 0 and <= 1000
                && root.TryGetProperty("rows", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt32(out var rows) && rows is > 0 and <= 1000
                ? (columns, rows)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
