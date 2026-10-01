using Microsoft.Win32;

namespace CodexStatusbar;

internal enum OverlayThemeKind
{
    Dark,
    Light
}

internal sealed record OverlayThemePalette(
    Color Background,
    Color Label,
    Color Value,
    Color Accent,
    Color Border,
    Color Divider,
    Color ProgressTrack,
    Color ProgressStart,
    Color ProgressEnd,
    Color TargetHighlight,
    Color TextShadow,
    Color Hint,
    Color EditFill,
    Color EditOutline)
{
    private static readonly OverlayThemePalette DarkPalette = new(
        Color.FromArgb(36, 38, 45),
        Color.FromArgb(157, 161, 170),
        // Codex's own --color-text in dark, which resolves through --app-color-text-foreground to
        // --gray-fixed-150 (#dfdfdf). Not eyeballed: taken from the CSS token the composer labels use.
        Color.FromArgb(223, 223, 223),
        Color.FromArgb(185, 174, 255),
        Color.FromArgb(36, 255, 255, 255),
        Color.FromArgb(80, 84, 93),
        Color.FromArgb(70, 74, 83),
        Color.FromArgb(142, 126, 255),
        Color.FromArgb(181, 169, 255),
        Color.FromArgb(142, 126, 255),
        Color.FromArgb(150, 0, 0, 0),
        Color.FromArgb(235, 245, 245, 247),
        // Edit mode only: a barely-there wash plus an outline that is legible on light *and* dark
        // backgrounds. It has to be visible enough to say "you can drag this" without becoming the
        // capsule that was just removed.
        Color.FromArgb(30, 255, 255, 255),
        Color.FromArgb(125, 255, 255, 255));

    private static readonly OverlayThemePalette LightPalette = new(
        Color.FromArgb(244, 244, 246),
        Color.FromArgb(92, 96, 105),
        // Codex's own --color-text in light: --app-color-text-foreground = #1a1c1f. This is the exact
        // colour measured off the model selector's label on screen (darkest ink pixel rgb(26,28,31)),
        // so a docked strip's text matches the control it sits beside rather than merely resembling it.
        Color.FromArgb(26, 28, 31),
        Color.FromArgb(91, 72, 190),
        Color.FromArgb(32, 0, 0, 0),
        Color.FromArgb(208, 210, 216),
        Color.FromArgb(221, 222, 227),
        Color.FromArgb(111, 91, 218),
        Color.FromArgb(150, 132, 232),
        Color.FromArgb(111, 91, 218),
        Color.FromArgb(165, 255, 255, 255),
        Color.FromArgb(235, 24, 25, 29),
        Color.FromArgb(24, 0, 0, 0),
        Color.FromArgb(115, 0, 0, 0));

    public static OverlayThemePalette For(OverlayThemeKind kind) =>
        kind == OverlayThemeKind.Light ? LightPalette : DarkPalette;

    /// <summary>
    /// The 1-pixel drop shadow that lifts the glyphs off a busy background. It is always the opposite
    /// of the text it sits under — light text gets a dark shadow, dark text gets a light one — so the
    /// same rule works for every foreground and for both themes without a per-colour table. Alpha is
    /// deliberately low: this should read as "floating", not as an outline.
    /// </summary>
    public static Color ShadowFor(Color foreground, double alphaScale = 1d)
    {
        var luminance = ((0.299 * foreground.R) + (0.587 * foreground.G) + (0.114 * foreground.B)) / 255d;
        var baseAlpha = luminance >= 0.5 ? 150 : 165;
        var alpha = Math.Clamp((int)Math.Round(baseAlpha * alphaScale), 0, 255);
        return luminance >= 0.5
            ? Color.FromArgb(alpha, 0, 0, 0)
            : Color.FromArgb(alpha, 255, 255, 255);
    }
}

internal interface IOverlayThemeSource : IDisposable
{
    OverlayThemeKind Current { get; }
    event EventHandler? Changed;
}

internal sealed class WindowsOverlayThemeSource : IOverlayThemeSource
{
    private const string PersonalizeRegistryPath =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValueName = "AppsUseLightTheme";

    private readonly object _gate = new();
    private readonly Func<object?> _readValue;
    private readonly Action<UserPreferenceChangedEventHandler> _unsubscribe;
    private readonly UserPreferenceChangedEventHandler _preferenceChangedHandler;
    private EventHandler? _changed;
    private OverlayThemeKind _current;
    private bool _subscribed;
    private bool _disposed;

    public WindowsOverlayThemeSource()
        : this(
            ReadRegistryValue,
            handler => SystemEvents.UserPreferenceChanged += handler,
            handler => SystemEvents.UserPreferenceChanged -= handler)
    {
    }

    internal WindowsOverlayThemeSource(
        Func<object?> readValue,
        Action<UserPreferenceChangedEventHandler> subscribe,
        Action<UserPreferenceChangedEventHandler> unsubscribe)
    {
        ArgumentNullException.ThrowIfNull(readValue);
        ArgumentNullException.ThrowIfNull(subscribe);
        ArgumentNullException.ThrowIfNull(unsubscribe);

        _readValue = readValue;
        _unsubscribe = unsubscribe;
        _preferenceChangedHandler = HandleUserPreferenceChanged;
        _current = ReadKind(_readValue);
        try
        {
            subscribe(_preferenceChangedHandler);
            _subscribed = true;
        }
        catch
        {
            _subscribed = false;
        }
    }

    public OverlayThemeKind Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public event EventHandler? Changed
    {
        add
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    _changed += value;
                }
            }
        }
        remove
        {
            lock (_gate)
            {
                _changed -= value;
            }
        }
    }

    public static OverlayThemeKind ResolveKind(object? value) => value switch
    {
        byte numeric => numeric == 0 ? OverlayThemeKind.Dark : OverlayThemeKind.Light,
        sbyte numeric => numeric == 0 ? OverlayThemeKind.Dark : OverlayThemeKind.Light,
        short numeric => numeric == 0 ? OverlayThemeKind.Dark : OverlayThemeKind.Light,
        ushort numeric => numeric == 0 ? OverlayThemeKind.Dark : OverlayThemeKind.Light,
        int numeric => numeric == 0 ? OverlayThemeKind.Dark : OverlayThemeKind.Light,
        uint numeric => numeric == 0 ? OverlayThemeKind.Dark : OverlayThemeKind.Light,
        long numeric => numeric == 0 ? OverlayThemeKind.Dark : OverlayThemeKind.Light,
        ulong numeric => numeric == 0 ? OverlayThemeKind.Dark : OverlayThemeKind.Light,
        _ => OverlayThemeKind.Dark
    };

    public static OverlayThemeKind ReadKind(Func<object?> readValue)
    {
        ArgumentNullException.ThrowIfNull(readValue);
        try
        {
            return ResolveKind(readValue());
        }
        catch
        {
            return OverlayThemeKind.Dark;
        }
    }

    internal void Refresh()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        var next = ReadKind(_readValue);
        EventHandler? changed;
        lock (_gate)
        {
            if (_disposed || next == _current)
            {
                return;
            }

            _current = next;
            changed = _changed;
        }

        changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        var shouldUnsubscribe = false;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            shouldUnsubscribe = _subscribed;
            _subscribed = false;
            _changed = null;
        }

        if (!shouldUnsubscribe)
        {
            return;
        }

        try
        {
            _unsubscribe(_preferenceChangedHandler);
        }
        catch
        {
            // Theme observation is best-effort and must not terminate the overlay.
        }
    }

    private static object? ReadRegistryValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeRegistryPath);
        return key?.GetValue(AppsUseLightThemeValueName);
    }

    private void HandleUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        Refresh();
    }
}

internal sealed class OverlayThemeBinding : IDisposable
{
    private readonly object _gate = new();
    private readonly Control _dispatcher;
    private readonly IOverlayThemeSource _source;
    private readonly Action<OverlayThemePalette> _apply;
    private readonly int _dispatcherThreadId;
    private OverlayThemeKind _desiredKind;
    private OverlayThemeKind? _lastAppliedKind;
    private bool _callbackPending;
    private bool _disposed;

    public OverlayThemeBinding(
        Control dispatcher,
        IOverlayThemeSource source,
        Action<OverlayThemePalette> apply)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(apply);

        _dispatcher = dispatcher;
        _source = source;
        _apply = apply;
        _dispatcherThreadId = Environment.CurrentManagedThreadId;
        _desiredKind = source.Current;
        _source.Changed += HandleThemeChanged;
        RequestApply(_desiredKind);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _callbackPending = false;
        }

        _source.Changed -= HandleThemeChanged;
        _source.Dispose();
    }

    private void HandleThemeChanged(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        RequestApply(_source.Current);
    }

    private void RequestApply(OverlayThemeKind kind)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _desiredKind = kind;
            if (_callbackPending || _lastAppliedKind == kind)
            {
                return;
            }

            _callbackPending = true;
        }

        if (!_dispatcher.InvokeRequired
            && Environment.CurrentManagedThreadId == _dispatcherThreadId)
        {
            ApplyPending(requireHandle: false);
            return;
        }

        if (_dispatcher.IsDisposed
            || _dispatcher.Disposing
            || !_dispatcher.IsHandleCreated)
        {
            CancelPending();
            return;
        }

        try
        {
            _dispatcher.BeginInvoke((Action)(() => ApplyPending(requireHandle: true)));
        }
        catch (ObjectDisposedException)
        {
            CancelPending();
        }
        catch (InvalidOperationException)
        {
            CancelPending();
        }
    }

    private void ApplyPending(bool requireHandle)
    {
        if (_dispatcher.IsDisposed
            || _dispatcher.Disposing
            || (requireHandle && !_dispatcher.IsHandleCreated))
        {
            CancelPending();
            return;
        }

        OverlayThemeKind kind;
        lock (_gate)
        {
            if (_disposed)
            {
                _callbackPending = false;
                return;
            }

            kind = _desiredKind;
            _callbackPending = false;
            if (_lastAppliedKind == kind)
            {
                return;
            }

            _lastAppliedKind = kind;
        }

        _apply(OverlayThemePalette.For(kind));
    }

    private void CancelPending()
    {
        lock (_gate)
        {
            _callbackPending = false;
        }
    }
}
