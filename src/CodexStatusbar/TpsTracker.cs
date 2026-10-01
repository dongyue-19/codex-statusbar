using System.Globalization;
using System.Text.Json;

namespace CodexStatusbar;

/// <summary>
/// Shared numeric rules for the status strip. Everything here is pure so the headless self-test can
/// exercise it without a window station.
/// </summary>
internal static class MetricFormat
{
    /// <summary>
    /// Collapsed-strip token format: value &lt; 1000 =&gt; "843"; 1000..999999 =&gt; "12.4K";
    /// &gt;= 1_000_000 =&gt; "5.7M".
    /// </summary>
    public static string CompactTokens(long value)
    {
        var magnitude = Math.Abs(value);
        if (magnitude >= 1_000_000)
        {
            return (value / 1_000_000d).ToString("0.0", CultureInfo.InvariantCulture) + "M";
        }

        if (magnitude >= 1_000)
        {
            return (value / 1_000d).ToString("0.0", CultureInfo.InvariantCulture) + "K";
        }

        return value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Expanded-panel token format: thousands separators, e.g. "5,699,084".</summary>
    public static string FullTokens(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>
    /// Cache-hit percentage. Returns null when the denominator is zero so callers render "--"
    /// instead of NaN.
    /// </summary>
    public static double? CacheHitPercent(long inputTokens, long cachedInputTokens) =>
        inputTokens <= 0 ? null : cachedInputTokens * 100d / inputTokens;

    public static string FormatCachePercent(long inputTokens, long cachedInputTokens)
    {
        var percent = CacheHitPercent(inputTokens, cachedInputTokens);
        return percent is null
            ? "--"
            : percent.Value.ToString("0.00", CultureInfo.InvariantCulture) + "%";
    }

    public static string FormatCachePercentCompact(long inputTokens, long cachedInputTokens)
    {
        var percent = CacheHitPercent(inputTokens, cachedInputTokens);
        return percent is null
            ? "--"
            : percent.Value.ToString("0", CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>TPS body: "--" when unknown, "~192" when the value is held while generation continues.</summary>
    public static string FormatTps(double? tps, bool isHeld)
    {
        if (!IsUsableTps(tps))
        {
            return "--";
        }

        var rounded = Math.Round(tps!.Value, MidpointRounding.AwayFromZero)
            .ToString("0", CultureInfo.InvariantCulture);
        return isHeld ? "~" + rounded : rounded;
    }

    public static string FormatTpsFloat(double? tps) =>
        !IsUsableTps(tps) ? "--" : tps!.Value.ToString("0.0", CultureInfo.InvariantCulture);

    public static string FormatTpsInt(long tokens, long milliseconds) =>
        milliseconds <= 0
            ? "--"
            : MetricFormat.FormatTps(1000d * tokens / milliseconds, isHeld: false);

    public static string FormatSeconds(double? milliseconds) =>
        milliseconds is null or < 0
            ? "--"
            : (milliseconds.Value / 1000d).ToString("0.00", CultureInfo.InvariantCulture) + " s";

    /// <summary>A TPS is only reported when it is finite and strictly positive — never 0, never NaN.</summary>
    public static bool IsUsableTps(double? tps) =>
        tps.HasValue && double.IsFinite(tps.Value) && tps.Value > 0;

    public static string ShortThreadId(string threadId, int maximumLength = 13)
    {
        var single = threadId
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);
        if (single.Length <= maximumLength)
        {
            return single;
        }

        if (maximumLength <= 1)
        {
            return maximumLength == 1 ? "…" : string.Empty;
        }

        var prefixLength = Math.Min(8, maximumLength - 1);
        var suffixLength = maximumLength - prefixLength - 1;
        return suffixLength <= 0
            ? single[..maximumLength]
            : string.Concat(
                single.AsSpan(0, prefixLength),
                "…",
                single.AsSpan(single.Length - suffixLength, suffixLength));
    }
}

/// <summary>
/// Definitions behind <see cref="TpsTracker"/>.
///
/// A <b>model-output item</b> is an <c>item_completed</c> record whose <c>item.type</c> is
/// <c>Reasoning</c> or <c>AgentMessage</c>: those are the only items the model itself emits.
/// <c>CommandExecution</c>, <c>FileChange</c>, <c>McpToolCall</c>, <c>WebSearch</c>,
/// <c>UserMessage</c> and <c>ImageView</c> are tool / IO / user time and must NEVER enter the TPS
/// denominator — mixing them in is what makes naive implementations report single-digit tok/s.
/// </summary>
internal static class TpsRules
{
    /// <summary>EMA weight for the newest sample. ~1/0.35 = 2.9 samples of effective memory.</summary>
    public const double SmoothingAlpha = 0.35;

    /// <summary>A single model-output item longer than this is suspect and contributes nothing.</summary>
    public const long MaximumItemDurationMs = 600_000;

    /// <summary>Restart recovery only ever reads this many bytes from the end of a rollout file.</summary>
    public const int TailReadBytes = 4 * 1024 * 1024;

    /// <summary>Anything above this means the denominator is wrong; refuse rather than display it.</summary>
    public const double ImplausibleTps = 100_000;

    private static readonly string[] ModelOutputItemTypes = ["Reasoning", "AgentMessage"];

    public static bool IsModelOutputItem(string? itemType) =>
        itemType is not null
        && Array.Exists(ModelOutputItemTypes, known => known.Equals(itemType, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Duration contribution of one item. Negative durations collapse to 0 and absurd single-item
    /// durations are dropped entirely instead of being clamped to a bogus positive value.
    /// </summary>
    public static long NormalizeItemDuration(long startedAtMs, long completedAtMs)
    {
        var duration = completedAtMs - startedAtMs;
        return duration < 0 || duration > MaximumItemDurationMs ? 0 : duration;
    }
}

/// <summary>
/// Derives tokens-per-second from <b>official</b> token counts only. Tokens are never estimated from
/// characters and TPS is never fabricated: anything that cannot be computed stays <c>null</c> and
/// renders as "--".
///
///   turnTps  = 1000 * sum(official output_tokens for this turn) / sum(generation_ms for this turn)
///   liveTps  = the same ratio over the turn currently in flight
///   heldTps  = the last measured value while that turn is still generating (shown with "~")
///   lastTurn = the final value, retained after the turn completes instead of resetting to "--"
/// </summary>
internal sealed class TpsTracker
{
    private readonly HashSet<string> _seenResponseKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// Raw model-output windows as they were observed, in arrival order. Reasoning and AgentMessage
    /// can interleave or overlap, so these are UNIONED (see <see cref="MergedModelElapsedMs"/>)
    /// rather than summed: two items covering [0 s,5 s] and [4 s,10 s] are 10 s of model activity,
    /// not 5 s + 6 s = 11 s.
    /// </summary>
    private readonly List<(long Start, long End)> _rawIntervals = [];

    private readonly List<(long Start, long End)> _mergedIntervals = [];

    private bool _intervalsDirty;

    // Turn identity and denominator (model-output time only).
    private string? _turnId;
    private long? _firstItemStartMs;
    private long? _lastItemEndMs;
    private bool _turnItemsComplete;

    // Numerator. Three official carriers are tracked independently. The two exact ones
    // (turn_token_usage and the thread-cumulative delta) are combined with max; the per-response
    // sum is only used when neither exact carrier is available, so repeat events can never inflate it.
    private long _turnOutputFromTurnUsage;
    private long _turnOutputFromThreadDelta;
    private long _turnOutputFromResponses;
    private long _threadOutputAtTurnStart;
    private bool _hasThreadOutputBaseline;

    // Smoothing.
    private double? _ema;
    private long _lastFoldGenerationMs = -1;
    private long _lastFoldOutputTokens = -1;

    public string? TurnId => _turnId;

    public bool Streaming { get; private set; }

    /// <summary>Union of this turn's Reasoning + AgentMessage windows, in milliseconds.</summary>
    public long TurnGenerationMs => MergedModelElapsedMs;

    /// <summary>
    /// Model activity time of the current turn: the merged (unioned) length of every
    /// Reasoning/AgentMessage window. Overlaps are counted once. Tool, IO and user time are
    /// not represented at all because those items are never recorded here.
    /// </summary>
    public long MergedModelElapsedMs
    {
        get
        {
            EnsureMerged();
            long total = 0;
            foreach (var (start, end) in _mergedIntervals)
            {
                total += end - start;
            }

            return total;
        }
    }

    /// <summary>The unioned windows, for the debug log's "Model intervals" lines.</summary>
    public IReadOnlyList<(long Start, long End)> ModelIntervals
    {
        get
        {
            EnsureMerged();
            return _mergedIntervals;
        }
    }

    /// <summary>How many raw windows were observed before unioning (diagnostic).</summary>
    public int RawIntervalCount => _rawIntervals.Count;

    public long? FirstItemStartMs => _firstItemStartMs;

    public long? LastItemEndMs => _lastItemEndMs;

    /// <summary>
    /// The turn-level TPS state machine used by the debug log and the strip:
    /// <c>idle</c> (no turn yet), <c>waiting</c> (turn begun, no model output measured yet),
    /// <c>generating</c> (model activity measured and the turn is live),
    /// <c>completed</c> (turn finished; the last measured value is retained).
    /// </summary>
    public string StateDescription
    {
        get
        {
            if (Streaming)
            {
                return SmoothedTps is null ? "waiting" : "generating";
            }

            return SmoothedTps is null && LastTurnTps is null ? "idle" : "completed";
        }
    }

    /// <summary>The unsmoothed last measurement — exposed for the debug log and the self-test.</summary>
    public double? RawLastSample { get; private set; }

    /// <summary>The smoothed value currently displayed (null when no valid measurement exists).</summary>
    public double? SmoothedTps { get; private set; }

    /// <summary>Average TPS retained after the most recent turn finished.</summary>
    public double? LastTurnTps { get; private set; }

    public long? TtftMs { get; set; }

    public long? LastTurnDurationMs { get; private set; }

    /// <summary>How many official measurements have been folded into the EMA.</summary>
    public int SampleCount { get; private set; }

    /// <summary>
    /// True when the whole turn was observed from its <c>task_started</c> record. When false the
    /// denominator is only a lower bound (the tail window began mid-turn), so TPS stays unknown:
    /// "--" is correct there, 0 would be a lie.
    /// </summary>
    public bool TurnItemsComplete => _turnItemsComplete;

    /// <summary>
    /// Official per-turn output tokens; 0 while the numerator is still unknown.
    ///
    /// The two exact, independent carriers — the sum of per-response <c>usage.output_tokens</c> and
    /// the thread-cumulative delta — are combined with max, which guards against a lagging baseline.
    /// <c>turn_token_usage</c> is only a last-resort fallback: some Codex builds write the
    /// <i>thread</i> total there rather than the turn total, so it must never win a max().
    /// </summary>
    public long TurnOutputTokens
    {
        get
        {
            var exact = Math.Max(_turnOutputFromResponses, _turnOutputFromThreadDelta);
            return exact > 0 ? exact : _turnOutputFromTurnUsage;
        }
    }

    /// <summary>
    /// Begins (or restarts) a turn. <paramref name="itemsComplete"/> is true only when the caller saw
    /// this turn's <c>task_started</c> record, which is what makes the denominator trustworthy.
    /// </summary>
    public void BeginTurn(string? turnId, bool itemsComplete, long threadCumulativeOutputTokens, bool hasCumulative)
    {
        if (string.Equals(_turnId, turnId, StringComparison.Ordinal) && _turnItemsComplete == itemsComplete)
        {
            return;
        }

        _turnId = turnId;
        _turnItemsComplete = itemsComplete;
        ClearIntervals();
        _firstItemStartMs = null;
        _lastItemEndMs = null;
        _turnOutputFromTurnUsage = 0;
        _turnOutputFromResponses = 0;
        _seenResponseKeys.Clear();
        _threadOutputAtTurnStart = threadCumulativeOutputTokens;
        _hasThreadOutputBaseline = hasCumulative;
        _turnOutputFromThreadDelta = 0;
        _lastFoldGenerationMs = -1;
        _lastFoldOutputTokens = -1;
        _activityAfterMeasurement = false;

        // The EMA is reseeded every turn. That keeps smoothing to well under three samples of
        // history (alpha 0.35 alone is already ~2.9), so a fresh turn reports its own rate instead
        // of lagging behind the previous one, while jitter inside a turn is still damped.
        _ema = null;
        SmoothedTps = null;
        Streaming = true;
    }

    public void MarkStreaming(bool streaming) => Streaming = streaming;

    /// <summary>
    /// Records that tool / IO work (CommandExecution, FileChange, McpToolCall, WebSearch) completed
    /// after the last model-output measurement. The displayed TPS was measured before that activity,
    /// so it is "held" and the strip prefixes it with "~". Cleared when a fresh sample is folded.
    /// </summary>
    public void NoteActivityAfterMeasurement() => _activityAfterMeasurement = true;

    /// <summary>
    /// Records one model-output item's window. Non model-output items must not be passed here.
    /// The window is UNIONED into the turn's model activity rather than added, so overlapping or
    /// interleaved Reasoning / AgentMessage items cannot inflate the denominator.
    /// </summary>
    public void ObserveModelOutputItem(long startedAtMs, long completedAtMs)
    {
        var duration = TpsRules.NormalizeItemDuration(startedAtMs, completedAtMs);
        if (duration > 0)
        {
            AddInterval(startedAtMs, startedAtMs + duration);
        }

        if (startedAtMs > 0 && (_firstItemStartMs is null || startedAtMs < _firstItemStartMs))
        {
            _firstItemStartMs = startedAtMs;
        }

        if (completedAtMs > 0 && (_lastItemEndMs is null || completedAtMs > _lastItemEndMs))
        {
            _lastItemEndMs = completedAtMs;
        }
    }

    private void AddInterval(long start, long end)
    {
        _rawIntervals.Add((start, end));

        // Bound the list: once it is large, collapse it in place. Unioning is idempotent, so
        // replacing the raw windows with their merged form loses nothing.
        if (_rawIntervals.Count > 4096)
        {
            EnsureMerged();
            _rawIntervals.Clear();
            _rawIntervals.AddRange(_mergedIntervals);
            _mergedIntervals.Clear();
        }

        _intervalsDirty = true;
    }

    private void ClearIntervals()
    {
        _rawIntervals.Clear();
        _mergedIntervals.Clear();
        _intervalsDirty = false;
    }

    /// <summary>
    /// Sorts and merges the raw windows into a disjoint, ascending set. Two windows that touch or
    /// overlap (next.Start &lt;= current.End) become one, which is what makes the total a true
    /// union instead of a sum of durations.
    /// </summary>
    private void EnsureMerged()
    {
        if (!_intervalsDirty)
        {
            return;
        }

        _rawIntervals.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        _mergedIntervals.Clear();

        long currentStart = 0;
        long currentEnd = 0;
        var open = false;

        foreach (var interval in _rawIntervals)
        {
            if (!open)
            {
                currentStart = interval.Start;
                currentEnd = interval.End;
                open = true;
                continue;
            }

            if (interval.Start <= currentEnd)
            {
                if (interval.End > currentEnd)
                {
                    currentEnd = interval.End;
                }
            }
            else
            {
                _mergedIntervals.Add((currentStart, currentEnd));
                currentStart = interval.Start;
                currentEnd = interval.End;
            }
        }

        if (open)
        {
            _mergedIntervals.Add((currentStart, currentEnd));
        }

        _intervalsDirty = false;
    }

    /// <summary>Official per-turn output total from <c>turn_token_usage.output_tokens</c>.</summary>
    public void ObserveTurnUsageOutput(long turnOutputTokens) =>
        _turnOutputFromTurnUsage = Math.Max(_turnOutputFromTurnUsage, turnOutputTokens);

    /// <summary>
    /// Official cumulative thread output tokens. Used to derive the in-flight turn's output as a
    /// delta, which is exact and needs no deduplication — this is the path that keeps older Codex
    /// sessions (no token_usage_record at all) correct.
    /// </summary>
    public void ObserveThreadCumulativeOutput(long threadOutputTokens, bool hasCumulative)
    {
        if (!hasCumulative)
        {
            return;
        }

        if (!_hasThreadOutputBaseline)
        {
            _threadOutputAtTurnStart = threadOutputTokens;
            _hasThreadOutputBaseline = true;
        }

        _turnOutputFromThreadDelta = Math.Max(0, threadOutputTokens - _threadOutputAtTurnStart);
    }

    /// <summary>
    /// Per-response official output, deduplicated by response identity. Used only when neither exact
    /// carrier exists, so a repeated token_count event cannot double count.
    /// </summary>
    public void ObserveResponseOutput(string responseKey, long outputTokens)
    {
        if (outputTokens <= 0)
        {
            return;
        }

        if (!_seenResponseKeys.Add(responseKey))
        {
            DuplicateResponseSamplesIgnored++;
            return;
        }

        _turnOutputFromResponses += outputTokens;
    }

    /// <summary>Per-response samples dropped because the same response was reported again.</summary>
    public int DuplicateResponseSamplesIgnored { get; private set; }

    /// <summary>Folds a fresh measurement into the EMA when either side of the ratio advanced.</summary>
    public void RefreshMeasurement()
    {
        var elapsed = MergedModelElapsedMs;
        if (!_turnItemsComplete || elapsed <= 0)
        {
            return;
        }

        var output = TurnOutputTokens;
        if (output <= 0)
        {
            return;
        }

        if (elapsed == _lastFoldGenerationMs && output == _lastFoldOutputTokens)
        {
            return;
        }

        var sample = 1000d * output / elapsed;
        if (!MetricFormat.IsUsableTps(sample) || sample > TpsRules.ImplausibleTps)
        {
            return;
        }

        _lastFoldGenerationMs = elapsed;
        _lastFoldOutputTokens = output;
        _activityAfterMeasurement = false;
        RawLastSample = sample;
        SampleCount++;
        _ema = _ema is null
            ? sample
            : (TpsRules.SmoothingAlpha * sample) + ((1 - TpsRules.SmoothingAlpha) * _ema.Value);
        SmoothedTps = MetricFormat.IsUsableTps(_ema) ? _ema : null;
    }

    /// <summary>Finalises a turn and retains its average as <see cref="LastTurnTps"/>.</summary>
    public void CompleteTurn(long? durationMs, long? ttftMs)
    {
        RefreshMeasurement();
        if (durationMs is > 0)
        {
            LastTurnDurationMs = durationMs;
        }

        if (ttftMs is not null)
        {
            TtftMs = ttftMs;
        }

        Streaming = false;
        LastTurnTps = SmoothedTps;
    }

    /// <summary>Aborts a turn (turn_aborted) without inventing a final average.</summary>
    public void AbortTurn()
    {
        Streaming = false;
        LastTurnTps = SmoothedTps;
    }

    /// <summary>
    /// Clears everything for a fresh conversation or a re-seed from the tail. TPS intentionally goes
    /// back to unknown: the numbers that belonged to the previous conversation must never be shown
    /// against the new one. A completed turn found inside the tail window re-derives its own value.
    /// </summary>
    public void ResetTurn()
    {
        _turnId = null;
        _turnItemsComplete = false;
        ClearIntervals();
        _firstItemStartMs = null;
        _lastItemEndMs = null;
        _turnOutputFromTurnUsage = 0;
        _turnOutputFromThreadDelta = 0;
        _turnOutputFromResponses = 0;
        _seenResponseKeys.Clear();
        _threadOutputAtTurnStart = 0;
        _hasThreadOutputBaseline = false;
        _lastFoldGenerationMs = -1;
        _lastFoldOutputTokens = -1;
        _activityAfterMeasurement = false;
        _ema = null;
        SmoothedTps = null;
        LastTurnTps = null;
        RawLastSample = null;
        SampleCount = 0;
        TtftMs = null;
        LastTurnDurationMs = null;
        Streaming = false;
    }

    /// <summary>
    /// What the collapsed strip shows.
    /// While a turn is live only a measurement from that turn counts, so a brand-new turn with no
    /// model output yet correctly reads "--" instead of the previous turn's number. Once the turn
    /// ends its final value is retained, which is the "keep the last turn's TPS" behaviour.
    /// During tool execution the last model measurement is simply kept — never 0.
    /// </summary>
    public double? DisplayTps => Streaming ? SmoothedTps : SmoothedTps ?? LastTurnTps;

    /// <summary>
    /// True when the displayed value is held rather than final: model-output time has accrued since
    /// the last sample, or tool / IO work completed after it, so generation has moved on.
    /// </summary>
    public bool IsHeld =>
        Streaming
        && SmoothedTps is not null
        && (_activityAfterMeasurement || MergedModelElapsedMs > _lastFoldGenerationMs);

    private bool _activityAfterMeasurement;
}

/// <summary>
/// Everything recovered from one rollout file. Cumulative usage comes from whichever carrier is
/// newest in file order: <c>token_usage_record.payload.thread_token_usage</c> (Codex 0.159+) or
/// <c>event_msg/token_count.payload.info.total_token_usage</c> (the only carrier on 0.151).
/// </summary>
internal sealed class RolloutStreamState
{
    private static readonly HashSet<string> KnownRecordTypes = new(StringComparer.Ordinal)
    {
        "session_meta", "turn_context", "response_item", "event_msg",
        "token_usage_record", "world_state", "compacted"
    };

    private static readonly HashSet<string> KnownEventTypes = new(StringComparer.Ordinal)
    {
        "task_started", "task_complete", "token_count", "item_completed",
        "thread_settings_applied", "turn_aborted", "agent_message"
    };

    private static readonly HashSet<string> KnownItemTypes = new(StringComparer.Ordinal)
    {
        "UserMessage", "Reasoning", "AgentMessage", "CommandExecution",
        "FileChange", "McpToolCall", "WebSearch", "ImageView"
    };

    public TpsTracker Tps { get; } = new();

    public string? SessionMetaId { get; set; }

    public string? SessionMetaOriginator { get; set; }

    public long InputTokens { get; set; }

    public long CachedInputTokens { get; set; }

    public long OutputTokens { get; set; }

    public long ReasoningOutputTokens { get; set; }

    public long TotalTokens { get; set; }

    public long ContextUsedTokens { get; set; }

    public long ContextWindowTokens { get; set; }

    public bool HasCumulativeUsage { get; set; }

    /// <summary>True when at least one cumulative reading came from the rollout itself.</summary>
    public bool HasRolloutUsage { get; set; }

    public string? CurrentTurnId { get; set; }

    public bool Streaming => Tps.Streaming;

    /// <summary>Distinct unknown record/event/item types, for the debug log's Warnings line.</summary>
    public SortedSet<string> Warnings { get; } = new(StringComparer.Ordinal);

    public long LineCount { get; set; }

    public bool SessionMetaSeen { get; set; }

    public long UncachedInputTokens => Math.Max(0, InputTokens - CachedInputTokens);

    /// <summary>
    /// Which official carrier supplied the cumulative usage:
    /// <c>token_usage_record</c> (Codex 0.159+), <c>token_count</c> (the only carrier on 0.151),
    /// both, or <c>none</c>.
    /// </summary>
    public string Carrier { get; private set; } = "none";

    /// <summary>Identical cumulative readings seen again and deliberately not re-applied.</summary>
    public int DuplicateUsageReadingsIgnored { get; private set; }

    public void ObserveLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        LineCount++;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            Warnings.Add("malformed-json");
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String)
            {
                Warnings.Add("missing-record-type");
                return;
            }

            var recordType = typeElement.GetString() ?? string.Empty;
            switch (recordType)
            {
                case "session_meta":
                    ObserveSessionMeta(root);
                    break;
                case "token_usage_record":
                    ObserveTokenUsageRecord(root);
                    break;
                case "event_msg":
                    ObserveEventMessage(root);
                    break;
                case "turn_context":
                case "response_item":
                case "world_state":
                case "compacted":
                    break;
                default:
                    Warnings.Add("record:" + recordType);
                    break;
            }
        }
    }

    private void ObserveSessionMeta(JsonElement root)
    {
        if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        SessionMetaSeen = true;
        SessionMetaId = GetString(payload, "id") ?? SessionMetaId;
        SessionMetaOriginator = GetString(payload, "originator") ?? SessionMetaOriginator;
    }

    private void ObserveEventMessage(JsonElement root)
    {
        if (!root.TryGetProperty("payload", out var payload)
            || payload.ValueKind != JsonValueKind.Object)
        {
            Warnings.Add("event_msg:missing-payload");
            return;
        }

        var eventType = GetString(payload, "type");
        if (eventType is null)
        {
            Warnings.Add("event_msg:missing-payload-type");
            return;
        }

        if (!KnownEventTypes.Contains(eventType))
        {
            Warnings.Add("event:" + eventType);
            return;
        }

        switch (eventType)
        {
            case "task_started":
                ObserveTaskStarted(payload);
                break;
            case "task_complete":
                ObserveTaskComplete(payload);
                break;
            case "token_count":
                ObserveTokenCount(payload);
                break;
            case "item_completed":
                ObserveItemCompleted(payload);
                break;
            case "turn_aborted":
                Tps.MarkStreaming(false);
                Tps.AbortTurn();
                break;
            case "thread_settings_applied":
            case "agent_message":
                break;
        }
    }

    private void ObserveTaskStarted(JsonElement payload)
    {
        var turnId = GetString(payload, "turn_id");
        CurrentTurnId = turnId;
        Tps.BeginTurn(turnId, itemsComplete: true, OutputTokens, HasCumulativeUsage);
    }

    private void ObserveTaskComplete(JsonElement payload)
    {
        var turnId = GetString(payload, "turn_id");
        if (!string.IsNullOrWhiteSpace(turnId))
        {
            CurrentTurnId = turnId;
        }

        Tps.MarkStreaming(false);
        Tps.CompleteTurn(TryGetLong(payload, "duration_ms"), TryGetLong(payload, "time_to_first_token_ms"));
    }

    private void ObserveTokenCount(JsonElement payload)
    {
        if (!payload.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (info.TryGetProperty("total_token_usage", out var total) && total.ValueKind == JsonValueKind.Object)
        {
            // Cumulative for the whole thread. Redundant with token_usage_record on 0.159 and the
            // only carrier on older Codex, so "last one wins in file order" is right for both.
            ApplyCumulativeUsage(total, "token_count");
        }

        if (TryGetLong(info, "model_context_window") is long window && window > 0)
        {
            ContextWindowTokens = window;
        }

        if (info.TryGetProperty("last_token_usage", out var last) && last.ValueKind == JsonValueKind.Object)
        {
            ContextUsedTokens = ReadRequestSize(last);

            var lastOutput = GetLong(last, "output_tokens");
            if (lastOutput > 0)
            {
                // Older Codex (0.151) has no token_usage_record, so the per-response official count
                // exists only here. The dedup key is derived from the VALUES rather than from the
                // carrier: 0.159 emits both carriers with byte-identical usage for one response, so a
                // carrier-specific key would count every response twice (measured: exactly 2x).
                Tps.ObserveResponseOutput(ResponseSampleKey(last), lastOutput);
            }
        }

        Tps.RefreshMeasurement();
    }

    private void ObserveTokenUsageRecord(JsonElement root)
    {
        if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var turnId = GetString(payload, "turn_id");
        if (!string.IsNullOrWhiteSpace(turnId))
        {
            if (!string.Equals(CurrentTurnId, turnId, StringComparison.Ordinal))
            {
                // A turn whose task_started we never saw: mark it incomplete so its TPS stays unknown.
                CurrentTurnId = turnId;
                Tps.BeginTurn(turnId, itemsComplete: false, OutputTokens, HasCumulativeUsage);
            }
            else if (Tps.TurnId is null)
            {
                Tps.BeginTurn(turnId, itemsComplete: false, OutputTokens, HasCumulativeUsage);
            }
        }

        if (payload.TryGetProperty("thread_token_usage", out var threadUsage)
            && threadUsage.ValueKind == JsonValueKind.Object)
        {
            ApplyCumulativeUsage(threadUsage, "token_usage_record");
        }

        if (payload.TryGetProperty("turn_token_usage", out var turnUsage)
            && turnUsage.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("thread_token_usage", out var threadForCompare)
            && threadForCompare.ValueKind == JsonValueKind.Object
            && !UsageEquals(turnUsage, threadForCompare))
        {
            // Genuinely turn-scoped: use it, because it survives a tail window that missed this
            // turn's earlier responses. Measured on Codex 26.928.3736.0 (core 0.159.2), however,
            // turn_token_usage is a byte-identical COPY of thread_token_usage for every record —
            // treating it as a per-turn value would put the whole conversation's output in the
            // numerator. Hence the equality guard: only a genuinely different value is trusted.
            Tps.ObserveTurnUsageOutput(GetLong(turnUsage, "output_tokens"));
        }

        if (payload.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            // "Context used" is the size of the current request, never the conversation total.
            ContextUsedTokens = ReadRequestSize(usage);
            Tps.ObserveResponseOutput(ResponseSampleKey(usage), GetLong(usage, "output_tokens"));
        }

        Tps.RefreshMeasurement();
    }

    private void ObserveItemCompleted(JsonElement payload)
    {
        var turnId = GetString(payload, "turn_id");
        if (!string.IsNullOrWhiteSpace(turnId) && !string.Equals(CurrentTurnId, turnId, StringComparison.Ordinal))
        {
            CurrentTurnId = turnId;
            Tps.BeginTurn(turnId, itemsComplete: false, OutputTokens, HasCumulativeUsage);
        }

        var itemType = payload.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object
            ? GetString(item, "type")
            : null;
        if (itemType is null)
        {
            Warnings.Add("item_completed:missing-item-type");
            return;
        }

        if (!KnownItemTypes.Contains(itemType))
        {
            Warnings.Add("item:" + itemType);
        }

        if (!TpsRules.IsModelOutputItem(itemType))
        {
            // Tool / user / IO time: it must never reach the TPS denominator. It does, however, mean
            // the displayed number was measured before this activity started, so mark it "held".
            Tps.NoteActivityAfterMeasurement();
            return;
        }

        Tps.ObserveModelOutputItem(
            TryGetLong(payload, "started_at_ms") ?? 0,
            TryGetLong(payload, "completed_at_ms") ?? 0);
        Tps.RefreshMeasurement();
    }

    private void ApplyCumulativeUsage(JsonElement usage, string carrier)
    {
        var total = GetLong(usage, "total_tokens");
        var input = GetLong(usage, "input_tokens");
        var output = GetLong(usage, "output_tokens");
        if (total <= 0 && input <= 0 && output <= 0)
        {
            return;
        }

        var cached = GetLong(usage, "cached_input_tokens");

        // Every carrier here is CUMULATIVE, so the newest reading simply replaces the previous one.
        // Nothing is ever added, which is what makes a duplicate reading harmless — but it is
        // counted and reported so a future Codex change that starts emitting deltas is visible
        // instead of silently corrupting the totals.
        if (HasCumulativeUsage && total == TotalTokens && input == InputTokens
            && cached == CachedInputTokens && output == OutputTokens)
        {
            DuplicateUsageReadingsIgnored++;
            return;
        }

        if (Carrier == "none")
        {
            Carrier = carrier;
        }
        else if (Carrier != carrier)
        {
            Carrier = "token_usage_record+token_count";
        }

        TotalTokens = total;
        InputTokens = input;
        CachedInputTokens = cached;
        OutputTokens = output;
        ReasoningOutputTokens = GetLong(usage, "reasoning_output_tokens");
        HasCumulativeUsage = true;
        HasRolloutUsage = true;
        Tps.ObserveThreadCumulativeOutput(output, hasCumulative: true);
    }

    /// <summary>Applies a baseline recovered from state_5.sqlite when the rollout tail had nothing.</summary>
    public void ApplyStateDatabaseBaseline(long tokensUsed)
    {
        if (tokensUsed <= 0 || HasRolloutUsage)
        {
            return;
        }

        TotalTokens = tokensUsed;
        OutputTokens = 0;
        InputTokens = tokensUsed;
        CachedInputTokens = 0;
        HasCumulativeUsage = true;
    }

    /// <summary>
    /// Size of the request that produced a response: <c>total_tokens</c>, falling back to the input
    /// side when a carrier omits the total. This is "context used", not the conversation total.
    /// </summary>
    private static long ReadRequestSize(JsonElement usage)
    {
        var total = GetLong(usage, "total_tokens");
        if (total > 0)
        {
            return total;
        }

        var input = GetLong(usage, "input_tokens");
        return input > 0 ? input : GetLong(usage, "output_tokens");
    }

    /// <summary>
    /// Carrier-independent identity of one official model response, built from its own usage values.
    /// Both carriers report identical values for a response, and repeated events repeat them too, so
    /// this key makes the per-response numerator immune to double counting.
    /// </summary>
    private static string ResponseSampleKey(JsonElement usage) => string.Create(
        CultureInfo.InvariantCulture,
        $"{GetLong(usage, "input_tokens")}:{GetLong(usage, "cached_input_tokens")}:"
        + $"{GetLong(usage, "output_tokens")}:{GetLong(usage, "total_tokens")}");

    /// <summary>True when two usage objects carry the same official counts.</summary>
    private static bool UsageEquals(JsonElement left, JsonElement right) =>
        GetLong(left, "input_tokens") == GetLong(right, "input_tokens")
        && GetLong(left, "cached_input_tokens") == GetLong(right, "cached_input_tokens")
        && GetLong(left, "output_tokens") == GetLong(right, "output_tokens")
        && GetLong(left, "total_tokens") == GetLong(right, "total_tokens");

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static long GetLong(JsonElement element, string propertyName) => TryGetLong(element, propertyName) ?? 0;

    private static long? TryGetLong(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.Number
        && property.TryGetInt64(out var value)
            ? value
            : null;
}