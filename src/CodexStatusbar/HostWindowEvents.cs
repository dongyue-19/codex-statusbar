using System.Runtime.InteropServices;

namespace CodexStatusbar;

/// <summary>The host-window events the fast path reacts to.</summary>
internal enum HostWindowEventKind
{
    Unknown = 0,
    LocationChange = 1,
    MovesizeStart = 2,
    MovesizeEnd = 3,
    Foreground = 4,
    MinimizeStart = 5,
    MinimizeEnd = 6
}

/// <summary>
/// The Win32 side of the fast path: a <c>SetWinEventHook</c> subscription scoped to the Codex process,
/// and a sampler that reads the one or two rectangles a fast write actually needs.
///
/// <para><b>Thread affinity.</b> These hooks are installed out-of-context, which means Windows
/// delivers them to the thread that installed them, through its message queue. That thread is the
/// overlay's UI thread, so the callback is already on the thread that owns the window and no
/// marshalling (<c>BeginInvoke</c>, a second queue, a lock) is involved — which is exactly what keeps
/// the fast path from acquiring the backlog and the visual trail the spec asks about.</para>
///
/// <para><b>Scope.</b> The <c>hwnd</c> parameter of <c>SetWinEventHook</c> is ignored for
/// out-of-context hooks, so the subscription is scoped with <c>idProcess</c> instead: the owning
/// process of the host window. That is one process rather than every window on the desktop, and the
/// callback still re-checks the handle, because Codex is an Electron application with several
/// top-level windows and only one of them is the editor.</para>
///
/// <para><b>Cost.</b> The callback does no work beyond a handle comparison, a counter and a
/// timestamp. It never reads UI Automation and never calls <c>SetWindowPos</c>: the write happens on
/// the scheduler's frame, which is what makes the event rate irrelevant to the write rate.</para>
/// </summary>
internal sealed class WinEventHostMonitor : IDisposable
{
    private const uint WineventOutofcontext = 0x0000;
    private const uint WineventSkipownprocess = 0x0002;

    private const int EventSystemForeground = 0x0003;
    private const int EventSystemMovesizestart = 0x000A;
    private const int EventSystemMovesizeend = 0x000B;
    private const int EventSystemMinimizestart = 0x0016;
    private const int EventSystemMinimizeend = 0x0017;
    private const int EventObjectLocationchange = 0x800B;

    /// <summary><c>OBJID_WINDOW</c>: the event is about the window itself, not about a child object.</summary>
    private const int ObjIdWindow = 0;

    private readonly Action<HostWindowEventKind> _onEvent;
    private readonly WinEventDelegate _callback;
    private readonly List<IntPtr> _hooks = new();

    private IntPtr _hostWindow;
    private uint _hostProcessId;
    private bool _disposed;

    public WinEventHostMonitor(Action<HostWindowEventKind> onEvent)
    {
        _onEvent = onEvent ?? throw new ArgumentNullException(nameof(onEvent));

        // The delegate is held for the lifetime of the hooks: the OS keeps the function pointer, so a
        // collected delegate would crash the process inside a native callback.
        _callback = HandleEvent;
    }

    public bool IsAttached => _hooks.Count > 0;

    public IntPtr HostWindow => _hostWindow;

    /// <summary>Every event that named the host window.</summary>
    public long EventCount { get; private set; }

    /// <summary>Events the subscription delivered for some other window of the same process.</summary>
    public long IgnoredCount { get; private set; }

    /// <summary>Events delivered while no host window was bound, or after a failed hook.</summary>
    public long StrayCount { get; private set; }

    public long ErrorCount { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>
    /// Subscribes to the events of <paramref name="hostWindow"/>'s process. Must be called on the
    /// thread that pumps messages (the overlay's UI thread). Re-binding to the same window is a no-op.
    /// </summary>
    public bool Attach(IntPtr hostWindow)
    {
        if (_disposed || hostWindow == IntPtr.Zero)
        {
            return false;
        }

        if (IsAttached && hostWindow == _hostWindow)
        {
            return true;
        }

        Detach();

        GetWindowThreadProcessId(hostWindow, out var processId);
        if (processId == 0)
        {
            RecordError("GetWindowThreadProcessId returned 0");
            return false;
        }

        var flags = WineventOutofcontext | WineventSkipownprocess;
        var systemRange = SetWinEventHook(
            EventSystemForeground,
            EventSystemMinimizeend,
            IntPtr.Zero,
            _callback,
            processId,
            0,
            flags);
        if (systemRange != IntPtr.Zero)
        {
            _hooks.Add(systemRange);
        }

        var locationOnly = SetWinEventHook(
            EventObjectLocationchange,
            EventObjectLocationchange,
            IntPtr.Zero,
            _callback,
            processId,
            0,
            flags);
        if (locationOnly != IntPtr.Zero)
        {
            _hooks.Add(locationOnly);
        }

        if (_hooks.Count == 0)
        {
            RecordError("SetWinEventHook failed for both subscriptions");
            return false;
        }

        _hostWindow = hostWindow;
        _hostProcessId = processId;
        return true;
    }

    public void Detach()
    {
        foreach (var hook in _hooks)
        {
            try
            {
                _ = UnhookWinEvent(hook);
            }
            catch (Exception exception)
            {
                RecordError("UnhookWinEvent: " + exception.Message);
            }
        }

        _hooks.Clear();
        _hostWindow = IntPtr.Zero;
        _hostProcessId = 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Detach();
    }

    /// <summary>
    /// The native callback. This is the one place in the pipeline where a thrown exception would cross
    /// a native frame, so everything is caught: a diagnostics problem must never take the process down
    /// from inside a system callback.
    /// </summary>
    private void HandleEvent(
        IntPtr hook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTime)
    {
        _ = hook;
        _ = idChild;
        _ = eventThread;
        _ = eventTime;

        try
        {
            if (_disposed)
            {
                return;
            }

            if (hwnd != _hostWindow || _hostWindow == IntPtr.Zero)
            {
                IgnoredCount++;
                return;
            }

            // Only the host window's own rectangle matters. A child object's location change (the
            // accessibility tree produces these) is the accurate path's business, not this one's.
            if (eventType == EventObjectLocationchange && idObject != ObjIdWindow)
            {
                IgnoredCount++;
                return;
            }

            EventCount++;
            _onEvent(Classify(eventType));
        }
        catch (Exception exception)
        {
            RecordError(exception.GetType().Name + ": " + exception.Message);
        }
    }

    private static HostWindowEventKind Classify(uint eventType) => eventType switch
    {
        EventObjectLocationchange => HostWindowEventKind.LocationChange,
        EventSystemMovesizestart => HostWindowEventKind.MovesizeStart,
        EventSystemMovesizeend => HostWindowEventKind.MovesizeEnd,
        EventSystemForeground => HostWindowEventKind.Foreground,
        EventSystemMinimizestart => HostWindowEventKind.MinimizeStart,
        EventSystemMinimizeend => HostWindowEventKind.MinimizeEnd,
        _ => HostWindowEventKind.Unknown
    };

    private void RecordError(string message)
    {
        ErrorCount++;
        LastError = message;
    }

    private delegate void WinEventDelegate(
        IntPtr hook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        int eventMin,
        int eventMax,
        IntPtr moduleHandle,
        WinEventDelegate callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);
}

/// <summary>
/// Cheap geometry for one host window: <c>GetWindowRect</c> on every sample, and the more expensive
/// facts (DWM's extended frame, the monitor's working area, the DPI) only when the size, the DPI or
/// the monitor actually changed.
///
/// <para>The full, layout-ready <see cref="CodexWindowInfo"/> is rebuilt from the cache only when a
/// recompute is required, so a pure translation costs one <c>GetWindowRect</c> — measured at a few
/// microseconds — rather than a caption-button query, five system metrics and an enumeration of the
/// display monitors.</para>
/// </summary>
internal sealed class HostWindowSampler : IDisposable
{
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const int DwmwaExtendedFrameBounds = 9;

    /// <summary>The accuracy rule the locator applies to a real Codex window; mirrored so the fast
    /// path cannot dock to a rectangle the accurate path would have rejected.</summary>
    private const int MinimumHostWidth = 500;
    private const int MinimumHostHeight = 400;

    private IntPtr _window;
    private IntRect _windowBounds;
    private IntRect _frameBounds;
    private IntRect _workingArea;
    private uint _dpi;
    private long _monitor;
    private bool _primed;

    // The sample that has been read but not yet applied to the window.
    private IntRect _pendingBounds;
    private IntRect _pendingFrame;
    private IntRect _pendingWorkingArea;
    private uint _pendingDpi;
    private long _pendingMonitor;
    private bool _hasPending;
    private WinEventHostMonitor? _events;

    public IntPtr Window => _window;

    public IntRect WindowBounds => _windowBounds;

    public IntRect FrameBounds => _frameBounds;

    public uint Dpi => _dpi;

    public long Monitor => _monitor;

    public IntRect WorkingArea => _workingArea;

    public WinEventHostMonitor? Events => _events;

    public long FullReadCount { get; private set; }

    public long SampleCount { get; private set; }

    /// <summary>Binds to a host window and installs the WinEvent subscription for its process.</summary>
    public bool Bind(IntPtr window, Action<HostWindowEventKind> onEvent)
    {
        ArgumentNullException.ThrowIfNull(onEvent);
        if (window == IntPtr.Zero)
        {
            return false;
        }

        if (_window == window && _primed)
        {
            return true;
        }

        Unbind();
        _window = window;
        _events = new WinEventHostMonitor(onEvent);
        if (!_events.Attach(window))
        {
            // A missing subscription costs responsiveness, not correctness: the idle cadence still
            // places the strip. Keep the sampler so the pipeline still works, and let the log show it.
            _events.Dispose();
            _events = null;
        }

        if (!TryFullRead(window, out _windowBounds, out _frameBounds, out _workingArea, out _dpi, out _monitor))
        {
            Unbind();
            return false;
        }

        _primed = true;
        return true;
    }

    public void Unbind()
    {
        _events?.Dispose();
        _events = null;
        _window = IntPtr.Zero;
        _primed = false;
        _hasPending = false;
        _windowBounds = default;
        _frameBounds = default;
        _workingArea = default;
        _pendingBounds = default;
        _pendingFrame = default;
        _pendingWorkingArea = default;
        _dpi = 0;
        _monitor = 0;
    }

    public void Dispose() => Unbind();

    /// <summary>
    /// The window's rectangle right now, read from the window rather than from the form's cached
    /// bounds. The position diagnostics report the difference between where the strip was told to go
    /// and where Windows actually put it, so it must not read the same cache it wrote.
    /// </summary>
    internal static bool TryReadWindowRect(IntPtr window, out IntRect bounds)
    {
        bounds = default;
        if (window == IntPtr.Zero || !GetWindowRect(window, out var native))
        {
            return false;
        }

        bounds = ToIntRect(native);
        return true;
    }

    /// <summary>
    /// One cheap sample, relative to the last <b>committed</b> geometry. Returns false when there is
    /// nothing to do: the rectangle, the DPI and the monitor are all unchanged, or the window cannot be
    /// read.
    ///
    /// <para>It deliberately does not advance the baseline — <see cref="Commit"/> does, and only once the
    /// sample has actually been applied to the window. A rate-limited frame therefore leaves the motion
    /// pending, and the next frame's delta covers everything that has happened since the last write.
    /// Consuming it here instead would silently drop the movement that arrived during a held-back frame,
    /// which is precisely the "strip is stuck a few dozen pixels behind" symptom this work exists to
    /// remove.</para>
    /// </summary>
    public bool TrySample(out HostGeometrySample sample)
    {
        sample = default;
        if (_window == IntPtr.Zero || !_primed)
        {
            return false;
        }

        SampleCount++;
        if (!GetWindowRect(_window, out var nativeBounds))
        {
            return false;
        }

        var current = ToIntRect(nativeBounds);

        // These two are read on every sample, not only when the rectangle changed: dragging a window
        // between two displays can change the DPI and the monitor with the rectangle staying exactly the
        // same size, and a 150% → 100% move with identical pixels is a real, visible event. Both calls
        // are a couple of microseconds.
        var monitor = MonitorFromWindow(_window, MonitorDefaultToNearest).ToInt64();
        var dpi = ReadDpi(_window);
        var sizeChanged = current.Width != _windowBounds.Width || current.Height != _windowBounds.Height;
        var monitorChanged = monitor != _monitor;
        var dpiChanged = dpi != _dpi;
        if (current == _windowBounds && !monitorChanged && !dpiChanged)
        {
            return false;
        }

        var pendingFrame = _frameBounds;
        var pendingWorkingArea = _workingArea;
        if (sizeChanged || monitorChanged || dpiChanged)
        {
            ReadFrameAndWorkingArea(_window, monitor, current, out pendingFrame, out pendingWorkingArea);
        }

        _pendingBounds = current;
        _pendingDpi = dpi;
        _pendingMonitor = monitor;
        _pendingFrame = pendingFrame;
        _pendingWorkingArea = pendingWorkingArea;
        _hasPending = true;

        sample = new HostGeometrySample(
            _windowBounds,
            current,
            _dpi,
            dpi,
            _monitor,
            monitor,
            pendingWorkingArea,
            PositionFastPathRules.IsUsableHostRect(current));
        return true;
    }

    /// <summary>
    /// Accepts the pending sample: its geometry becomes the baseline the next delta is measured from.
    /// Called by the fast path only when the sample was applied to the window (moved, re-laid-out or
    /// hidden) — never for a frame that was held back.
    /// </summary>
    public void Commit()
    {
        if (!_hasPending)
        {
            return;
        }

        _hasPending = false;
        _windowBounds = _pendingBounds;
        _dpi = _pendingDpi;
        _monitor = _pendingMonitor;
        _frameBounds = _pendingFrame;
        _workingArea = _pendingWorkingArea;
        CommitCount++;
    }

    /// <summary>How many samples have been accepted. Samples held back by the rate limiter are not.</summary>
    public long CommitCount { get; private set; }

    /// <summary>
    /// The layout-ready description of the host, from live geometry plus the two facts only a
    /// recompute needs. Returns false when the window is no longer a plausible Codex editor, matching
    /// the accurate path's own sanity rule.
    /// </summary>
    public bool TryBuildHostInfo(out CodexWindowInfo info)
    {
        info = null!;
        if (_window == IntPtr.Zero
            || !_primed
            || _frameBounds.Width < MinimumHostWidth
            || _frameBounds.Height < MinimumHostHeight)
        {
            return false;
        }

        info = new CodexWindowInfo(
            _window,
            _windowBounds,
            _frameBounds,
            CodexWindowLocator.TryReadCaptionButtonBounds(_window, _windowBounds),
            _workingArea,
            _dpi,
            CodexWindowLocator.ReadChromeMetrics(_dpi));
        return true;
    }

    private static bool TryFullRead(
        IntPtr window,
        out IntRect windowBounds,
        out IntRect frameBounds,
        out IntRect workingArea,
        out uint dpi,
        out long monitor)
    {
        windowBounds = default;
        frameBounds = default;
        workingArea = default;
        dpi = 0;
        monitor = 0;

        if (!GetWindowRect(window, out var nativeBounds))
        {
            return false;
        }

        windowBounds = ToIntRect(nativeBounds);
        monitor = MonitorFromWindow(window, MonitorDefaultToNearest).ToInt64();
        dpi = ReadDpi(window);
        ReadFrameAndWorkingArea(window, monitor, windowBounds, out frameBounds, out workingArea);
        return true;
    }

    /// <summary>
    /// The two reads that only a shape change needs. Both degrade instead of failing: a DWM refusal
    /// falls back to the window rectangle (which is what the locator does) and an unreadable working
    /// area stays empty, which the planner reads as "nowhere to clamp to".
    /// </summary>
    private static void ReadFrameAndWorkingArea(
        IntPtr window,
        long monitor,
        IntRect windowBounds,
        out IntRect frameBounds,
        out IntRect workingArea)
    {
        frameBounds = windowBounds;
        var result = DwmGetWindowAttribute(
            window,
            DwmwaExtendedFrameBounds,
            out var nativeFrame,
            Marshal.SizeOf<NativeRectangle>());
        if (result == 0)
        {
            frameBounds = ToIntRect(nativeFrame);
        }

        workingArea = default;
        var monitorHandle = new IntPtr(monitor);
        if (monitorHandle == IntPtr.Zero)
        {
            return;
        }

        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (GetMonitorInfo(monitorHandle, ref monitorInfo))
        {
            workingArea = new IntRect(
                monitorInfo.Work.Left,
                monitorInfo.Work.Top,
                monitorInfo.Work.Right - monitorInfo.Work.Left,
                monitorInfo.Work.Bottom - monitorInfo.Work.Top);
        }
    }

    /// <summary>Same fallback chain as the locator: per-window DPI, then the system DPI, then 0.</summary>
    private static uint ReadDpi(IntPtr window)
    {
        try
        {
            var dpi = GetDpiForWindow(window);
            if (dpi > 0)
            {
                return dpi;
            }
        }
        catch (EntryPointNotFoundException)
        {
        }

        try
        {
            return GetDpiForSystem();
        }
        catch (EntryPointNotFoundException)
        {
            return 0;
        }
    }

    private static IntRect ToIntRect(NativeRectangle rectangle) => new(
        rectangle.Left,
        rectangle.Top,
        rectangle.Right - rectangle.Left,
        rectangle.Bottom - rectangle.Top);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRectangle Monitor;
        public NativeRectangle Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRectangle rectangle);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitorHandle, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        out NativeRectangle value,
        int valueSize);
}
