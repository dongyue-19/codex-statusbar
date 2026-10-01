using System.Globalization;
using System.Text;

namespace CodexStatusbar;

/// <summary>
/// <c>--debug</c> diagnostics. Emits exactly the documented block to
/// <c>%LOCALAPPDATA%\CodexStatusbar\debug.log</c> and, when a console is attached, to stderr.
/// </summary>
internal sealed class DebugDiagnostics
{
    private const int LabelWidth = 21;

    private readonly object _sync = new();

    public DebugDiagnostics(bool enabled, string? logPath = null)
    {
        Enabled = enabled;
        LogPath = logPath ?? DefaultLogPath;
    }

    public static string DefaultLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexStatusbar",
        "debug.log");

    public bool Enabled { get; }

    public string LogPath { get; }

    public long BlocksWritten { get; private set; }

    public string? LastBlock { get; private set; }

    public void Write(TokenSnapshot? snapshot)
    {
        if (!Enabled)
        {
            return;
        }

        var block = Build(snapshot);
        lock (_sync)
        {
            LastBlock = block;
            BlocksWritten++;
            try
            {
                var directory = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(LogPath, block + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Diagnostics must never take the overlay down.
            }
        }

        if (HasConsole())
        {
            try
            {
                Console.Error.WriteLine(block);
                Console.Error.Flush();
            }
            catch (IOException)
            {
                // Console disappeared mid-write.
            }
        }
    }

    /// <summary>
    /// Appends a <c>[position]</c> section. It is written as its own block rather than added to the
    /// metric block so existing tools that read the metric block keep working untouched, and the
    /// section header deliberately contains no colon so it is not mistaken for a field.
    /// </summary>
    public void WritePosition(string body)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        // Trailing blank line: it terminates the section, so a reader (or a script) can split the log
        // into blocks without knowing every field name.
        AppendRaw(Environment.NewLine + "[position]" + Environment.NewLine + body + Environment.NewLine);
    }

    private void AppendRaw(string text)
    {
        lock (_sync)
        {
            try
            {
                var directory = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(LogPath, text + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Diagnostics must never take the overlay down.
            }
        }
    }

    /// <summary>
    /// One timestamped lifecycle line, e.g. <c>[12:03:18] Codex process detected PID=12345</c>. These
    /// are appended as they happen rather than folded into a block, because the ordering across a
    /// Codex start is the thing being diagnosed.
    /// </summary>
    public void Event(string message)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        AppendRaw($"[{DateTime.Now:HH:mm:ss}] {message}");
    }

    /// <summary>
    /// Appends the <c>[lifecycle]</c> section: which mode the process is in, whether it is the
    /// primary instance, what Windows will launch at logon, and where the watcher currently is. Like
    /// <c>[position]</c> it is its own block with a colon-free header, so readers of the metric block
    /// are unaffected.
    /// </summary>
    public void WriteLifecycle(string body)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        AppendRaw(Environment.NewLine + "[lifecycle]" + Environment.NewLine + body + Environment.NewLine);
    }

    /// <summary>
    /// Everything the <c>[lifecycle]</c> block needs. Gathered by the caller because it owns the
    /// watcher, the registry and the overlay; kept as plain values so this type stays free of them.
    /// </summary>
    internal readonly record struct LifecycleReport(
        string Mode,
        string SingleInstance,
        string StartupRegistration,
        string StartupCommand,
        string WatcherState,
        bool CodexDetected,
        int CodexProcessId,
        string Package,
        string CodexVersion,
        long CodexWindowHandle,
        int AttachAttempt,
        bool IpcConnected,
        bool SessionReady,
        bool UiaReady,
        bool OverlayVisible,
        string DetectionRule,
        long DetectCount,
        double MillisecondsSinceDetect,
        long ErrorCount,
        string? WatcherError);

    public static string BuildLifecycle(LifecycleReport report)
    {
        var builder = new StringBuilder();
        Append(builder, "Mode:", report.Mode);
        Append(builder, "Single instance:", report.SingleInstance);
        Append(builder, "Startup registration:", report.StartupRegistration);
        Append(builder, "Startup command:", report.StartupCommand);
        Append(builder, "Watcher state:", report.WatcherState);
        Append(
            builder,
            "Codex detected:",
            report.CodexDetected ? $"true (PID {report.CodexProcessId})" : "false");
        Append(
            builder,
            "Codex PID:",
            report.CodexDetected ? report.CodexProcessId.ToString(CultureInfo.InvariantCulture) : "--");
        Append(builder, "Package:", report.CodexDetected ? report.Package : "--");
        Append(builder, "Codex version:", report.CodexDetected ? report.CodexVersion : "--");
        Append(
            builder,
            "Codex HWND:",
            report.CodexDetected && report.CodexWindowHandle != 0
                ? report.CodexWindowHandle.ToString("X", CultureInfo.InvariantCulture)
                : "--");
        Append(builder, "Attach attempt:", report.AttachAttempt.ToString(CultureInfo.InvariantCulture));
        Append(builder, "IPC:", report.IpcConnected ? "connected" : "waiting");
        Append(builder, "Session:", report.SessionReady ? "ready" : "waiting");
        Append(builder, "UIA:", report.UiaReady ? "ready" : "waiting");
        Append(builder, "Overlay:", report.OverlayVisible ? "visible" : "hidden");
        Append(builder, "Detection rule:", string.IsNullOrWhiteSpace(report.DetectionRule) ? "--" : report.DetectionRule);
        // A heartbeat: a watcher thread that has stopped detecting shows up here as a growing age
        // instead of as a strip that quietly never comes back.
        Append(
            builder,
            "Detections:",
            string.Create(
                CultureInfo.InvariantCulture,
                $"{report.DetectCount} (last {report.MillisecondsSinceDetect:0} ms ago)"));
        Append(
            builder,
            "Watcher error:",
            string.IsNullOrWhiteSpace(report.WatcherError)
                ? "none"
                : $"{report.WatcherError} ({report.ErrorCount} total)");
        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Everything the <c>[position]</c> block needs about a composer-docked placement. Computed by the
    /// caller because only it knows the layout ladder's resolved numbers; kept as a plain record so this
    /// type stays free of UI Automation.
    /// </summary>
    internal readonly record struct DockedPositionReport(
        ComposerDockSnapshot Dock,
        int ReferenceLeft,
        int RowCenterY,
        int StripLeftLimit,
        int ResponsiveLevel,
        double GapDip,
        IReadOnlyList<double> VariantWidthsDip);

    /// <summary>The position report, e.g. <c>Position mode: ComposerContextLeft</c>.</summary>
    public static string BuildPosition(
        WindowAttachment attachment,
        OverlayPositionMode mode,
        IntRect? codexWindow,
        IntRect? stripWindow,
        Point? requestedTopLeft,
        bool clamped,
        uint dpi,
        string theme,
        string themeSource,
        bool transparent,
        DockedPositionReport? docked = null,
        bool textShadow = false)
    {
        var builder = new StringBuilder();
        Append(builder, "Position mode:", OverlaySettings.PositionModeName(mode));
        if (mode == OverlayPositionMode.FixedScreen)
        {
            Append(builder, "Anchor:", "(none — fixed on screen)");
            Append(builder, "Offset:", "(none)");
        }
        else if (mode == OverlayPositionMode.FollowCodex)
        {
            Append(builder, "Anchor:", attachment.ReferencePoint.ToString());
            Append(
                builder,
                "Offset:",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"({attachment.OffsetXDip:0.###}, {attachment.OffsetYDip:0.###}) DIP"));
        }
        else
        {
            Append(builder, "Anchor:", "(none — docked to the Context control)");
            Append(builder, "Offset:", "(none — no absolute coordinate is stored)");
        }

        if (docked is { } report)
        {
            AppendDocked(builder, report, stripWindow, dpi);
        }

        Append(builder, "Monitor DPI:", dpi.ToString(CultureInfo.InvariantCulture));
        Append(builder, "Theme:", $"{theme} (from {themeSource})");
        Append(builder, "Background:", transparent ? "transparent (text only)" : "capsule fill");
        Append(builder, "Text shadow:", textShadow ? "on" : "off");
        Append(builder, "Codex rect:", Describe(codexWindow));
        Append(builder, "Requested strip rect:", Describe(requestedTopLeft));
        Append(builder, "Actual strip rect:", Describe(stripWindow));
        Append(builder, "Clamped:", clamped ? "yes (display only — saved offset untouched)" : "no");
        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// The dock report. Deliberately prints the reference element's identity *and* the two geometry
    /// errors, so that after a Codex update one glance at the log answers "did the selector break, or
    /// did the layout move?" — those two failures need completely different fixes.
    /// </summary>
    private static void AppendDocked(
        StringBuilder builder,
        DockedPositionReport report,
        IntRect? stripWindow,
        uint dpi)
    {
        var dock = report.Dock;
        Append(builder, "Reference source:", dock.DescribeSource());
        Append(builder, "Reference element:", string.Empty);
        Append(builder, "   Name:", string.IsNullOrEmpty(dock.ElementName) ? "(none)" : dock.ElementName);
        Append(builder, "   AutomationId:", string.IsNullOrEmpty(dock.AutomationId) ? "(none)" : dock.AutomationId);
        Append(builder, "   ControlType:", string.IsNullOrEmpty(dock.ControlType) ? "(none)" : dock.ControlType);
        Append(
            builder,
            "   ClassName:",
            string.IsNullOrEmpty(dock.ClassName)
                ? "(none)"
                : dock.ClassName.Length > 96 ? dock.ClassName[..96] + "…" : dock.ClassName);
        Append(builder, "Reference rect:", Describe(dock.ReferenceRect));
        Append(builder, "Row rect:", Describe(dock.RowRect));
        Append(builder, "Composer rect:", Describe(dock.ComposerRect));
        Append(builder, "Left cluster rect:", Describe(dock.LeftClusterRect));
        Append(builder, "Reference left edge px:", report.ReferenceLeft.ToString(CultureInfo.InvariantCulture));
        Append(builder, "Row centre Y px:", report.RowCenterY.ToString(CultureInfo.InvariantCulture));
        Append(
            builder,
            "Gap:",
            string.Create(
                CultureInfo.InvariantCulture,
                $"{report.GapDip:0.###} DIP = {ManualAttachmentRules.DipToPixels(report.GapDip, dpi)} px (target 8–12 DIP)"));
        Append(
            builder,
            "Responsive level:",
            report.ResponsiveLevel switch
            {
                0 => "0 (full strip)",
                1 => "1 (short units)",
                2 => "2 (short units, no icon)",
                3 => "3 (totals only)",
                _ => "hidden"
            });
        if (report.VariantWidthsDip.Count > 0)
        {
            Append(
                builder,
                "Variant widths DIP:",
                string.Join(
                    " / ",
                    report.VariantWidthsDip.Select(value => value.ToString("0.0", CultureInfo.InvariantCulture)))
                + "  (full -> narrowest, at 100% scale)");
        }

        if (stripWindow is not { } strip || strip.IsEmpty)
        {
            Append(builder, "Right-edge error px:", "-- (strip hidden)");
            Append(builder, "Vertical centre error px:", "-- (strip hidden)");
            var budgetPixels = report.ReferenceLeft
                - ManualAttachmentRules.DipToPixels(report.GapDip, dpi)
                - report.StripLeftLimit;
            Append(
                builder,
                "Width budget DIP:",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{ManualAttachmentRules.PixelsToDip(budgetPixels, dpi):0.0} (nothing in the ladder fits)"));
            return;
        }

        Append(
            builder,
            "Width budget DIP:",
            string.Create(
                CultureInfo.InvariantCulture,
                $"{ManualAttachmentRules.PixelsToDip(report.ReferenceLeft - ManualAttachmentRules.DipToPixels(report.GapDip, dpi) - report.StripLeftLimit, dpi):0.0}"));

        // Measured against the strip's own rectangle rather than its text, because that rectangle is
        // what the click-through region and the input area are built from.
        var gapPixels = report.ReferenceLeft - strip.Right;
        Append(
            builder,
            "Right-edge error px:",
            string.Create(
                CultureInfo.InvariantCulture,
                $"{gapPixels} (reference.left - strip.right; excess over the gap = {gapPixels - ManualAttachmentRules.DipToPixels(report.GapDip, dpi)})"));
        var centreError = Math.Abs(report.RowCenterY - (strip.Y + (strip.Height / 2)));
        Append(
            builder,
            "Vertical centre error px:",
            string.Create(
                CultureInfo.InvariantCulture,
                $"{centreError} (|row centre - strip centre|; target <= 2)"));
        Append(
            builder,
            "Left limit px:",
            report.StripLeftLimit.ToString(CultureInfo.InvariantCulture));
    }

    private static string Describe(IntRect? rect) =>
        rect is { } value && !value.IsEmpty
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"({value.X}, {value.Y}) {value.Width}x{value.Height}")
            : "--";

    private static string Describe(Point? point) =>
        point is { } value
            ? string.Create(CultureInfo.InvariantCulture, $"top-left ({value.X}, {value.Y})")
            : "--";

    public static string Build(TokenSnapshot? snapshot)
    {
        var builder = new StringBuilder();
        if (snapshot is null)
        {
            foreach (var (label, value) in EmptyBlock())
            {
                Append(builder, label, value);
            }

            return builder.ToString().TrimEnd();
        }

        Append(builder, "Codex Desktop PID:", snapshot.CodexProcessId.ToString(CultureInfo.InvariantCulture));
        Append(builder, "Codex version:", snapshot.CodexVersion);
        Append(builder, "Current thread:", string.IsNullOrWhiteSpace(snapshot.ThreadId) ? "--" : snapshot.ThreadId);
        Append(builder, "Title:", string.IsNullOrWhiteSpace(snapshot.Title) ? "--" : snapshot.Title);
        Append(builder, "Active thread source:", snapshot.Source);
        Append(builder, "Turn:", string.IsNullOrWhiteSpace(snapshot.TurnId) ? "--" : snapshot.TurnId!);
        Append(builder, "Streaming:", snapshot.Streaming ? "true" : "false");
        Append(builder, "TPS state:", snapshot.TpsState);
        Append(builder, "Input:", snapshot.InputTokens.ToString(CultureInfo.InvariantCulture));
        Append(builder, "Cached:", snapshot.CachedInputTokens.ToString(CultureInfo.InvariantCulture));
        Append(builder, "Output:", snapshot.OutputTokens.ToString(CultureInfo.InvariantCulture));
        Append(
            builder,
            "Total:",
            (snapshot.InputTokens + snapshot.OutputTokens).ToString(CultureInfo.InvariantCulture));
        Append(builder, "Cache hit:", MetricFormat.FormatCachePercent(snapshot.InputTokens, snapshot.CachedInputTokens));
        Append(
            builder,
            "Merged model elapsed ms:",
            snapshot.MergedModelElapsedMs.ToString(CultureInfo.InvariantCulture));
        Append(builder, "Turn output tokens:", snapshot.TurnOutputTokens.ToString(CultureInfo.InvariantCulture));
        Append(
            builder,
            "Model intervals:",
            snapshot.ModelIntervals is { Count: > 0 } intervals
                ? string.Join(' ', intervals)
                : "none");
        Append(builder, "First item start:", FormatTimestamp(snapshot.FirstItemStartMs));
        Append(builder, "Last item end:", FormatTimestamp(snapshot.LastItemEndMs));
        Append(builder, "Current realtime TPS:", MetricFormat.FormatTpsFloat(snapshot.Tps));
        Append(builder, "Last completed TPS:", MetricFormat.FormatTpsFloat(snapshot.LastTurnTps));
        Append(
            builder,
            "TTFT ms:",
            snapshot.TtftMs is null ? "--" : snapshot.TtftMs.Value.ToString("0.##", CultureInfo.InvariantCulture));
        Append(builder, "Carrier:", snapshot.Carrier);
        Append(
            builder,
            "Dedup:",
            snapshot.DedupIgnored > 0
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"{snapshot.DedupIgnored} usage record(s) ignored as duplicate")
                : "none");
        Append(builder, "Rollout path:", string.IsNullOrWhiteSpace(snapshot.LogPath) ? "--" : snapshot.LogPath);
        Append(builder, "Rollout offset:", snapshot.RolloutOffset.ToString(CultureInfo.InvariantCulture));
        Append(builder, "Baseline source:", snapshot.BaselineSource);
        Append(
            builder,
            "Warnings:",
            snapshot.Warnings is { Count: > 0 } warnings ? string.Join(", ", warnings) : "none");
        return builder.ToString().TrimEnd();
    }

    /// <summary>The same field list with placeholder values, used before any conversation is known.</summary>
    private static IEnumerable<(string Label, string Value)> EmptyBlock()
    {
        yield return ("Codex Desktop PID:", "0");
        yield return ("Codex version:", "unknown");
        yield return ("Current thread:", "--");
        yield return ("Title:", "--");
        yield return ("Active thread source:", "none");
        yield return ("Turn:", "--");
        yield return ("Streaming:", "false");
        yield return ("TPS state:", "idle");
        yield return ("Input:", "0");
        yield return ("Cached:", "0");
        yield return ("Output:", "0");
        yield return ("Total:", "0");
        yield return ("Cache hit:", "--");
        yield return ("Merged model elapsed ms:", "0");
        yield return ("Turn output tokens:", "0");
        yield return ("Model intervals:", "none");
        yield return ("First item start:", "--");
        yield return ("Last item end:", "--");
        yield return ("Current realtime TPS:", "--");
        yield return ("Last completed TPS:", "--");
        yield return ("TTFT ms:", "--");
        yield return ("Carrier:", "none");
        yield return ("Dedup:", "none");
        yield return ("Rollout path:", "--");
        yield return ("Rollout offset:", "0");
        yield return ("Baseline source:", "none");
        yield return ("Warnings:", "none");
    }

    private static void Append(StringBuilder builder, string label, string value) =>
        builder.Append(label.PadRight(LabelWidth)).Append(value).Append('\n');

    /// <summary>Unix milliseconds to an ISO-8601 UTC timestamp, or "--" when unknown.</summary>
    public static string FormatTimestamp(long? unixMilliseconds)
    {
        if (unixMilliseconds is null or <= 0)
        {
            return "--";
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds.Value)
                .UtcDateTime
                .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return "--";
        }
    }

    private static bool HasConsole()
    {
        try
        {
            return !Console.IsErrorRedirected || Environment.UserInteractive;
        }
        catch (IOException)
        {
            return false;
        }
    }
}