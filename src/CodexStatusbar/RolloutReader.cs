using System.Text;

namespace CodexStatusbar;

/// <summary>Outcome of an incremental read attempt.</summary>
internal enum RolloutUpdateResult
{
    /// <summary>No new bytes; nothing changed.</summary>
    Unchanged,

    /// <summary>New complete lines were parsed.</summary>
    Updated,

    /// <summary>The file shrank or its first line changed (truncate / rotate / replace): re-seed from the tail.</summary>
    NeedsReinitialize
}

/// <summary>
/// Offset-based incremental reader for one rollout JSONL file.
///
/// Restart recovery reads only the last <see cref="TpsRules.TailReadBytes"/> bytes, then tracks
/// appends from the byte offset where that tail read ended. Parsing is therefore O(new bytes) and
/// sessions of 70-270 MB are never read whole. A partially written trailing line is kept for the
/// next read and never parsed.
/// </summary>
internal sealed class RolloutReader
{
    private const int HeadProbeBytes = 64 * 1024;

    private byte[] _pending = [];
    private string? _headIdentity;
    private bool _initialized;

    public RolloutReader(string path)
    {
        Path = path;
        State = new RolloutStreamState();
    }

    public string Path { get; }

    public RolloutStreamState State { get; }

    /// <summary>Byte offset of the end of the last successfully parsed complete line.</summary>
    public long Offset { get; private set; }

    /// <summary>Total bytes handed to the parser so far (for the debug log).</summary>
    public long ParsedBytes { get; private set; }

    /// <summary>True when the tail read produced at least one cumulative reading.</summary>
    public bool RecoveredFromRollout => State.HasRolloutUsage;

    public bool IsInitialized => _initialized;

    /// <summary>First physical line's session_meta id, used to detect file replacement/rotation.</summary>
    public string? HeadIdentity => _headIdentity;

    /// <summary>
    /// Rebuilds state from the tail of the file only. The tail window may start inside an
    /// unfinished turn; <see cref="TpsTracker.TurnItemsComplete"/> then stays true only if the
    /// turn's <c>task_started</c> was inside the window, so a half-observed turn reports unknown TPS
    /// rather than 0.
    ///
    /// The window starts at <see cref="TpsRules.TailReadBytes"/> (the bounded fast path) and is
    /// extended backwards, doubling, until a cumulative usage reading is found or the whole file has
    /// been covered. The window must never be assumed to be "always enough": a future Codex could
    /// emit one enormous record, or a pathological session could hold no usable usage at all.
    /// Extending only happens when the tail genuinely had nothing, which the 501-file corpus shows
    /// essentially never occurs, so the usual cost stays O(4 MB).
    /// </summary>
    public void Initialize()
    {
        State.Tps.ResetTurn();
        ResetState();

        using var stream = OpenRead();
        if (stream is null)
        {
            _initialized = true;
            return;
        }

        var length = SafeLength(stream);
        _headIdentity = ReadHeadIdentity(stream);

        if (length <= 0)
        {
            _initialized = true;
            return;
        }

        var window = (long)TpsRules.TailReadBytes;
        var extended = false;

        while (true)
        {
            var tailStart = Math.Max(0, length - window);
            var bytes = ReadRange(stream, tailStart, length);
            ParsedBytes += bytes.Length;

            if (tailStart > 0)
            {
                // The window almost certainly starts mid-line: drop through the first newline.
                var firstNewline = Array.IndexOf(bytes, (byte)'\n');
                if (firstNewline < 0)
                {
                    // One single record longer than the whole window. Nothing parseable in it; widen
                    // rather than give up, unless we already have the entire file.
                    if (tailStart == 0)
                    {
                        _pending = bytes;
                        Offset = length;
                        State.Warnings.Add("oversized-single-record");
                        break;
                    }

                    window = NextWindow(window, length);
                    extended = true;
                    continue;
                }

                bytes = bytes[(firstNewline + 1)..];
            }

            ConsumeCompleteLines(bytes);
            Offset = length - _pending.Length;

            if (State.HasCumulativeUsage || tailStart == 0 || window >= length)
            {
                break;
            }

            // No usage recovered yet: look further back, but re-parse from scratch each time (state
            // must reflect the whole window, not a partial one).
            State.Tps.ResetTurn();
            ResetState();
            window = NextWindow(window, length);
            extended = true;
        }

        if (extended)
        {
            TailWindowBytes = Math.Min(window, length);
        }

        _initialized = true;
    }

    /// <summary>
    /// Size of the window that actually produced the recovered state; equals the 4 MB fast path when
    /// no backward extension was needed. Reported in the debug log.
    /// </summary>
    public long TailWindowBytes { get; private set; } = TpsRules.TailReadBytes;

    /// <summary>True when the tail window had to be widened past the 4 MB fast path.</summary>
    public bool TailWasExtended { get; private set; }

    private long NextWindow(long window, long length)
    {
        TailWasExtended = true;
        var doubled = window * 2;
        return Math.Min(doubled > window ? doubled : length, length);
    }

    /// <summary>
    /// Parses only bytes appended since the previous call. Returns
    /// <see cref="RolloutUpdateResult.NeedsReinitialize"/> when the file shrank or was replaced, in
    /// which case the caller must call <see cref="Initialize"/> again.
    /// </summary>
    public RolloutUpdateResult TryUpdate()
    {
        if (!_initialized)
        {
            return RolloutUpdateResult.NeedsReinitialize;
        }

        using var stream = OpenRead();
        if (stream is null)
        {
            return RolloutUpdateResult.Unchanged;
        }

        var length = SafeLength(stream);
        if (length < Offset)
        {
            // Size shrank: the file was truncated or replaced. A fresh tail read is the only safe move.
            return RolloutUpdateResult.NeedsReinitialize;
        }

        var identity = ReadHeadIdentity(stream);
        if (!string.Equals(identity, _headIdentity, StringComparison.Ordinal))
        {
            // First physical line changed: this is a different session at the same path.
            return RolloutUpdateResult.NeedsReinitialize;
        }

        var appended = length - Offset;
        if (appended <= 0)
        {
            return RolloutUpdateResult.Unchanged;
        }

        if (appended > TpsRules.TailReadBytes)
        {
            // Slept too long / huge burst: cheaper and bounded to re-seed from the tail.
            return RolloutUpdateResult.NeedsReinitialize;
        }

        var bytes = ReadRange(stream, Offset, length);
        ParsedBytes += bytes.Length;
        ConsumeCompleteLines(bytes);
        Offset = length - _pending.Length;
        return RolloutUpdateResult.Updated;
    }

    private void ResetState()
    {
        State.InputTokens = 0;
        State.CachedInputTokens = 0;
        State.OutputTokens = 0;
        State.ReasoningOutputTokens = 0;
        State.TotalTokens = 0;
        State.ContextUsedTokens = 0;
        State.ContextWindowTokens = 0;
        State.HasCumulativeUsage = false;
        State.HasRolloutUsage = false;
        State.CurrentTurnId = null;
        State.Warnings.Clear();
        State.LineCount = 0;
        _pending = [];
        Offset = 0;
        ParsedBytes = 0;
    }

    /// <summary>
    /// Splits on newlines and parses every complete line. The trailing fragment is only parsed when
    /// it is already valid JSON (i.e. the writer finished the line but has not yet flushed the
    /// newline); a genuinely half-written line stays pending.
    /// </summary>
    private void ConsumeCompleteLines(byte[] bytes)
    {
        var buffer = _pending.Length == 0 ? bytes : Concat(_pending, bytes);
        if (buffer.Length == 0)
        {
            _pending = [];
            return;
        }

        var start = 0;
        var lastNewline = Array.LastIndexOf(buffer, (byte)'\n');
        if (lastNewline >= 0)
        {
            while (start <= lastNewline)
            {
                var end = Array.IndexOf(buffer, (byte)'\n', start);
                if (end < 0 || end > lastNewline)
                {
                    end = lastNewline;
                }

                ParseLine(buffer, start, end - start);
                start = end + 1;
            }
        }

        var tail = buffer[start..];
        if (tail.Length == 0)
        {
            _pending = [];
            return;
        }

        var text = Encoding.UTF8.GetString(tail).Trim();
        if (text.Length > 0 && text.StartsWith('{') && text.EndsWith('}'))
        {
            // A complete record whose trailing newline has not been flushed yet. A genuinely
            // half-written line never parses, so it stays pending for the next read.
            State.ObserveLine(text);
            _pending = [];
            return;
        }

        if (tail.Length > TpsRules.TailReadBytes)
        {
            // Defensive: an unterminated multi-megabyte fragment is garbage, not a record.
            State.Warnings.Add("oversized-unterminated-line");
            _pending = [];
            return;
        }

        _pending = tail;
    }

    private void ParseLine(byte[] buffer, int start, int count)
    {
        if (count <= 0)
        {
            return;
        }

        var line = Encoding.UTF8.GetString(buffer, start, count).Trim();
        if (line.Length == 0)
        {
            return;
        }

        State.ObserveLine(line);
    }

    private FileStream? OpenRead()
    {
        try
        {
            return new FileStream(
                Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long SafeLength(FileStream stream)
    {
        try
        {
            return stream.Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>Reads the first physical line and extracts the session_meta id (authoritative for the root session).</summary>
    private string? ReadHeadIdentity(FileStream stream)
    {
        try
        {
            stream.Seek(0, SeekOrigin.Begin);
            var probe = new byte[HeadProbeBytes];
            var read = stream.Read(probe, 0, probe.Length);
            if (read <= 0)
            {
                return null;
            }

            var text = Encoding.UTF8.GetString(probe, 0, read);
            var newline = text.IndexOf('\n');
            var firstLine = (newline >= 0 ? text[..newline] : text).Trim();
            if (firstLine.Length == 0 || !firstLine.StartsWith('{'))
            {
                return null;
            }

            using var document = System.Text.Json.JsonDocument.Parse(firstLine);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type)
                || type.ValueKind != System.Text.Json.JsonValueKind.String
                || type.GetString() != "session_meta")
            {
                return null;
            }

            if (root.TryGetProperty("payload", out var payload)
                && payload.ValueKind == System.Text.Json.JsonValueKind.Object
                && payload.TryGetProperty("id", out var id)
                && id.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return id.GetString();
            }

            return "session_meta";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static byte[] ReadRange(FileStream stream, long start, long end)
    {
        var count = (int)Math.Max(0, end - start);
        if (count == 0)
        {
            return [];
        }

        var buffer = new byte[count];
        stream.Seek(start, SeekOrigin.Begin);
        var offset = 0;
        while (offset < count)
        {
            var read = stream.Read(buffer, offset, count - offset);
            if (read <= 0)
            {
                break;
            }

            offset += read;
        }

        return offset == count ? buffer : buffer[..offset];
    }

    private static byte[] Concat(byte[] first, byte[] second)
    {
        var result = new byte[first.Length + second.Length];
        first.CopyTo(result, 0);
        second.CopyTo(result, first.Length);
        return result;
    }
}