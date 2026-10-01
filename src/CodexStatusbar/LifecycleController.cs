namespace CodexStatusbar;

/// <summary>
/// The Codex lifecycle, as the tray and the debug log report it.
///
/// <para>The watcher stays alive across every transition: Codex exiting is <b>not</b> a reason to end
/// the process. Only the tray's Exit does that.</para>
/// </summary>
internal enum CodexLifecycleState
{
    /// <summary>No Codex Desktop process. Nothing Codex-specific is running.</summary>
    WaitingForCodex,

    /// <summary>Codex exists; the subsystems are starting and being probed for readiness.</summary>
    Attaching,

    /// <summary>Attached: IPC/session or the composer reference resolved. The overlay can show.</summary>
    Active,

    /// <summary>Codex exited; the subsystems are being torn down. Transient.</summary>
    Detaching
}

/// <summary>
/// The parts of the lifecycle that are pure arithmetic or pure decisions, kept separate so the
/// self-test can pin them without a Codex, a registry, or a clock.
/// </summary>
internal static class LifecycleRules
{
    /// <summary>
    /// Attach retry ladder. The first attempt is immediate, then it backs off to a 2 s steady rate —
    /// long enough that an Electron cold start (window, then pipe, then accessibility tree, all
    /// seconds apart) is waited out quietly, short enough that the strip appears promptly once the
    /// conversation is readable.
    /// </summary>
    private static readonly int[] AttachBackoffMs = { 0, 250, 500, 1000, 2000 };

    public const int SteadyAttachIntervalMs = 2000;

    /// <summary>Delay before attempt <paramref name="attempt"/> (0 = the first attempt).</summary>
    public static int DelayForAttempt(int attempt)
    {
        if (attempt < 0)
        {
            return AttachBackoffMs[0];
        }

        return attempt < AttachBackoffMs.Length
            ? AttachBackoffMs[attempt]
            : SteadyAttachIntervalMs;
    }

    /// <summary>
    /// When attaching counts as finished. Either the routing is genuinely usable (the IPC pipe is
    /// connected <i>and</i> a conversation was identified) or the composer's Context control was
    /// found, which is enough to place the strip on the toolbar row.
    /// </summary>
    public static bool IsAttachComplete(bool ipcConnected, bool sessionReady, bool uiaReady) =>
        (ipcConnected && sessionReady) || uiaReady;

    /// <summary>
    /// How often the UI timer runs, per state. Waiting is the long-lived state for a machine where
    /// Codex is closed, so it ticks slowest; attaching needs the ladder's resolution.
    /// </summary>
    public static int TickIntervalFor(CodexLifecycleState state) => state switch
    {
        CodexLifecycleState.WaitingForCodex => 1000,
        CodexLifecycleState.Attaching => 250,
        CodexLifecycleState.Detaching => 250,
        _ => 350
    };
}

/// <summary>
/// Drives WAITING_FOR_CODEX → ATTACHING → ACTIVE → DETACHING → WAITING_FOR_CODEX and tells the owner
/// when to start and stop Codex-specific subsystems.
///
/// It is driven by the owner's UI timer rather than by its own loop: the timer already exists, and
/// the ladder is expressed as "not before this timestamp", so a tick that arrives early does nothing
/// and there is no busy loop anywhere.
/// </summary>
internal sealed class LifecycleController
{
    private readonly DebugDiagnostics _debug;

    private DateTime _nextAttemptUtc = DateTime.MinValue;
    private string _lastLoggedTransition = string.Empty;
    private bool _announcedWaiting;

    public LifecycleController(DebugDiagnostics debug)
    {
        _debug = debug ?? throw new ArgumentNullException(nameof(debug));
        State = CodexLifecycleState.WaitingForCodex;
        Codex = CodexProcessInfo.None;
    }

    /// <summary>Raised when the subsystems should start for the given Codex process.</summary>
    public event Action<CodexProcessInfo>? AttachRequested;

    /// <summary>Raised when the subsystems should stop and every Codex-specific handle be dropped.</summary>
    public event Action? DetachRequested;

    public CodexLifecycleState State { get; private set; }

    /// <summary>The Codex process this state refers to; retained through DETACHING for the log.</summary>
    public CodexProcessInfo Codex { get; private set; }

    public int AttachAttempt { get; private set; }

    public DateTime? ActiveSinceUtc { get; private set; }

    /// <summary>Reported by the owner, for the state decision and the debug block.</summary>
    public bool IpcConnected { get; set; }

    public bool SessionReady { get; set; }

    public bool UiaReady { get; set; }

    public bool OverlayVisible { get; set; }

    public bool IsAttached => State == CodexLifecycleState.Active;

    /// <summary>True once the subsystems for the current Codex process have been started.</summary>
    public bool SubsystemsRunning { get; private set; }

    /// <summary>Tray "Start / Attach now": re-detect and retry immediately instead of waiting out the ladder.</summary>
    public void RequestImmediateAttach()
    {
        _nextAttemptUtc = DateTime.MinValue;
        if (State == CodexLifecycleState.Active)
        {
            State = CodexLifecycleState.Attaching;
            AttachAttempt = 0;
        }
    }

    /// <summary>
    /// One evaluation. <paramref name="codex"/> is the watcher's current reading, and the three
    /// readiness flags come from the owner because only it can see the IPC pipe, the selected
    /// conversation and the accessibility tree.
    /// </summary>
    public void Observe(
        CodexProcessInfo codex,
        bool ipcConnected,
        bool sessionReady,
        bool uiaReady,
        DateTime nowUtc)
    {
        IpcConnected = ipcConnected;
        SessionReady = sessionReady;
        UiaReady = uiaReady;

        // Announce the resting state once, so the event log of a launch that finds no Codex reads the
        // same way as one that finds Codex already running.
        if (State == CodexLifecycleState.WaitingForCodex && !codex.IsRunning && !_announcedWaiting)
        {
            _announcedWaiting = true;
            LogTransition("waiting", "no Codex detected");
        }

        if (codex.IsRunning)
        {
            if (State is CodexLifecycleState.WaitingForCodex or CodexLifecycleState.Detaching)
            {
                EnterAttaching(codex, nowUtc, reason: State == CodexLifecycleState.Detaching
                    ? "Codex restarted"
                    : "Codex process detected");
            }
        }
        else if (State is CodexLifecycleState.Attaching or CodexLifecycleState.Active)
        {
            State = CodexLifecycleState.Detaching;
            LogTransition("detaching", SubsystemsRunning ? "Codex process exited" : "Codex process exited (not attached)");
            DetachRequested?.Invoke();
            SubsystemsRunning = false;
            ActiveSinceUtc = null;
            AttachAttempt = 0;
            return;
        }
        else if (State == CodexLifecycleState.Detaching)
        {
            State = CodexLifecycleState.WaitingForCodex;
            Codex = CodexProcessInfo.None;
            _announcedWaiting = true;
            LogTransition("waiting", "no Codex detected");
            return;
        }

        if (State == CodexLifecycleState.Attaching)
        {
            if (LifecycleRules.IsAttachComplete(ipcConnected, sessionReady, uiaReady))
            {
                State = CodexLifecycleState.Active;
                ActiveSinceUtc = nowUtc;
                LogTransition(
                    "active",
                    $"attached after {AttachAttempt} attempt(s): ipc={Flag(ipcConnected)} session={Flag(sessionReady)} uia={Flag(uiaReady)}");
                return;
            }

            if (nowUtc >= _nextAttemptUtc)
            {
                AttachAttempt++;
                _nextAttemptUtc = nowUtc.AddMilliseconds(LifecycleRules.DelayForAttempt(AttachAttempt));
                _debug.Event(
                    $"attach attempt {AttachAttempt}: ipc={Flag(ipcConnected)} session={Flag(sessionReady)} uia={Flag(uiaReady)}");
            }
        }
    }

    private void EnterAttaching(CodexProcessInfo codex, DateTime nowUtc, string reason)
    {
        Codex = codex;
        State = CodexLifecycleState.Attaching;
        AttachAttempt = 0;
        _announcedWaiting = false;
        _nextAttemptUtc = nowUtc;
        LogTransition(
            "attaching",
            $"{reason} PID={codex.ProcessId} package={codex.Package} version={codex.Version} window={(codex.HasMainWindow ? "yes" : "no")}");
        _debug.Event("main window " + (codex.HasMainWindow ? "ready" : "not ready yet"));

        if (!SubsystemsRunning)
        {
            SubsystemsRunning = true;
            AttachRequested?.Invoke(codex);
        }
    }

    private void LogTransition(string state, string detail)
    {
        _debug.Event($"{state}: {detail}");
        _lastLoggedTransition = state;
    }

    /// <summary>The state name as the debug block spells it.</summary>
    public string StateName => State switch
    {
        CodexLifecycleState.WaitingForCodex => "WAITING_FOR_CODEX",
        CodexLifecycleState.Attaching => "ATTACHING",
        CodexLifecycleState.Active => "ACTIVE",
        _ => "DETACHING"
    };

    public string StateSummary => State switch
    {
        CodexLifecycleState.WaitingForCodex => "Waiting",
        CodexLifecycleState.Attaching => "Connecting",
        CodexLifecycleState.Active => "Active",
        _ => "Detaching"
    };

    private static string Flag(bool value) => value ? "ready" : "waiting";

    /// <summary>Only used by the debug block, to show whether a transition was ever logged.</summary>
    public string LastTransition => _lastLoggedTransition;
}