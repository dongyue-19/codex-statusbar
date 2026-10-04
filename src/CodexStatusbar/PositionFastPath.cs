namespace CodexStatusbar;

/// <summary>
/// Which channel the position pipeline is currently running on.
///
/// <para>The point of the split is that "follow the host window quickly" and "know the composer's
/// exact layout" have completely different prices. Cheap <c>GetWindowRect</c>/<c>SetWindowPos</c>
/// geometry can run at frame rate; a cross-process UI Automation read cannot. So the cheap channel
/// carries the motion and the expensive one only calibrates.</para>
/// </summary>
internal enum PositionUpdateMode
{
    /// <summary>Nothing is moving. The accurate path's own cadence is enough and nothing polls faster.</summary>
    Idle = 0,

    /// <summary>
    /// The host window is moving or being resized right now (the spec's TRACKING_BURST). Cheap geometry
    /// runs at frame rate here; the accurate path is asked to refresh at <see cref="PositionFastPathRules.AccurateIntervalMs"/>.
    /// </summary>
    Burst = 1,

    /// <summary>
    /// Motion stopped less than <see cref="PositionFastPathRules.BurstTailMs"/> ago. One forced full
    /// accurate resync is requested, and then the pipeline goes back to sleep.
    /// </summary>
    Settling = 2
}

/// <summary>What the fast path decided to do this frame.</summary>
internal enum FastPathAction
{
    /// <summary>The host rect did not move, or the write was folded into a later frame.</summary>
    None = 0,

    /// <summary>The host window translated. Only the window position changes — no layout, no repaint.</summary>
    MoveOnly = 1,

    /// <summary>Size, DPI or monitor changed: the placement has to be recomputed.</summary>
    Relayout = 2,

    /// <summary>The host reported <c>(-32000,-32000)</c> or is otherwise unusable: hide.</summary>
    HideForUnusableHost = 3
}

/// <summary>A decision, plus the numbers behind it so the log can explain it without re-deriving.</summary>
internal readonly record struct FastPathPlan(
    FastPathAction Action,
    IntRect WindowBounds,
    int ShiftX,
    int ShiftY,
    string Reason)
{
    public static readonly FastPathPlan None = new(FastPathAction.None, default, 0, 0, "host rect unchanged");
    public static readonly FastPathPlan Hide = new(FastPathAction.HideForUnusableHost, default, 0, 0, "host rect unusable");
}

/// <summary>
/// The host window as sampled by cheap Win32 geometry only. This is deliberately not
/// <see cref="CodexWindowInfo"/>: everything the layout needs that costs more than a couple of
/// syscalls (caption buttons, chrome metrics) is read separately, and only when a full recompute is
/// actually required.
/// </summary>
internal readonly record struct HostGeometrySample(
    IntRect PreviousBounds,
    IntRect CurrentBounds,
    uint PreviousDpi,
    uint CurrentDpi,
    long PreviousMonitor,
    long CurrentMonitor,
    IntRect WorkingArea,
    bool Usable)
{
    public bool ClientSizeChanged =>
        PreviousBounds.Width != CurrentBounds.Width || PreviousBounds.Height != CurrentBounds.Height;

    public bool DpiChanged => PreviousDpi != CurrentDpi;

    public bool MonitorChanged => PreviousMonitor != CurrentMonitor;

    /// <summary>True when the whole host rectangle is inside the monitor's working area.</summary>
    public bool FullyVisible => PositionFastPathRules.IsInside(CurrentBounds, WorkingArea);

    /// <summary>
    /// True when the placement can no longer be produced by translating the old one.
    ///
    /// <para>The third case is the one that is easy to miss and was measured on the real window: when a
    /// window is pushed partly off the screen, Codex's own composer stops translating rigidly with the
    /// window rectangle — the row was observed moving 39 px for a 63 px window move — and the layout's
    /// <c>ClampToVisible</c> has to run for the strip to stay on screen. A translation answers neither,
    /// so a host that is not fully inside the working area is treated exactly like a resize: recompute,
    /// and let the accurate path run at the faster cadence until it is back on screen.</para>
    /// </summary>
    public bool RequiresRelayout =>
        ClientSizeChanged || DpiChanged || MonitorChanged || !FullyVisible;
}

/// <summary>
/// The whole decision surface of the fast path, as pure functions. Every rule here is asserted by the
/// position section of <c>--self-test</c> with synthetic rectangles, because a rule that needs a real
/// Codex window to be exercised is a rule that never gets exercised.
/// </summary>
internal static class PositionFastPathRules
{
    /// <summary>
    /// How often cheap geometry is allowed to write while the host is moving. 16 ms is the frame time
    /// the spec asks for ("≤ 16–33 ms"), and one <c>SetWindowPos</c> is tens of microseconds, so this
    /// is bounded work rather than a busy loop.
    /// </summary>
    public const int FastIntervalMs = 16;

    /// <summary>
    /// A pure translation is so cheap (one <c>GetWindowRect</c> plus one <c>SetWindowPos</c> that does
    /// not even touch the surface) that it is allowed to run faster than a frame. 8 ms ≈ 125 Hz keeps
    /// a drag glued to the mouse even when the compositor reports moves faster than the display.
    /// </summary>
    public const int MoveIntervalMs = 8;

    /// <summary>
    /// UIA refresh rate during a burst. Cross-process, so deliberately below the geometry rate: the
    /// geometry carries the motion and this only stops the calibration from drifting during a long
    /// resize. Matches the spec's "33~50 ms".
    /// </summary>
    public const int AccurateIntervalMs = 40;

    /// <summary>The accurate path's own cadence when nothing is moving. Unchanged from the original design.</summary>
    public const int IdleAccurateIntervalMs = 250;

    /// <summary>Fast-path interval while settling: slower than a burst, faster than idle.</summary>
    public const int SettlingIntervalMs = 33;

    /// <summary>How long the burst keeps running after the last geometry event.</summary>
    public const int BurstTailMs = 300;

    /// <summary>How long settling lasts before the pipeline releases the timer entirely.</summary>
    public const int SettlingTailMs = 250;

    /// <summary>
    /// The overlay's own idle cadence, unchanged: the lifecycle timer still drives the metric,
    /// theme and menu work at its original rate, so an idle machine costs what it cost before.
    /// </summary>
    public const int IdleTickMs = 350;

    /// <summary>Tolerance for "the position is already right", in physical pixels.</summary>
    public const int RoundingTolerancePx = 1;

    /// <summary>Which timer interval the burst/settling scheduler wants while in <paramref name="mode"/>.</summary>
    public static int BurstIntervalFor(PositionUpdateMode mode) => mode switch
    {
        PositionUpdateMode.Burst => FastIntervalMs,
        PositionUpdateMode.Settling => SettlingIntervalMs,
        _ => IdleTickMs
    };

    /// <summary>
    /// How often the accurate path should re-read while in <paramref name="mode"/>.
    ///
    /// <para>Note the first case, which is the one that keeps this change honest: a burst that is only a
    /// translation keeps the accurate path at its original 250 ms. The geometry translation is not an
    /// approximation for a move — it is exact — so raising the cross-process read rate there would buy
    /// nothing and cost CPU. Only a shape change raises it.</para>
    ///
    /// <para><b>Why <paramref name="hasCheapReReadPath"/> gates it.</b> When the accessibility ladder has
    /// degraded to the window rung, the tracker has nothing to re-read cheaply and every poll becomes a
    /// full traversal. Speeding it up there would turn one drag into ~25 tree walks per second — measured
    /// at 8 walks in a single burst before this gate existed. A degraded ladder therefore always keeps
    /// the slow cadence, where a walk is the safety net rather than the main path.</para>
    /// </summary>
    public static int AccurateIntervalFor(
        PositionUpdateMode mode,
        bool shapeChangeInBurst,
        bool hasCheapReReadPath = true)
    {
        if (!hasCheapReReadPath)
        {
            return IdleAccurateIntervalMs;
        }

        return mode switch
        {
            PositionUpdateMode.Burst when !shapeChangeInBurst => IdleAccurateIntervalMs,
            PositionUpdateMode.Burst => AccurateIntervalMs,
            PositionUpdateMode.Settling => AccurateIntervalMs,
            _ => IdleAccurateIntervalMs
        };
    }

    /// <summary>Windows reports a minimised or hidden window at this sentinel instead of a real rect.</summary>
    public static bool IsUsableHostRect(IntRect bounds) =>
        bounds.Width > 0
        && bounds.Height > 0
        && bounds.X > -30000
        && bounds.Y > -30000;

    public static bool IsInside(IntRect inner, IntRect outer) =>
        outer.IsEmpty
        || (inner.Left >= outer.Left
            && inner.Top >= outer.Top
            && inner.Right <= outer.Right
            && inner.Bottom <= outer.Bottom);

    /// <summary>Translate <paramref name="bounds"/> by the host's own delta.</summary>
    public static IntRect Translate(IntRect bounds, int dx, int dy) =>
        new(bounds.X + dx, bounds.Y + dy, bounds.Width, bounds.Height);

    /// <summary>
    /// Re-anchor a rectangle that was measured against <paramref name="fromHost"/> onto
    /// <paramref name="toHost"/>.
    ///
    /// <para>This is bottom-right anchored on purpose. Everything the strip is placed against — the
    /// Context indicator, the composer's right-hand cluster — sits in the window's bottom-right
    /// corner, and that corner is what survives a resize that drags the left or top edge. A pure
    /// translation is therefore exact; a resize is a first-order prediction that the accurate path
    /// corrects within one burst frame.</para>
    /// </summary>
    public static IntRect ReAnchor(IntRect bounds, IntRect fromHost, IntRect toHost) =>
        Translate(bounds, toHost.Right - fromHost.Right, toHost.Bottom - fromHost.Bottom);

    /// <summary>
    /// The decision. Pure, total, and cheap: no window calls, no allocation.
    /// </summary>
    public static FastPathPlan Plan(in HostGeometrySample host, IntRect currentWindowBounds)
    {
        if (!host.Usable)
        {
            return FastPathPlan.Hide;
        }

        // Note the order: "nothing to do" is only true when the rectangle *and* the DPI *and* the
        // monitor are all unchanged. A window dragged between two displays of the same resolution keeps
        // its rectangle exactly and still has to be re-measured, because every DIP-to-pixel conversion
        // in the placement depends on the DPI.
        if (!host.RequiresRelayout && host.CurrentBounds == host.PreviousBounds)
        {
            return FastPathPlan.None;
        }

        var dx = host.CurrentBounds.X - host.PreviousBounds.X;
        var dy = host.CurrentBounds.Y - host.PreviousBounds.Y;

        // §5: the whole window moved. No UIA, no layout, no repaint — translate and be done.
        if (!host.RequiresRelayout)
        {
            var translated = Translate(currentWindowBounds, dx, dy);

            // The only case a translation cannot answer: the strip would leave the monitor's working
            // area (dragged to the screen edge or to another display). That needs the clamp, and the
            // clamp lives in the layout calculator, so fall through to a recompute.
            return IsInside(translated, host.WorkingArea)
                ? new FastPathPlan(FastPathAction.MoveOnly, translated, dx, dy, "pure move")
                : new FastPathPlan(
                    FastPathAction.Relayout,
                    default,
                    dx,
                    dy,
                    "move leaves the working area");
        }

        var reason = host.ClientSizeChanged
            ? "host resized"
            : host.DpiChanged
                ? "dpi changed"
                : host.MonitorChanged
                    ? "monitor changed"
                    : "host is not fully on screen";
        return new FastPathPlan(FastPathAction.Relayout, default, dx, dy, reason);
    }
}

/// <summary>
/// One reporting window's worth of position counters. Deltas, not cumulative totals: the question the
/// block answers is "what did that drag cost", and a cumulative total cannot answer it twice.
/// </summary>
internal readonly record struct PositionCounters(
    long WinEvents,
    long FastUpdates,
    long MoveOnlyWrites,
    long RelayoutWrites,
    long HideWrites,
    long CoalescedEvents,
    long RateLimitedFrames,
    long SkippedUnchanged,
    long DpiChanges,
    long MonitorChanges,
    long SizeChanges,
    long SettleResyncs,
    long PositionSamples,
    long OffscreenChanges);
/// <summary>What <see cref="PositionScheduler.Advance"/> concluded about the tail of a burst.</summary>
internal enum SettleTransition
{
    None = 0,

    /// <summary>Motion stopped: ask the accurate path for one full resync now.</summary>
    EnterSettling = 1,

    /// <summary>Settling finished: release the fast timer and go back to the idle cadence.</summary>
    Settled = 2
}

/// <summary>
/// The adaptive burst state machine, plus every counter the <c>[position-performance]</c> block reports.
///
/// <para>It is separated from the overlay and driven by an explicit timestamp so the whole
/// idle → burst → settling → idle cycle, including its 300 ms tail, is asserted deterministically in
/// <c>--self-test</c> with a synthetic clock instead of by sleeping.</para>
/// </summary>
internal sealed class PositionScheduler
{
    private long _notes;

    private PositionCounters _reported;

    /// <summary>
    /// The counters accumulated since the previous call, and resets the reporting window. Every field is
    /// a difference of two monotonic counters, so a reporting window can never double-count.
    /// </summary>
    public PositionCounters TakeDelta()
    {
        var current = new PositionCounters(
            WinEvents,
            FastUpdates,
            MoveOnlyWrites,
            RelayoutWrites,
            HideWrites,
            CoalescedEvents,
            RateLimitedFrames,
            SkippedUnchanged,
            DpiChanges,
            MonitorChanges,
            SizeChanges,
            SettleResyncs,
            PositionSamples,
            OffscreenChanges);
        var delta = new PositionCounters(
            current.WinEvents - _reported.WinEvents,
            current.FastUpdates - _reported.FastUpdates,
            current.MoveOnlyWrites - _reported.MoveOnlyWrites,
            current.RelayoutWrites - _reported.RelayoutWrites,
            current.HideWrites - _reported.HideWrites,
            current.CoalescedEvents - _reported.CoalescedEvents,
            current.RateLimitedFrames - _reported.RateLimitedFrames,
            current.SkippedUnchanged - _reported.SkippedUnchanged,
            current.DpiChanges - _reported.DpiChanges,
            current.MonitorChanges - _reported.MonitorChanges,
            current.SizeChanges - _reported.SizeChanges,
            current.SettleResyncs - _reported.SettleResyncs,
            current.PositionSamples - _reported.PositionSamples,
            current.OffscreenChanges - _reported.OffscreenChanges);
        _reported = current;
        return delta;
    }

    /// <summary>Current mode, i.e. which cadence the position pipeline is running at.</summary>
    public PositionUpdateMode Mode { get; private set; } = PositionUpdateMode.Idle;

    /// <summary>True when a fast update is owed: an event arrived that has not been written yet.</summary>
    public bool Dirty { get; private set; }

    /// <summary>
    /// True when the current burst contains a shape change (size, DPI or monitor). It is what decides
    /// whether the accurate path is asked to run faster: a <b>pure move</b> is answered exactly by
    /// translating the geometry, so it needs no extra cross-process UI Automation reads at all, while a
    /// resize genuinely re-lays-out the composer and does.
    /// </summary>
    public bool ShapeChangeInBurst { get; private set; }

    /// <summary>Monotonic count of every recorded observation. Used to detect "nothing changed".</summary>
    public long Notes => _notes;

    public DateTime LastHostActivityUtc { get; private set; } = DateTime.MinValue;

    public DateTime LastWriteUtc { get; private set; } = DateTime.MinValue;

    public DateTime ModeSinceUtc { get; private set; } = DateTime.MinValue;

    /// <summary>How far the last fast update's target was from the last accurate measurement, in px.</summary>
    public int LastPositionErrorX { get; private set; }

    public int LastPositionErrorY { get; private set; }

    public long WinEvents { get; private set; }

    public long FastUpdates { get; private set; }

    public long MoveOnlyWrites { get; private set; }

    public long RelayoutWrites { get; private set; }

    public long HideWrites { get; private set; }

    /// <summary>
    /// Position events that did not get a window call of their own, i.e. the ones that were absorbed by
    /// a frame that carried the latest position instead of replaying each intermediate one.
    ///
    /// <para>It is an identity rather than a separate accumulator on purpose: every event either caused
    /// a write or was folded into one, so counters that could disagree with each other would be worse
    /// than useless in the one place where a backlog would show up as a visual trail.</para>
    /// </summary>
    public long CoalescedEvents => Math.Max(0, WinEvents - (MoveOnlyWrites + RelayoutWrites + HideWrites));

    public long RateLimitedFrames { get; private set; }

    public long SkippedUnchanged { get; private set; }

    public long DpiChanges { get; private set; }

    public long MonitorChanges { get; private set; }

    /// <summary>Samples where the host was not fully inside the working area, i.e. where a translation
    /// could not be trusted and the layout's own clamp had to run.</summary>
    public long OffscreenChanges { get; private set; }

    public long SettleResyncs { get; private set; }

    public long SizeChanges { get; private set; }

    public long PositionSamples { get; private set; }

    /// <summary>One WinEvent naming this host window. Records the motion and wakes the burst.</summary>
    public void OnHostEvent(DateTime nowUtc)
    {
        WinEvents++;
        _notes++;
        LastHostActivityUtc = nowUtc;
        Dirty = true;
        if (Mode != PositionUpdateMode.Burst)
        {
            Mode = PositionUpdateMode.Burst;
            ModeSinceUtc = nowUtc;
            ShapeChangeInBurst = false;
        }
    }

    /// <summary>Marks that a write is still owed (the frame was rate limited, so the timer must catch up).</summary>
    public void MarkDirty() => Dirty = true;

    /// <summary>
    /// Whether cheap geometry may write now. A pure move is allowed to run at
    /// <see cref="PositionFastPathRules.MoveIntervalMs"/>; anything that has to recompute is held to
    /// <see cref="PositionFastPathRules.FastIntervalMs"/>.
    /// </summary>
    public bool ShouldWriteNow(DateTime nowUtc, bool geometryChange)
    {
        if (Mode == PositionUpdateMode.Idle)
        {
            return false;
        }

        var minimum = geometryChange
            ? PositionFastPathRules.FastIntervalMs
            : PositionFastPathRules.MoveIntervalMs;
        return (nowUtc - LastWriteUtc).TotalMilliseconds >= minimum;
    }

    public void NoteObservation(in HostGeometrySample host)
    {
        _notes++;
        if (!host.RequiresRelayout)
        {
            return;
        }

        ShapeChangeInBurst = true;
        if (host.ClientSizeChanged)
        {
            SizeChanges++;
        }

        if (host.DpiChanged)
        {
            DpiChanges++;
        }

        if (host.MonitorChanged)
        {
            MonitorChanges++;
        }

        if (!host.FullyVisible)
        {
            OffscreenChanges++;
        }
    }

    public void NoteMoveOnlyWrite(DateTime nowUtc)
    {
        MoveOnlyWrites++;
        _notes++;
        LastWriteUtc = nowUtc;
    }

    public void NoteRelayoutWrite(DateTime nowUtc)
    {
        RelayoutWrites++;
        _notes++;
        LastWriteUtc = nowUtc;
    }

    public void NoteHide()
    {
        HideWrites++;
        _notes++;
        Dirty = false;
    }

    public void NoteUnchanged()
    {
        SkippedUnchanged++;
        _notes++;
        Dirty = false;
    }

    public void NoteRateLimited()
    {
        RateLimitedFrames++;
        Dirty = true;
    }

    public void NoteFastUpdate() => FastUpdates++;

    public void NotePositionSample()
    {
        PositionSamples++;
        _notes++;
    }

    /// <summary>Records how far the fast path's target was from the accurate measurement.</summary>
    public void NotePositionError(int errorX, int errorY)
    {
        LastPositionErrorX = errorX;
        LastPositionErrorY = errorY;
    }

    /// <summary>
    /// Ages the state machine. Called from the fast timer; returns whether a settle transition
    /// happened so the caller can request the forced accurate resync / release the fast timer.
    /// </summary>
    public SettleTransition Advance(DateTime nowUtc)
    {
        if (Mode == PositionUpdateMode.Burst
            && (nowUtc - LastHostActivityUtc).TotalMilliseconds >= PositionFastPathRules.BurstTailMs)
        {
            Mode = PositionUpdateMode.Settling;
            ModeSinceUtc = nowUtc;
            SettleResyncs++;
            return SettleTransition.EnterSettling;
        }

        if (Mode == PositionUpdateMode.Settling
            && (nowUtc - ModeSinceUtc).TotalMilliseconds >= PositionFastPathRules.SettlingTailMs)
        {
            Mode = PositionUpdateMode.Idle;
            ModeSinceUtc = nowUtc;
            Dirty = false;
            return SettleTransition.Settled;
        }

        return SettleTransition.None;
    }

    /// <summary>Drops every counter and returns to idle. Used when the Codex process changes.</summary>
    public void Reset()
    {
        Mode = PositionUpdateMode.Idle;
        ModeSinceUtc = DateTime.MinValue;
        LastHostActivityUtc = DateTime.MinValue;
        LastWriteUtc = DateTime.MinValue;
        Dirty = false;
        _notes = 0;
    }}

/// <summary>
/// The window operations the fast path performs. Implemented by the live overlay, and by a fake in
/// <c>--self-test</c> so the orchestration below is the production one while the window is synthetic.
/// </summary>
internal interface IFastPathTarget
{
    /// <summary>True while there is a host window to follow and a strip that is allowed to be placed.</summary>
    bool CanTrack { get; }

    /// <summary>The strip's current window rectangle, in physical screen pixels.</summary>
    IntRect CurrentWindowBounds { get; }

    /// <summary>Cheap geometry sample. False when nothing changed or the host is unreadable. Sampling
    /// does not consume the change: <see cref="CommitHostGeometry"/> does.</summary>
    bool TrySampleHost(out HostGeometrySample sample);

    /// <summary>
    /// Accepts the sampled geometry as the new baseline. Called only when the sample was actually applied
    /// — a frame held back by the rate limiter must leave it pending, so the next frame's delta covers
    /// everything that happened since the last write instead of silently dropping it.
    /// </summary>
    void CommitHostGeometry();

    /// <summary>Position-only write: no re-measure, no re-render, no repaint of the surface.</summary>
    void MoveWindowTo(IntRect bounds);

    /// <summary>Full recompute through the accurate path, used when the geometry changed shape.</summary>
    void RelayoutFromAccuratePath();

    void HideForUnusableHost();
}

/// <summary>
/// One fast-path frame: sample → decide → write. Kept out of the overlay so the exact orchestration the
/// self-test drives is the one that ships.
/// </summary>
internal static class FastPathDriver
{
    /// <param name="force">True for the timer's catch-up frame, which bypasses the rate limit because
    /// the timer itself is already the pacing.</param>
    public static FastPathAction Step(
        IFastPathTarget target,
        PositionScheduler scheduler,
        DateTime nowUtc,
        bool force)
    {
        if (!target.CanTrack || !target.TrySampleHost(out var host))
        {
            return FastPathAction.None;
        }

        scheduler.NotePositionSample();
        scheduler.NoteObservation(host);
        var plan = PositionFastPathRules.Plan(host, target.CurrentWindowBounds);
        switch (plan.Action)
        {
            case FastPathAction.HideForUnusableHost:
                scheduler.NoteHide();
                scheduler.NoteFastUpdate();
                target.HideForUnusableHost();
                target.CommitHostGeometry();
                return FastPathAction.HideForUnusableHost;

            case FastPathAction.None:
                scheduler.NoteUnchanged();
                return FastPathAction.None;

            case FastPathAction.MoveOnly:
                if (!force && !scheduler.ShouldWriteNow(nowUtc, geometryChange: false))
                {
                    scheduler.NoteRateLimited();
                    return FastPathAction.None;
                }

                scheduler.NoteFastUpdate();
                scheduler.NoteMoveOnlyWrite(nowUtc);
                target.MoveWindowTo(plan.WindowBounds);
                target.CommitHostGeometry();
                return FastPathAction.MoveOnly;

            default:
                if (!force && !scheduler.ShouldWriteNow(nowUtc, geometryChange: true))
                {
                    scheduler.NoteRateLimited();
                    return FastPathAction.None;
                }

                scheduler.NoteFastUpdate();
                scheduler.NoteRelayoutWrite(nowUtc);
                target.RelayoutFromAccuratePath();
                target.CommitHostGeometry();
                return FastPathAction.Relayout;
        }
    }
}
