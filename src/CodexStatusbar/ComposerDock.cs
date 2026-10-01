using System.Windows.Automation;

namespace CodexStatusbar;

/// <summary>Which element the strip is currently docked against.</summary>
internal enum ComposerReferenceSource
{
    /// <summary>No usable reference; the strip cannot be docked.</summary>
    None = 0,

    /// <summary>The official Context-usage indicator itself. The preferred source.</summary>
    UiaContext = 1,

    /// <summary>The model selector: its left edge stands in for the Context indicator's.</summary>
    UiaModel = 2,

    /// <summary>The composer card: the toolbar row is derived from its bottom-right corner.</summary>
    UiaComposer = 3,

    /// <summary>Nothing but the Codex window rectangle: fixed DIP offsets from its bottom-right.</summary>
    Window = 4
}

/// <summary>
/// One measurement of the Codex composer toolbar, in physical screen pixels.
///
/// <para>Every rectangle here was read from the real UI Automation tree of the running Codex
/// Desktop window, not inferred from a window-size percentage. <see cref="ReferenceRect"/> is the
/// element the strip's right edge is placed against; <see cref="RowRect"/> is the toolbar row's own
/// box, used for vertical alignment, because the Context icon is not centred in its row (its CSS
/// <c>vertical-align: middle</c> sits it ~2.5 px low at 150%) and aligning to it would put the text
/// baseline below the model selector's.</para>
/// </summary>
internal sealed record ComposerDockSnapshot(
    ComposerReferenceSource Source,
    IntRect ReferenceRect,
    IntRect RowRect,
    IntRect ComposerRect,
    IntRect LeftClusterRect,
    string ElementName,
    string AutomationId,
    string ControlType,
    string ClassName,
    long WindowHandle,
    int ElementIndex,
    string? Failure)
{
    public static readonly ComposerDockSnapshot Empty = new(
        ComposerReferenceSource.None,
        default,
        default,
        default,
        default,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        0,
        -1,
        null);

    /// <summary>True when there is a real element rectangle to dock against.</summary>
    public bool HasReference =>
        Source is ComposerReferenceSource.UiaContext or ComposerReferenceSource.UiaModel
        && !ReferenceRect.IsEmpty;

    /// <summary>True when the toolbar row's own box is known, which is what vertical alignment uses.</summary>
    public bool HasRow => !RowRect.IsEmpty;

    /// <summary>The row's vertical centre: the row box when known, otherwise the reference box.</summary>
    public int RowCenterY => HasRow ? RowRect.Y + (RowRect.Height / 2) : ReferenceRect.Y + (ReferenceRect.Height / 2);

    public string DescribeSource() => Source switch
    {
        ComposerReferenceSource.UiaContext => "uia-context",
        ComposerReferenceSource.UiaModel => "uia-model",
        ComposerReferenceSource.UiaComposer => "uia-composer",
        ComposerReferenceSource.Window => "window-fallback",
        _ => "none"
    };
}

/// <summary>
/// Finds and tracks the official Codex composer toolbar through UI Automation.
///
/// <para><b>The ladder.</b> The Context-usage indicator is the primary reference because it is a
/// direct child of the composer card and survived every geometry change tested (resize, maximise,
/// restore, move). The model selector is the fallback: it is found the same way but React replaces
/// its DOM node on relayout, so a held reference to it goes stale and it must be re-discovered.</para>
///
/// <para><b>Why a thread.</b> UI Automation reads cross the process boundary and can block for as
/// long as the target application takes to answer. All UIA work therefore happens on one dedicated
/// background thread which publishes an immutable snapshot; the overlay's UI thread only ever reads
/// that snapshot, so a slow or wedged Codex cannot stall the strip.</para>
///
/// <para><b>Read-only.</b> Nothing here invokes a pattern, sets a value, or sends input. The only
/// operations are tree walks and property reads.</para>
/// </summary>
internal sealed class ComposerDockTracker : IDisposable
{
    /// <summary>ClassName Chromium gives the composer's contenteditable host.</summary>
    private const string ComposerEditorClass = "ProseMirror";

    /// <summary>English and Chinese labels of the Context-usage indicator, used only as a hint.</summary>
    private static readonly string[] ContextNameHints =
    {
        "上下文用量", "Context usage", "Context window", "context window"
    };

    /// <summary>Design-system class fragment on the Context indicator, a text-independent signal.</summary>
    private const string ContextClassHint = "codex-description";

    /// <summary>How often the fast path (re-reading the held element) runs.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>A full tree walk at least this often, as a safety net against silent layout drift.</summary>
    private static readonly TimeSpan ResyncInterval = TimeSpan.FromSeconds(20);

    private readonly object _gate = new();
    private readonly Func<IntPtr> _windowProvider;
    private Thread? _thread;
    private CancellationTokenSource? _cancellation;
    private volatile ComposerDockSnapshot _latest = ComposerDockSnapshot.Empty;
    private volatile bool _enabled = true;
    private long _walkCount;
    private long _fastReadCount;
    private long _lastWalkMilliseconds;

    public ComposerDockTracker(Func<IntPtr> windowProvider)
    {
        _windowProvider = windowProvider ?? throw new ArgumentNullException(nameof(windowProvider));
    }

    public ComposerDockSnapshot Latest => _latest;

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public long WalkCount => Interlocked.Read(ref _walkCount);

    public long FastReadCount => Interlocked.Read(ref _fastReadCount);

    public long LastWalkMilliseconds => Interlocked.Read(ref _lastWalkMilliseconds);

    public void Start()
    {
        lock (_gate)
        {
            if (_thread is not null)
            {
                return;
            }

            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;
            _thread = new Thread(() => Run(token))
            {
                IsBackground = true,
                Name = "CodexStatusbar.ComposerDock"
            };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }
    }

    public void Dispose()
    {
        Thread? thread;
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            thread = _thread;
            cancellation = _cancellation;
            _thread = null;
            _cancellation = null;
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down.
        }

        // Do not join: a UIA call inside the target process can outlive the cancel request, and the
        // thread is a background thread so it cannot keep the process alive.
        _ = thread;
        cancellation?.Dispose();
    }

    private void Run(CancellationToken token)
    {
        AutomationElement? composer = null;
        AutomationElement? reference = null;
        AutomationElement? model = null;
        var lastReferenceRect = default(IntRect);
        var walksRemaining = 2;
        var lastWalkAt = DateTime.UtcNow;

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!_enabled)
                {
                    Publish(ComposerDockSnapshot.Empty);
                    reference = null;
                    composer = null;
                    model = null;
                    walksRemaining = 2;
                    Sleep(token);
                    continue;
                }

                var hwnd = _windowProvider();
                if (hwnd == IntPtr.Zero)
                {
                    Publish(ComposerDockSnapshot.Empty);
                    reference = null;
                    composer = null;
                    model = null;
                    walksRemaining = 2;
                    Sleep(token);
                    continue;
                }

                var changedWindow = _latest.WindowHandle != hwnd.ToInt64();
                if (changedWindow)
                {
                    reference = null;
                    composer = null;
                    model = null;
                    walksRemaining = 2;
                }

                // Fast path: re-read the held element only. One cross-process call instead of a walk.
                var fastRect = default(IntRect);
                var hadContextReference = _latest.Source == ComposerReferenceSource.UiaContext;
                var lostReference = false;
                if (reference is not null)
                {
                    if (TryReadRect(reference, out fastRect))
                    {
                        Interlocked.Increment(ref _fastReadCount);
                    }
                    else
                    {
                        reference = null;
                        lostReference = true;
                    }
                }

                if (!fastRect.IsEmpty && fastRect != lastReferenceRect)
                {
                    // The layout moved. Two walks, not one: the Context indicator's own rectangle is
                    // updated before its siblings', so a single walk taken as soon as the reference
                    // moves can read the *previous* composer layout for everything else. Measured at
                    // the 1100 px sidebar breakpoint, where one walk left the left-cluster boundary at
                    // its pre-breakpoint value (607 px instead of 880) for as long as the geometry
                    // stayed put — the resync interval was the only thing that eventually fixed it.
                    walksRemaining = 2;
                }

                if (DateTime.UtcNow - lastWalkAt > ResyncInterval)
                {
                    walksRemaining = 2;
                }

                if (walksRemaining > 0 || lostReference || (hadContextReference && reference is null) || composer is null)
                {
                    var walkStarted = Environment.TickCount64;
                    var snapshot = Walk(hwnd, ref composer, ref reference, ref model);
                    Interlocked.Increment(ref _walkCount);
                    Interlocked.Exchange(ref _lastWalkMilliseconds, Environment.TickCount64 - walkStarted);
                    lastWalkAt = DateTime.UtcNow;
                    walksRemaining--;
                    lastReferenceRect = snapshot.ReferenceRect;
                    Publish(snapshot);
                }
                else if (!fastRect.IsEmpty)
                {
                    lastReferenceRect = fastRect;
                    Publish(_latest with
                    {
                        Source = ComposerReferenceSource.UiaContext,
                        ReferenceRect = fastRect,
                        WindowHandle = hwnd.ToInt64(),
                        Failure = null
                    });
                }
            }
            catch (ElementNotAvailableException)
            {
                reference = null;
                composer = null;
                model = null;
                walksRemaining = 2;
            }
            catch (InvalidOperationException)
            {
                reference = null;
                composer = null;
                model = null;
                walksRemaining = 2;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Publish(ComposerDockSnapshot.Empty with { Failure = exception.GetType().Name });
                reference = null;
                composer = null;
                model = null;
                walksRemaining = 2;
                Sleep(token);
            }

            Sleep(token);
        }
    }

    private static void Sleep(CancellationToken token)
    {
        try
        {
            token.WaitHandle.WaitOne(PollInterval);
        }
        catch (ObjectDisposedException)
        {
            Thread.Sleep(PollInterval);
        }
    }

    private void Publish(ComposerDockSnapshot snapshot)
    {
        if (snapshot == _latest)
        {
            return;
        }

        _latest = snapshot;
    }

    // ------------------------------------------------------------------ the walk

    /// <summary>
    /// One full discovery pass. Returns the best snapshot the ladder can produce, holding on to the
    /// AutomationElements so the next iterations can use the cheap fast path.
    /// </summary>
    private static ComposerDockSnapshot Walk(
        IntPtr hwnd,
        ref AutomationElement? composer,
        ref AutomationElement? reference,
        ref AutomationElement? model)
    {
        var root = AutomationElement.FromHandle(hwnd);
        if (root is null)
        {
            composer = null;
            reference = null;
            model = null;
            return ComposerDockSnapshot.Empty with { WindowHandle = hwnd.ToInt64(), Failure = "no-root" };
        }

        var walker = TreeWalker.ControlViewWalker;

        // Re-acquire the composer when the held node died.
        if (composer is not null && !TryReadRect(composer, out _))
        {
            composer = null;
        }

        composer ??= FindComposer(root, walker);
        if (composer is null)
        {
            reference = null;
            model = null;
            return ComposerDockSnapshot.Empty with
            {
                Source = ComposerReferenceSource.Window,
                WindowHandle = hwnd.ToInt64(),
                Failure = "no-composer"
            };
        }

        if (!TryReadRect(composer, out var composerRect))
        {
            composer = null;
            reference = null;
            model = null;
            return ComposerDockSnapshot.Empty with { WindowHandle = hwnd.ToInt64(), Failure = "composer-stale" };
        }

        // Enumerate the composer's direct children once: the Context indicator, the model selector
        // and the left control cluster are all read out of this single list.
        var children = new List<ChildInfo>(16);
        var childElement = TryFirstChild(walker, composer);
        var index = 0;
        while (childElement is not null && index < 64)
        {
            if (TryReadRect(childElement, out var rect)
                && TryReadProperties(
                    childElement,
                    out var childControlType,
                    out var childName,
                    out var childAutomationId,
                    out var childClassName))
            {
                children.Add(new ChildInfo(
                    childElement,
                    index,
                    rect,
                    childControlType,
                    childName,
                    childAutomationId,
                    childClassName));
            }

            childElement = TryNextSibling(walker, childElement);
            index++;
        }

        var contextIndex = -1;
        AutomationElement? contextElement = null;
        for (var i = 0; i < children.Count; i++)
        {
            if (children[i].ControlType != "Image")
            {
                continue;
            }

            contextIndex = i;
            contextElement = children[i].Element;
            break;
        }

        // Prefer the element whose label looks like the Context indicator; among Images there is
        // normally exactly one, but if Codex adds a second icon the label decides.
        for (var i = 0; i < children.Count; i++)
        {
            var candidate = children[i];
            if (candidate.ControlType != "Image")
            {
                continue;
            }

            if (LooksLikeContextUsage(candidate.Name, candidate.ClassName))
            {
                contextIndex = i;
                contextElement = candidate.Element;
                break;
            }
        }

        if (contextElement is not null && !TryReadRect(contextElement, out _))
        {
            contextElement = null;
            contextIndex = -1;
        }

        reference = contextElement;

        var contextRect = contextIndex >= 0 ? children[contextIndex].Rect : default;

        // Model selector: a Button (possibly one level down inside a display:contents group) that
        // exposes ExpandCollapse — i.e. it opens a menu — lying to the right of the Context icon.
        model = null;
        var modelRect = default(IntRect);
        var modelFound = false;
        foreach (var candidate in children)
        {
            if (!TryExpandableButton(candidate.Element, walker, out var button, out var buttonRect))
            {
                continue;
            }

            if (!contextRect.IsEmpty && buttonRect.Left < contextRect.Left)
            {
                continue;   // the permission button, which also expands
            }

            if (!modelFound || buttonRect.Left < modelRect.Left)
            {
                model = button;
                modelRect = buttonRect;
                modelFound = true;
            }
        }

        if (model is not null && !TryReadRect(model, out modelRect))
        {
            model = null;
            modelFound = false;
        }

        // Left cluster: the rightmost toolbar-row control that sits left of the Context indicator.
        var leftClusterRight = 0;
        var rowTop = contextRect.IsEmpty ? modelRect.Y : contextRect.Y;
        var rowBottom = contextRect.IsEmpty ? modelRect.Bottom : contextRect.Bottom;
        if (!modelRect.IsEmpty)
        {
            rowTop = Math.Min(rowTop, modelRect.Y);
            rowBottom = Math.Max(rowBottom, modelRect.Bottom);
        }

        foreach (var candidate in children)
        {
            if (contextIndex >= 0 && candidate.Index >= contextIndex)
            {
                continue;
            }

            if (candidate.Rect.IsEmpty || candidate.Rect.Height <= 1)
            {
                continue;   // the editor itself spans the whole card
            }

            // Only real controls count as the barrier. The composer's `display: contents` wrappers have
            // no layout box, so Chromium synthesises a rectangle for them that is wider than any of
            // their children — counting one would shrink the width budget by a hundred pixels or more
            // and hide a strip that had room all along.
            if (candidate.ControlType is not ("Button" or "Image"))
            {
                continue;
            }

            var overlapsRow = candidate.Rect.Bottom > rowTop && candidate.Rect.Y < rowBottom;
            if (!overlapsRow)
            {
                continue;
            }

            leftClusterRight = Math.Max(leftClusterRight, candidate.Rect.Right);
        }

        var source = ComposerReferenceSource.None;
        var referenceRect = default(IntRect);
        var elementName = string.Empty;
        var elementAutomationId = string.Empty;
        var elementControlType = string.Empty;
        var elementClassName = string.Empty;

        if (!contextRect.IsEmpty)
        {
            source = ComposerReferenceSource.UiaContext;
            referenceRect = contextRect;
            if (contextIndex >= 0)
            {
                elementName = children[contextIndex].Name;
                elementAutomationId = children[contextIndex].AutomationId;
                elementControlType = children[contextIndex].ControlType;
                elementClassName = children[contextIndex].ClassName;
            }
        }
        else if (!modelRect.IsEmpty)
        {
            // Level 2: the model selector's left edge, minus the measured distance from the Context
            // indicator's left edge to it. The layout adds that reservation back.
            source = ComposerReferenceSource.UiaModel;
            referenceRect = modelRect;
        }
        else
        {
            // Level 3: the composer card itself; the toolbar row is derived from its bottom edge.
            source = ComposerReferenceSource.UiaComposer;
            referenceRect = composerRect;
        }

        return new ComposerDockSnapshot(
            source,
            referenceRect,
            modelRect.IsEmpty ? contextRect : modelRect,
            composerRect,
            // The left cluster is recorded as a rectangle spanning from the card's left edge to the
            // right edge of its last toolbar-row control. Its `.Right` is what the responsive policy
            // treats as the barrier the strip must not cross.
            leftClusterRight > composerRect.Left
                ? new IntRect(
                    composerRect.Left,
                    rowTop,
                    leftClusterRight - composerRect.Left,
                    Math.Max(1, rowBottom - rowTop))
                : default,
            elementName,
            elementAutomationId,
            elementControlType,
            elementClassName,
            hwnd.ToInt64(),
            contextIndex,
            null);
    }

    private readonly record struct ChildInfo(
        AutomationElement Element,
        int Index,
        IntRect Rect,
        string ControlType,
        string Name,
        string AutomationId,
        string ClassName);

    private static bool LooksLikeContextUsage(string name, string className)
    {
        foreach (var hint in ContextNameHints)
        {
            if (name.Contains(hint, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return className.Contains(ContextClassHint, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The composer card: the ControlView parent of the ProseMirror contenteditable. The card has no
    /// AutomationId and no stable class of its own, but its editor does, and the relationship is
    /// structural rather than textual.
    /// </summary>
    private static AutomationElement? FindComposer(AutomationElement root, TreeWalker walker)
    {
        var stack = new Stack<(AutomationElement Element, int Depth)>();
        stack.Push((root, 0));
        var visited = 0;
        while (stack.Count > 0 && visited < 4000)
        {
            var (element, depth) = stack.Pop();
            if (depth > 30)
            {
                continue;
            }

            var child = TryFirstChild(walker, element);
            while (child is not null)
            {
                visited++;
                if (TryReadProperties(child, out _, out _, out _, out var childClass)
                    && string.Equals(childClass, ComposerEditorClass, StringComparison.Ordinal))
                {
                    return TryParent(walker, child);
                }

                stack.Push((child, depth + 1));
                child = TryNextSibling(walker, child);
            }
        }

        return null;
    }

    /// <summary>
    /// A menu-opening button inside a composer child. The model selector's AutomationId is a
    /// Radix-generated <c>radix-_r_NN_</c> that changes between renders, and its Name contains the
    /// model name, which changes when the user switches models — so neither is used. The
    /// ExpandCollapse pattern plus the geometry is the stable signal.
    /// </summary>
    private static bool TryExpandableButton(
        AutomationElement element,
        TreeWalker walker,
        out AutomationElement button,
        out IntRect rect)
    {
        button = element;
        rect = default;
        var type = TryControlType(element);
        if (type == "Button" && IsExpandable(element) && TryReadRect(element, out rect))
        {
            return true;
        }

        // One level down: the selector lives inside a `display: contents` group.
        var child = TryFirstChild(walker, element);
        var guard = 0;
        while (child is not null && guard < 8)
        {
            if (string.Equals(TryControlType(child), "Button", StringComparison.Ordinal)
                && IsExpandable(child)
                && TryReadRect(child, out rect))
            {
                button = child;
                return true;
            }

            child = TryNextSibling(walker, child);
            guard++;
        }

        return false;
    }

    private static bool IsExpandable(AutomationElement element)
    {
        try
        {
            return element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out _);
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ small UIA helpers
    //
    // Every property read crosses into the target process, so each one is individually guarded: a
    // node that disappears between two calls must degrade the snapshot, not abort the walk.

    private static AutomationElement? TryFirstChild(TreeWalker walker, AutomationElement element)
    {
        try
        {
            return walker.GetFirstChild(element);
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static AutomationElement? TryNextSibling(TreeWalker walker, AutomationElement element)
    {
        try
        {
            return walker.GetNextSibling(element);
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static AutomationElement? TryParent(TreeWalker walker, AutomationElement element)
    {
        try
        {
            return walker.GetParent(element);
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string TryControlType(AutomationElement element)
    {
        try
        {
            return element.Current.ControlType.ProgrammaticName.Replace("ControlType.", string.Empty, StringComparison.Ordinal);
        }
        catch (ElementNotAvailableException)
        {
            return string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static bool TryReadProperties(
        AutomationElement element,
        out string controlType,
        out string name,
        out string automationId,
        out string className)
    {
        controlType = string.Empty;
        name = string.Empty;
        automationId = string.Empty;
        className = string.Empty;
        try
        {
            var current = element.Current;
            controlType = current.ControlType.ProgrammaticName.Replace(
                "ControlType.",
                string.Empty,
                StringComparison.Ordinal);
            name = current.Name ?? string.Empty;
            automationId = current.AutomationId ?? string.Empty;
            className = current.ClassName ?? string.Empty;
            return true;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryReadRect(AutomationElement element, out IntRect rect)
    {
        rect = default;
        try
        {
            var bounds = element.Current.BoundingRectangle;
            if (double.IsNaN(bounds.X) || double.IsInfinity(bounds.X)
                || double.IsNaN(bounds.Y) || double.IsInfinity(bounds.Y)
                || double.IsNaN(bounds.Width) || double.IsInfinity(bounds.Width)
                || double.IsNaN(bounds.Height) || double.IsInfinity(bounds.Height))
            {
                return false;
            }

            rect = new IntRect(
                (int)Math.Round(bounds.X),
                (int)Math.Round(bounds.Y),
                (int)Math.Round(bounds.Width),
                (int)Math.Round(bounds.Height));

            // Windows parks a minimised or hidden window at (-32000, -32000), and Chromium lays its
            // tree out around the same sentinel. A reference there is not a reference.
            return !rect.IsEmpty && !OverlayLayoutCalculator.IsUnusableRect(rect);
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
