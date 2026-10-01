using System.Drawing;
using System.Text.Json.Serialization;

namespace CodexStatusbar;

internal readonly record struct IntRect(int X, int Y, int Width, int Height)
{
    [JsonIgnore] public int Left => X;
    [JsonIgnore] public int Top => Y;
    [JsonIgnore] public int Right => X + Width;
    [JsonIgnore] public int Bottom => Y + Height;
    [JsonIgnore] public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(int x, int y) =>
        x >= Left && x < Right && y >= Top && y < Bottom;

    public Rectangle ToRectangle() => new(X, Y, Width, Height);

    public static IntRect FromRectangle(Rectangle value) =>
        new(value.X, value.Y, value.Width, value.Height);
}

internal readonly record struct WindowChromeMetrics(
    int CaptionButtonWidth,
    int CaptionButtonHeight,
    int FrameWidth,
    int FrameHeight,
    int PaddedBorderWidth);

internal sealed record CodexWindowInfo(
    IntPtr Handle,
    IntRect WindowBounds,
    IntRect ExtendedFrameBounds,
    IntRect? CaptionButtonBounds,
    IntRect WorkingArea,
    uint Dpi,
    WindowChromeMetrics ChromeMetrics);

internal enum OverlayVisualState { Collapsed, Expanded, HiddenForSpace }
internal enum CollapsedDisplayMode { TwoFields, PrimaryOnly }
internal enum ExpansionDirection { Down, Up }

internal sealed record OverlayLayoutRequest(
    CodexWindowInfo HostWindow,
    AnchorMode AnchorMode,
    bool RequestExpanded,
    int ExpandedRowCount,
    bool ShowContextProgress,
    Point? ManualCapsuleTopLeft = null,
    int ScalePercent = ManualAttachmentRules.DefaultScalePercent,
    Size? CapsuleSize = null,
    ComposerDockSnapshot? Dock = null,
    double ContextGapDip = OverlaySettings.DefaultContextGapDip);

internal sealed record OverlayLayoutResult(
    OverlayVisualState State,
    CollapsedDisplayMode CollapsedDisplay,
    ExpansionDirection ExpansionDirection,
    uint Dpi,
    IntRect WindowBounds,
    IntRect CapsuleBounds,
    IntRect PanelBounds,
    int ExpandedRowHeight,
    int ScalePercent = ManualAttachmentRules.DefaultScalePercent)
{
    public bool ContainsClientPoint(Point point) =>
        CapsuleBounds.Contains(point.X, point.Y) || PanelBounds.Contains(point.X, point.Y);

    public bool ContainsScreenPoint(Point point) => ContainsClientPoint(new Point(
        point.X - WindowBounds.X,
        point.Y - WindowBounds.Y));
}

internal static class OverlayLayoutCalculator
{
    // The collapsed strip carries three metrics on one line
    // (⚡ 243 tok/s   ·   5.7M tok   ·   Cache 98%), so it needs more room than the two-field
    // layouts this was forked from. OverlayPresentation.WaitingCapsuleText is the widest case.
    private const int CollapsedWidthDip = 330;
    private const int TitleBarCollapsedWidthDip = 330;
    private const int PrimaryOnlyWidthDip = 116;
    private const int CollapsedHeightDip = 34;
    private const int ExpandedWidthDip = 270;
    private const int CapsulePanelGapDip = 6;
    private const int CaptionSafetyGapDip = 8;
    private const int TitleLeftReserveDip = 160;
    private const int PanelChromeHeightDip = 122;
    private const int NormalRowHeightDip = 30;
    private const int MinimumRowHeightDip = 24;

    private const int LegacyOutsideGapDip = 10;
    private const int LegacyInsideMarginDip = 18;
    private const int LegacyHeaderOffsetDip = 56;
    private const int LegacyBottomOffsetDip = 70;

    // Fallback reserves, in DIP, for when UI Automation cannot see the Context indicator itself.
    // All four were measured on the real composer at 150% (composer right 2100, composer bottom
    // 1439, Context left 1708, row centre 1406, window right 2356, window bottom 1468) and are
    // bottom-right anchored: they describe where the toolbar's right-hand cluster sits relative to
    // a container corner, which is what survives a window resize. They are the last two rungs of
    // the ladder and exist so that a Codex update which hides its accessibility tree degrades to a
    // slightly wrong position rather than to no strip at all.
    private const double ComposerRightReserveDip = 261.3;
    private const double ComposerBottomReserveDip = 22.0;
    private const double WindowRightReserveDip = 432.0;
    private const double WindowBottomReserveDip = 41.3;

    /// <summary>
    /// How much room the composer's own left-hand control cluster takes, in DIP, when UI Automation
    /// cannot measure it. 160 DIP comfortably clears the "+" button and the permission chip.
    /// </summary>
    private const double ComposerLeftReserveDip = 160d;

    /// <summary>
    /// Padding around the strip's text, in DIP. The window rectangle is sized from the measured text
    /// so the transparent surface hugs the glyphs instead of reserving a fixed 330 DIP; the padding
    /// exists only so that ink which overhangs the measured advance (a fallback glyph, an
    /// anti-aliased edge) is never clipped.
    /// </summary>
    public const int CapsulePaddingXDip = 6;

    /// <summary>
    /// The strip's own height, in DIP. The native composer labels sit in an 18 DIP line box; 22 gives
    /// a little breathing room around it without approaching the 28 DIP row height, so the strip
    /// reads as a label rather than as a control.
    /// </summary>
    public const int CapsuleHeightDip = 22;

    public static OverlayLayoutResult Calculate(OverlayLayoutRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.HostWindow);

        if (request.Dock is { } dock && dock.Source != ComposerReferenceSource.None)
        {
            return CalculateDocked(request, dock);
        }

        if (request.ManualCapsuleTopLeft is not null)
        {
            return CalculateManual(request);
        }

        return request.AnchorMode == AnchorMode.TitleBarTopRight
            ? CalculateTitleBar(request)
            : CalculateLegacy(request);
    }

    /// <summary>The collapsed strip's size for a measured text width, in physical pixels.</summary>
    public static Size GetCapsuleSizeForText(double textWidthDip, uint dpi, int scalePercent) =>
        new(
            ScaleOverlayDip(textWidthDip + (2 * CapsulePaddingXDip), dpi, scalePercent),
            ScaleOverlayDip(CapsuleHeightDip, dpi, scalePercent));

    /// <summary>The width the docked reference reserves for the Context indicator when only the model selector
    /// can be located, in physical pixels.</summary>
    public static int GetContextReservedPixels(uint dpi) =>
        ScaleSystemDip(ManualAttachmentRules.ContextReservedDip, dpi);

    /// <summary>The host window's rectangle clipped to the working area — what is actually on screen.</summary>
    public static IntRect ResolveVisibleHost(CodexWindowInfo host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return Intersect(PreferredHostBounds(host), host.WorkingArea);
    }

    /// <summary>
    /// True when a rectangle cannot be used as a reference at all: empty, or sitting on Windows'
    /// <c>(-32000, -32000)</c> sentinel for a minimised or hidden window.
    ///
    /// <para>Without this the geometry still "works" — a minimised Codex produces a large negative
    /// budget and the strip hides — but it hides for the wrong reason and every number in the debug
    /// block is nonsense, which is worse than useless when the block is what you diagnose a Codex
    /// update with. Measured after deliberately driving the window into that state.</para>
    /// </summary>
    public static bool IsUnusableRect(IntRect rect) =>
        rect.IsEmpty || rect.X <= -30000 || rect.Y <= -30000;

    /// <summary>
    /// The x the strip's right edge is measured against, for whichever rung of the ladder produced
    /// <paramref name="dock"/>. Shared by the placement and by the responsive width budget so the two
    /// can never disagree about where the Context indicator is.
    /// </summary>
    public static int ResolveReferenceLeft(ComposerDockSnapshot dock, IntRect visibleHost, uint dpi)
    {
        ArgumentNullException.ThrowIfNull(dock);
        return dock.Source switch
        {
            ComposerReferenceSource.UiaModel =>
                dock.ReferenceRect.Left - GetContextReservedPixels(dpi),
            ComposerReferenceSource.UiaComposer =>
                dock.ComposerRect.Right - ScaleSystemDip(ComposerRightReserveDip, dpi),
            ComposerReferenceSource.Window =>
                visibleHost.Right - ScaleSystemDip(WindowRightReserveDip, dpi),
            _ => dock.ReferenceRect.Left
        };
    }

    /// <summary>The toolbar row's vertical centre, for whichever rung of the ladder produced <paramref name="dock"/>.</summary>
    public static int ResolveRowCenterY(ComposerDockSnapshot dock, IntRect visibleHost, uint dpi)
    {
        ArgumentNullException.ThrowIfNull(dock);
        return dock.Source switch
        {
            ComposerReferenceSource.UiaComposer =>
                dock.ComposerRect.Bottom - ScaleSystemDip(ComposerBottomReserveDip, dpi),
            ComposerReferenceSource.Window =>
                visibleHost.Bottom - ScaleSystemDip(WindowBottomReserveDip, dpi),
            _ => dock.RowCenterY
        };
    }

    /// <summary>
    /// The leftmost x the strip may reach without covering the composer's own left-hand controls.
    /// With UI Automation this is the real right edge of the "+" / permission cluster; without it,
    /// the composer's own left edge plus the measured reserve.
    /// </summary>
    public static int ResolveStripLeftLimit(ComposerDockSnapshot dock, IntRect visibleHost, uint dpi)
    {
        ArgumentNullException.ThrowIfNull(dock);
        if (!dock.LeftClusterRect.IsEmpty)
        {
            return dock.LeftClusterRect.Right
                + ScaleSystemDip(ManualAttachmentRules.MinimumDockGapDip, dpi);
        }

        var origin = dock.ComposerRect.IsEmpty ? visibleHost.Left : dock.ComposerRect.Left;
        return origin + ScaleSystemDip(ComposerLeftReserveDip, dpi);
    }

    public static Size GetCollapsedSize(
        uint dpi,
        int scalePercent,
        CollapsedDisplayMode display,
        Size? overrideSize = null) =>
        overrideSize is { } measured && display == CollapsedDisplayMode.TwoFields
            ? measured
            : new(
                ScaleOverlayDip(
                    display == CollapsedDisplayMode.TwoFields
                        ? CollapsedWidthDip
                        : PrimaryOnlyWidthDip,
                    dpi,
                    scalePercent),
                ScaleOverlayDip(CollapsedHeightDip, dpi, scalePercent));

    /// <summary>
    /// The size the collapsed strip will actually take for this host, including the narrow
    /// two-fields-to-one-field fallback. Callers that need to place the strip before laying it out
    /// (anchor resolution) must use this so placement and layout agree on the strip's own size.
    /// </summary>
    public static Size GetManualCapsuleSize(uint dpi, int scalePercent, IntRect workingArea)
    {
        var twoFields = GetCollapsedSize(dpi, scalePercent, CollapsedDisplayMode.TwoFields);
        return !workingArea.IsEmpty && twoFields.Width > workingArea.Width
            ? GetCollapsedSize(dpi, scalePercent, CollapsedDisplayMode.PrimaryOnly)
            : twoFields;
    }

    private static Size GetTitleBarCollapsedSize(
        uint dpi,
        int scalePercent,
        CollapsedDisplayMode display) =>
        display == CollapsedDisplayMode.TwoFields
            ? new Size(
                ScaleOverlayDip(TitleBarCollapsedWidthDip, dpi, scalePercent),
                ScaleOverlayDip(CollapsedHeightDip, dpi, scalePercent))
            : GetCollapsedSize(dpi, scalePercent, display);

    /// <summary>
    /// Docks the strip inside the composer toolbar, immediately left of the Context-usage indicator
    /// and vertically centred on the toolbar row.
    ///
    /// <para><b>Right-anchored, never a percentage.</b> The strip's right edge is placed a fixed
    /// number of DIP to the left of the reference element's left edge, and its vertical centre is a
    /// fixed number of pixels from the row's own centre. Every term is either a measured DIP constant
    /// or a live UI Automation rectangle, so a narrower window moves the strip exactly as far as the
    /// control moved and no further.</para>
    /// </summary>
    private static OverlayLayoutResult CalculateDocked(OverlayLayoutRequest request, ComposerDockSnapshot dock)
    {
        var host = request.HostWindow;
        var dpi = host.Dpi == 0 ? 96u : host.Dpi;
        var scalePercent = ManualAttachmentRules.SanitizeScale(request.ScalePercent);
        var workingArea = host.WorkingArea;
        if (workingArea.IsEmpty)
        {
            return Hidden(host.Dpi, ExpansionDirection.Down, scalePercent);
        }

        var size = request.CapsuleSize ?? GetCollapsedSize(dpi, scalePercent, CollapsedDisplayMode.TwoFields);

        // Every rung of the ladder is resolved by the shared helpers, so the position the layout
        // computes and the width budget the responsive policy computes use one definition of where
        // the reference is.
        var visibleHost = ResolveVisibleHost(host);
        if (visibleHost.IsEmpty)
        {
            return Hidden(host.Dpi, ExpansionDirection.Down, scalePercent);
        }

        var referenceLeft = ResolveReferenceLeft(dock, visibleHost, dpi);
        var rowCenterY = ResolveRowCenterY(dock, visibleHost, dpi);

        var gap = Math.Max(
            ScaleSystemDip(ManualAttachmentRules.MinimumDockGapDip, dpi),
            ScaleSystemDip(OverlaySettings.SanitizeContextGap(request.ContextGapDip), dpi));

        var desired = new IntRect(
            referenceLeft - gap - size.Width,
            rowCenterY - (size.Height / 2),
            size.Width,
            size.Height);

        var leftLimit = ResolveStripLeftLimit(dock, visibleHost, dpi);
        if (desired.Left < leftLimit)
        {
            // The strip would sit on top of the composer's own buttons. The responsive policy is
            // expected to have shortened it already; if it still does not fit, keep it out of the way
            // rather than printing text over the toolbar.
            desired = desired with { X = leftLimit };
        }

        var capsuleScreen = ClampToVisible(
            desired,
            workingArea,
            ScaleSystemDip(ManualAttachmentRules.MinimumVisibleDip, dpi));

        var collapsed = Collapsed(
            host.Dpi,
            CollapsedDisplayMode.TwoFields,
            ExpansionDirection.Down,
            capsuleScreen,
            scalePercent);
        return request.RequestExpanded
            ? TryExpandFromCollapsed(request, collapsed, workingArea, scalePercent)
            : collapsed;
    }

    private static OverlayLayoutResult CalculateManual(OverlayLayoutRequest request)
    {
        var host = request.HostWindow;
        var scalePercent = ManualAttachmentRules.SanitizeScale(request.ScalePercent);
        var workingArea = host.WorkingArea;
        if (host.Dpi == 0 || workingArea.IsEmpty)
        {
            return Hidden(host.Dpi, ExpansionDirection.Down, scalePercent);
        }

        var collapsedSize = request.CapsuleSize
            ?? GetManualCapsuleSize(host.Dpi, scalePercent, workingArea);
        var display = request.CapsuleSize is not null
            || collapsedSize.Width == ScaleOverlayDip(CollapsedWidthDip, host.Dpi, scalePercent)
                ? CollapsedDisplayMode.TwoFields
                : CollapsedDisplayMode.PrimaryOnly;

        // Deliberately no "HiddenForSpace" here. However small the user shrinks Codex, the strip is
        // clamped back into view rather than vanishing; the clamp is display-only and never persisted.
        var desiredTopLeft = request.ManualCapsuleTopLeft!.Value;
        var capsuleScreen = ClampToVisible(
            new IntRect(
                desiredTopLeft.X,
                desiredTopLeft.Y,
                collapsedSize.Width,
                collapsedSize.Height),
            workingArea,
            ScaleSystemDip(ManualAttachmentRules.MinimumVisibleDip, host.Dpi));
        var collapsed = Collapsed(
            host.Dpi,
            display,
            ExpansionDirection.Down,
            capsuleScreen,
            scalePercent);
        return request.RequestExpanded
            ? TryExpandFromCollapsed(request, collapsed, workingArea, scalePercent)
            : collapsed;
    }

    /// <summary>
    /// Attaches the expanded panel below the strip, or above it when there is no room below. Shared
    /// by the docked and manual placements so both expand identically; the caption-button clipping
    /// only ever bites when the strip happens to sit in the title bar.
    ///
    /// <para>Reads everything it needs from <paramref name="collapsed"/>, so the two placement paths
    /// cannot drift apart in how they expand.</para>
    /// </summary>
    private static OverlayLayoutResult TryExpandFromCollapsed(
        OverlayLayoutRequest request,
        OverlayLayoutResult collapsed,
        IntRect workingArea,
        int scalePercent)
    {
        var host = request.HostWindow;
        var dpi = host.Dpi == 0 ? 96u : host.Dpi;
        var capsuleScreen = collapsed.WindowBounds;
        var panelWidth = Math.Max(
            ScaleOverlayDip(ExpandedWidthDip, dpi, scalePercent),
            capsuleScreen.Width);
        if (panelWidth > workingArea.Width)
        {
            return collapsed;
        }

        var maximumCapsuleRight = workingArea.Right;
        var visibleHost = Intersect(PreferredHostBounds(host), workingArea);
        var captionSafetyGap = ScaleSystemDip(CaptionSafetyGapDip, dpi);
        if (!visibleHost.IsEmpty
            && TryGetCaptionRegion(
                host,
                visibleHost,
                captionSafetyGap,
                out var captionTop,
                out var captionBottom,
                out var captionSafeRight)
            && capsuleScreen.Top < captionBottom
            && capsuleScreen.Bottom > captionTop)
        {
            maximumCapsuleRight = Math.Min(maximumCapsuleRight, captionSafeRight);
        }

        var minimumCapsuleX = workingArea.Left + panelWidth - capsuleScreen.Width;
        var maximumCapsuleX = maximumCapsuleRight - capsuleScreen.Width;
        if (maximumCapsuleX < minimumCapsuleX)
        {
            return collapsed;
        }

        capsuleScreen = capsuleScreen with
        {
            X = Clamp(capsuleScreen.X, minimumCapsuleX, maximumCapsuleX)
        };
        var panelLeft = capsuleScreen.Right - panelWidth;
        var panelGap = ScaleOverlayDip(CapsulePanelGapDip, dpi, scalePercent);
        var availableBelow = workingArea.Bottom - capsuleScreen.Bottom - panelGap;
        if (TryGetPanelSize(
            request,
            dpi,
            scalePercent,
            availableBelow,
            out var panelHeight,
            out var rowHeight))
        {
            var panelTop = capsuleScreen.Bottom + panelGap;
            return new OverlayLayoutResult(
                OverlayVisualState.Expanded,
                collapsed.CollapsedDisplay,
                ExpansionDirection.Down,
                dpi,
                new IntRect(
                    panelLeft,
                    capsuleScreen.Top,
                    panelWidth,
                    panelTop + panelHeight - capsuleScreen.Top),
                new IntRect(
                    capsuleScreen.Left - panelLeft,
                    0,
                    capsuleScreen.Width,
                    capsuleScreen.Height),
                new IntRect(0, panelTop - capsuleScreen.Top, panelWidth, panelHeight),
                rowHeight,
                scalePercent);
        }

        var availableAbove = capsuleScreen.Top - panelGap - workingArea.Top;
        if (TryGetPanelSize(
            request,
            dpi,
            scalePercent,
            availableAbove,
            out panelHeight,
            out rowHeight))
        {
            var panelTop = capsuleScreen.Top - panelGap - panelHeight;
            return new OverlayLayoutResult(
                OverlayVisualState.Expanded,
                collapsed.CollapsedDisplay,
                ExpansionDirection.Up,
                dpi,
                new IntRect(
                    panelLeft,
                    panelTop,
                    panelWidth,
                    capsuleScreen.Bottom - panelTop),
                new IntRect(
                    capsuleScreen.Left - panelLeft,
                    panelHeight + panelGap,
                    capsuleScreen.Width,
                    capsuleScreen.Height),
                new IntRect(0, 0, panelWidth, panelHeight),
                rowHeight,
                scalePercent);
        }

        return collapsed;
    }

    private static OverlayLayoutResult CalculateTitleBar(OverlayLayoutRequest request)
    {
        var host = request.HostWindow;
        var scalePercent = ManualAttachmentRules.SanitizeScale(request.ScalePercent);
        if (host.Dpi == 0)
        {
            return Hidden(host.Dpi, ExpansionDirection.Down, scalePercent);
        }

        var workingArea = host.WorkingArea;
        var visibleHost = Intersect(PreferredHostBounds(host), workingArea);
        if (visibleHost.IsEmpty)
        {
            return Hidden(host.Dpi, ExpansionDirection.Down, scalePercent);
        }

        var safetyGap = ScaleSystemDip(CaptionSafetyGapDip, host.Dpi);
        if (!TryGetCaptionRegion(host, visibleHost, safetyGap, out var captionTop, out var captionBottom, out var safeRight))
        {
            return Hidden(host.Dpi, ExpansionDirection.Down, scalePercent);
        }

        safeRight = Math.Min(safeRight, Math.Min(visibleHost.Right, workingArea.Right));
        var titleLeft = Math.Max(visibleHost.Left, workingArea.Left) + ScaleSystemDip(TitleLeftReserveDip, host.Dpi);
        var availableWidth = safeRight - titleLeft;
        var captionHeight = captionBottom - captionTop;
        if (!TryGetLargestTitleBarFit(
            host.Dpi,
            scalePercent,
            availableWidth,
            captionHeight,
            out var effectiveScale,
            out var collapsedDisplay,
            out var capsuleSize))
        {
            return Hidden(host.Dpi, ExpansionDirection.Down, scalePercent);
        }

        var capsuleScreen = new IntRect(
            safeRight - capsuleSize.Width,
            captionTop + ((captionHeight - capsuleSize.Height) / 2),
            capsuleSize.Width,
            capsuleSize.Height);
        var collapsed = Collapsed(
            host.Dpi,
            collapsedDisplay,
            ExpansionDirection.Down,
            capsuleScreen,
            effectiveScale);
        if (!request.RequestExpanded)
        {
            return collapsed;
        }

        var panelWidth = Math.Max(
            ScaleOverlayDip(ExpandedWidthDip, host.Dpi, effectiveScale),
            capsuleSize.Width);
        var panelLeft = capsuleScreen.Right - panelWidth;
        if (panelLeft < workingArea.Left || capsuleScreen.Right > workingArea.Right)
        {
            return collapsed;
        }

        var panelTop = capsuleScreen.Bottom + ScaleOverlayDip(
            CapsulePanelGapDip,
            host.Dpi,
            effectiveScale);
        var availablePanelHeight = workingArea.Bottom - panelTop;
        if (!TryGetPanelSize(
            request,
            host.Dpi,
            effectiveScale,
            availablePanelHeight,
            out var panelHeight,
            out var rowHeight))
        {
            return collapsed;
        }

        var formBounds = new IntRect(
            panelLeft,
            capsuleScreen.Top,
            panelWidth,
            panelTop + panelHeight - capsuleScreen.Top);
        return new OverlayLayoutResult(
            OverlayVisualState.Expanded,
            collapsedDisplay,
            ExpansionDirection.Down,
            host.Dpi,
            formBounds,
            new IntRect(
                capsuleScreen.Left - panelLeft,
                0,
                capsuleSize.Width,
                capsuleSize.Height),
            new IntRect(0, panelTop - capsuleScreen.Top, panelWidth, panelHeight),
            rowHeight,
            effectiveScale);
    }

    private static bool TryGetLargestTitleBarFit(
        uint dpi,
        int requestedScale,
        int availableWidth,
        int captionHeight,
        out int effectiveScale,
        out CollapsedDisplayMode display,
        out Size capsuleSize)
    {
        requestedScale = ManualAttachmentRules.SanitizeScale(requestedScale);
        for (var candidate = requestedScale;
             candidate >= ManualAttachmentRules.MinimumScalePercent;
             candidate--)
        {
            var twoFields = GetTitleBarCollapsedSize(dpi, candidate, CollapsedDisplayMode.TwoFields);
            if (twoFields.Width <= availableWidth && twoFields.Height <= captionHeight)
            {
                effectiveScale = candidate;
                display = CollapsedDisplayMode.TwoFields;
                capsuleSize = twoFields;
                return true;
            }

            var primary = GetCollapsedSize(dpi, candidate, CollapsedDisplayMode.PrimaryOnly);
            if (primary.Width <= availableWidth && primary.Height <= captionHeight)
            {
                effectiveScale = candidate;
                display = CollapsedDisplayMode.PrimaryOnly;
                capsuleSize = primary;
                return true;
            }
        }

        effectiveScale = requestedScale;
        display = CollapsedDisplayMode.TwoFields;
        capsuleSize = Size.Empty;
        return false;
    }

    private static OverlayLayoutResult CalculateLegacy(OverlayLayoutRequest request)
    {
        var host = request.HostWindow;
        var dpi = host.Dpi == 0 ? 96u : host.Dpi;
        var scalePercent = ManualAttachmentRules.SanitizeScale(request.ScalePercent);
        var workingArea = host.WorkingArea;
        var visibleHost = Intersect(PreferredHostBounds(host), workingArea);
        if (visibleHost.IsEmpty || workingArea.IsEmpty)
        {
            return Hidden(host.Dpi, DirectionFor(request.AnchorMode), scalePercent);
        }

        var outsideGap = ScaleSystemDip(LegacyOutsideGapDip, dpi);
        var insideMargin = ScaleSystemDip(LegacyInsideMarginDip, dpi);
        var fullCapsuleSize = GetCollapsedSize(
            dpi,
            scalePercent,
            CollapsedDisplayMode.TwoFields,
            request.CapsuleSize);
        var primaryCapsuleSize = GetCollapsedSize(dpi, scalePercent, CollapsedDisplayMode.PrimaryOnly);
        var capsuleHeight = fullCapsuleSize.Height;
        var fullCapsuleWidth = fullCapsuleSize.Width;
        var primaryCapsuleWidth = primaryCapsuleSize.Width;
        var availableWidth = Math.Max(0, workingArea.Width - (2 * outsideGap));
        var collapsedDisplay = availableWidth >= fullCapsuleWidth
            ? CollapsedDisplayMode.TwoFields
            : CollapsedDisplayMode.PrimaryOnly;
        var capsuleWidth = collapsedDisplay == CollapsedDisplayMode.TwoFields
            ? fullCapsuleWidth
            : primaryCapsuleWidth;
        if (availableWidth < primaryCapsuleWidth || workingArea.Height < capsuleHeight)
        {
            return Hidden(host.Dpi, DirectionFor(request.AnchorMode), scalePercent);
        }

        var placement = request.AnchorMode;
        var chooseAutomaticDirection = placement == AnchorMode.Auto;
        var outsideRight = false;
        var outsideLeft = false;
        if (placement == AnchorMode.Auto)
        {
            var requiredWidth = request.RequestExpanded
                ? ScaleOverlayDip(ExpandedWidthDip, dpi, scalePercent)
                : capsuleWidth;
            if (workingArea.Right - visibleHost.Right >= requiredWidth + outsideGap)
            {
                outsideRight = true;
            }
            else if (visibleHost.Left - workingArea.Left >= requiredWidth + outsideGap)
            {
                outsideLeft = true;
            }
            else
            {
                placement = AnchorMode.InsideTopRight;
            }
        }

        int capsuleX;
        int capsuleY;
        if (outsideRight)
        {
            capsuleX = visibleHost.Right + outsideGap;
            capsuleY = Math.Max(
                workingArea.Top + outsideGap,
                visibleHost.Bottom - capsuleHeight - ScaleSystemDip(LegacyBottomOffsetDip, dpi));
        }
        else if (outsideLeft)
        {
            capsuleX = visibleHost.Left - capsuleWidth - outsideGap;
            capsuleY = Math.Max(
                workingArea.Top + outsideGap,
                visibleHost.Bottom - capsuleHeight - ScaleSystemDip(LegacyBottomOffsetDip, dpi));
        }
        else if (placement == AnchorMode.InsideBottomRight)
        {
            capsuleX = visibleHost.Right - capsuleWidth - insideMargin;
            capsuleY = visibleHost.Bottom - capsuleHeight - insideMargin;
        }
        else
        {
            capsuleX = visibleHost.Right - capsuleWidth - insideMargin;
            capsuleY = visibleHost.Top + ScaleSystemDip(LegacyHeaderOffsetDip, dpi);
        }

        capsuleX = Clamp(capsuleX, workingArea.Left + outsideGap, workingArea.Right - capsuleWidth - outsideGap);
        capsuleY = Clamp(capsuleY, workingArea.Top + outsideGap, workingArea.Bottom - capsuleHeight - outsideGap);
        var capsuleScreen = new IntRect(capsuleX, capsuleY, capsuleWidth, capsuleHeight);
        var direction = chooseAutomaticDirection
            ? ChooseDirection(request, capsuleScreen, workingArea, dpi, scalePercent)
            : placement == AnchorMode.InsideBottomRight
                ? ExpansionDirection.Up
                : ExpansionDirection.Down;
        var collapsed = Collapsed(
            host.Dpi,
            collapsedDisplay,
            direction,
            capsuleScreen,
            scalePercent);
        if (!request.RequestExpanded)
        {
            return collapsed;
        }

        var panelWidth = ScaleOverlayDip(ExpandedWidthDip, dpi, scalePercent);
        var panelLeft = capsuleScreen.Right - panelWidth;
        if (panelLeft < workingArea.Left || capsuleScreen.Right > workingArea.Right)
        {
            return collapsed;
        }

        var panelGap = ScaleOverlayDip(CapsulePanelGapDip, dpi, scalePercent);
        var availablePanelHeight = direction == ExpansionDirection.Down
            ? workingArea.Bottom - capsuleScreen.Bottom - panelGap
            : capsuleScreen.Top - panelGap - workingArea.Top;
        if (!TryGetPanelSize(
            request,
            dpi,
            scalePercent,
            availablePanelHeight,
            out var panelHeight,
            out var rowHeight))
        {
            return collapsed;
        }

        if (direction == ExpansionDirection.Down)
        {
            var panelTop = capsuleScreen.Bottom + panelGap;
            var formBounds = new IntRect(
                panelLeft,
                capsuleScreen.Top,
                panelWidth,
                panelTop + panelHeight - capsuleScreen.Top);
            return new OverlayLayoutResult(
                OverlayVisualState.Expanded,
                collapsedDisplay,
                direction,
                host.Dpi,
                formBounds,
                new IntRect(capsuleScreen.Left - panelLeft, 0, capsuleWidth, capsuleHeight),
                new IntRect(0, panelTop - capsuleScreen.Top, panelWidth, panelHeight),
                rowHeight,
                scalePercent);
        }
        else
        {
            var panelTop = capsuleScreen.Top - panelGap - panelHeight;
            var formBounds = new IntRect(
                panelLeft,
                panelTop,
                panelWidth,
                capsuleScreen.Bottom - panelTop);
            return new OverlayLayoutResult(
                OverlayVisualState.Expanded,
                collapsedDisplay,
                direction,
                host.Dpi,
                formBounds,
                new IntRect(capsuleScreen.Left - panelLeft, panelHeight + panelGap, capsuleWidth, capsuleHeight),
                new IntRect(0, 0, panelWidth, panelHeight),
                rowHeight,
                scalePercent);
        }
    }

    private static bool TryGetCaptionRegion(
        CodexWindowInfo host,
        IntRect visibleHost,
        int safetyGap,
        out int captionTop,
        out int captionBottom,
        out int safeRight)
    {
        if (host.CaptionButtonBounds is { } caption &&
            !caption.IsEmpty &&
            caption.Left >= visibleHost.Left &&
            caption.Left < visibleHost.Right &&
            caption.Bottom > visibleHost.Top &&
            caption.Top < visibleHost.Bottom)
        {
            captionTop = Math.Max(caption.Top, visibleHost.Top);
            captionBottom = Math.Min(caption.Bottom, visibleHost.Bottom);
            safeRight = caption.Left - safetyGap;
            return captionBottom > captionTop;
        }

        var metrics = host.ChromeMetrics;
        if (metrics.CaptionButtonWidth <= 0 ||
            metrics.CaptionButtonHeight <= 0 ||
            metrics.FrameWidth < 0 ||
            metrics.FrameHeight < 0 ||
            metrics.PaddedBorderWidth < 0)
        {
            captionTop = 0;
            captionBottom = 0;
            safeRight = 0;
            return false;
        }

        var reservedWidth =
            (3 * metrics.CaptionButtonWidth) +
            (2 * metrics.FrameWidth) +
            (2 * metrics.PaddedBorderWidth);
        var rawCaptionTop = host.WindowBounds.Top + metrics.FrameHeight + metrics.PaddedBorderWidth;
        var rawCaptionBottom = rawCaptionTop + metrics.CaptionButtonHeight;
        captionTop = Math.Max(visibleHost.Top, rawCaptionTop);
        captionBottom = Math.Min(visibleHost.Bottom, rawCaptionBottom);
        safeRight = host.WindowBounds.Right - reservedWidth - safetyGap;
        return captionBottom > captionTop && safeRight > visibleHost.Left;
    }

    private static bool TryGetPanelSize(
        OverlayLayoutRequest request,
        uint dpi,
        int scalePercent,
        int availableHeight,
        out int panelHeight,
        out int rowHeight)
    {
        var rowCount = Math.Max(0, request.ExpandedRowCount);
        var chromeHeight = ScaleOverlayDip(PanelChromeHeightDip, dpi, scalePercent);
        var normalRowHeight = ScaleOverlayDip(NormalRowHeightDip, dpi, scalePercent);
        var minimumRowHeight = ScaleOverlayDip(MinimumRowHeightDip, dpi, scalePercent);
        rowHeight = normalRowHeight;
        panelHeight = chromeHeight + (rowCount * rowHeight);
        if (panelHeight <= availableHeight)
        {
            return true;
        }

        if (rowCount == 0)
        {
            rowHeight = 0;
            panelHeight = 0;
            return false;
        }

        rowHeight = (availableHeight - chromeHeight) / rowCount;
        if (rowHeight < minimumRowHeight)
        {
            rowHeight = 0;
            panelHeight = 0;
            return false;
        }

        rowHeight = Math.Min(rowHeight, normalRowHeight);
        panelHeight = chromeHeight + (rowCount * rowHeight);
        return panelHeight <= availableHeight;
    }

    private static ExpansionDirection ChooseDirection(
        OverlayLayoutRequest request,
        IntRect capsule,
        IntRect workingArea,
        uint dpi,
        int scalePercent)
    {
        var gap = ScaleOverlayDip(CapsulePanelGapDip, dpi, scalePercent);
        var below = workingArea.Bottom - capsule.Bottom - gap;
        var above = capsule.Top - gap - workingArea.Top;
        var rowCount = Math.Max(0, request.ExpandedRowCount);
        var normalPanelHeight =
            ScaleOverlayDip(PanelChromeHeightDip, dpi, scalePercent) +
            (rowCount * ScaleOverlayDip(NormalRowHeightDip, dpi, scalePercent));
        if (below >= normalPanelHeight)
        {
            return ExpansionDirection.Down;
        }
        if (above >= normalPanelHeight)
        {
            return ExpansionDirection.Up;
        }

        var minimumPanelHeight =
            ScaleOverlayDip(PanelChromeHeightDip, dpi, scalePercent) +
            (rowCount * ScaleOverlayDip(MinimumRowHeightDip, dpi, scalePercent));
        var belowFitsMinimum = below >= minimumPanelHeight;
        var aboveFitsMinimum = above >= minimumPanelHeight;
        if (belowFitsMinimum != aboveFitsMinimum)
        {
            return belowFitsMinimum
                ? ExpansionDirection.Down
                : ExpansionDirection.Up;
        }

        return below >= above
            ? ExpansionDirection.Down
            : ExpansionDirection.Up;
    }

    private static ExpansionDirection DirectionFor(AnchorMode anchorMode) =>
        anchorMode == AnchorMode.InsideBottomRight
            ? ExpansionDirection.Up
            : ExpansionDirection.Down;

    private static OverlayLayoutResult Collapsed(
        uint dpi,
        CollapsedDisplayMode display,
        ExpansionDirection direction,
        IntRect capsuleScreen,
        int scalePercent) =>
        new(
            OverlayVisualState.Collapsed,
            display,
            direction,
            dpi,
            capsuleScreen,
            new IntRect(0, 0, capsuleScreen.Width, capsuleScreen.Height),
            default,
            0,
            ManualAttachmentRules.SanitizeScale(scalePercent));

    private static OverlayLayoutResult Hidden(
        uint dpi,
        ExpansionDirection direction,
        int scalePercent) =>
        new(
            OverlayVisualState.HiddenForSpace,
            CollapsedDisplayMode.TwoFields,
            direction,
            dpi,
            default,
            default,
            default,
            0,
            ManualAttachmentRules.SanitizeScale(scalePercent));

    private static IntRect PreferredHostBounds(CodexWindowInfo host) =>
        host.ExtendedFrameBounds.IsEmpty ? host.WindowBounds : host.ExtendedFrameBounds;

    private static IntRect Intersect(IntRect first, IntRect second)
    {
        var left = Math.Max(first.Left, second.Left);
        var top = Math.Max(first.Top, second.Top);
        var right = Math.Min(first.Right, second.Right);
        var bottom = Math.Min(first.Bottom, second.Bottom);
        return right <= left || bottom <= top
            ? default
            : new IntRect(left, top, right - left, bottom - top);
    }

    private static int Clamp(int value, int minimum, int maximum) =>
        maximum < minimum ? minimum : Math.Clamp(value, minimum, maximum);

    /// <summary>
    /// Keeps the strip on screen. <b>Display-only:</b> the result is never written back to settings,
    /// because the saved anchor + offset is what a later, larger window is resolved from. If a clamp
    /// were persisted, shrinking and re-growing Codex would move the strip for good.
    ///
    /// <para>Normally the whole strip is pushed fully inside the working area. Only when the strip is
    /// larger than the working area does it fall back to guaranteeing
    /// <paramref name="minimumVisible"/> pixels of it stay reachable.</para>
    /// </summary>
    private static IntRect ClampToVisible(IntRect desired, IntRect workingArea, int minimumVisible)
    {
        if (workingArea.IsEmpty)
        {
            return desired;
        }

        return desired with
        {
            X = ClampAxis(
                desired.X,
                desired.Width,
                workingArea.Left,
                workingArea.Width,
                minimumVisible),
            Y = ClampAxis(
                desired.Y,
                desired.Height,
                workingArea.Top,
                workingArea.Height,
                minimumVisible)
        };
    }

    private static int ClampAxis(
        int position,
        int size,
        int areaOrigin,
        int areaSize,
        int minimumVisible)
    {
        if (size <= areaSize)
        {
            return Clamp(position, areaOrigin, areaOrigin + areaSize - size);
        }

        var keep = Math.Max(1, Math.Min(minimumVisible, areaSize));
        return Clamp(
            position,
            areaOrigin + keep - size,
            areaOrigin + areaSize - keep);
    }

    private static int ScaleSystemDip(double dip, uint dpi) =>
        (int)Math.Round(dip * dpi / 96d, MidpointRounding.AwayFromZero);

    private static int ScaleOverlayDip(double dip, uint dpi, int scalePercent) =>
        (int)Math.Round(
            dip * dpi / 96d * ManualAttachmentRules.SanitizeScale(scalePercent) / 100d,
            MidpointRounding.AwayFromZero);
}

internal sealed class LayoutProbeRequest
{
    public IReadOnlyList<LayoutProbeCaseRequest> Cases { get; init; } = [];
}

internal sealed class LayoutProbeCaseRequest
{
    public string Name { get; init; } = string.Empty;
    public required LayoutProbeWindowInfo HostWindow { get; init; }
    public AnchorMode AnchorMode { get; init; }
    public bool RequestExpanded { get; init; }
    public int ExpandedRowCount { get; init; }
    public bool ShowContextProgress { get; init; }
    public LayoutProbePoint? ManualCapsuleTopLeft { get; init; }
    public int ScalePercent { get; init; } = ManualAttachmentRules.DefaultScalePercent;
    public IReadOnlyList<LayoutProbePoint> ClientPoints { get; init; } = [];
    public IReadOnlyList<LayoutProbePoint> ScreenPoints { get; init; } = [];

    public OverlayLayoutRequest ToModel() => new(
        HostWindow.ToModel(),
        AnchorMode,
        RequestExpanded,
        ExpandedRowCount,
        ShowContextProgress,
        ManualCapsuleTopLeft?.ToPoint(),
        ScalePercent);
}

internal sealed record LayoutProbePoint(int X, int Y)
{
    public Point ToPoint() => new(X, Y);
}

internal sealed class LayoutProbeWindowInfo
{
    public long Handle { get; init; }
    public IntRect WindowBounds { get; init; }
    public IntRect ExtendedFrameBounds { get; init; }
    public IntRect? CaptionButtonBounds { get; init; }
    public IntRect WorkingArea { get; init; }
    public uint Dpi { get; init; }
    public WindowChromeMetrics ChromeMetrics { get; init; }

    public CodexWindowInfo ToModel() => new(
        new IntPtr(Handle),
        WindowBounds,
        ExtendedFrameBounds,
        CaptionButtonBounds,
        WorkingArea,
        Dpi,
        ChromeMetrics);
}

internal sealed record LayoutProbeCaseResult(
    string Name,
    long Handle,
    OverlayLayoutResult Layout,
    IReadOnlyList<bool> ContainsClientPoints,
    IReadOnlyList<bool> ContainsScreenPoints);

internal sealed record LayoutProbeResult(IReadOnlyList<LayoutProbeCaseResult> Cases);

internal static class LayoutProbe
{
    public static LayoutProbeResult Execute(LayoutProbeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cases = request.Cases.Select(item =>
        {
            var model = item.ToModel();
            var layout = OverlayLayoutCalculator.Calculate(model);
            return new LayoutProbeCaseResult(
                item.Name,
                model.HostWindow.Handle.ToInt64(),
                layout,
                item.ClientPoints.Select(point => layout.ContainsClientPoint(point.ToPoint())).ToArray(),
                item.ScreenPoints.Select(point => layout.ContainsScreenPoint(point.ToPoint())).ToArray());
        }).ToArray();
        return new LayoutProbeResult(cases);
    }
}
