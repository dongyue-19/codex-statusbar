using System.Globalization;

namespace CodexStatusbar;

internal sealed record OverlayMetric(
    DisplayField Field,
    string CompactLabel,
    string ExpandedLabel,
    string Value,
    bool HasValue);

internal sealed record OverlayPresentation(
    OverlayMetric Primary,
    OverlayMetric Secondary,
    IReadOnlyList<OverlayMetric> ExpandedRows,
    double ContextPercent,
    bool ShowContextProgress,
    string? StatusText,
    string? CapsuleText = null,
    IReadOnlyList<string>? CapsuleVariants = null)
{
    /// <summary>
    /// The collapsed strip's text at every supported verbosity, widest first. The last entry is the
    /// narrowest form that still carries all three metrics; callers pick the first one that fits and
    /// hide the strip when none does.
    /// </summary>
    public IReadOnlyList<string> Variants => CapsuleVariants ?? (CapsuleText is null ? [] : [CapsuleText]);

    /// <summary>The separator between the three metrics. A middle dot with single spaces: the double
    /// and triple spaces the strip used to carry read as a HUD, not as a native toolbar label.</summary>
    private const string Separator = " · ";

    /// <summary>The narrowest variant that is still worth showing: total and cache, no speed.</summary>
    public string NarrowestVariant => Variants.Count > 0 ? Variants[^1] : string.Empty;
}

internal static class OverlayPresentationBuilder
{
    private const string NoValue = "—";

    /// <summary>The separator between the three metrics. A middle dot with single spaces: the double
    /// and triple spaces the strip used to carry read as a HUD, not as a native toolbar label.</summary>
    private const string Separator = " · ";

    /// <summary>The collapsed strip before any conversation is known.</summary>
    public const string WaitingCapsuleText = "⚡ -- tok/s · -- tok · Cache --";

    /// <summary>
    /// The strip's verbosity ladder, widest first. Level 0 is what the user sees at normal widths —
    /// the full wording is never abbreviated by default; the shorter forms exist only to keep the
    /// strip from colliding with the composer's own buttons when Codex is dragged very narrow.
    /// </summary>
    public static IReadOnlyList<string> BuildCapsuleVariants(
        string tps,
        string total,
        string cache,
        bool hasUsage)
    {
        var tokens = hasUsage ? total : "--";
        var cacheText = hasUsage ? cache : "--";
        return
        [
            // 0 — full
            $"⚡ {tps} tok/s{Separator}{tokens} tok{Separator}Cache {cacheText}",

            // 1 — drop the unit suffix from the token count and the "Cache" word
            $"⚡ {tps} t/s{Separator}{tokens}{Separator}{cacheText}",

            // 2 — drop the lightning glyph too
            $"{tps} t/s{Separator}{tokens}{Separator}{cacheText}",

            // 3 — the two quantities that need no unit: total tokens and cache ratio
            $"{tokens}{Separator}{cacheText}"
        ];
    }

    public static OverlayPresentation CreateWaiting(
        string statusText,
        DisplayField primaryField,
        DisplayField secondaryField,
        DisplayField visibleFields)
    {
        ValidateHighlightedFields(primaryField, secondaryField);
        return new OverlayPresentation(
            CreateWaitingMetric(primaryField),
            CreateWaitingMetric(secondaryField),
            CreateExpandedRows(visibleFields, primaryField, secondaryField, CreateWaitingMetric),
            0,
            false,
            SanitizeSingleLine(statusText),
            WaitingCapsuleText,
            BuildCapsuleVariants("--", "--", "--", hasUsage: false));
    }

    public static OverlayPresentation Create(
        TokenSnapshot snapshot,
        DisplayField primaryField,
        DisplayField secondaryField,
        DisplayField visibleFields)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateHighlightedFields(primaryField, secondaryField);
        var contextPercent = Math.Clamp(snapshot.ContextPercent, 0, 100);
        return new OverlayPresentation(
            CreateMetric(snapshot, primaryField, contextPercent),
            CreateMetric(snapshot, secondaryField, contextPercent),
            CreateExpandedRows(
                visibleFields,
                primaryField,
                secondaryField,
                field => CreateMetric(snapshot, field, contextPercent)),
            contextPercent,
            (visibleFields & DisplayField.ContextPercent) != 0,
            null,
            BuildCapsuleVariants(snapshot)[0],
            BuildCapsuleVariants(snapshot));
    }

    public static string FormatTokenCount(long value) => value switch
    {
        >= 1_000_000 => (value / 1_000_000d).ToString("0.00", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (value / 1_000d).ToString("0.0", CultureInfo.InvariantCulture) + "k",
        _ => value.ToString("N0", CultureInfo.InvariantCulture)
    };

    public static string ShortThreadId(string threadId, int maximumLength = 12)
    {
        var singleLineThreadId = SanitizeSingleLine(threadId);
        if (singleLineThreadId.Length <= maximumLength)
        {
            return singleLineThreadId;
        }

        if (maximumLength <= 1)
        {
            return maximumLength == 1 ? "…" : string.Empty;
        }

        var prefixLength = Math.Min(4, maximumLength - 1);
        var suffixLength = Math.Min(6, maximumLength - prefixLength - 1);
        return string.Concat(
            singleLineThreadId.AsSpan(0, prefixLength),
            "…",
            singleLineThreadId.AsSpan(singleLineThreadId.Length - suffixLength, suffixLength));
    }

    public static string GetFieldMenuText(DisplayField field)
    {
        return field switch
        {
            DisplayField.Total => "总 token",
            DisplayField.Input => "输入 token",
            DisplayField.Output => "输出 token",
            DisplayField.CacheHit => "缓存命中",
            DisplayField.CacheHitRate => "缓存命中率",
            DisplayField.CacheMiss => "缓存未命中（推导）",
            DisplayField.Context => "上下文用量",
            DisplayField.ContextPercent => "上下文百分比",
            DisplayField.Reasoning => "推理输出",
            DisplayField.Thread => "会话 ID",
            DisplayField.Tps => "输出速度 TPS",
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "不支持的展示字段。")
        };
    }

    /// <summary>
    /// The collapsed strip at every verbosity, widest first, as one line each:
    /// <c>⚡ 243 tok/s · 5.7M tok · Cache 98%</c>.
    ///
    /// Total is rendered as <c>input + output</c> — the strict definition — not as the carrier's
    /// own <c>total_tokens</c> field, so what is displayed is literally the documented formula.
    /// Cache renders as <c>--</c> when input is 0, never as 0% or NaN. TPS renders as <c>--</c>
    /// whenever it is unknown, and gains a leading <c>~</c> while it is a held value.
    /// </summary>
    public static IReadOnlyList<string> BuildCapsuleVariants(TokenSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return BuildCapsuleVariants(
            MetricFormat.FormatTps(snapshot.Tps, snapshot.TpsHeld),
            MetricFormat.CompactTokens(snapshot.InputTokens + snapshot.OutputTokens),
            MetricFormat.FormatCachePercentCompact(snapshot.InputTokens, snapshot.CachedInputTokens),
            snapshot.HasUsage);
    }

    /// <summary>The widest variant — what the strip shows whenever there is room for it.</summary>
    public static string BuildCapsuleText(TokenSnapshot snapshot) => BuildCapsuleVariants(snapshot)[0];

    private static void ValidateHighlightedFields(DisplayField primaryField, DisplayField secondaryField)
    {
        if (!DisplayFieldRules.IsSingleSupported(primaryField)
            || !DisplayFieldRules.IsSingleSupported(secondaryField))
        {
            throw new ArgumentException("收起指标必须是受支持的单个字段。");
        }
    }

    private static IReadOnlyList<OverlayMetric> CreateExpandedRows(
        DisplayField visibleFields,
        DisplayField primaryField,
        DisplayField secondaryField,
        Func<DisplayField, OverlayMetric> createMetric)
    {
        return DisplayFieldRules.Ordered
            .Where(field => (visibleFields & field) != 0)
            .Where(field => field != primaryField && field != secondaryField)
            .Select(createMetric)
            .ToArray();
    }

    private static OverlayMetric CreateWaitingMetric(DisplayField field)
    {
        var labels = GetLabels(field);
        return new OverlayMetric(field, labels.Compact, labels.Expanded, NoValue, false);
    }

    private static OverlayMetric CreateMetric(
        TokenSnapshot snapshot,
        DisplayField field,
        double contextPercent)
    {
        var labels = GetLabels(field);
        var value = field switch
        {
            DisplayField.Total => FormatTokenCount(snapshot.TotalTokens),
            DisplayField.Input => FormatTokenCount(snapshot.InputTokens),
            DisplayField.Output => FormatTokenCount(snapshot.OutputTokens),
            DisplayField.CacheHit => FormatTokenCount(snapshot.CachedInputTokens),
            DisplayField.CacheHitRate => $"{snapshot.CacheHitPercent:0}%",
            DisplayField.CacheMiss => FormatTokenCount(snapshot.UncachedInputTokens),
            DisplayField.Context => $"{FormatTokenCount(snapshot.ContextUsedTokens)} / {FormatTokenCount(snapshot.ContextWindowTokens)}",
            DisplayField.ContextPercent => $"{contextPercent:0}%",
            DisplayField.Reasoning => FormatTokenCount(snapshot.ReasoningOutputTokens),
            DisplayField.Thread => ShortThreadId(snapshot.ThreadId),
            DisplayField.Tps => FormatTpsValue(snapshot),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "不支持的展示字段。")
        };
        var hasValue = field != DisplayField.Thread || !string.IsNullOrWhiteSpace(snapshot.ThreadId);
        return new OverlayMetric(field, labels.Compact, labels.Expanded, value, hasValue);
    }

    private static (string Compact, string Expanded) GetLabels(DisplayField field)
    {
        return field switch
        {
            DisplayField.Total => ("总", "总 Token"),
            DisplayField.Input => ("入", "输入"),
            DisplayField.Output => ("出", "输出"),
            DisplayField.CacheHit => ("命中", "缓存命中"),
            DisplayField.CacheHitRate => ("命中率", "缓存命中率"),
            DisplayField.CacheMiss => ("未中", "缓存未命中"),
            DisplayField.Context => ("上下文", "上下文用量"),
            DisplayField.ContextPercent => ("上下文", "上下文占用"),
            DisplayField.Reasoning => ("推理", "推理输出"),
            DisplayField.Thread => ("会话", "会话"),
            DisplayField.Tps => ("⚡", "输出速度"),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "不支持的展示字段。")
        };
    }

    /// <summary>TPS cell used by the expanded panel and the tray menu: e.g. "243 tok/s", "-- tok/s".</summary>
    private static string FormatTpsValue(TokenSnapshot snapshot) =>
        MetricFormat.FormatTps(snapshot.Tps, snapshot.TpsHeld) + " tok/s";

    private static string SanitizeSingleLine(string value)
    {
        return value.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);
    }
}

internal sealed class PresentationProbeRequest
{
    public List<PresentationProbeCase> Cases { get; set; } = new();
}

internal sealed class PresentationProbeCase
{
    public string Name { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public TokenSnapshot? Snapshot { get; set; }
    public int? PrimaryField { get; set; }
    public int? SecondaryField { get; set; }
    public int? VisibleFields { get; set; }
    public string? StatusText { get; set; }
}

internal sealed record PresentationProbeCaseResult(string Name, OverlayPresentation Presentation);

internal sealed record PresentationProbeResult(IReadOnlyList<PresentationProbeCaseResult> Cases);

internal static class PresentationProbe
{
    public static PresentationProbeResult Execute(PresentationProbeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var results = new List<PresentationProbeCaseResult>();
        foreach (var probeCase in request.Cases)
        {
            var primaryField = RequireField(probeCase.PrimaryField, nameof(probeCase.PrimaryField));
            var secondaryField = RequireField(probeCase.SecondaryField, nameof(probeCase.SecondaryField));
            var visibleFields = (DisplayField)(probeCase.VisibleFields ?? 0);
            var presentation = probeCase.Operation switch
            {
                "Create" => OverlayPresentationBuilder.Create(
                    probeCase.Snapshot ?? throw new ArgumentException("Create 操作需要 Snapshot。", nameof(probeCase)),
                    primaryField,
                    secondaryField,
                    visibleFields),
                "Waiting" => OverlayPresentationBuilder.CreateWaiting(
                    probeCase.StatusText ?? string.Empty,
                    primaryField,
                    secondaryField,
                    visibleFields),
                _ => throw new ArgumentException($"不支持的展示探针操作：{probeCase.Operation}", nameof(probeCase))
            };
            results.Add(new PresentationProbeCaseResult(probeCase.Name, presentation));
        }

        return new PresentationProbeResult(results);
    }

    private static DisplayField RequireField(int? value, string parameterName)
    {
        if (!value.HasValue)
        {
            throw new ArgumentException("展示探针需要字段。", parameterName);
        }

        return (DisplayField)value.Value;
    }
}
