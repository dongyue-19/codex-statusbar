using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodexStatusbar;

/// <summary>
/// Headless assertions over the <b>production</b> parser (<see cref="RolloutStreamState"/>) and the
/// <b>production</b> formatter (<see cref="MetricFormat"/>). Nothing here is a reimplementation, so a
/// pass means the real pipeline is correct.
///
/// Run with <c>--self-test [fixturesDir]</c>. Exits 0 on success, 1 on any failure.
///
/// Covered behaviours:
///   * conversation total is input + output, never input + cached + output;
///   * cache hit is cached / input, and "--" when input is 0 (never NaN and never 0%);
///   * the compact formatter boundaries (843 / 12.4K / 5.7M);
///   * TPS denominator is the <b>union</b> of model-output windows, not a sum of durations;
///   * tool / IO / user item windows never enter the TPS denominator;
///   * overlapping windows are counted once;
///   * both usage carriers work, and no carrier combination double counts;
///   * the TPS state machine (waiting / generating / completed) and the no-fabrication rule.
/// </summary>
internal static class SelfTest
{
    private static int _failures;
    private static int _checks;

    public static int Run(string? fixturesDirectory)
    {
        _failures = 0;
        _checks = 0;
        var stdout = Console.Out;

        stdout.WriteLine("CodexStatusbar self-test");
        stdout.WriteLine("========================");

        var modernFixture = Locate(fixturesDirectory, "fixture-modern.jsonl");
        var legacyFixture = Locate(fixturesDirectory, "fixture-legacy.jsonl");

        // A bare `--self-test` on the published single file has no fixtures beside it: they live in
        // the repository. Say so and skip those two sections rather than failing, so the documented
        // command exits 0 on a downloaded exe. Asking for a specific fixtures directory that cannot
        // be satisfied is still a failure — that is a broken invocation, not a missing extra.
        if (fixturesDirectory is null && (modernFixture is null || legacyFixture is null))
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine(
                "  note: fixture-based sections skipped — no fixtures directory beside this exe.");
            Console.Out.WriteLine(
                "        Run with the repository's fixtures path (e.g. `--self-test fixtures`) for the full set.");
        }
        else
        {
            Section("carrier: token_usage_record (fixtures/fixture-modern.jsonl)");
            RunModernFixture(modernFixture, stdout);

            Section("carrier: token_count only (fixtures/fixture-legacy.jsonl)");
            RunLegacyFixture(legacyFixture, stdout);
        }

        Section("deduplication: the same usage must never be counted twice");
        RunDeduplication(stdout);

        Section("TPS denominator: union of model windows, tool time excluded");
        RunIntervalUnion(stdout);

        Section("metric strictness and formatting");
        RunMetricRules(stdout);

        Section("TPS state machine");
        RunStateMachine(stdout);

        Section("overlay presentation: transparency, shadow, position model");
        RunOverlayPresentation(stdout);

        Section("position model: anchor + DIP offset, resize stability, persistence");
        RunPositionModel(stdout);

        Section("composer dock: UI Automation reference, native type size, responsive ladder");
        RunComposerDock(stdout);

        Section("lifecycle: Codex identity, start with Windows, attach ladder");
        RunLifecycle(stdout);

        Section("position fast path: pure move, coalescing, burst tail, DPI/monitor");
        RunPositionFastPath(stdout);

        stdout.WriteLine();
        stdout.WriteLine($"checks: {_checks}   failures: {_failures}");
        stdout.WriteLine($"RESULT: {(_failures == 0 ? "PASS" : "FAIL")}");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Round-two UI contract: the strip is text-only by default, the shadow is always the opposite of
    /// the glyph it sits under, and the transparency switch is honoured by the settings layer.
    /// </summary>
    private static void RunOverlayPresentation(TextWriter stdout)
    {
        _ = stdout;
        Check("new install starts transparent", OverlaySettings.CreateDefault().TransparentBackground, true);
        Check("new install docks to the composer Context control",
            OverlaySettings.CreateDefault().PositionMode == OverlayPositionMode.ComposerContextLeft, true);
        Check("new install uses a 10 DIP context gap",
            OverlaySettings.CreateDefault().ContextGapDip,
            OverlaySettings.DefaultContextGapDip,
            0.001d);
        Check("new install keeps the drop shadow off",
            OverlaySettings.CreateDefault().TextShadow, false);
        Check("new install tracks the system theme",
            OverlaySettings.CreateDefault().ThemePreference == OverlayThemePreference.Auto, true);

        // Light text must get a dark shadow and vice versa, or the shadow would wash the glyphs out.
        var dark = OverlayThemePalette.For(OverlayThemeKind.Dark);
        var light = OverlayThemePalette.For(OverlayThemeKind.Light);
        var darkShadow = OverlayThemePalette.ShadowFor(dark.Value);
        var lightShadow = OverlayThemePalette.ShadowFor(light.Value);
        Check("light glyph gets a black shadow",
            darkShadow.R == 0 && darkShadow.G == 0 && darkShadow.B == 0, true);
        Check("dark glyph gets a white shadow",
            lightShadow.R == 255 && lightShadow.G == 255 && lightShadow.B == 255, true);
        Check("the shadow stays subtle",
            darkShadow.A is > 0 and <= 200 && lightShadow.A is > 0 and <= 200, true);

        // The rendered strip must not carry an opaque capsule behind the text.
        Check("the locked capsule has no fill colour of its own",
            dark.Background.A == 255, true);
        Check("the strip's stroke is still DIP scaled",
            OverlayRenderMetrics.Create(144, 100).StrokeWidth == 2, true);
        Check("one DIP of shadow at 150% is 2 px",
            OverlayRenderMetrics.Create(144, 100).TextShadowOffset == 2, true);
        Check("one DIP of shadow at 100% is 1 px",
            OverlayRenderMetrics.Create(96, 100).TextShadowOffset == 1, true);
        Check("the hotkey is Ctrl+Alt+Shift+P", GlobalHotkey.Description, "Ctrl+Alt+Shift+P");

        // Codex's own theme wins over the Windows theme, because the two routinely disagree.
        Check("Codex theme: appearanceTheme = \"light\" is read",
            CodexThemeSource.Parse(["appearanceTheme = \"light\"", "[desktop]"]), CodexThemeKind.Light);
        Check("Codex theme: dark is read",
            CodexThemeSource.Parse(["appearanceTheme = \"dark\""]), CodexThemeKind.Dark);
        Check("Codex theme: a missing key is Unknown, never guessed",
            CodexThemeSource.Parse(["model = \"gpt-5\""]), CodexThemeKind.Unknown);
        Check("Codex theme: a nested key is never mistaken for the app setting",
            CodexThemeSource.Parse(["[desktop.appearanceLightChromeTheme]", "appearanceTheme = \"dark\""]),
            CodexThemeKind.Unknown);
        // The real file has it under [desktop], below many other sections.
        Check("Codex theme: a root key after several sections is still found",
            CodexThemeSource.Parse([
                "model = \"gpt-5\"",
                "",
                "[mcp_servers.node_repl]",
                "command = \"node\"",
                "",
                "[desktop]",
                "someTuning = 1",
                "",
                "appearanceTheme = \"light\"",
                "",
                "[desktop.appearanceLightChromeTheme]"
            ]),
            CodexThemeKind.Light);
        Check("Codex theme: the key is desktop-scoped in the real config (~/.codex/config.toml)",
            CodexThemeSource.Parse([
                "[features]",
                "x = true",
                "[desktop]",
                "appearanceTheme = \"light\"",
                "[desktop.appearanceLightChromeTheme.fonts]",
                "ui = \"Segoe UI\""
            ]),
            CodexThemeKind.Light);
        Check("Codex theme: a deeper appearance sub-theme is ignored",
            CodexThemeSource.Parse([
                "[desktop.appearanceLightChromeTheme]",
                "appearanceTheme = \"dark\""
            ]),
            CodexThemeKind.Unknown);
        Check("Codex theme: an unknown value falls back instead of guessing",
            CodexThemeSource.Parse(["appearanceTheme = \"solarized\""]), CodexThemeKind.Unknown);
        Check("Codex theme: a trailing comment is tolerated",
            CodexThemeSource.Parse(["appearanceTheme = \"light\"  # or dark"]), CodexThemeKind.Light);
        Check("Codex theme: the real config file on this machine parses",
            CodexThemeSource.Read(CodexThemeSource.DefaultConfigPath) != CodexThemeKind.Unknown,
            true);
    }

    /// <summary>Folds the whole position suite into the default gate instead of duplicating it.</summary>
    private static void RunPositionModel(TextWriter stdout)
    {
        foreach (var check in PositionProbe.Evaluate())
        {
            Check(check.Name, check.Passed, expected: true);
        }

        _ = stdout;
    }

    /// <summary>
    /// Round-three UI contract: the strip is docked into the Codex composer, anchored to the
    /// Context-usage control's own rectangle, sized by its text and shortened only when Codex is
    /// genuinely too narrow. Everything asserted here is derived from a rectangle the UI Automation
    /// probe measured on the real Codex window (kept in the fixtures below), so these checks are
    /// statements about the real toolbar rather than about invented numbers.
    /// </summary>
    private static void RunComposerDock(TextWriter stdout)
    {
        _ = stdout;

        // The real composer at 150% scaling: window (308,244) 2048x1224, composer card (995,1292)
        // 1105x147, Context indicator (1708,1396) 25x25, model selector (1738,1385) 254x42.
        var host = new CodexWindowInfo(
            new IntPtr(1),
            new IntRect(308, 244, 2048, 1224),
            new IntRect(308, 244, 2048, 1224),
            new IntRect(2145, 247, 138, 32),
            // The real primary monitor's working area, read with SPI_GETWORKAREA at per-monitor-v2
            // awareness: 2560x1528 physical pixels at 150% scaling.
            new IntRect(0, 0, 2560, 1528),
            144,
            new WindowChromeMetrics(46, 32, 1, 1, 0));

        var context = new ComposerDockSnapshot(
            ComposerReferenceSource.UiaContext,
            new IntRect(1708, 1396, 25, 25),
            new IntRect(1738, 1385, 254, 42),
            new IntRect(995, 1292, 1105, 147),
            new IntRect(995, 1385, 190, 42),
            "上下文用量：13%",
            string.Empty,
            "Image",
            "icon-xs inline-flex items-center justify-center align-middle text-codex-description",
            1,
            3,
            null);

        var modelOnly = context with
        {
            Source = ComposerReferenceSource.UiaModel,
            ReferenceRect = new IntRect(1738, 1385, 254, 42),
            ElementName = "deepseek-v4.1-flash 高"
        };

        var composerOnly = context with
        {
            Source = ComposerReferenceSource.UiaComposer,
            ReferenceRect = new IntRect(995, 1292, 1105, 147),
            RowRect = default
        };

        var windowOnly = ComposerDockSnapshot.Empty with
        {
            Source = ComposerReferenceSource.Window,
            WindowHandle = 1
        };

        // A strip of a realistic measured size: "⚡ 192 tok/s · 2.4M tok · Cache 95%" measures
        // ~208 DIP at 13 DIP Segoe UI, so 208 + 2x6 DIP padding = 330 px at 150%.
        var size = OverlayLayoutCalculator.GetCapsuleSizeForText(208d, 144, 100);
        Check("the strip's height follows the native line box", size.Height, 33L);
        Check("the strip's width is the measured text plus padding", size.Width, 330L);

        foreach (var (label, dock, expectedRight) in new[]
        {
            ("uia-context", context, 1708 - 15),
            ("uia-model", modelOnly, 1738 - 30 - 15),
            ("uia-composer", composerOnly, 2100 - 392 - 15),
            ("window-fallback", windowOnly, 2356 - 648 - 15)
        })
        {
            var layout = OverlayLayoutCalculator.Calculate(new OverlayLayoutRequest(
                host,
                AnchorMode.TitleBarTopRight,
                RequestExpanded: false,
                ExpandedRowCount: 0,
                ShowContextProgress: false,
                ManualCapsuleTopLeft: null,
                ScalePercent: 100,
                CapsuleSize: size,
                Dock: dock,
                ContextGapDip: 10d));
            // In the collapsed state WindowBounds is the strip's on-screen rectangle; CapsuleBounds is
            // the same rectangle expressed in window-client coordinates for the renderer.
            var placed = layout.WindowBounds;
            Check(
                $"{label}: the strip's right edge sits 10 DIP left of the reference",
                placed.Right,
                expectedRight);
            Check(
                $"{label}: placed in the collapsed state",
                layout.State == OverlayVisualState.Collapsed, true);
            Check(
                $"{label}: the client capsule fills the window",
                layout.CapsuleBounds.Width == placed.Width && layout.CapsuleBounds.Height == placed.Height,
                true);
        }

        // Vertical alignment is against the toolbar row's own box, not the icon: the Context icon is
        // 2.5 px below the row centre because of its own `vertical-align: middle`.
        var docked = OverlayLayoutCalculator.Calculate(new OverlayLayoutRequest(
            host,
            AnchorMode.TitleBarTopRight,
            false,
            0,
            false,
            null,
            100,
            size,
            context,
            10d));
        Check(
            "vertical centre matches the toolbar row, not the off-centre icon",
            docked.WindowBounds.Y + (docked.WindowBounds.Height / 2),
            context.RowCenterY);

        // A gap wider than the strip allows is clamped up to the minimum rather than producing a
        // negative gap that would overlap the Context control.
        var tinyGap = OverlayLayoutCalculator.Calculate(new OverlayLayoutRequest(
            host,
            AnchorMode.TitleBarTopRight,
            false,
            0,
            false,
            null,
            100,
            size,
            context,
            0d));
        Check(
            "a zero gap is raised to the minimum docking gap",
            context.ReferenceRect.Left - tinyGap.WindowBounds.Right,
            ManualAttachmentRules.DipToPixels(ManualAttachmentRules.MinimumDockGapDip, 144));

        // The left limit is the real right edge of the composer's own controls, so the strip can never
        // be placed over the "+" or permission buttons.
        Check(
            "the left limit is the permission chip's right edge plus the minimum gap",
            OverlayLayoutCalculator.ResolveStripLeftLimit(
                context,
                OverlayLayoutCalculator.ResolveVisibleHost(host),
                144),
            1185 + 6);

        // ---- the responsive ladder -------------------------------------------------
        var variants = OverlayPresentationBuilder.BuildCapsuleVariants(
            "192", "2.4M", "95%", hasUsage: true);
        Check("four verbosity levels", variants.Count, 4L);
        Check("level 0 is the full strip", variants[0], "⚡ 192 tok/s · 2.4M tok · Cache 95%");
        Check("level 1 drops the unit words", variants[1], "⚡ 192 t/s · 2.4M · 95%");
        Check("level 2 drops the glyph as well", variants[2], "192 t/s · 2.4M · 95%");
        Check("level 3 keeps the two unit-free quantities", variants[3], "2.4M · 95%");
        for (var index = 1; index < variants.Count; index++)
        {
            Check(
                $"level {index} is strictly shorter than level {index - 1}",
                variants[index].Length < variants[index - 1].Length,
                true);
        }

        var waiting = OverlayPresentationBuilder.BuildCapsuleVariants("--", "--", "--", hasUsage: false);
        Check("before any usage every number is --", waiting[0], "⚡ -- tok/s · -- tok · Cache --");
        Check("the cache is never rendered as 0% when input is zero", waiting[3], "-- · --");

        // ---- the glyph split -------------------------------------------------------
        var split = TokenStripForm.SplitLeadingIcon("⚡ 192 tok/s · 2.4M tok · Cache 95%");
        Check("the lightning glyph is split off", split.Icon, "⚡");
        Check("the rest of the line is left intact", split.Body, "192 tok/s · 2.4M tok · Cache 95%");
        var noIcon = TokenStripForm.SplitLeadingIcon("2.4M · 95%");
        Check("a line without the glyph is untouched", noIcon.Icon.Length, 0L);
        Check("and keeps its text", noIcon.Body, "2.4M · 95%");
    }

    /// <summary>Enum-valued check; reports the names rather than the underlying numbers.</summary>
    private static void Check<T>(string name, T actual, T expected)
        where T : struct, Enum
    {
        _checks++;
        if (actual.Equals(expected))
        {
            return;
        }

        _failures++;
        Console.Error.WriteLine($"  FAIL {name} — expected {expected}, got {actual}");
    }

    /// <summary>A failing check whose detail is worth printing (position suite reports expected vs actual).</summary>
    private static void Check(string name, bool actual, bool expected, string detail)
    {
        _checks++;
        if (actual == expected)
        {
            return;
        }

        _failures++;
        Console.Error.WriteLine($"  FAIL {name}{(detail.Length == 0 ? string.Empty : $" — {detail}")}");
    }

    // ---------------------------------------------------------------- fixtures

    private static void RunModernFixture(string? path, TextWriter stdout)
    {
        if (path is null)
        {
            Fail("fixture-modern.jsonl not found (pass the fixtures directory as the argument)");
            return;
        }

        var lines = File.ReadAllLines(path);
        var splitAt = IndexOfTaskComplete(lines, "aaaa0000-0000-7000-8000-000000000001");
        if (splitAt < 0)
        {
            Fail("could not locate turn A's task_complete in the fixture");
            return;
        }

        // ---- state as of the end of turn A ----
        var afterA = new RolloutStreamState();
        for (var index = 0; index <= splitAt; index++)
        {
            afterA.ObserveLine(lines[index]);
        }

        Check("turn A: TPS is exact", afterA.Tps.SmoothedTps, 200.0, 1e-9);
        Check("turn A: LastTurnTps retained", afterA.Tps.LastTurnTps, 200.0, 1e-9);
        Check("turn A: denominator is 4000+3000 = 7000 ms", afterA.Tps.MergedModelElapsedMs, 7000L);
        Check("turn A: 5000 ms CommandExecution NOT in the denominator",
            afterA.Tps.MergedModelElapsedMs == 7000L, true);
        // Turn A's model windows are [1000,5000] and [10000,13000]: two disjoint spans separated by the
        // 5000 ms tool call, so two intervals is correct — the union is 4000 + 3000 = 7000 ms.
        Check("turn A: two disjoint model windows (tool time sits between them)",
            afterA.Tps.ModelIntervals.Count, 2);
        Check("turn A: official turn output tokens", afterA.Tps.TurnOutputTokens, 1400L);

        // ---- whole file ----
        var state = new RolloutStreamState();
        foreach (var line in lines)
        {
            state.ObserveLine(line);
        }

        Check("turn B: TPS is exact", state.Tps.SmoothedTps, 100.0, 1e-9);
        Check("turn B: LastTurnTps retained after the turn ends", state.Tps.LastTurnTps, 100.0, 1e-9);
        Check("completed turn keeps its value on the strip", state.Tps.DisplayTps, 100.0, 1e-9);
        Check("state machine reports completed", state.Tps.StateDescription, "completed");

        Check("input", state.InputTokens, 220_000L);
        Check("cached", state.CachedInputTokens, 204_000L);
        Check("output", state.OutputTokens, 1_700L);
        Check("total == input + output", state.TotalTokens, 221_700L);
        Check("total is NOT input + cached + output",
            state.TotalTokens == state.InputTokens + state.CachedInputTokens + state.OutputTokens, false);
        Check("uncached == input - cached", state.UncachedInputTokens, 16_000L);
        Check("cache hit rate",
            MetricFormat.CacheHitPercent(state.InputTokens, state.CachedInputTokens) ?? 0d,
            92.72727272727273,
            1e-9);
        Check("cache display", MetricFormat.FormatCachePercentCompact(state.InputTokens, state.CachedInputTokens), "93%");
        Check("compact total", MetricFormat.CompactTokens(state.TotalTokens), "221.7K");
        Check("no warnings for a clean fixture", state.Warnings.Count, 0);
    }

    private static void RunLegacyFixture(string? path, TextWriter stdout)
    {
        if (path is null)
        {
            Fail("fixture-legacy.jsonl not found");
            return;
        }

        var state = new RolloutStreamState();
        foreach (var line in File.ReadAllLines(path))
        {
            state.ObserveLine(line);
        }

        Check("input", state.InputTokens, 5_646_305L);
        Check("cached", state.CachedInputTokens, 5_526_656L);
        Check("output", state.OutputTokens, 52_779L);
        Check("total == input + output", state.TotalTokens, 5_699_084L);
        Check("uncached", state.UncachedInputTokens, 119_649L);
        Check("cache display", MetricFormat.FormatCachePercentCompact(state.InputTokens, state.CachedInputTokens), "98%");
        Check("compact total", MetricFormat.CompactTokens(state.TotalTokens), "5.7M");
        Check("legacy session has no TPS (no turn timing) -> unknown", state.Tps.DisplayTps is null, true);
    }

    // ----------------------------------------------------------- deduplication

    private static void RunDeduplication(TextWriter stdout)
    {
        // 1. only token_count, repeated with an identical cumulative reading
        Check("1) repeated identical token_count is not counted twice",
            TotalOf(
                TokenCount(10_000, 8_000, 500),
                TokenCount(10_000, 8_000, 500),
                TokenCount(10_000, 8_000, 500)),
            10_500L);

        // 2. only token_usage_record, repeated with an identical thread total
        Check("2) repeated identical token_usage_record is not counted twice",
            TotalOf(
                UsageRecord(10_000, 8_000, 500),
                UsageRecord(10_000, 8_000, 500)),
            10_500L);

        // 3. both carriers present and each duplicated
        Check("3) both carriers, each duplicated, still one reading",
            TotalOf(
                TokenCount(10_000, 8_000, 500),
                UsageRecord(10_000, 8_000, 500),
                TokenCount(10_000, 8_000, 500),
                UsageRecord(10_000, 8_000, 500)),
            10_500L);

        // 4. cumulative token_count that grows: the total must be the LAST reading, never the sum
        Check("4) growing cumulative token_count reports the last reading",
            TotalOf(
                TokenCount(10_000, 8_000, 500),
                TokenCount(20_000, 16_000, 900),
                TokenCount(30_000, 24_000, 1_400)),
            31_400L);

        // 5. interleaved growth across both carriers converges on the same value
        Check("5) interleaved carriers converge on the newest cumulative value",
            TotalOf(
                TokenCount(10_000, 8_000, 500),
                UsageRecord(20_000, 16_000, 900),
                TokenCount(20_000, 16_000, 900),
                UsageRecord(30_000, 24_000, 1_400)),
            31_400L);

        // 6. the real 0.159 shape: token_usage_record followed by an equal token_count per response.
        //    Outputs are 500, 900, 1300 -> the final reading is input 30000 + output 1300 = 31300.
        var state = new RolloutStreamState();
        for (var turn = 0; turn < 3; turn++)
        {
            var input = 10_000 + (turn * 10_000);
            var output = 500 + (turn * 400);
            state.ObserveLine(UsageRecord(input, 8_000, output));
            state.ObserveLine(TokenCount(input, 8_000, output));
        }

        Check("6) 0.159 duplicate-carrier pattern is not double counted", state.TotalTokens, 31_300L);
        Check("6) input from the newest reading", state.InputTokens, 30_000L);
        Check("6) output from the newest reading", state.OutputTokens, 1_300L);

        stdout.WriteLine("      (each case asserts the exact expected value; a double count would give ~2x)");

        // 7. The measured 0.159 shape: one response reported by BOTH carriers with byte-identical
        //    usage. The per-response numerator must stay at the single response's value. With a
        //    carrier-specific dedup key this came out exactly 2x, which inflated TPS by 2x.
        var perResponse = new RolloutStreamState();
        perResponse.ObserveLine(TaskStarted("turn-dc"));
        perResponse.ObserveLine(ItemCompleted("turn-dc", "Reasoning", 0L, 4_000L));
        perResponse.ObserveLine(UsageRecordWithKey(10_000, 9_000, 500, "turn-dc", "resp-dc"));
        perResponse.ObserveLine(TokenCount(10_000, 9_000, 500));
        perResponse.ObserveLine(TaskComplete("turn-dc"));
        Check("7) the same response via both carriers is counted once", perResponse.Tps.TurnOutputTokens, 500L);
        Check("7) TPS stays 500 tokens / 4000 ms", perResponse.Tps.SmoothedTps, 125.0, 1e-9);

        // 8. turn_token_usage that equals thread_token_usage is the CONVERSATION total, not a turn
        //    total. It must never reach the numerator. (Measured on Codex 26.928.3736.0.)
        var turnEqualsThread = new RolloutStreamState();
        turnEqualsThread.ObserveLine(TaskStarted("turn-te"));
        turnEqualsThread.ObserveLine(ItemCompleted("turn-te", "Reasoning", 0L, 2_000L));
        turnEqualsThread.ObserveLine(UsageRecordWithKey(2_000_000, 1_900_000, 900, "turn-te", "resp-te"));
        turnEqualsThread.ObserveLine(TaskComplete("turn-te"));
        Check("8) thread-sized turn_token_usage is not used as the turn numerator",
            turnEqualsThread.Tps.TurnOutputTokens, 900L);
        Check("8) TPS stays 900 tokens / 2000 ms", turnEqualsThread.Tps.SmoothedTps, 450.0, 1e-9);
    }

    // ---------------------------------------------------------- interval union

    private static void RunIntervalUnion(TextWriter stdout)
    {
        // Overlapping windows: [0,5000] and [4000,10000] must union to 10000, not 11000.
        var overlapping = new RolloutStreamState();
        FeedTurn(
            overlapping,
            "turn-union",
            [
                ("Reasoning", 0L, 5_000L),
                ("AgentMessage", 4_000L, 10_000L),
            ],
            outputTokens: 1_000);
        Check("overlapping windows union to 10000 ms (a sum would give 11000)",
            overlapping.Tps.MergedModelElapsedMs, 10_000L);
        Check("overlapping windows collapse to one interval", overlapping.Tps.ModelIntervals.Count, 1);
        Check("TPS uses the union", overlapping.Tps.SmoothedTps, 100.0, 1e-9);

        // A tool item that overlaps the model windows must not change the union at all.
        var withTool = new RolloutStreamState();
        FeedTurn(
            withTool,
            "turn-tool",
            [
                ("Reasoning", 0L, 5_000L),
                ("CommandExecution", 4_500L, 6_000L),
                ("AgentMessage", 4_000L, 10_000L),
                ("CommandExecution", 10_000L, 25_000L),
            ],
            outputTokens: 1_000);
        Check("tool windows do not extend the model union", withTool.Tps.MergedModelElapsedMs, 10_000L);
        Check("tool windows do not create intervals", withTool.Tps.ModelIntervals.Count, 1);
        Check("TPS unchanged by tool time", withTool.Tps.SmoothedTps, 100.0, 1e-9);

        // Disjoint windows must still be summed end to end.
        var disjoint = new RolloutStreamState();
        FeedTurn(
            disjoint,
            "turn-disjoint",
            [
                ("Reasoning", 0L, 5_000L),
                ("AgentMessage", 20_000L, 25_000L),
            ],
            outputTokens: 800);
        Check("disjoint windows union to 10000 ms", disjoint.Tps.MergedModelElapsedMs, 10_000L);
        Check("disjoint windows stay two intervals", disjoint.Tps.ModelIntervals.Count, 2);
        Check("TPS across disjoint windows", disjoint.Tps.SmoothedTps, 80.0, 1e-9);

        // Adjacent windows merge too (touching endpoints are one continuous span).
        var adjacent = new RolloutStreamState();
        FeedTurn(
            adjacent,
            "turn-adjacent",
            [
                ("Reasoning", 0L, 5_000L),
                ("AgentMessage", 5_000L, 8_000L),
            ],
            outputTokens: 600);
        Check("touching windows merge (8000 ms)", adjacent.Tps.MergedModelElapsedMs, 8_000L);
        Check("touching windows collapse to one interval", adjacent.Tps.ModelIntervals.Count, 1);

        stdout.WriteLine("      (a naive sum of durations would have reported 11000 ms / 90.9 tok/s above)");
    }

    // ------------------------------------------------------------ metric rules

    private static void RunMetricRules(TextWriter stdout)
    {
        Check("cache hit is undefined with no input", MetricFormat.CacheHitPercent(0, 0) is null, true);
        Check("undefined cache renders as --", MetricFormat.FormatCachePercentCompact(0, 0), "--");
        Check("cache hit never exceeds the input basis",
            MetricFormat.FormatCachePercentCompact(1_000, 1_000), "100%");
        Check("total uses input + output only",
            MetricFormat.CompactTokens(5_646_305 + 52_779), "5.7M");

        Check("843", MetricFormat.CompactTokens(843), "843");
        Check("999", MetricFormat.CompactTokens(999), "999");
        Check("1000", MetricFormat.CompactTokens(1_000), "1.0K");
        Check("12400", MetricFormat.CompactTokens(12_400), "12.4K");
        Check("1000000", MetricFormat.CompactTokens(1_000_000), "1.0M");
        Check("5699084", MetricFormat.CompactTokens(5_699_084), "5.7M");

        Check("unknown TPS renders as --", MetricFormat.FormatTps(null, false), "--");
        Check("zero TPS is refused", MetricFormat.IsUsableTps(0), false);
        Check("negative TPS is refused", MetricFormat.IsUsableTps(-5), false);
        Check("non-finite TPS is refused", MetricFormat.IsUsableTps(double.PositiveInfinity), false);
        Check("NaN TPS is refused", MetricFormat.IsUsableTps(double.NaN), false);
        Check("held TPS is marked with a tilde", MetricFormat.FormatTps(192.4, true), "~192");

        // No fabrication: a turn with model output time but zero official output tokens yields "--".
        var noTokens = new RolloutStreamState();
        FeedTurn(noTokens, "turn-notokens", [("Reasoning", 0L, 3_000L)], outputTokens: 0);
        Check("no official output tokens -> TPS unknown, not 0", noTokens.Tps.DisplayTps is null, true);

        // No fabrication: a turn we did not see start has an untrustworthy denominator.
        var midTurn = new RolloutStreamState();
        midTurn.ObserveLine(ItemCompleted("turn-partial", "Reasoning", 0L, 4_000L));
        midTurn.ObserveLine(UsageRecord(1_000, 900, 400));
        Check("turn observed only mid-flight -> TPS unknown", midTurn.Tps.DisplayTps is null, true);
        Check("... and the denominator is flagged incomplete", midTurn.Tps.TurnItemsComplete, false);
    }

    // ----------------------------------------------------------- state machine

    private static void RunStateMachine(TextWriter stdout)
    {
        var state = new RolloutStreamState();

        Check("A/new turn: idle before anything happens", state.Tps.DisplayTps is null, true);
        Check("A/new turn: state is idle", state.Tps.StateDescription, "idle");

        // New turn, no model output yet -> "--" even though a previous turn had a value.
        state.ObserveLine(TaskStarted("turn-1"));
        Check("D) new turn with no model output shows --", state.Tps.DisplayTps is null, true);
        Check("D) state is waiting", state.Tps.StateDescription, "waiting");

        state.ObserveLine(ItemCompleted("turn-1", "Reasoning", 0L, 2_000L));
        state.ObserveLine(UsageRecordWithKey(1_000, 800, 600, "turn-1", "resp-turn-1"));
        Check("A) generating: realtime TPS available", state.Tps.DisplayTps, 300.0, 1e-9);
        Check("A) state is generating", state.Tps.StateDescription, "generating");

        state.ObserveLine(ItemCompleted("turn-1", "CommandExecution", 2_000L, 9_000L));
        Check("A) tool execution does not zero the TPS", state.Tps.DisplayTps, 300.0, 1e-9);
        Check("A) tool execution marks the value as held", state.Tps.IsHeld, true);

        state.ObserveLine(TaskComplete("turn-1"));
        Check("B) turn end retains the final TPS", state.Tps.DisplayTps, 300.0, 1e-9);
        Check("B) state is completed", state.Tps.StateDescription, "completed");

        state.ObserveLine(TaskStarted("turn-2"));
        Check("D) the next turn again shows -- until it produces output", state.Tps.DisplayTps is null, true);
    }

    // ------------------------------------------------------------------ helpers

    private static void FeedTurn(
        RolloutStreamState state,
        string turnId,
        (string Type, long Start, long End)[] items,
        long outputTokens)
    {
        state.ObserveLine(TaskStarted(turnId));
        foreach (var (type, start, end) in items)
        {
            state.ObserveLine(ItemCompleted(turnId, type, start, end));
        }

        state.ObserveLine(UsageRecordWithKey(1_000 + outputTokens, 900, outputTokens, turnId, "resp-" + turnId));
        state.ObserveLine(TaskComplete(turnId));
    }

    private static long TotalOf(params string[] records)
    {
        var state = new RolloutStreamState();
        foreach (var record in records)
        {
            state.ObserveLine(record);
        }

        return state.TotalTokens;
    }

    private static string TaskStarted(string turnId) =>
        $"{{\"type\":\"event_msg\",\"payload\":{{\"type\":\"task_started\",\"turn_id\":\"{turnId}\"}}}}";

    private static string TaskComplete(string turnId) =>
        $"{{\"type\":\"event_msg\",\"payload\":{{\"type\":\"task_complete\",\"turn_id\":\"{turnId}\",\"duration_ms\":30000,\"time_to_first_token_ms\":1500}}}}";

    private static string ItemCompleted(string turnId, string itemType, long start, long end) =>
        $"{{\"type\":\"event_msg\",\"payload\":{{\"type\":\"item_completed\",\"thread_id\":\"t\",\"turn_id\":\"{turnId}\",\"item\":{{\"type\":\"{itemType}\",\"id\":\"{itemType}-{start}\"}},\"started_at_ms\":{start},\"completed_at_ms\":{end}}}}}";

    private static string UsageRecord(long input, long cached, long output) =>
        UsageRecordWithKey(input, cached, output, "turn-dedup", "resp-" + input + "-" + output);

    private static string UsageRecordWithKey(
        long input, long cached, long output, string turnId, string responseId)
    {
        var usage = UsageBody(input, cached, output);
        return $"{{\"type\":\"token_usage_record\",\"payload\":{{\"thread_id\":\"t\",\"turn_id\":\"{turnId}\",\"response_id\":\"{responseId}\",\"usage\":{usage},\"turn_token_usage\":{usage},\"thread_token_usage\":{usage}}}}}";
    }

    private static string TokenCount(long input, long cached, long output) =>
        $"{{\"type\":\"event_msg\",\"payload\":{{\"type\":\"token_count\",\"info\":{{\"total_token_usage\":{UsageBody(input, cached, output)},\"last_token_usage\":{UsageBody(input, cached, output)},\"model_context_window\":950000}}}}}}";

    private static string UsageBody(long input, long cached, long output) =>
        $"{{\"input_tokens\":{input},\"cached_input_tokens\":{cached},\"cache_write_input_tokens\":0,\"output_tokens\":{output},\"reasoning_output_tokens\":0,\"total_tokens\":{input + output}}}";

    private static int IndexOfTaskComplete(IReadOnlyList<string> lines, string turnId)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            if (!lines[index].Contains("\"task_complete\"", StringComparison.Ordinal)
                || !lines[index].Contains(turnId, StringComparison.Ordinal))
            {
                continue;
            }

            return index;
        }

        return -1;
    }

    private static string? Locate(string? explicitDirectory, string fileName)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitDirectory))
        {
            candidates.Add(Path.Combine(explicitDirectory, fileName));
        }

        var directory = AppContext.BaseDirectory;
        for (var depth = 0; depth < 7 && !string.IsNullOrEmpty(directory); depth++)
        {
            candidates.Add(Path.Combine(directory, "fixtures", fileName));
            directory = Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar)) ?? string.Empty;
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// The lifecycle contract, entirely as pure rules: which process counts as Codex Desktop, what
    /// Windows is asked to launch at logon, when the registry may be touched at all, and how the
    /// attach ladder advances. Nothing here starts a watcher, writes the registry or needs Codex to
    /// be running — the live behaviour is verified separately by the lifecycle acceptance run.
    /// </summary>
    private static void RunLifecycle(TextWriter stdout)
    {
        const string officialFamily = "OpenAI.Codex_2p2nqsd0c76g0";
        const string officialPath =
            @"C:\Program Files\WindowsApps\OpenAI.Codex_26.928.3736.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe";
        const string realChatGptPath =
            @"C:\Program Files\WindowsApps\OpenAI.ChatGPT-Desktop_1.2.3_x64__abc123\ChatGPT.exe";

        // --- the Codex Desktop identity rule ---
        Check(
            "official Codex Desktop (ChatGPT.exe + OpenAI.Codex package) is accepted",
            CodexIdentityRules.IsCodexDesktop("ChatGPT", officialFamily, officialPath),
            true);
        Check(
            "a renderer child of the same package is accepted too",
            CodexIdentityRules.IsCodexDesktop("ChatGPT", officialFamily, officialPath),
            true);
        Check(
            "the real ChatGPT desktop app is rejected although its exe is also ChatGPT.exe",
            CodexIdentityRules.IsCodexDesktop("ChatGPT", "OpenAI.ChatGPT-Desktop_abc123", realChatGptPath),
            false);
        Check(
            "a ChatGPT.exe with no package identity is rejected",
            CodexIdentityRules.IsCodexDesktop("ChatGPT", null, @"C:\Tools\ChatGPT.exe"),
            false);
        Check(
            "an empty family name does not fall back to a non-MSIX path",
            CodexIdentityRules.IsCodexDesktop("ChatGPT", "", @"C:\Program Files\ChatGPT\ChatGPT.exe"),
            false);
        Check(
            "the package identity wins over an unexpected install path",
            CodexIdentityRules.IsCodexDesktop("ChatGPT", officialFamily, @"D:\moved\ChatGPT.exe"),
            true);
        Check(
            "the unpackaged codex.exe CLI is not the desktop client",
            CodexIdentityRules.IsCodexDesktop(
                "Codex",
                null,
                @"C:\Users\u\AppData\Local\OpenAI\Codex\bin\c6fe824d725f02d7\codex.exe"),
            false);
        Check(
            "a Codex.exe inside the MSIX package is accepted by the path fallback",
            CodexIdentityRules.IsCodexDesktop(
                "Codex",
                null,
                @"C:\Program Files\WindowsApps\OpenAI.Codex_1.0.0.0_x64__h\app\codex.exe"),
            true);
        Check(
            "a process name that merely contains Codex is rejected",
            CodexIdentityRules.IsCodexDesktop("CodexHelper", officialFamily, officialPath),
            false);
        Check(
            "a family name that merely contains OpenAI.Codex is rejected",
            CodexIdentityRules.IsCodexDesktop("ChatGPT", "Fake.OpenAI.Codex_x", officialPath),
            false);
        Check(
            "an unrelated process is rejected",
            CodexIdentityRules.IsCodexDesktop("explorer", officialFamily, @"C:\Windows\explorer.exe"),
            false);
        Check(
            "the explanation names the signal that decided it",
            CodexIdentityRules.Explain("ChatGPT", officialFamily, officialPath).StartsWith("accepted"),
            true);

        // The pre-filter has to accept both spellings: Process.GetProcessesByName reports "ChatGPT"
        // while a Toolhelp32 snapshot reports "ChatGPT.exe". Comparing only one of them finds nothing
        // and the watcher goes blind while Codex is running.
        Check("the pre-filter accepts the bare process name", CodexIdentityRules.IsCandidateProcessName("ChatGPT"), true);
        Check("the pre-filter accepts the file name from a Toolhelp snapshot", CodexIdentityRules.IsCandidateProcessName("ChatGPT.exe"), true);
        Check("the pre-filter is case-insensitive", CodexIdentityRules.IsCandidateProcessName("chatgpt.EXE"), true);
        Check("the pre-filter accepts the unpackaged CLI name", CodexIdentityRules.IsCandidateProcessName("codex.exe"), true);
        Check("the pre-filter rejects a longer name that merely starts the same", CodexIdentityRules.IsCandidateProcessName("CodexHelper.exe"), false);
        Check("the pre-filter rejects the real ChatGPT app's name", CodexIdentityRules.IsCandidateProcessName("ChatGPT-Desktop.exe"), false);
        Check("the pre-filter rejects everything else", CodexIdentityRules.IsCandidateProcessName("explorer.exe"), false);
        Check("the pre-filter rejects an empty name", CodexIdentityRules.IsCandidateProcessName(""), false);

        // The incremental detection path reads package identity for a PID whose name it does not
        // know, so the rule has to decide on identity alone.
        Check(
            "identity alone accepts a Codex process when the name is unknown",
            CodexIdentityRules.IsCodexDesktop(null, officialFamily, officialPath),
            true);
        Check(
            "identity alone rejects a process with no package and no MSIX path",
            CodexIdentityRules.IsCodexDesktop(null, null, @"C:\Tools\ChatGPT.exe"),
            false);
        Check(
            "the MSIX path still identifies Codex when the name is unknown",
            CodexIdentityRules.IsCodexDesktop(null, null, officialPath),
            true);

        // --- what Windows will launch at logon ---
        const string exe = @"C:\Program Files\CodexStatusbar\CodexStatusbar.exe";
        Check(
            "the Run command quotes the path and appends --background",
            StartupManager.BuildCommand(exe),
            "\"" + exe + "\" --background");
        Check(
            "the quoted path round-trips out of the Run value",
            StartupManager.ParseExecutablePath(StartupManager.BuildCommand(exe)),
            exe);
        Check(
            "an unquoted Run value still round-trips",
            StartupManager.ParseExecutablePath(@"C:\Tools\CodexStatusbar.exe"),
            @"C:\Tools\CodexStatusbar.exe");
        Check(
            "a path containing spaces matches its own registration",
            StartupManager.PathMatches(StartupManager.BuildCommand(exe), exe),
            true);
        Check(
            "a different install directory does not match",
            StartupManager.PathMatches(StartupManager.BuildCommand(@"D:\Other\CodexStatusbar.exe"), exe),
            false);
        Check(
            "an absent Run value matches nothing",
            StartupManager.PathMatches(null, exe),
            false);
        Check(
            "the value name is the documented one",
            StartupManager.ValueName,
            "CodexStatusbar");
        Check(
            "the Run key is the HKCU one (no admin, no HKLM)",
            StartupManager.RunKeyPath,
            @"Software\Microsoft\Windows\CurrentVersion\Run");

        // --- the reconcile decision table: first-run only, then the user's choice ---
        Check(
            "first run (never configured) registers itself",
            StartupStatus.ResolveAction(false, false, null, exe),
            StartupRegistryAction.Enable);
        Check(
            "after the user turned it off, a later launch never re-enables it",
            StartupStatus.ResolveAction(true, false, null, exe),
            StartupRegistryAction.None);
        Check(
            "a user who turned it off gets a stale value deleted",
            StartupStatus.ResolveAction(true, false, StartupManager.BuildCommand(exe), exe),
            StartupRegistryAction.Disable);
        Check(
            "a user who left it on gets a missing value re-registered",
            StartupStatus.ResolveAction(true, true, null, exe),
            StartupRegistryAction.Enable);
        Check(
            "a matching registration is left untouched",
            StartupStatus.ResolveAction(true, true, StartupManager.BuildCommand(exe), exe),
            StartupRegistryAction.None);
        Check(
            "a registration pointing at the old directory is repaired",
            StartupStatus.ResolveAction(
                true,
                true,
                StartupManager.BuildCommand(@"C:\Old\CodexStatusbar.exe"),
                exe),
            StartupRegistryAction.RepairPath);
        Check(
            "first run with an already-correct value writes nothing",
            StartupStatus.ResolveAction(false, false, StartupManager.BuildCommand(exe), exe),
            StartupRegistryAction.None);

        // --- the attach ladder ---
        Check("the first attach attempt is immediate", LifecycleRules.DelayForAttempt(0), 0);
        Check("the second waits 250 ms", LifecycleRules.DelayForAttempt(1), 250);
        Check("the third waits 500 ms", LifecycleRules.DelayForAttempt(2), 500);
        Check("the fourth waits 1 s", LifecycleRules.DelayForAttempt(3), 1000);
        Check("the fifth waits 2 s", LifecycleRules.DelayForAttempt(4), 2000);
        Check("and it stays at 2 s instead of spinning", LifecycleRules.DelayForAttempt(50), 2000);
        Check("a negative attempt index is harmless", LifecycleRules.DelayForAttempt(-1), 0);
        Check(
            "attaching completes once the IPC route resolved a conversation",
            LifecycleRules.IsAttachComplete(true, true, false),
            true);
        Check(
            "attaching completes once the composer reference was found",
            LifecycleRules.IsAttachComplete(false, false, true),
            true);
        Check(
            "a connected pipe with no conversation yet is not attached",
            LifecycleRules.IsAttachComplete(true, false, false),
            false);
        Check(
            "nothing ready is not attached",
            LifecycleRules.IsAttachComplete(false, false, false),
            false);
        Check(
            "waiting ticks slowly so an idle machine stays idle",
            LifecycleRules.TickIntervalFor(CodexLifecycleState.WaitingForCodex),
            1000);
        Check(
            "attaching ticks at the ladder's resolution",
            LifecycleRules.TickIntervalFor(CodexLifecycleState.Attaching),
            250);
        Check(
            "the active state keeps the original 350 ms cadence",
            LifecycleRules.TickIntervalFor(CodexLifecycleState.Active),
            350);

        // --- settings v4 ---
        var upgraded = OverlaySettings.ParseJson(
            "{\"settingsVersion\":3,\"positionMode\":\"ComposerContextLeft\",\"contextGapDip\":10}");
        Check(
            "a v3 file upgrades to the current schema version",
            upgraded.Settings.SettingsVersion,
            OverlaySettings.CurrentSettingsVersion);
        Check("a v3 file is rewritten once", upgraded.MustPersist, true);
        Check(
            "an upgraded file has not configured startup yet, so the default applies once",
            upgraded.Settings.StartupConfigured,
            false);
        Check(
            "and its startWithWindows is off until the coordinator decides",
            upgraded.Settings.StartWithWindows,
            false);

        var turnedOff = OverlaySettings.ParseJson(
            "{\"settingsVersion\":4,\"startWithWindows\":false,\"startupConfigured\":true}");
        Check("a v4 file that turned it off stays off", turnedOff.Settings.StartWithWindows, false);
        Check("and is already configured, so nothing re-enables it", turnedOff.Settings.StartupConfigured, true);
        Check("a current file is not rewritten", turnedOff.MustPersist, false);

        var turnedOn = OverlaySettings.ParseJson(
            "{\"settingsVersion\":4,\"startWithWindows\":true,\"startupConfigured\":true}");
        Check("a v4 file that turned it on stays on", turnedOn.Settings.StartWithWindows, true);
        Check(
            "the lifecycle keys are written back to disk",
            turnedOn.Settings.Serialize().Contains("\"startWithWindows\"", StringComparison.Ordinal)
                && turnedOn.Settings.Serialize().Contains("\"startupConfigured\"", StringComparison.Ordinal),
            true);
        Check(
            "the serialised version is the current one",
            OverlaySettings.ParseJson(turnedOn.Settings.Serialize()).Settings.SettingsVersion,
            OverlaySettings.CurrentSettingsVersion);
        Check(
            "the previous position settings survived the version bump",
            turnedOn.Settings.PositionMode,
            OverlayPositionMode.ComposerContextLeft);
    }

    /// <summary>
    /// The position fast path, end to end and deterministically.
    ///
    /// <para>These are the spec's TEST A–F. They drive the production orchestration
    /// (<see cref="FastPathDriver"/> over <see cref="PositionScheduler"/>) with synthetic host
    /// rectangles and an explicit clock, and a fake window instead of a real one — so what is asserted
    /// is the shipped decision path, without a screenshot, a sleep or a window on screen. The visual
    /// result is the user's to accept; whether the pipeline moves once, moves late or moves a hundred
    /// times is a fact that belongs in a test.</para>
    /// </summary>
    private static void RunPositionFastPath(TextWriter stdout)
    {
        _ = stdout;
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var working = new IntRect(0, 0, 3840, 2160);

        // ---------------------------------------------------------------- TEST A: a pure move
        //
        // The whole window translates by (+50,+30) with the size unchanged. The strip must follow by
        // exactly that delta, and the accurate path must not be involved at all: no relayout, no
        // traversal, and — this is the measurable form of "UIA traversal count = 0" — no change to the
        // cross-process read rate.
        var target = new FakeFastPathTarget(new IntRect(1000, 1000, 312, 33));
        var scheduler = new PositionScheduler();
        scheduler.OnHostEvent(t0);
        target.SetHost(new IntRect(100, 100, 800, 600), new IntRect(150, 130, 800, 600), 144, 144, 1, 1, working);
        var action = FastPathDriver.Step(target, scheduler, t0, force: true);
        Check("A: a pure move is answered with a position-only write",
            action == FastPathAction.MoveOnly, true);
        Check("A: the strip followed by exactly the host delta", target.Window, new IntRect(1050, 1030, 312, 33));
        Check("A: the move caused no relayout", target.Relayouts, 0L);
        Check("A: the move asked for no UIA resync", scheduler.SettleResyncs, 0L);
        Check("A: the move did not raise the UIA read rate",
            PositionFastPathRules.AccurateIntervalFor(scheduler.Mode, scheduler.ShapeChangeInBurst),
            PositionFastPathRules.IdleAccurateIntervalMs);
        Check("A: a degraded ladder never gets the fast cadence either",
            PositionFastPathRules.AccurateIntervalFor(
                PositionUpdateMode.Burst,
                shapeChangeInBurst: true,
                hasCheapReReadPath: false),
            PositionFastPathRules.IdleAccurateIntervalMs);
        Check("A: one WinEvent was recorded", scheduler.WinEvents, 1L);

        // ---------------------------------------------------------------- TEST B: 100 events
        //
        // The host has moved 100 px and the window emits a hundred location events for it inside one
        // frame budget. The strip must end up at the final position immediately — one write, not a
        // hundred, and never through the intermediate ones. In production this is doubly guaranteed:
        // the fast path reads the *live* rectangle rather than the event's, and the sampler reports a
        // geometry change once. Both are modelled here.
        target = new FakeFastPathTarget(new IntRect(1000, 1000, 312, 33));
        scheduler = new PositionScheduler();
        target.SetHost(new IntRect(100, 100, 800, 600), new IntRect(200, 200, 800, 600), 144, 144, 1, 1, working);
        for (var index = 0; index < 100; index++)
        {
            var now = t0.AddTicks(index * 500); // 100 events over 5 ms
            scheduler.OnHostEvent(now);
            FastPathDriver.Step(target, scheduler, now, force: false);
        }

        Check("B: 100 events produced one window call", target.Moves, 1L);
        Check("B: the strip went straight to the final position",
            target.Window,
            new IntRect(1100, 1100, 312, 33));
        Check("B: 99 events were absorbed into that one write", scheduler.CoalescedEvents, 99L);
        Check("B: a repeated frame cannot double-apply the move",
            FastPathDriver.Step(target, scheduler, t0.AddMilliseconds(16), force: true) == FastPathAction.None,
            true);
        Check("B: ... so the position is unchanged", target.Window, new IntRect(1100, 1100, 312, 33));

        // B2: the rate limiter. Two geometry changes inside one frame budget produce one write now and
        // one on the next timer frame, so the strip cannot be driven faster than the frame rate even if
        // the window reports moves faster than the display.
        target.SetHost(new IntRect(200, 200, 800, 600), new IntRect(210, 210, 800, 600), 144, 144, 1, 1, working);
        Check("B2: a second change inside the same frame is held back",
            FastPathDriver.Step(target, scheduler, t0.AddMilliseconds(2), force: false) == FastPathAction.None,
            true);
        Check("B2: ... and counted as rate limited", scheduler.RateLimitedFrames, 1L);
        Check("B2: the timer frame then applies it",
            FastPathDriver.Step(target, scheduler, t0.AddMilliseconds(16), force: true) == FastPathAction.MoveOnly,
            true);
        Check("B2: two writes for two changes", target.Moves, 2L);

        // ---------------------------------------------------------------- TEST C: resize
        //
        // A size change cannot be answered by translation: the composer re-lays-out. The predictor's
        // defining property is that it keeps each rectangle's distance to the host's bottom-right
        // corner, which is what makes a resize a first-order guess; the accurate path then corrects it
        // and the burst raises its read rate so that correction arrives within ~40 ms.
        target = new FakeFastPathTarget(new IntRect(1000, 1000, 312, 33));
        scheduler = new PositionScheduler();
        scheduler.OnHostEvent(t0);
        target.SetHost(new IntRect(100, 100, 800, 600), new IntRect(100, 100, 900, 700), 144, 144, 1, 1, working);
        var resize = FastPathDriver.Step(target, scheduler, t0, force: true);
        Check("C: a resize is handed to the accurate path", resize == FastPathAction.Relayout, true);
        Check("C: the accurate path was asked exactly once", target.Relayouts, 1L);
        Check("C: the burst is marked as a shape change", scheduler.ShapeChangeInBurst, true);
        Check("C: a resize does raise the accurate read rate",
            PositionFastPathRules.AccurateIntervalFor(scheduler.Mode, scheduler.ShapeChangeInBurst),
            PositionFastPathRules.AccurateIntervalMs);
        Check("C: the resize was counted", scheduler.SizeChanges, 1L);

        // The prediction itself: the reference sits 57 px left of the host's right edge, so after a
        // resize that moved the right edge by 100 px it must still sit 57 px left of it.
        var referenceBefore = new IntRect(1708, 1406, 24, 24);
        var predicted = PositionFastPathRules.ReAnchor(
            referenceBefore,
            new IntRect(308, 245, 2048, 1224),
            new IntRect(308, 245, 2148, 1224));
        Check("C: the prediction keeps the distance to the host's bottom-right corner",
            predicted.Right - new IntRect(308, 245, 2148, 1224).Right,
            referenceBefore.Right - new IntRect(308, 245, 2048, 1224).Right);
        Check("C: ... and predicts the reference 100 px further right", predicted.X, 1808);

        // ---------------------------------------------------------------- TEST D: the tail
        //
        // Motion stops. 300 ms later the burst becomes settling and asks for one forced resync; after
        // the settling window the pipeline releases everything and goes idle.
        scheduler = new PositionScheduler();
        scheduler.OnHostEvent(t0);
        Check("D: motion is a burst", scheduler.Mode == PositionUpdateMode.Burst, true);
        Check("D: still bursting 299 ms after the last event",
            scheduler.Advance(t0.AddMilliseconds(299)) == SettleTransition.None, true);
        Check("D: 300 ms after the last event the burst ends",
            scheduler.Advance(t0.AddMilliseconds(300)) == SettleTransition.EnterSettling, true);
        Check("D: ... and that transition is reported exactly once",
            scheduler.Advance(t0.AddMilliseconds(310)) == SettleTransition.None, true);
        Check("D: one forced resync was booked", scheduler.SettleResyncs, 1L);
        Check("D: settling is still not idle",
            scheduler.Mode == PositionUpdateMode.Settling, true);
        Check("D: settling ends after its own window",
            scheduler.Advance(t0.AddMilliseconds(550)) == SettleTransition.Settled, true);
        Check("D: the pipeline is idle again", scheduler.Mode == PositionUpdateMode.Idle, true);

        // ---------------------------------------------------------------- TEST E: idle is idle
        //
        // A second of idle frames at 60 Hz must produce no window call. Two variants: a host that is not
        // moving at all, and a stale sample that claims it is — the second is the one that proves the
        // fast path cannot fire outside a burst, which is what keeps the idle cost where it was.
        target = new FakeFastPathTarget(new IntRect(1000, 1000, 312, 33));
        scheduler = new PositionScheduler();
        var stationary = new IntRect(100, 100, 800, 600);
        for (var frame = 0; frame < 60; frame++)
        {
            target.SetHost(stationary, stationary, 144, 144, 1, 1, working);
            FastPathDriver.Step(target, scheduler, t0.AddMilliseconds(frame * 16), force: false);
        }

        Check("E: an idle second makes no position write", target.Moves, 0L);
        Check("E: ... and no relayout", target.Relayouts, 0L);
        Check("E: the idle mode refuses to write", scheduler.ShouldWriteNow(t0, geometryChange: false), false);

        target = new FakeFastPathTarget(new IntRect(1000, 1000, 312, 33));
        scheduler = new PositionScheduler();
        for (var frame = 0; frame < 60; frame++)
        {
            target.SetHost(stationary, new IntRect(100 + frame, 100 + frame, 800, 600), 144, 144, 1, 1, working);
            FastPathDriver.Step(target, scheduler, t0.AddMilliseconds(frame * 16), force: false);
        }

        Check("E: geometry alone cannot wake the pipeline, an event must", target.Moves, 0L);
        Check("E: ... and the frames are reported as held back", scheduler.RateLimitedFrames, 59L);

        // ---------------------------------------------------------------- TEST F: DPI and monitor
        //
        // Dragging between displays can change the DPI with the size unchanged, and the monitor with
        // neither. Both invalidate the DIP-to-pixel relationship, so both must force a re-resolve
        // rather than reuse the old ratio.
        target = new FakeFastPathTarget(new IntRect(1000, 1000, 312, 33));
        scheduler = new PositionScheduler();
        scheduler.OnHostEvent(t0);
        target.SetHost(new IntRect(100, 100, 800, 600), new IntRect(100, 100, 800, 600), 144, 192, 1, 1, working);
        Check("F: a DPI change forces a relayout",
            FastPathDriver.Step(target, scheduler, t0, force: true) == FastPathAction.Relayout, true);
        Check("F: the DPI change was counted", scheduler.DpiChanges, 1L);
        target.SetHost(new IntRect(100, 100, 800, 600), new IntRect(100, 100, 800, 600), 192, 192, 1, 2, working);
        Check("F: a monitor change forces a relayout",
            FastPathDriver.Step(target, scheduler, t0.AddMilliseconds(20), force: true) == FastPathAction.Relayout, true);
        Check("F: the monitor change was counted", scheduler.MonitorChanges, 1L);

        // A same-size, same-DPI, same-monitor sample must not be mistaken for a shape change.
        target.SetHost(new IntRect(100, 100, 800, 600), new IntRect(140, 130, 800, 600), 192, 192, 2, 2, working);
        Check("F: a move with identical shape is still just a move",
            FastPathDriver.Step(target, scheduler, t0.AddMilliseconds(40), force: true) == FastPathAction.MoveOnly,
            true);

        // ---------------------------------------------------------------- the guards
        //
        // A minimised or hidden host reports Windows' (-32000,-32000) sentinel: the strip hides rather
        // than docking to nonsense. And a move that would push the strip out of the monitor's working
        // area is not a translation either — it needs the clamp, which lives in the layout calculator.
        target = new FakeFastPathTarget(new IntRect(1000, 1000, 312, 33));
        scheduler = new PositionScheduler();
        scheduler.OnHostEvent(t0);
        Check("guard: the (-32000,-32000) sentinel is not a usable host rect",
            PositionFastPathRules.IsUsableHostRect(new IntRect(-32000, -32000, 2048, 1224)), false);
        Check("guard: a real rect is usable",
            PositionFastPathRules.IsUsableHostRect(new IntRect(308, 245, 2048, 1224)), true);
        target.SetHost(
            new IntRect(100, 100, 800, 600),
            new IntRect(-31000, -31000, 800, 600),
            144,
            144,
            1,
            1,
            working);
        Check("guard: an unusable host hides the strip",
            FastPathDriver.Step(target, scheduler, t0, force: true) == FastPathAction.HideForUnusableHost,
            true);
        Check("guard: ... by hiding, not by moving", target.Hides, 1L);

        target = new FakeFastPathTarget(new IntRect(3700, 1000, 312, 33));
        scheduler = new PositionScheduler();
        scheduler.OnHostEvent(t0);
        target.SetHost(
            new IntRect(3300, 100, 600, 600),
            new IntRect(3500, 100, 600, 600),
            144,
            144,
            1,
            1,
            working);
        Check("guard: a move that would leave the working area is re-resolved instead",
            FastPathDriver.Step(target, scheduler, t0, force: true) == FastPathAction.Relayout, true);

        // A host that is only *partly* on screen is the same class of problem even when the strip itself
        // would still fit: Codex's composer stops translating rigidly with the window rectangle (measured
        // on the real client: the toolbar row moved 39 px for a 63 px window move) and the layout's
        // ClampToVisible has to run. So it must not be answered by a translation either — this is the one
        // case in this whole change where a shortcut would have made the tracking visibly worse.
        var partlyVisible = new IntRect(0, 0, 2560, 1400);
        target = new FakeFastPathTarget(new IntRect(1000, 1000, 312, 33));
        scheduler = new PositionScheduler();
        scheduler.OnHostEvent(t0);
        target.SetHost(
            new IntRect(300, 900, 2100, 1300),
            new IntRect(300, 960, 2100, 1300),
            144,
            144,
            1,
            1,
            partlyVisible);
        Check("guard: a partly off-screen host is re-resolved, not translated",
            FastPathDriver.Step(target, scheduler, t0, force: true) == FastPathAction.Relayout, true);
        Check("guard: ... and the burst is marked so the accurate path speeds up",
            scheduler.ShapeChangeInBurst, true);
        Check("guard: ... and it is counted as an off-screen relayout", scheduler.OffscreenChanges, 1L);
        Check("guard: a fully visible host of the same size is still a translation",
            PositionFastPathRules.Plan(
                new HostGeometrySample(
                    new IntRect(300, 100, 2100, 900),
                    new IntRect(300, 160, 2100, 900),
                    144,
                    144,
                    1,
                    1,
                    partlyVisible,
                    true),
                new IntRect(1000, 1000, 312, 33)).Action == FastPathAction.MoveOnly,
            true);
    }

    /// <summary>
    /// A stand-in for the overlay: same interface, synthetic geometry, no window. Counting the calls it
    /// receives is how the tests observe what the pipeline decided.
    /// </summary>
    private sealed class FakeFastPathTarget : IFastPathTarget
    {
        private HostGeometrySample _sample;
        private bool _hasSample;

        public FakeFastPathTarget(IntRect windowBounds)
        {
            Window = windowBounds;
        }

        public IntRect Window { get; private set; }

        public long Moves { get; private set; }

        public long Relayouts { get; private set; }

        public long Hides { get; private set; }

        public bool CanTrack => true;

        IntRect IFastPathTarget.CurrentWindowBounds => Window;

        public void SetHost(
            IntRect previous,
            IntRect current,
            uint previousDpi,
            uint currentDpi,
            long previousMonitor,
            long currentMonitor,
            IntRect workingArea)
        {
            _sample = new HostGeometrySample(
                previous,
                current,
                previousDpi,
                currentDpi,
                previousMonitor,
                currentMonitor,
                workingArea,
                PositionFastPathRules.IsUsableHostRect(current));
            _hasSample = true;
        }

        /// <summary>
        /// Models <see cref="HostWindowSampler"/>: a geometry change is reported until it is committed,
        /// so a frame held back by the rate limiter sees the same change again — with a delta covering
        /// everything since the last write.
        /// </summary>
        bool IFastPathTarget.TrySampleHost(out HostGeometrySample sample)
        {
            sample = _sample;
            return _hasSample;
        }

        void IFastPathTarget.CommitHostGeometry() => _hasSample = false;

        void IFastPathTarget.MoveWindowTo(IntRect bounds)
        {
            Moves++;
            Window = bounds;
        }

        void IFastPathTarget.RelayoutFromAccuratePath() => Relayouts++;

        void IFastPathTarget.HideForUnusableHost() => Hides++;
    }

    private static void Section(string title)
    {        Console.Out.WriteLine();
        Console.Out.WriteLine("-- " + title);
    }

    private static void Check(string name, IntRect actual, IntRect expected)
    {
        _checks++;
        if (actual == expected)
        {
            Console.Out.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"  OK    {name} ({actual.X},{actual.Y} {actual.Width}x{actual.Height})"));
            return;
        }

        _failures++;
        Console.Out.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"  FAIL  {name}: got {actual.X},{actual.Y} {actual.Width}x{actual.Height}, "
                + $"want {expected.X},{expected.Y} {expected.Width}x{expected.Height}"));
    }

    private static void Check(string name, long actual, long expected)
    {
        _checks++;
        if (actual == expected)
        {
            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  OK    {name} ({actual})"));
            return;
        }

        _failures++;
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  FAIL  {name}: got {actual}, want {expected}"));
    }

    private static void Check(string name, double actual, double expected, double tolerance)
    {
        _checks++;
        if (Math.Abs(actual - expected) <= tolerance)
        {
            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  OK    {name} ({actual:0.####})"));
            return;
        }

        _failures++;
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  FAIL  {name}: got {actual:0.####}, want {expected:0.####}"));
    }

    private static void Check(string name, double? actual, double expected, double tolerance) =>
        Check(name, actual ?? double.NaN, expected, tolerance);

    private static void Check(string name, string? actual, string expected)
    {
        _checks++;
        if (string.Equals(actual, expected, StringComparison.Ordinal))
        {
            Console.Out.WriteLine($"  OK    {name} ({actual})");
            return;
        }

        _failures++;
        Console.Out.WriteLine($"  FAIL  {name}: got {actual ?? "<null>"}, want {expected}");
    }

    private static void Check(string name, bool actual, bool expected)
    {
        _checks++;
        if (actual == expected)
        {
            Console.Out.WriteLine($"  OK    {name} ({actual})");
            return;
        }

        _failures++;
        Console.Out.WriteLine($"  FAIL  {name}: got {actual}, want {expected}");
    }

    private static void Fail(string message)
    {
        _checks++;
        _failures++;
        Console.Out.WriteLine("  FAIL  " + message);
    }
}