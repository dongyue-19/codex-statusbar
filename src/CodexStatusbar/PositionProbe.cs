using System.Text;
using System.Text.Json;

namespace CodexStatusbar;

/// <summary>
/// Numeric acceptance harness for the position model, driven through the <b>production</b>
/// <see cref="ManualAttachmentCalculator"/> and <see cref="OverlayLayoutCalculator"/> — the same two
/// types the live overlay resolves its window position with.
///
/// <para>It answers the questions that matter for this round: does a drag produce the anchor a human
/// would name, is the offset measured in DIP, does a resize land within rounding error, does repeated
/// resizing drift, is a clamp ever written back, and does the position survive a settings round-trip.</para>
/// </summary>
internal static class PositionProbe
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private const uint Dpi = 144;            // the user's 150% monitor
    private const int Tolerance = 2;         // DIP-to-pixel rounding plus integer division

    public static int Run(string outputPath)
    {
        var checks = Evaluate();
        var failures = checks.Count(item => !item.Passed);
        var report = new
        {
            Dpi,
            ScalePercent = Dpi * 100 / 96,
            TolerancePixels = Tolerance,
            Checks = checks,
            Passed = checks.Count - failures,
            Failed = failures,
            Verdict = failures == 0 ? "PASS" : "FAIL"
        };
        File.WriteAllText(
            outputPath,
            JsonSerializer.Serialize(report, JsonOptions),
            new UTF8Encoding(false));

        Console.WriteLine($"position probe written to {outputPath}");
        foreach (var check in checks)
        {
            Console.WriteLine(
                $"  [{(check.Passed ? "ok  " : "FAIL")}] {check.Name}"
                + (check.Detail.Length == 0 ? string.Empty : $" — {check.Detail}"));
        }

        Console.WriteLine($"  {report.Passed} passed, {failures} failed");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// The whole suite as data, so <c>--self-test</c> can fold it into the one gate rather than
    /// duplicating assertions.
    /// </summary>
    public static IReadOnlyList<PositionCheck> Evaluate()
    {
        var checks = new List<PositionCheck>();
        var drag = TestDragSelectsAnchor(checks);
        TestResizeKeepsOffset(drag, checks);
        TestNoCumulativeDrift(drag, checks);
        TestMaximizeRestore(drag, checks);
        TestMoveFollows(drag, checks);
        TestClampIsDisplayOnly(checks);
        TestAnchorSemantics(checks);
        TestPersistence(drag, checks);
        TestDpiIndependence(checks);
        return checks;
    }

    internal sealed record PositionCheck(string Name, bool Passed, string Detail);

    private static void Check(List<PositionCheck> checks, string name, bool passed, string detail = "") =>
        checks.Add(new PositionCheck(name, passed, detail));

    // ------------------------------------------------------------------ TEST 2

    private static DragResult TestDragSelectsAnchor(List<PositionCheck> checks)
    {
        var window = new IntRect(120, 90, 1400, 900);
        var capsuleSize = OverlayLayoutCalculator.GetManualCapsuleSize(Dpi, 100, new IntRect(0, 0, 2560, 1400));
        // The user drops the strip in the top-right region, 24 DIP in from the right edge and 16 DIP
        // down from the top — the example from the spec.
        var capsule = new IntRect(
            window.Right - ManualAttachmentRules.DipToPixels(24, Dpi) - capsuleSize.Width,
            window.Top + ManualAttachmentRules.DipToPixels(16, Dpi),
            capsuleSize.Width,
            capsuleSize.Height);
        var attachment = ManualAttachmentCalculator.Capture(window, capsule, Dpi);

        Check(
            checks,
            "TEST 2  drag to the top-right region selects the TopRight anchor",
            attachment.ReferencePoint == AttachmentReferencePoint.TopRight,
            attachment.ReferencePoint.ToString());
        Check(
            checks,
            "TEST 2  offsetX is -24 DIP (measured from the window's right edge)",
            Math.Abs(attachment.OffsetXDip + 24d) <= 0.5,
            $"{-24d} vs {attachment.OffsetXDip:0.###}");
        Check(
            checks,
            "TEST 2  offsetY is +16 DIP (measured down from the window's top edge)",
            Math.Abs(attachment.OffsetYDip - 16d) <= 0.5,
            $"{16d} vs {attachment.OffsetYDip:0.###}");

        var back = ManualAttachmentCalculator.ResolveTopLeft(window, attachment, capsuleSize, Dpi);
        Check(
            checks,
            "TEST 2  capture -> resolve returns the same pixel rectangle",
            Math.Abs(back.X - capsule.X) <= Tolerance && Math.Abs(back.Y - capsule.Y) <= Tolerance,
            $"({capsule.X},{capsule.Y}) vs ({back.X},{back.Y})");

        return new DragResult(window, attachment, capsuleSize, capsule);
    }

    private sealed record DragResult(
        IntRect Window,
        WindowAttachment Attachment,
        Size CapsuleSize,
        IntRect Capsule);

    /// <summary>
    /// Runs the full production path — resolve top-left, then lay out through
    /// <see cref="OverlayLayoutCalculator"/>, which is what actually calls <c>SetBounds</c>.
    /// </summary>
    private static OverlayLayoutResult Place(DragResult drag, IntRect window)
    {
        var expected = ManualAttachmentCalculator.ResolveTopLeft(
            window,
            drag.Attachment,
            drag.CapsuleSize,
            Dpi);
        return OverlayLayoutCalculator.Calculate(new OverlayLayoutRequest(
            Host(window),
            AnchorMode.TitleBarTopRight,
            RequestExpanded: false,
            ExpandedRowCount: 0,
            ShowContextProgress: false,
            ManualCapsuleTopLeft: expected,
            ScalePercent: 100));
    }

    private static CodexWindowInfo Host(IntRect window) => new(
        (IntPtr)1,
        window,
        window,
        null,
        new IntRect(0, 0, 2560, 1400),
        Dpi,
        new WindowChromeMetrics(46, 30, 1, 1, 1));

    /// <summary>The position <c>windowAnchorPoint + offset</c> asks for, before any clamping.</summary>
    private static Point RequestedTopLeft(DragResult drag, IntRect window) =>
        ManualAttachmentCalculator.ResolveTopLeft(window, drag.Attachment, drag.CapsuleSize, Dpi);

    // ------------------------------------------------------------------ TEST 3 / 4

    private static void TestResizeKeepsOffset(DragResult drag, List<PositionCheck> checks)
    {
        var original = RequestedTopLeft(drag, drag.Window);
        var sizes = new[]
        {
            new IntRect(120, 90, 1000, 700),
            new IntRect(120, 90, 1440, 900),
            new IntRect(0, 0, 800, 600)
        };

        foreach (var window in sizes)
        {
            var layout = Place(drag, window);
            var requested = RequestedTopLeft(drag, window);
            var rightMarginDip = ManualAttachmentRules.PixelsToDip(
                window.Right - (layout.WindowBounds.X + layout.WindowBounds.Width),
                Dpi);
            Check(
                checks,
                $"TEST 3/4  {window.Width}x{window.Height}: lands within {Tolerance} px of anchor + offset",
                Math.Abs(layout.WindowBounds.X - requested.X) <= Tolerance
                    && Math.Abs(layout.WindowBounds.Y - requested.Y) <= Tolerance,
                $"expected ({requested.X},{requested.Y}) actual ({layout.WindowBounds.X},{layout.WindowBounds.Y})");
            Check(
                checks,
                $"TEST 3/4  {window.Width}x{window.Height}: right margin stays 24 DIP",
                Math.Abs(rightMarginDip - 24d) <= 1d,
                $"{rightMarginDip:0.##} DIP");
        }

        var restored = RequestedTopLeft(drag, drag.Window);
        Check(
            checks,
            "TEST 4  growing the window back restores the original position exactly",
            restored == original,
            $"({original.X},{original.Y}) vs ({restored.X},{restored.Y})");
    }

    private static void TestNoCumulativeDrift(DragResult drag, List<PositionCheck> checks)
    {
        var attachment = drag.Attachment;
        var window = drag.Window;
        var first = RequestedTopLeft(drag, window);
        var sizes = new[] { 1400, 900, 1300, 1100, 1400, 700, 1400 };
        for (var round = 0; round < 5; round++)
        {
            foreach (var width in sizes)
            {
                window = new IntRect(120, 90, width, (int)(width * 0.64));
                var layout = Place(drag, window);
                _ = layout;
            }
        }

        Check(
            checks,
            "TEST 4  25 resize cycles never rewrite the saved offset (no cumulative drift)",
            attachment == drag.Attachment,
            $"{drag.Attachment.ReferencePoint} ({drag.Attachment.OffsetXDip:0.###}, {drag.Attachment.OffsetYDip:0.###})");

        var last = RequestedTopLeft(drag, drag.Window);
        Check(
            checks,
            "TEST 4  after 25 cycles the original window size still gives the original position",
            last == first,
            $"({first.X},{first.Y}) vs ({last.X},{last.Y})");
    }

    // ------------------------------------------------------------------ TEST 5

    private static void TestMaximizeRestore(DragResult drag, List<PositionCheck> checks)
    {
        var maximized = new IntRect(0, 0, 2560, 1400);
        var layout = Place(drag, maximized);
        var requested = RequestedTopLeft(drag, maximized);
        Check(
            checks,
            "TEST 5  maximize: position is anchor + offset on the maximized frame",
            Math.Abs(layout.WindowBounds.X - requested.X) <= Tolerance
                && Math.Abs(layout.WindowBounds.Y - requested.Y) <= Tolerance,
            $"expected ({requested.X},{requested.Y}) actual ({layout.WindowBounds.X},{layout.WindowBounds.Y})");

        var restoredLayout = Place(drag, drag.Window);
        var restoredRequested = RequestedTopLeft(drag, drag.Window);
        Check(
            checks,
            "TEST 5  restore: position returns to the pre-maximize value",
            restoredLayout.WindowBounds.X == restoredRequested.X
                && restoredLayout.WindowBounds.Y == restoredRequested.Y,
            $"({restoredRequested.X},{restoredRequested.Y})");
    }

    // ------------------------------------------------------------------ TEST 7

    private static void TestMoveFollows(DragResult drag, List<PositionCheck> checks)
    {
        var moved = drag.Window with { X = 700, Y = 420 };
        var layout = Place(drag, moved);
        var before = RequestedTopLeft(drag, drag.Window);
        var after = RequestedTopLeft(drag, moved);
        Check(
            checks,
            "TEST 7  moving Codex moves the strip by exactly the same delta",
            layout.WindowBounds.X - drag.Capsule.X == 700 - drag.Window.X
                && layout.WindowBounds.Y - drag.Capsule.Y == 420 - drag.Window.Y,
            $"delta ({layout.WindowBounds.X - drag.Capsule.X},{layout.WindowBounds.Y - drag.Capsule.Y}) "
                + $"vs ({700 - drag.Window.X},{420 - drag.Window.Y})");
        Check(
            checks,
            "TEST 7  the anchor + offset are identical before and after the move",
            after.X - before.X == 700 - drag.Window.X
                && after.Y - before.Y == 420 - drag.Window.Y,
            $"({before.X},{before.Y}) -> ({after.X},{after.Y})");
    }

    // ------------------------------------------------------------------ clamp

    private static void TestClampIsDisplayOnly(List<PositionCheck> checks)
    {
        // BottomRight with a 12 DIP inward margin, then shrink the window so the requested position
        // would fall off the bottom-right of the screen.
        var attachment = new WindowAttachment(AttachmentReferencePoint.BottomRight, -12d, -12d);
        var capsuleSize = OverlayLayoutCalculator.GetManualCapsuleSize(Dpi, 100, new IntRect(0, 0, 2560, 1400));
        var tiny = new IntRect(2380, 1280, 300, 200);
        var requested = ManualAttachmentCalculator.ResolveTopLeft(tiny, attachment, capsuleSize, Dpi);
        var layout = OverlayLayoutCalculator.Calculate(new OverlayLayoutRequest(
            Host(tiny),
            AnchorMode.TitleBarTopRight,
            RequestExpanded: false,
            ExpandedRowCount: 0,
            ShowContextProgress: false,
            ManualCapsuleTopLeft: requested,
            ScalePercent: 100));

        var visibleWidth = Math.Min(layout.WindowBounds.Right, 2560) - Math.Max(layout.WindowBounds.Left, 0);
        var visibleHeight = Math.Min(layout.WindowBounds.Bottom, 1400) - Math.Max(layout.WindowBounds.Top, 0);
        Check(
            checks,
            "CLAMP  a strip pushed off-screen is pulled back into view",
            visibleWidth >= layout.WindowBounds.Width && visibleHeight >= layout.WindowBounds.Height,
            $"visible {visibleWidth}x{visibleHeight} of {layout.WindowBounds.Width}x{layout.WindowBounds.Height}");

        // The whole point: the clamp must not have contaminated the saved attachment.
        var grown = new IntRect(120, 90, 1400, 900);
        var regrown = ManualAttachmentCalculator.ResolveTopLeft(grown, attachment, capsuleSize, Dpi);
        Check(
            checks,
            "CLAMP  the saved anchor + offset are untouched by the clamp",
            attachment == new WindowAttachment(AttachmentReferencePoint.BottomRight, -12d, -12d),
            $"{attachment.ReferencePoint} ({attachment.OffsetXDip:0.###}, {attachment.OffsetYDip:0.###})");
        Check(
            checks,
            "CLAMP  growing the window afterwards returns the un-clamped position",
            regrown == ManualAttachmentCalculator.ResolveTopLeft(grown, attachment, capsuleSize, Dpi)
                && regrown != requested,
            $"clamped ({requested.X},{requested.Y}) -> restored ({regrown.X},{regrown.Y})");
    }

    // ------------------------------------------------------------------ anchor semantics

    private static void TestAnchorSemantics(List<PositionCheck> checks)
    {
        var window = new IntRect(120, 90, 1400, 900);
        var workingArea = new IntRect(0, 0, 2560, 1400);
        var capsuleSize = OverlayLayoutCalculator.GetManualCapsuleSize(Dpi, 100, workingArea);
        var expectations = new (AttachmentReferencePoint Anchor, int X, int Y)[]
        {
            (AttachmentReferencePoint.TopLeft, window.Left, window.Top),
            (AttachmentReferencePoint.TopCenter, window.Left + (window.Width / 2), window.Top),
            (AttachmentReferencePoint.TopRight, window.Right, window.Top),
            (AttachmentReferencePoint.LeftCenter, window.Left, window.Top + (window.Height / 2)),
            (AttachmentReferencePoint.Center, window.Left + (window.Width / 2), window.Top + (window.Height / 2)),
            (AttachmentReferencePoint.RightCenter, window.Right, window.Top + (window.Height / 2)),
            (AttachmentReferencePoint.BottomLeft, window.Left, window.Bottom),
            (AttachmentReferencePoint.BottomCenter, window.Left + (window.Width / 2), window.Bottom),
            (AttachmentReferencePoint.BottomRight, window.Right, window.Bottom)
        };

        foreach (var (anchor, expectedX, expectedY) in expectations)
        {
            var attachment = ManualAttachmentRules.OffsetForAnchor(anchor);
            var topLeft = ManualAttachmentCalculator.ResolveTopLeft(window, attachment, capsuleSize, Dpi);
            var vector = AnchorVector.For(anchor);
            var expected = new Point(
                expectedX + ManualAttachmentRules.DipToPixels(attachment.OffsetXDip, Dpi)
                    - vector.OffsetInside(capsuleSize).X,
                expectedY + ManualAttachmentRules.DipToPixels(attachment.OffsetYDip, Dpi)
                    - vector.OffsetInside(capsuleSize).Y);
            Check(
                checks,
                $"ANCHOR  {anchor} resolves to its own point on the window",
                Math.Abs(topLeft.X - expected.X) <= Tolerance && Math.Abs(topLeft.Y - expected.Y) <= Tolerance,
                $"expected ({expected.X},{expected.Y}) actual ({topLeft.X},{topLeft.Y})");
        }

        // The 3x3 region rule: a drop in each third must name that third.
        var regionCases = new (double RatioX, double RatioY, AttachmentReferencePoint Expected)[]
        {
            (0.10, 0.10, AttachmentReferencePoint.TopLeft),
            (0.50, 0.05, AttachmentReferencePoint.TopCenter),
            (0.90, 0.20, AttachmentReferencePoint.TopRight),
            (0.05, 0.50, AttachmentReferencePoint.LeftCenter),
            (0.50, 0.50, AttachmentReferencePoint.Center),
            (0.95, 0.50, AttachmentReferencePoint.RightCenter),
            (0.10, 0.90, AttachmentReferencePoint.BottomLeft),
            (0.50, 0.95, AttachmentReferencePoint.BottomCenter),
            (0.85, 0.85, AttachmentReferencePoint.BottomRight)
        };
        foreach (var (ratioX, ratioY, expected) in regionCases)
        {
            var capsule = new IntRect(
                window.Left + (int)(window.Width * ratioX) - (capsuleSize.Width / 2),
                window.Top + (int)(window.Height * ratioY) - (capsuleSize.Height / 2),
                capsuleSize.Width,
                capsuleSize.Height);
            var selected = ManualAttachmentCalculator.SelectReferencePoint(window, capsule);
            Check(
                checks,
                $"ANCHOR  3x3 region ({ratioX:0.00}, {ratioY:0.00}) -> {expected}",
                selected == expected,
                selected.ToString());
        }
    }

    // ------------------------------------------------------------------ TEST 6

    private static void TestPersistence(DragResult drag, List<PositionCheck> checks)
    {
        var settings = OverlaySettings.CreateDefault();
        settings.MainAttachment = drag.Attachment;
        settings.PositionMode = OverlayPositionMode.FollowCodex;
        settings.ThemePreference = OverlayThemePreference.Auto;

        var json = settings.Serialize();
        Check(
            checks,
            "TEST 6  settings.json carries anchor / offsetX / offsetY as flat keys",
            json.Contains("\"anchor\": \"TopRight\"", StringComparison.Ordinal)
                && json.Contains("\"offsetX\": -24", StringComparison.Ordinal),
            FirstLines(json, 8));

        var reloaded = OverlaySettings.ParseJson(json).Settings;
        Check(
            checks,
            "TEST 6  reload gives back an identical attachment",
            reloaded.MainAttachment == drag.Attachment,
            $"{reloaded.MainAttachment.ReferencePoint} "
                + $"({reloaded.MainAttachment.OffsetXDip:0.###}, {reloaded.MainAttachment.OffsetYDip:0.###})");
        Check(
            checks,
            "TEST 6  reload keeps transparency on and the position mode",
            reloaded.TransparentBackground
                && reloaded.PositionMode == OverlayPositionMode.FollowCodex
                && reloaded.ThemePreference == OverlayThemePreference.Auto,
            $"transparent={reloaded.TransparentBackground} mode={reloaded.PositionMode} theme={reloaded.ThemePreference}");

        var migrated = OverlaySettings.ParseJson(
            """
            {
              "settingsVersion": 1,
              "manualPlacementEnabled": true,
              "mainAttachment": { "referencePoint": 2, "offsetXDip": -344, "offsetYDip": 24 }
            }
            """);
        Check(
            checks,
            "MIGRATION  an untouched v1 default adopts the new TopCenter default instead of the stale corner",
            migrated.MustPersist
                && migrated.Settings.MainAttachment == ManualAttachmentRules.DefaultMainAttachment,
            $"{migrated.Settings.MainAttachment.ReferencePoint} "
                + $"({migrated.Settings.MainAttachment.OffsetXDip}, {migrated.Settings.MainAttachment.OffsetYDip})");

        var customised = OverlaySettings.ParseJson(
            """
            {
              "settingsVersion": 1,
              "manualPlacementEnabled": true,
              "mainAttachment": { "referencePoint": 0, "offsetXDip": 40, "offsetYDip": 75 }
            }
            """);
        Check(
            checks,
            "MIGRATION  a v1 position the user actually chose is preserved",
            customised.Settings.MainAttachment
                == new WindowAttachment(AttachmentReferencePoint.TopLeft, 40d, 75d),
            $"{customised.Settings.MainAttachment.ReferencePoint} "
                + $"({customised.Settings.MainAttachment.OffsetXDip}, {customised.Settings.MainAttachment.OffsetYDip})");

        var defaultJson = OverlaySettings.CreateDefault().Serialize();
        Check(
            checks,
            "DEFAULT  a fresh install starts at TopCenter with a 10 DIP top margin",
            defaultJson.Contains("\"anchor\": \"TopCenter\"", StringComparison.Ordinal)
                && defaultJson.Contains("\"offsetY\": 10", StringComparison.Ordinal)
                && defaultJson.Contains("\"transparentBackground\": true", StringComparison.Ordinal),
            FirstLines(defaultJson, 8));
    }

    // ------------------------------------------------------------------ DPI

    private static void TestDpiIndependence(List<PositionCheck> checks)
    {
        var window = new IntRect(0, 0, 1600, 1000);
        var attachment = new WindowAttachment(AttachmentReferencePoint.TopRight, -24d, 16d);
        var workingArea = new IntRect(0, 0, 3200, 2000);
        var marginPixels = new List<(uint Dpi, int Pixels)>();

        foreach (var dpi in new[] { 96u, 120u, 144u, 192u })
        {
            var size = OverlayLayoutCalculator.GetManualCapsuleSize(dpi, 100, workingArea);
            var topLeft = ManualAttachmentCalculator.ResolveTopLeft(window, attachment, size, dpi);
            marginPixels.Add((dpi, window.Right - (topLeft.X + size.Width)));
        }

        var marginsDip = marginPixels
            .Select(item => ManualAttachmentRules.PixelsToDip(item.Pixels, item.Dpi))
            .ToArray();
        Check(
            checks,
            "DPI  the same offset measures 24 DIP at 100 / 125 / 150 / 200%",
            marginsDip.All(value => Math.Abs(value - 24d) <= 0.6),
            string.Join(", ", marginPixels.Select(
                item => $"{item.Dpi}dpi={item.Pixels}px ({ManualAttachmentRules.PixelsToDip(item.Pixels, item.Dpi):0.##}dip)")));

        var widthsDip = new[] { 96u, 120u, 144u, 192u }
            .Select(dpi => ManualAttachmentRules.PixelsToDip(
                OverlayLayoutCalculator.GetManualCapsuleSize(dpi, 100, workingArea).Width,
                dpi))
            .ToArray();
        Check(
            checks,
            "DPI  the strip's own width is 330 DIP on every monitor",
            widthsDip.All(value => Math.Abs(value - 330d) <= 0.5),
            string.Join(", ", widthsDip.Select(value => $"{value:0.##}dip")));

        // Crossing monitors: the pixel position legitimately changes, because a 24 DIP margin and a
        // 330 DIP strip are physically bigger at 150%. What must NOT happen is an extra shift on top of
        // that scaling. And crucially the shift must be independent of how wide the window is — which
        // is exactly what a percentage-based model would get wrong.
        var expectedShift = -(
            OverlayLayoutCalculator.GetManualCapsuleSize(144, 100, workingArea).Width
            - OverlayLayoutCalculator.GetManualCapsuleSize(96, 100, workingArea).Width)
            + (ManualAttachmentRules.DipToPixels(-24d, 144) - ManualAttachmentRules.DipToPixels(-24d, 96));
        var narrow = new IntRect(0, 0, 1400, 900);
        var wide = new IntRect(0, 0, 2600, 1200);
        var narrowShift = ResolveAt(narrow, attachment, workingArea, 144).X
            - ResolveAt(narrow, attachment, workingArea, 96).X;
        var wideShift = ResolveAt(wide, attachment, workingArea, 144).X
            - ResolveAt(wide, attachment, workingArea, 96).X;
        Check(
            checks,
            "DPI  crossing 100% -> 150% shifts only by the DPI scaling, with no extra jump",
            narrowShift == expectedShift,
            $"observed {narrowShift}px, explained {expectedShift}px");
        Check(
            checks,
            "DPI  that shift does not depend on the window width (not a percentage model)",
            narrowShift == wideShift,
            $"1400px window {narrowShift}px, 2600px window {wideShift}px");
    }

    private static Point ResolveAt(
        IntRect window,
        WindowAttachment attachment,
        IntRect workingArea,
        uint dpi) => ManualAttachmentCalculator.ResolveTopLeft(
        window,
        attachment,
        OverlayLayoutCalculator.GetManualCapsuleSize(dpi, 100, workingArea),
        dpi);

    private static string FirstLines(string value, int count) =>
        string.Join(" / ", value
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().TrimEnd(','))
            .Take(count));
}