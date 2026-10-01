using System.Drawing;

namespace CodexStatusbar;

/// <summary>
/// The nine anchor points of the Codex window that the strip can be pinned to.
///
/// <para><b>Why <see cref="Center"/> is 8 and not 4.</b> Version 1 of the settings file persisted this
/// enum as a bare integer, and the old order was row-major over the eight frame points
/// (<c>RightCenter = 4</c>). Inserting <c>Center</c> in the middle would silently renumber every
/// anchor that was already saved and move a user's strip to a different corner. Appending it keeps
/// every previously persisted value meaning exactly what it meant before.</para>
/// </summary>
internal enum AttachmentReferencePoint
{
    TopLeft = 0,
    TopCenter = 1,
    TopRight = 2,
    LeftCenter = 3,
    RightCenter = 4,
    BottomLeft = 5,
    BottomCenter = 6,
    BottomRight = 7,

    /// <summary>Added in settings v2. Appended deliberately — see the remarks on this type.</summary>
    Center = 8
}

/// <summary>
/// Column and row of an anchor inside a rectangle, each in <c>{-1, 0, +1}</c>.
/// The strip's anchor point is placed at the window's anchor point plus the offset.
/// </summary>
internal readonly record struct AnchorVector(int Column, int Row)
{
    public static AnchorVector For(AttachmentReferencePoint point) => point switch
    {
        AttachmentReferencePoint.TopLeft => new AnchorVector(-1, -1),
        AttachmentReferencePoint.TopCenter => new AnchorVector(0, -1),
        AttachmentReferencePoint.TopRight => new AnchorVector(+1, -1),
        AttachmentReferencePoint.LeftCenter => new AnchorVector(-1, 0),
        AttachmentReferencePoint.Center => new AnchorVector(0, 0),
        AttachmentReferencePoint.RightCenter => new AnchorVector(+1, 0),
        AttachmentReferencePoint.BottomLeft => new AnchorVector(-1, +1),
        AttachmentReferencePoint.BottomCenter => new AnchorVector(0, +1),
        AttachmentReferencePoint.BottomRight => new AnchorVector(+1, +1),
        _ => throw new ArgumentOutOfRangeException(nameof(point), point, "未知的锚点。")
    };

    public AttachmentReferencePoint ToPoint() => (Column, Row) switch
    {
        (-1, -1) => AttachmentReferencePoint.TopLeft,
        (0, -1) => AttachmentReferencePoint.TopCenter,
        (+1, -1) => AttachmentReferencePoint.TopRight,
        (-1, 0) => AttachmentReferencePoint.LeftCenter,
        (0, 0) => AttachmentReferencePoint.Center,
        (+1, 0) => AttachmentReferencePoint.RightCenter,
        (-1, +1) => AttachmentReferencePoint.BottomLeft,
        (0, +1) => AttachmentReferencePoint.BottomCenter,
        (+1, +1) => AttachmentReferencePoint.BottomRight,
        _ => throw new ArgumentOutOfRangeException(nameof(Column), this, "未知的锚点向量。")
    };

    /// <summary>The point this vector names inside <paramref name="rect"/>.</summary>
    public Point Resolve(IntRect rect) => new(
        ResolveAxis(rect.Left, rect.Width, Column),
        ResolveAxis(rect.Top, rect.Height, Row));

    /// <summary>
    /// How far this vector's point sits from the rectangle's top-left corner. Used to turn a desired
    /// anchor point back into the rectangle's top-left corner.
    /// </summary>
    public Point OffsetInside(Size size) => new(
        OffsetAxis(size.Width, Column),
        OffsetAxis(size.Height, Row));

    private static int ResolveAxis(int origin, int length, int direction) => direction switch
    {
        < 0 => origin,
        > 0 => origin + length,
        _ => origin + (length / 2)
    };

    private static int OffsetAxis(int length, int direction) => direction switch
    {
        < 0 => 0,
        > 0 => length,
        _ => length / 2
    };
}

/// <summary>
/// A saved position, in device-independent pixels:
/// <c>stripAnchorPoint - windowAnchorPoint</c>.
///
/// <para>It is captured <b>once</b>, when the user locks the strip, and from then on only ever read.
/// Every move / resize / maximize / restore re-evaluates <c>windowAnchorPoint + offset</c>; nothing
/// ever rewrites the offset from a freshly observed position, so repeated resizes cannot accumulate
/// drift.</para>
/// </summary>
internal sealed record WindowAttachment(
    AttachmentReferencePoint ReferencePoint,
    double OffsetXDip,
    double OffsetYDip);

internal sealed record ManualPlacementSnapshot(
    bool Enabled,
    WindowAttachment MainAttachment,
    int ScalePercent);

internal sealed record AttachmentTargetBounds(
    long MainHandle,
    IntRect MainBounds,
    IntRect WorkingArea,
    uint Dpi);

internal sealed record AttachmentTargetHit(
    long Handle,
    IntRect Bounds);

internal static class ManualAttachmentRules
{
    public const int MinimumScalePercent = 60;
    public const int MaximumScalePercent = 130;
    public const int DefaultScalePercent = 100;
    public const double MaximumAbsoluteOffsetDip = 4096d;

    /// <summary>
    /// Default: top-centre, 10 DIP below the window's top edge — a small, unobtrusive margin.
    /// The offset is measured to the strip's own top edge (see <see cref="WindowAttachment"/>), so
    /// <c>OffsetY = 10</c> means literally "10 DIP from the top", independent of the strip's height.
    /// </summary>
    public static readonly WindowAttachment DefaultMainAttachment =
        new(AttachmentReferencePoint.TopCenter, 0d, 10d);

    /// <summary>
    /// The pre-v2 default (<c>TopRight, -344, 24</c>). Recognised during migration so that a settings
    /// file the user never customised adopts the new default instead of keeping a stale one, while a
    /// file the user <i>did</i> customise keeps their position untouched.
    /// </summary>
    public static readonly WindowAttachment LegacyDefaultMainAttachment =
        new(AttachmentReferencePoint.TopRight, -344d, 24d);

    /// <summary>Always keep at least this much of the strip on screen when it cannot fit outright.</summary>
    public const int MinimumVisibleDip = 12;

    /// <summary>
    /// Distance from the Context indicator's left edge to the model selector's left edge. Measured
    /// live on the real composer (30 physical px at 150%) and stable across every window geometry
    /// tested, because both controls sit in the same right-hand cluster. It is only used by the
    /// fallback that docks against the model selector when the Context indicator itself is not
    /// exposed by UI Automation.
    /// </summary>
    public const double ContextReservedDip = 20d;

    /// <summary>The smallest gap that still reads as a gap rather than a collision.</summary>
    public const double MinimumDockGapDip = 4d;

    /// <summary>The inward margin used when an anchor is chosen from the tray menu rather than dragged.</summary>
    public const int MenuAnchorMarginDip = 12;

    public const int MenuAnchorTopMarginDip = 10;

    /// <summary>
    /// A sensible offset for a menu-chosen anchor: a small inward margin so the strip always lands
    /// inside the window, whatever corner was picked.
    /// </summary>
    public static WindowAttachment OffsetForAnchor(AttachmentReferencePoint anchor)
    {
        var vector = AnchorVector.For(anchor);
        return new WindowAttachment(
            anchor,
            vector.Column switch { < 0 => MenuAnchorMarginDip, > 0 => -MenuAnchorMarginDip, _ => 0d },
            vector.Row switch { < 0 => MenuAnchorTopMarginDip, > 0 => -MenuAnchorMarginDip, _ => 0d });
    }

    public static int SanitizeScale(int? value) =>
        Math.Clamp(value ?? DefaultScalePercent, MinimumScalePercent, MaximumScalePercent);

    public static WindowAttachment SanitizeMain(WindowAttachment? value) =>
        TrySanitize(value, out var result) ? result : DefaultMainAttachment;

    public static bool TrySanitize(WindowAttachment? value, out WindowAttachment result)
    {
        if (value is not null
            && Enum.IsDefined(value.ReferencePoint)
            && double.IsFinite(value.OffsetXDip)
            && double.IsFinite(value.OffsetYDip)
            && Math.Abs(value.OffsetXDip) <= MaximumAbsoluteOffsetDip
            && Math.Abs(value.OffsetYDip) <= MaximumAbsoluteOffsetDip)
        {
            result = new WindowAttachment(
                value.ReferencePoint,
                value.OffsetXDip,
                value.OffsetYDip);
            return true;
        }

        result = null!;
        return false;
    }

    public static int DipToPixels(double dip, uint dpi) =>
        (int)Math.Round(dip * dpi / 96d, MidpointRounding.AwayFromZero);

    public static double PixelsToDip(int pixels, uint dpi) => pixels * 96d / dpi;
}

internal static class ManualAttachmentCalculator
{
    /// <summary>
    /// Picks the anchor from the strip's 3x3 region inside the window: each axis is split into
    /// thirds, so "left / centre / right" and "top / centre / bottom" are decided independently.
    /// Called exactly once per lock (and once per resize gesture), never on a plain relayout.
    /// </summary>
    public static AttachmentReferencePoint SelectReferencePoint(IntRect target, IntRect capsule)
    {
        ValidateTarget(target);
        if (capsule.Width <= 0 || capsule.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capsule));
        }

        var center = new Point(
            capsule.Left + (capsule.Width / 2),
            capsule.Top + (capsule.Height / 2));
        var column = ThirdOf(center.X, target.Left, target.Width);
        var row = ThirdOf(center.Y, target.Top, target.Height);
        return new AnchorVector(column, row).ToPoint();
    }

    /// <summary>Turns a placed strip into a saved anchor + offset. The only writer of an offset.</summary>
    public static WindowAttachment Capture(IntRect target, IntRect capsule, uint dpi)
    {
        ValidateTargetAndDpi(target, dpi);
        if (capsule.Width <= 0 || capsule.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capsule));
        }

        var referencePoint = SelectReferencePoint(target, capsule);
        var vector = AnchorVector.For(referencePoint);
        var windowAnchor = vector.Resolve(target);
        var stripAnchor = vector.Resolve(capsule);
        return new WindowAttachment(
            referencePoint,
            ManualAttachmentRules.PixelsToDip(stripAnchor.X - windowAnchor.X, dpi),
            ManualAttachmentRules.PixelsToDip(stripAnchor.Y - windowAnchor.Y, dpi));
    }

    /// <summary>
    /// The saved anchor point of the window, in physical pixels, for the host's current DPI.
    /// This is the only input a relayout needs besides the stored offset.
    /// </summary>
    public static Point ResolveAnchorPoint(IntRect target, WindowAttachment attachment, uint dpi)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ValidateTargetAndDpi(target, dpi);
        var windowAnchor = AnchorVector.For(attachment.ReferencePoint).Resolve(target);
        return new Point(
            checked(windowAnchor.X + ManualAttachmentRules.DipToPixels(attachment.OffsetXDip, dpi)),
            checked(windowAnchor.Y + ManualAttachmentRules.DipToPixels(attachment.OffsetYDip, dpi)));
    }

    /// <summary>
    /// <c>windowAnchorPoint + offset</c>, then back to a top-left corner for the given strip size.
    /// Pure function of the stored attachment: it never writes back, so clamping downstream cannot
    /// contaminate the saved position.
    /// </summary>
    public static Point ResolveTopLeft(
        IntRect target,
        WindowAttachment attachment,
        Size capsuleSize,
        uint dpi)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        if (capsuleSize.Width <= 0 || capsuleSize.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capsuleSize));
        }

        var anchorPoint = ResolveAnchorPoint(target, attachment, dpi);
        var inside = AnchorVector.For(attachment.ReferencePoint).OffsetInside(capsuleSize);
        return new Point(
            checked(anchorPoint.X - inside.X),
            checked(anchorPoint.Y - inside.Y));
    }

    public static AttachmentTargetHit? SelectTarget(
        AttachmentTargetBounds targets,
        Point cursor,
        bool hostSurfaceHit)
    {
        return hostSurfaceHit && !targets.MainBounds.IsEmpty && targets.MainBounds.Contains(cursor.X, cursor.Y)
            ? new AttachmentTargetHit(
                targets.MainHandle,
                targets.MainBounds)
            : null;
    }

    public static int CalculateScale(
        Size startSize,
        int startScalePercent,
        int deltaX,
        int deltaY)
    {
        var sanitizedStartScale = ManualAttachmentRules.SanitizeScale(startScalePercent);
        if (startSize.Width <= 0 || startSize.Height <= 0)
        {
            return sanitizedStartScale;
        }

        var widthRatio = Math.Max(0d, ((double)startSize.Width + deltaX) / startSize.Width);
        var heightRatio = Math.Max(0d, ((double)startSize.Height + deltaY) / startSize.Height);
        var desiredScale = (int)Math.Round(
            sanitizedStartScale * Math.Max(widthRatio, heightRatio),
            MidpointRounding.AwayFromZero);
        return ManualAttachmentRules.SanitizeScale(desiredScale);
    }

    /// <summary>-1 for the first third, 0 for the middle third, +1 for the last third.</summary>
    private static int ThirdOf(int value, int origin, int length)
    {
        if (length <= 0)
        {
            return 0;
        }

        var ratio = (value - origin) / (double)length;
        if (ratio < 1d / 3d)
        {
            return -1;
        }

        return ratio > 2d / 3d ? +1 : 0;
    }

    private static void ValidateTargetAndDpi(IntRect target, uint dpi)
    {
        ValidateTarget(target);
        if (dpi == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dpi));
        }
    }

    private static void ValidateTarget(IntRect target)
    {
        if (target.IsEmpty)
        {
            throw new ArgumentOutOfRangeException(nameof(target));
        }
    }
}

internal sealed class ManualPlacementEditState
{
    private ManualPlacementSnapshot? _original;
    private ManualPlacementSnapshot? _draft;

    public bool IsActive => _draft is not null;

    public ManualPlacementSnapshot Draft =>
        _draft ?? throw new InvalidOperationException("手动定位编辑尚未开始。");

    public void Begin(ManualPlacementSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (IsActive)
        {
            throw new InvalidOperationException("手动定位编辑已经开始。");
        }

        _original = snapshot;
        _draft = snapshot with { };
    }

    public void ApplyAttachment(WindowAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        _draft = Draft with { MainAttachment = attachment };
    }

    public void ApplyScale(int scalePercent)
    {
        _draft = Draft with
        {
            ScalePercent = ManualAttachmentRules.SanitizeScale(scalePercent)
        };
    }

    public void ApplyEnabled(bool enabled)
    {
        _draft = Draft with { Enabled = enabled };
    }

    public ManualPlacementSnapshot Commit()
    {
        var committed = Draft;
        End();
        return committed;
    }

    public ManualPlacementSnapshot Cancel()
    {
        _ = Draft;
        var original = _original
            ?? throw new InvalidOperationException("手动定位编辑尚未开始。");
        End();
        return original;
    }

    private void End()
    {
        _original = null;
        _draft = null;
    }
}

internal sealed record ManualAttachmentTransition(
    ManualPlacementSnapshot Draft,
    bool IsEditing,
    bool CanSave,
    bool RequiresPersist,
    bool ShouldCollapse,
    IntRect? HighlightBounds,
    Point? ResolvedTopLeft);

internal sealed class ManualAttachmentCoordinator
{
    private readonly ManualPlacementEditState _editState = new();
    private bool _canSave;
    private bool _gesturePreviewActive;

    public bool IsEditing => _editState.IsActive;

    public ManualPlacementSnapshot Draft => _editState.Draft;

    public bool CanSave => IsEditing && _canSave;

    public bool ShouldApplyStaticDraft => IsEditing && !_gesturePreviewActive;

    public bool ShouldShowStaticHighlight => ShouldApplyStaticDraft && CanSave;

    public ManualAttachmentTransition BeginEdit(
        ManualPlacementSnapshot original,
        AttachmentTargetBounds targets,
        Size capsuleSize)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(targets);
        _gesturePreviewActive = false;
        _editState.Begin(SanitizeSnapshot(original));
        _editState.ApplyEnabled(true);
        var target = ResolveMainTarget(targets);
        _canSave = target is not null;
        return Transition(
            _editState.Draft,
            requiresPersist: false,
            shouldCollapse: true,
            target?.Bounds,
            ResolveTopLeft(_editState.Draft, targets, capsuleSize));
    }

    public void BeginGesturePreview()
    {
        _ = Draft;
        _gesturePreviewActive = true;
    }

    public void EndGesturePreview()
    {
        _ = Draft;
        _gesturePreviewActive = false;
    }

    /// <summary>
    /// While the pointer is over the Codex window the strip simply follows the cursor; the saved
    /// offset is only recomputed on <see cref="CompleteMove"/>, so a drag cannot leak positions.
    /// </summary>
    public ManualAttachmentTransition PreviewMove(
        AttachmentTargetBounds targets,
        Point cursor,
        IntRect capsuleScreen,
        bool hostSurfaceHit,
        Size capsuleSize)
    {
        _ = Draft;
        var hit = ManualAttachmentCalculator.SelectTarget(targets, cursor, hostSurfaceHit);
        _canSave = hit is not null;
        return Transition(
            Draft,
            requiresPersist: false,
            shouldCollapse: true,
            hit?.Bounds,
            hit is null
                ? ResolveTopLeft(Draft, targets, capsuleSize)
                : new Point(capsuleScreen.X, capsuleScreen.Y));
    }

    public ManualAttachmentTransition CompleteMove(
        AttachmentTargetBounds targets,
        Point cursor,
        IntRect capsuleScreen,
        bool hostSurfaceHit,
        Size capsuleSize)
    {
        _ = Draft;
        var hit = ManualAttachmentCalculator.SelectTarget(targets, cursor, hostSurfaceHit);
        if (hit is null)
        {
            _canSave = false;
            return Transition(
                Draft,
                requiresPersist: false,
                shouldCollapse: true,
                highlightBounds: null,
                ResolveTopLeft(Draft, targets, capsuleSize));
        }

        _editState.ApplyAttachment(
            ManualAttachmentCalculator.Capture(hit.Bounds, capsuleScreen, targets.Dpi));
        _editState.ApplyEnabled(true);
        _canSave = true;
        return Transition(
            Draft,
            requiresPersist: false,
            shouldCollapse: true,
            hit.Bounds,
            ManualAttachmentCalculator.ResolveTopLeft(
                hit.Bounds,
                Draft.MainAttachment,
                capsuleSize,
                targets.Dpi));
    }

    public ManualAttachmentTransition PreviewResize(
        AttachmentTargetBounds targets,
        Point fixedTopLeft,
        int scalePercent,
        CollapsedDisplayMode display)
    {
        var draft = Draft;
        var target = ResolveMainTarget(targets);
        if (target is null || targets.Dpi == 0)
        {
            _canSave = false;
            return Transition(
                draft,
                requiresPersist: false,
                shouldCollapse: true,
                highlightBounds: null,
                resolvedTopLeft: null);
        }

        var sanitizedScale = ManualAttachmentRules.SanitizeScale(scalePercent);
        var size = OverlayLayoutCalculator.GetCollapsedSize(
            targets.Dpi,
            sanitizedScale,
            display);
        var capsule = new IntRect(fixedTopLeft.X, fixedTopLeft.Y, size.Width, size.Height);
        _editState.ApplyScale(sanitizedScale);
        _editState.ApplyAttachment(
            ManualAttachmentCalculator.Capture(target.Bounds, capsule, targets.Dpi));
        _editState.ApplyEnabled(true);
        _canSave = true;
        return Transition(
            Draft,
            requiresPersist: false,
            shouldCollapse: true,
            target.Bounds,
            fixedTopLeft);
    }

    public ManualAttachmentTransition Commit()
    {
        if (!CanSave)
        {
            throw new InvalidOperationException("当前手势没有有效的 Codex 吸附目标。");
        }

        var committed = _editState.Commit() with { Enabled = true };
        _canSave = false;
        _gesturePreviewActive = false;
        return Transition(
            committed,
            requiresPersist: true,
            shouldCollapse: true,
            highlightBounds: null,
            resolvedTopLeft: null);
    }

    public ManualAttachmentTransition Cancel()
    {
        var cancelled = _editState.Cancel();
        _canSave = false;
        _gesturePreviewActive = false;
        return Transition(
            cancelled,
            requiresPersist: false,
            shouldCollapse: true,
            highlightBounds: null,
            resolvedTopLeft: null);
    }

    public static Point? ResolveTopLeft(
        ManualPlacementSnapshot snapshot,
        AttachmentTargetBounds targets,
        Size capsuleSize)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(targets);
        var target = ResolveMainTarget(targets);
        if (target is null || targets.Dpi == 0)
        {
            return null;
        }

        return ManualAttachmentCalculator.ResolveTopLeft(
            target.Bounds,
            snapshot.MainAttachment,
            capsuleSize,
            targets.Dpi);
    }

    private ManualAttachmentTransition Transition(
        ManualPlacementSnapshot snapshot,
        bool requiresPersist,
        bool shouldCollapse,
        IntRect? highlightBounds,
        Point? resolvedTopLeft) => new(
            snapshot,
            IsEditing,
            CanSave,
            requiresPersist,
            shouldCollapse,
            highlightBounds,
            resolvedTopLeft);

    private static ManualPlacementSnapshot SanitizeSnapshot(ManualPlacementSnapshot snapshot)
    {
        return new ManualPlacementSnapshot(
            snapshot.Enabled,
            ManualAttachmentRules.SanitizeMain(snapshot.MainAttachment),
            ManualAttachmentRules.SanitizeScale(snapshot.ScalePercent));
    }

    private static AttachmentTargetHit? ResolveMainTarget(AttachmentTargetBounds targets) =>
        !targets.MainBounds.IsEmpty
            ? new AttachmentTargetHit(targets.MainHandle, targets.MainBounds)
            : null;
}