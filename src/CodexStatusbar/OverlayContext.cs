using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CodexStatusbar;

internal sealed class OverlayContext : ApplicationContext, IFastPathTarget
{
    private readonly OverlaySettings _settings;
    private readonly CodexIpcActiveThreadMonitor _routeMonitor = new();
    private readonly CodexProcessWatcher _watcher;
    private readonly LifecycleController _lifecycle;
    private readonly bool _background;
    private readonly string _modeLabel;
    private readonly string _sessionRoot;
    private readonly string _executablePath;
    private TokenLogMonitor? _monitor;
    private string _startupOutcome = "not evaluated";
    private bool _startupRegistryEnabled;
    private ToolStripMenuItem _statusCodexItem = null!;
    private ToolStripMenuItem _statusBarItem = null!;
    private ToolStripMenuItem _startWithWindowsItem = null!;
    private string _lifecycleMenuText = string.Empty;
    private CodexProcessInfo _lastLoggedCodex = CodexProcessInfo.None;
    private DateTime _lastLifecycleBlockUtc = DateTime.MinValue;
    private readonly TokenStripForm _form = new();
    private readonly AttachmentTargetHighlightForm _targetHighlight = new();
    private readonly OverlayThemeBinding _themeBinding;
    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _outsideClickTimer;
    private readonly ToolStripMenuItem _sessionMenuItem;
    private readonly ToolStripMenuItem _visibilityMenuItem;
    private readonly ToolStripMenuItem _pinSessionMenuItem;
    private readonly ToolStripMenuItem _adjustManualMenuItem;
    private readonly ToolStripMenuItem _saveManualMenuItem;
    private readonly ToolStripMenuItem _cancelManualMenuItem;
    private readonly ToolStripMenuItem _resetManualMenuItem;
    private readonly ToolStripMenuItem _traditionalMenuItem;
    private readonly ToolStripMenuItem _positionInfoMenuItem;
    private readonly ToolStripMenuItem _transparencyMenuItem;
    private readonly ToolStripMenuItem? _textShadowMenuItem;
    private readonly ToolStripMenuItem _detailMenuItem;
    private readonly Dictionary<AttachmentReferencePoint, ToolStripMenuItem> _anchorPointItems = new();
    private readonly Dictionary<OverlayPositionMode, ToolStripMenuItem> _positionModeItems = new();
    private readonly Dictionary<OverlayThemePreference, ToolStripMenuItem> _themeItems = new();
    private readonly Dictionary<AnchorMode, ToolStripMenuItem> _anchorItems = new();
    private readonly Dictionary<DisplayField, ToolStripMenuItem> _fieldItems = new();
    private readonly Dictionary<(CollapsedSlot Slot, DisplayField Field), ToolStripMenuItem>
        _collapsedFieldItems = new();
    private readonly OverlayInteractionState _interaction = new();
    private readonly ActiveRouteThreadState _activeRouteThread = new();
    private readonly OverlayAnchorTargetState _anchorTargetState = new();
    private readonly ManualAttachmentCoordinator _manualAttachment = new();
    private readonly string? _settingsPath;
    private readonly DebugDiagnostics _debug;
    private readonly string? _forcedThreadId;
    private readonly GlobalHotkey _hotkey = new();
    private readonly CodexThemeSource _codexTheme = new();
    private readonly ComposerDockTracker _dockTracker;
    private ComposerDockSnapshot _lastDock = ComposerDockSnapshot.Empty;

    // ---------------------------------------------------------------- position fast path
    //
    // The strip used to be repositioned only on the UI tick (350 ms in the active state), which is
    // exactly the delay a person sees when dragging Codex: the window moves, the strip stays, and a
    // third of a second later it catches up. The pipeline below splits that into two channels — cheap
    // Win32 geometry driven by window events, and the existing UI Automation calibration — and only
    // the cheap one runs at frame rate.
    private readonly PositionScheduler _positionScheduler = new();
    private readonly HostWindowSampler _hostSampler = new();
    private readonly System.Windows.Forms.Timer _positionTimer;
    private bool _positionTimerRunning;
    private bool _inTick;

    /// <summary>The dock snapshot and the host rectangle it was measured against, paired by revision.
    /// Every rectangle inside a snapshot is an absolute screen coordinate, so re-anchoring it to the
    /// current host rectangle is what turns a stale measurement into a correct prediction.</summary>
    private long _observedDockRevision = -1;
    private ComposerDockSnapshot _dockSnapshot = ComposerDockSnapshot.Empty;
    private IntRect _dockSnapshotHost;

    /// <summary>The host rectangle the *siblings* (row, composer, left cluster) were measured against,
    /// i.e. the anchor of the last full discovery pass. The reference rectangle has its own, newer one.</summary>
    private IntRect _siblingAnchorHost;

    private long _observedWalkCount = -1;
    private DateTime _lastDockRevisionChangeUtc = DateTime.MinValue;
    private long _lastReportedFastReads;
    private long _lastReportedWalks;
    private int _lastReportedSetWindowPos;

    /// <summary>Where the pipeline last told Windows to put the strip, for the position-error report.</summary>
    private IntRect _lastDesiredWindowBounds;
    private DateTime _lastImmediateTickUtc = DateTime.MinValue;
    private DateTime _lastPositionReportUtc = DateTime.MinValue;
    private PositionUpdateMode _lastReportedPositionMode = PositionUpdateMode.Idle;
    private long _lastReportedNotes = -1;
    private bool _positionDiagnostics;
    private ToolStripMenuItem _positionDiagnosticsMenuItem = null!;
    private IntRect _lastDockReferenceRect;
    private int _responsiveLevel;
    private OverlayThemePalette _autoThemePalette = OverlayThemePalette.For(OverlayThemeKind.Dark);
    private OverlayThemeKind _effectiveTheme = OverlayThemeKind.Dark;
    private string _effectiveThemeSource = "Windows";
    private OverlayPresentation _presentation;
    private CodexWindowTarget? _currentTarget;
    private ManualPlacementSnapshot? _settingsSnapshotBeforeEdit;
    private bool _saveFailureNotified;
    private TokenSnapshot? _lastSnapshot;
    private bool _manuallyHidden;
    private int _pollInFlight;
    private int _disposed;
    private TokenSnapshot? _pendingSnapshot;
    private long _pendingSessionVersion = -1;
    private string? _pendingThreadId;
    private long _observedSessionVersion = -1;
    private string? _observedThreadId;
    private ActiveThreadRouteStatus _pendingRouteStatus = new(null, 0, false, 0, null);
    private long _observedRouteVersion = -1;
    private string? _lastPositionSignature;
    private int _themeRefreshTicks;
    private bool _menuReady;

    public OverlayContext(
        string sessionRoot,
        string? settingsPath = null,
        DebugDiagnostics? debug = null,
        string? forcedThreadId = null,
        bool background = false,
        bool positionDiagnostics = false)
    {
        _settingsPath = settingsPath;
        _debug = debug ?? new DebugDiagnostics(false);
        _forcedThreadId = forcedThreadId;
        _background = background;
        _positionDiagnostics = positionDiagnostics;
        _modeLabel = background ? "background" : "interactive";
        _sessionRoot = sessionRoot;
        _executablePath = StartupCommandLine.ResolveExecutablePath();

        _settings = OverlaySettings.Load(_settingsPath);

        // "Start with Windows" is reconciled once per launch, here, in both modes. This is what makes
        // the first run of this build opt in, and what upgrades a stale Run path after the exe moves.
        ApplyStartupRegistration();

        _lifecycle = new LifecycleController(_debug);
        _lifecycle.AttachRequested += AttachSubsystems;
        _lifecycle.DetachRequested += DetachSubsystems;
        _watcher = new CodexProcessWatcher();
        _watcher.Changed += OnCodexProcessChanged;
        _debug.Event($"watcher started · mode {_modeLabel} · primary instance");

        // §10: detect an already-running Codex before the first tick, so starting the watcher while
        // Codex is open attaches immediately instead of waiting for a future process-start event.
        _watcher.DetectNow();

        _dockTracker = new ComposerDockTracker(
            () => _currentTarget?.HostWindow.Handle ?? IntPtr.Zero)
        {
            // Off until the lifecycle attaches. Voting the accessibility tree is only worthwhile once
            // there is a Codex conversation to dock to; while waiting this stays silent so an idle
            // machine does no UIA work at all.
            Enabled = false
        };
        _dockTracker.Start();
        _presentation = OverlayPresentationBuilder.CreateWaiting(
            "正在寻找当前 Codex 会话…",
            _settings.CollapsedPrimaryField,
            _settings.CollapsedSecondaryField,
            _settings.VisibleFields);
        _ = _targetHighlight.Handle;
        _themeBinding = new OverlayThemeBinding(
            _targetHighlight,
            new WindowsOverlayThemeSource(),
            ApplyTheme);

        var menu = new ContextMenuStrip();

        // Lifecycle status first: with the watcher running these two lines answer "is Codex up, and is
        // the strip attached to it" without opening the debug log.
        _statusCodexItem = new ToolStripMenuItem("Codex：检测中…") { Enabled = false };
        _statusBarItem = new ToolStripMenuItem("状态条：启动中…") { Enabled = false };
        var statusMenu = new ToolStripMenuItem("Status  ·  状态");
        statusMenu.DropDownItems.Add(_statusCodexItem);
        statusMenu.DropDownItems.Add(_statusBarItem);
        menu.Items.Add(statusMenu);
        menu.Items.Add(new ToolStripSeparator());

        _sessionMenuItem = new ToolStripMenuItem("会话：等待数据") { Enabled = false };
        menu.Items.Add(_sessionMenuItem);
        _pinSessionMenuItem = new ToolStripMenuItem("锁定当前会话") { Enabled = false, CheckOnClick = true };
        _pinSessionMenuItem.CheckedChanged += (_, _) =>
        {
            if (_monitor is { } monitor)
            {
                monitor.PinActiveSession = _pinSessionMenuItem.Checked;
            }

            _pinSessionMenuItem.Text = _pinSessionMenuItem.Checked ? "已锁定当前会话" : "锁定当前会话";
        };
        menu.Items.Add(_pinSessionMenuItem);
        menu.Items.Add(new ToolStripSeparator());

        _adjustManualMenuItem = new ToolStripMenuItem("Adjust Position  ·  调整位置");
        _adjustManualMenuItem.Click += (_, _) => BeginManualEditing();
        menu.Items.Add(_adjustManualMenuItem);
        _saveManualMenuItem = new ToolStripMenuItem("Lock Position  ·  锁定位置") { Visible = false };
        _saveManualMenuItem.Click += (_, _) => SaveManualEditing();
        menu.Items.Add(_saveManualMenuItem);
        _cancelManualMenuItem = new ToolStripMenuItem("取消调整") { Visible = false };
        _cancelManualMenuItem.Click += (_, _) => CancelManualEditing();
        menu.Items.Add(_cancelManualMenuItem);
        _resetManualMenuItem = new ToolStripMenuItem("Reset Position  ·  重置位置");
        _resetManualMenuItem.Click += (_, _) => ResetManualPlacement();
        menu.Items.Add(_resetManualMenuItem);

        _positionInfoMenuItem = new ToolStripMenuItem("位置：--") { Enabled = false };
        menu.Items.Add(_positionInfoMenuItem);

        // The manual counterpart of --position-fast-debug: it only changes how often the
        // [position-performance] block is summarised, so it stays useful without a restart. It needs the
        // debug log to exist at all, so without --debug it says so rather than looking broken.
        _positionDiagnosticsMenuItem = new ToolStripMenuItem("Position diagnostics  ·  位置诊断")
        {
            CheckOnClick = true,
            Enabled = _debug.Enabled,
            Checked = _positionDiagnostics
        };
        if (!_debug.Enabled)
        {
            _positionDiagnosticsMenuItem.Text += "（需要 --debug）";
        }

        _positionDiagnosticsMenuItem.CheckedChanged += (_, _) =>
        {
            _positionDiagnostics = _positionDiagnosticsMenuItem.Checked;
            _lastPositionReportUtc = DateTime.MinValue;
            _debug.Event($"position diagnostics {(_positionDiagnostics ? "on (1 s summaries)" : "off")}");
            ReportPositionPerformance(DateTime.UtcNow);
        };
        menu.Items.Add(_positionDiagnosticsMenuItem);

        var anchorMenu = new ToolStripMenuItem("Anchor  ·  锚点");
        foreach (var anchor in AnchorPoints)
        {
            AddAnchorPointMenu(anchorMenu, anchor);
        }
        menu.Items.Add(anchorMenu);

        var positionModeMenu = new ToolStripMenuItem("Position  ·  定位");
        AddPositionModeMenu(
            positionModeMenu,
            "Dock left of Context  ·  停靠到 Context 左侧",
            OverlayPositionMode.ComposerContextLeft);
        AddPositionModeMenu(
            positionModeMenu,
            "Manual position  ·  手动位置",
            OverlayPositionMode.FollowCodex);
        AddPositionModeMenu(
            positionModeMenu,
            "Fixed on Screen  ·  固定屏幕位置",
            OverlayPositionMode.FixedScreen);
        menu.Items.Add(positionModeMenu);

        var themeMenu = new ToolStripMenuItem("主题");
        AddThemeMenu(themeMenu, "Auto  ·  跟随系统", OverlayThemePreference.Auto);
        AddThemeMenu(themeMenu, "Dark  ·  深色文字", OverlayThemePreference.Dark);
        AddThemeMenu(themeMenu, "Light  ·  浅色文字", OverlayThemePreference.Light);
        menu.Items.Add(themeMenu);

        _transparencyMenuItem = new ToolStripMenuItem("透明背景") { CheckOnClick = true };
        _transparencyMenuItem.Checked = _settings.TransparentBackground;
        _transparencyMenuItem.CheckedChanged += (_, _) =>
        {
            _settings.TransparentBackground = _transparencyMenuItem.Checked;
            _settings.Save(_settingsPath);
            RefreshTransparencyMode();
        };
        menu.Items.Add(_transparencyMenuItem);

        // Off by default. Docked inside the composer the strip uses Codex's own text colour on Codex's
        // own background, so the shadow adds nothing but a halo; this exists so it can be turned back
        // on if the strip is ever moved somewhere it disagrees with its backdrop.
        _textShadowMenuItem = new ToolStripMenuItem("文字阴影") { CheckOnClick = true };
        _textShadowMenuItem.Checked = _settings.TextShadow;
        _textShadowMenuItem.CheckedChanged += (_, _) =>
        {
            _settings.TextShadow = _textShadowMenuItem.Checked;
            _settings.Save(_settingsPath);
            _form.SetTextShadow(_settings.TextShadow);
        };
        menu.Items.Add(_textShadowMenuItem);
        menu.Items.Add(new ToolStripSeparator());

        _traditionalMenuItem = new ToolStripMenuItem("传统定位");
        AddAnchorMenu(_traditionalMenuItem, "标题栏右上", AnchorMode.TitleBarTopRight);
        AddAnchorMenu(_traditionalMenuItem, "自动吸附", AnchorMode.Auto);
        AddAnchorMenu(_traditionalMenuItem, "窗口内右上", AnchorMode.InsideTopRight);
        AddAnchorMenu(_traditionalMenuItem, "窗口内右下", AnchorMode.InsideBottomRight);
        menu.Items.Add(_traditionalMenuItem);
        menu.Items.Add(new ToolStripSeparator());

        var collapsedFieldsMenu = new ToolStripMenuItem("收起时显示");
        var primaryMenu = new ToolStripMenuItem("左侧指标");
        var secondaryMenu = new ToolStripMenuItem("右侧指标");
        foreach (var field in DisplayFieldRules.Ordered)
        {
            var text = OverlayPresentationBuilder.GetFieldMenuText(field);
            AddCollapsedFieldMenu(primaryMenu, text, CollapsedSlot.Primary, field);
            AddCollapsedFieldMenu(secondaryMenu, text, CollapsedSlot.Secondary, field);
        }
        collapsedFieldsMenu.DropDownItems.Add(primaryMenu);
        collapsedFieldsMenu.DropDownItems.Add(secondaryMenu);
        menu.Items.Add(collapsedFieldsMenu);

        var fieldsMenu = new ToolStripMenuItem("显示字段");
        foreach (var field in DisplayFieldRules.Ordered)
        {
            AddVisibleFieldMenu(fieldsMenu, OverlayPresentationBuilder.GetFieldMenuText(field), field);
        }
        menu.Items.Add(fieldsMenu);
        menu.Items.Add(new ToolStripSeparator());

        _detailMenuItem = new ToolStripMenuItem("详情面板  ·  展开 / 收起");
        _detailMenuItem.Click += (_, _) => ToggleDetailPanel();
        menu.Items.Add(_detailMenuItem);

        _visibilityMenuItem = new ToolStripMenuItem("暂时隐藏");
        _visibilityMenuItem.Click += (_, _) =>
        {
            if (_manualAttachment.IsEditing)
            {
                CancelManualEditing();
            }
            _manuallyHidden = !_manuallyHidden;
            _visibilityMenuItem.Text = _manuallyHidden ? "恢复显示" : "暂时隐藏";
            if (_manuallyHidden)
            {
                CollapseAndHide();
            }
            else
            {
                Tick();
            }
        };
        menu.Items.Add(_visibilityMenuItem);

        // Lifecycle controls. "Start with Windows" is a toggle onto HKCU\...\Run — no elevation, and
// the state shown is the registry's, not an intention.
        _startWithWindowsItem = new ToolStripMenuItem("Start with Windows  ·  开机自动启动")
        {
            CheckOnClick = true,
            Checked = _startupRegistryEnabled
        };
        _startWithWindowsItem.CheckedChanged += (_, _) => ToggleStartWithWindows(_startWithWindowsItem.Checked);
        menu.Items.Add(_startWithWindowsItem);

        var attachNowItem = new ToolStripMenuItem("Start / Attach now  ·  立即检测并附着");
        attachNowItem.Click += (_, _) => AttachNow();
        menu.Items.Add(attachNowItem);

        var restartItem = new ToolStripMenuItem("Restart Statusbar  ·  重启状态条");
        restartItem.Click += (_, _) => RestartOverlay();
        menu.Items.Add(restartItem);
        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitOverlay();
        menu.Items.Add(exitItem);

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Information,
            Text = "Codex Token 状态条",
            Visible = true,
            ContextMenuStrip = menu
        };

        _form.SetPresentation(_presentation);
        _form.SetTransparentBackground(_settings.TransparentBackground);
        _form.SetTextShadow(_settings.TextShadow);
        _form.CapsuleClicked += HandleCapsuleClicked;
        _form.EditPreviewChanged += HandleEditPreviewChanged;
        _form.EditGestureCompleted += HandleEditGestureCompleted;
        _form.EditSaveRequested += (_, _) => SaveManualEditing();
        _form.EditCancelRequested += (_, _) => CancelManualEditing();
        _hotkey.Pressed += (_, _) => ToggleManualEditing();
        _hotkey.TryRegister();
        _menuReady = true;
        UpdateAnchorChecks();
        UpdateManualMenuState();
        UpdateFieldChecks();
        UpdateCollapsedFieldChecks();

        _timer = new System.Windows.Forms.Timer { Interval = 350 };
        _timer.Tick += (_, _) => Tick();
        _outsideClickTimer = new System.Windows.Forms.Timer { Interval = 40 };
        _outsideClickTimer.Tick += (_, _) => PollOutsidePointer();

        // The position pipeline's own timer. It only exists while the host window is moving: it is
        // started by the first WinEvent of a burst and stopped again when the burst settles, so an idle
        // machine has exactly the timers it had before.
        _positionTimer = new System.Windows.Forms.Timer { Interval = PositionFastPathRules.FastIntervalMs };
        _positionTimer.Tick += (_, _) => OnPositionTimerTick();

        _timer.Start();
    }

    private void AddAnchorMenu(ToolStripMenuItem menu, string text, AnchorMode mode)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) =>
        {
            if (_manualAttachment.IsEditing)
            {
                CancelManualEditing();
            }
            _settings.ManualPlacementEnabled = false;
            _settings.AnchorMode = mode;
            _settings.Save(_settingsPath);
            UpdateAnchorChecks();
            UpdateManualMenuState();
            if (_currentTarget is not null && !_manuallyHidden)
            {
                ApplyLayout(_currentTarget);
            }
        };
        _anchorItems[mode] = item;
        menu.DropDownItems.Add(item);
    }

    private void AddVisibleFieldMenu(ToolStripMenuItem parent, string text, DisplayField field)
    {
        var item = new ToolStripMenuItem(text) { CheckOnClick = false };
        item.Click += (_, _) =>
        {
            var updated = _settings.VisibleFields.HasFlag(field)
                ? _settings.VisibleFields & ~field
                : _settings.VisibleFields | field;
            if (updated == DisplayField.None)
            {
                return;
            }

            _settings.VisibleFields = updated;
            _settings.Save(_settingsPath);
            UpdateFieldChecks();
            RefreshPresentation();
            if (_currentTarget is not null && !_manuallyHidden)
            {
                ApplyLayout(_currentTarget);
            }
        };
        _fieldItems[field] = item;
        parent.DropDownItems.Add(item);
    }

    private void AddCollapsedFieldMenu(
        ToolStripMenuItem parent,
        string text,
        CollapsedSlot slot,
        DisplayField field)
    {
        var item = new ToolStripMenuItem(text) { CheckOnClick = false };
        item.Click += (_, _) =>
        {
            if (!_settings.SelectCollapsedField(slot, field))
            {
                return;
            }

            _settings.Save(_settingsPath);
            UpdateCollapsedFieldChecks();
            RefreshPresentation();
            if (_currentTarget is not null && !_manuallyHidden)
            {
                ApplyLayout(_currentTarget);
            }
        };
        _collapsedFieldItems[(slot, field)] = item;
        parent.DropDownItems.Add(item);
    }

    private static readonly AttachmentReferencePoint[] AnchorPoints =
    [
        AttachmentReferencePoint.TopLeft,
        AttachmentReferencePoint.TopCenter,
        AttachmentReferencePoint.TopRight,
        AttachmentReferencePoint.LeftCenter,
        AttachmentReferencePoint.Center,
        AttachmentReferencePoint.RightCenter,
        AttachmentReferencePoint.BottomLeft,
        AttachmentReferencePoint.BottomCenter,
        AttachmentReferencePoint.BottomRight
    ];

    /// <summary>
    /// Explicitly re-anchors the strip. Unlike a drag (which captures whatever the user dropped), this
    /// is a deliberate "pin it here" action, so it uses a small inward margin for the chosen corner
    /// instead of carrying over an offset that belonged to a different corner.
    /// </summary>
    private void AddAnchorPointMenu(ToolStripMenuItem menu, AttachmentReferencePoint anchor)
    {
        var item = new ToolStripMenuItem(anchor.ToString());
        item.Click += (_, _) =>
        {
            if (_manualAttachment.IsEditing)
            {
                CancelManualEditing();
            }

            _settings.ManualPlacementEnabled = true;
            _settings.PositionMode = OverlayPositionMode.FollowCodex;
            _settings.MainAttachment = ManualAttachmentRules.OffsetForAnchor(anchor);
            _settings.Save(_settingsPath);
            UpdateManualMenuState();
            if (_currentTarget is not null && !_manuallyHidden)
            {
                ApplyLayout(_currentTarget);
            }
        };
        _anchorPointItems[anchor] = item;
        menu.DropDownItems.Add(item);
    }

    private void AddPositionModeMenu(ToolStripMenuItem menu, string text, OverlayPositionMode mode)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) =>
        {
            if (_manualAttachment.IsEditing)
            {
                CancelManualEditing();
            }

            if (mode == OverlayPositionMode.FixedScreen && _form.Visible && _form.CurrentLayout is { } layout)
            {
                // Adopt wherever the strip currently sits so switching modes does not make it jump.
                _settings.FixedScreenPosition = new Point(
                    _form.Left + layout.CapsuleBounds.X,
                    _form.Top + layout.CapsuleBounds.Y);
            }

            _settings.PositionMode = mode;
            _settings.ManualPlacementEnabled = true;
            _settings.Save(_settingsPath);

            // The accessibility tree is only queried while the strip is docked.
            _dockTracker.Enabled = mode == OverlayPositionMode.ComposerContextLeft;
            UpdateManualMenuState();
            if (_currentTarget is not null && !_manuallyHidden)
            {
                ApplyLayout(_currentTarget);
            }
        };
        _positionModeItems[mode] = item;
        menu.DropDownItems.Add(item);
    }

    private void AddThemeMenu(ToolStripMenuItem menu, string text, OverlayThemePreference preference)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) =>
        {
            _settings.ThemePreference = preference;
            _settings.Save(_settingsPath);
            UpdateManualMenuState();
            ApplyEffectiveTheme();
        };
        _themeItems[preference] = item;
        menu.DropDownItems.Add(item);
    }

    private void RefreshTransparencyMode()
    {
        _form.SetTransparentBackground(_settings.TransparentBackground);
        if (_currentTarget is not null && !_manuallyHidden)
        {
            ApplyLayout(_currentTarget);
        }
    }

    private void UpdateAnchorChecks()
    {
        foreach (var pair in _anchorItems)
        {
            pair.Value.Checked = pair.Key == _settings.AnchorMode;
        }
    }

    private void UpdateManualMenuState()
    {
        // The theme binding fires ApplyTheme during construction, before any of these items exist.
        if (!_menuReady)
        {
            return;
        }

        var editing = _manualAttachment.IsEditing;
        _adjustManualMenuItem.Visible = !editing;
        _adjustManualMenuItem.Enabled = !editing && _currentTarget is not null;
        _saveManualMenuItem.Visible = editing;
        _saveManualMenuItem.Enabled = editing && CanCommitEditPosition;
        _cancelManualMenuItem.Visible = editing;
        _cancelManualMenuItem.Enabled = editing;
        _resetManualMenuItem.Enabled = !editing;
        _traditionalMenuItem.Enabled = !editing;
        _detailMenuItem.Enabled = !editing;
        _transparencyMenuItem.Checked = _settings.TransparentBackground;
        if (_textShadowMenuItem is not null)
        {
            _textShadowMenuItem.Checked = _settings.TextShadow;
        }

        foreach (var pair in _anchorItems)
        {
            pair.Value.Checked = !_settings.ManualPlacementEnabled
                && pair.Key == _settings.AnchorMode;
        }

        foreach (var pair in _anchorPointItems)
        {
            pair.Value.Checked = _settings.ManualPlacementEnabled
                && _settings.PositionMode == OverlayPositionMode.FollowCodex
                && pair.Key == _settings.MainAttachment.ReferencePoint;
        }

        foreach (var pair in _positionModeItems)
        {
            pair.Value.Checked = pair.Key == _settings.PositionMode;
        }

        foreach (var pair in _themeItems)
        {
            pair.Value.Checked = pair.Key == _settings.ThemePreference;
            if (pair.Key == OverlayThemePreference.Auto)
            {
                // Show what auto actually resolved to, so a mismatch is visible without opening the log.
                pair.Value.Text = $"Auto  ·  跟随 Codex ({( _effectiveTheme == OverlayThemeKind.Light ? "Light" : "Dark")}" +
                    $"，来源 {_effectiveThemeSource})";
            }
        }

        var attachment = ManualAttachmentRules.SanitizeMain(_settings.MainAttachment);
        _positionInfoMenuItem.Text = _settings.PositionMode switch
        {
            OverlayPositionMode.ComposerContextLeft =>
                $"位置：停靠 Context 左侧 · 间距 {_settings.ContextGapDip:0} DIP · 来源 {_lastDock.DescribeSource()}",
            OverlayPositionMode.FixedScreen =>
                $"位置：Fixed on Screen ({_settings.FixedScreenPosition.X}, {_settings.FixedScreenPosition.Y}) px",
            _ => $"位置：Follow Codex · {attachment.ReferencePoint} · 偏移 ("
                + $"{attachment.OffsetXDip:0}, {attachment.OffsetYDip:0}) DIP"
        };
    }

    private void UpdateFieldChecks()
    {
        foreach (var pair in _fieldItems)
        {
            pair.Value.Checked = _settings.VisibleFields.HasFlag(pair.Key);
        }
    }

    private void UpdateCollapsedFieldChecks()
    {
        foreach (var pair in _collapsedFieldItems)
        {
            pair.Value.Checked = pair.Key.Slot switch
            {
                CollapsedSlot.Primary => pair.Key.Field == _settings.CollapsedPrimaryField,
                CollapsedSlot.Secondary => pair.Key.Field == _settings.CollapsedSecondaryField,
                _ => false
            };
        }
    }

    /// <summary>
    /// Reconciles <c>HKCU\...\Run</c> with the settings once per launch. The first launch of a build
    /// that has this feature opts in; every later launch only enforces what the user chose, and
    /// repairs the registered path if the exe has moved.
    /// </summary>
    private void ApplyStartupRegistration()
    {
        if (_executablePath.Length == 0)
        {
            _startupRegistryEnabled = StartupManager.Read() is not null;
            _startupOutcome = "startup registration: not evaluated (running under the .NET host)";
            return;
        }

        var result = StartupCoordinator.Apply(_settings, _executablePath, out var changed);
        _startupRegistryEnabled = result.RegistryEnabled;
        _startupOutcome = "startup registration: " + result.Outcome
            + (result.FirstRunOptIn ? " (first run; on by default, the tray can turn it off)" : string.Empty)
            + (result.Error is { Length: > 0 } error ? " — " + error : string.Empty);
        if (changed)
        {
            _settings.Save(_settingsPath);
        }
    }

    /// <summary>
    /// Logs the presence flips only. Which renderer happens to be the current anchor changes as Codex
    /// shuts down its helper processes, and logging every one of those would bury the two lines that
    /// matter; the anchor's PID is always visible in the <c>[lifecycle]</c> block anyway.
    /// </summary>
    private void OnCodexProcessChanged(CodexProcessInfo info)
    {
        var wasRunning = _lastLoggedCodex.IsRunning;
        _lastLoggedCodex = info;

        if (info.IsRunning && !wasRunning)
        {
            _debug.Event(
                $"Codex process detected PID={info.ProcessId} package={info.Package} version={info.Version}");
        }
        else if (!info.IsRunning && wasRunning)
        {
            _debug.Event("Codex process exited");
        }
    }

    /// <summary>
    /// Starts the Codex-specific subsystems: the IPC reader, the rollout reader and (in dock mode)
    /// the accessibility tracker. Called once per Codex process by the lifecycle and never while
    /// Codex is absent — that is what keeps an unattended machine at essentially zero CPU.
    /// </summary>
    private void AttachSubsystems(CodexProcessInfo info)
    {
        _routeMonitor.Start();
        _monitor ??= CreateMonitor();
        _dockTracker.Enabled = _settings.PositionMode == OverlayPositionMode.ComposerContextLeft;

        // Everything below belonged to a previous Codex process, or to none. Clearing it here is what
        // makes "close Codex, open Codex again" behave exactly like a first attach: no old HWND, no
        // old element, no old conversation, no old pipe state.
        _currentTarget = null;
        _lastDock = ComposerDockSnapshot.Empty;
        _lastDockReferenceRect = default;
        _lastSnapshot = null;
        _pendingSnapshot = null;
        _pendingSessionVersion = -1;
        _observedSessionVersion = -1;
        _pendingThreadId = null;
        _observedThreadId = null;
        _pendingRouteStatus = new ActiveThreadRouteStatus(null, 0, false, 0, null);
        _observedRouteVersion = -1;
        _lastPositionSignature = null;
        _pinSessionMenuItem.Enabled = false;
        _pinSessionMenuItem.Checked = false;
        _interaction.CollapseForHostChange();
        StopOutsideClickPolling();
        _form.Hide();
        RefreshPresentation();
        UpdateSessionMenuText();

        _debug.Event(
            $"subsystems started: ipc reader, rollout reader, dock tracker={(_dockTracker.Enabled ? "on" : "off")}");
        WriteLifecycleBlock();
    }

    private TokenLogMonitor CreateMonitor()
    {
        var monitor = new TokenLogMonitor(_sessionRoot);
        if (!string.IsNullOrWhiteSpace(_forcedThreadId))
        {
            monitor.PinActiveSession = true;
            monitor.IsForcedPin = true;
            monitor.PreferredThreadId = _forcedThreadId;
        }

        return monitor;
    }

    /// <summary>
    /// Stops every Codex-specific subsystem and hides the strip, without touching the watcher: the
    /// next Codex start must find a clean slate, and this process stays alive waiting for it.
    /// </summary>
    private void DetachSubsystems()
    {
        // The WinEvent subscription belongs to this Codex process: unhook it and forget the host
        // rectangle, so the next Codex start is a first attach in every respect.
        _hostSampler.Unbind();
        _positionScheduler.Reset();
        _observedDockRevision = -1;
        _dockSnapshotHost = default;
        _lastDesiredWindowBounds = default;
        EnsurePositionTimerState();
        _dockTracker.Enabled = false;
        _routeMonitor.Stop();
        _monitor?.Dispose();
        _monitor = null;
        CollapseAndHide();
        _currentTarget = null;
        _lastDock = ComposerDockSnapshot.Empty;
        _lastDockReferenceRect = default;
        _lastSnapshot = null;
        _pendingSnapshot = null;
        _pendingThreadId = null;
        _observedThreadId = null;
        _pendingSessionVersion = -1;
        _observedSessionVersion = -1;
        _pendingRouteStatus = new ActiveThreadRouteStatus(null, 0, false, 0, null);
        _observedRouteVersion = -1;
        _lastPositionSignature = null;
        _pinSessionMenuItem.Enabled = false;
        RefreshPresentation();
        _trayIcon.Text = TrimTrayText("Codex Statusbar — Waiting for Codex");
        _debug.Event("subsystems stopped · overlay hidden");
        WriteLifecycleBlock();
    }

    private void ApplyLifecycleTickInterval()
    {
        var interval = LifecycleRules.TickIntervalFor(_lifecycle.State);
        if (_timer.Interval != interval)
        {
            _timer.Interval = interval;
        }
    }

    // -------------------------------------------------------------------- position fast path

    /// <summary>
    /// Brings the position pipeline in line with the current state: subscribes to the host window's
    /// events, ages the burst state machine, sets the accurate path's cadence, and starts or stops the
    /// fast timer. Called at the end of every tick, so every path out of the tick — placing, hiding,
    /// waiting, editing — converges here.
    /// </summary>
    private void UpdatePositionPipeline()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var nowUtc = DateTime.UtcNow;
        var hostHandle = _currentTarget?.HostWindow.Handle ?? IntPtr.Zero;
        if (_lifecycle.SubsystemsRunning && hostHandle != IntPtr.Zero)
        {
            if (_hostSampler.Window != hostHandle)
            {
                if (_hostSampler.Bind(hostHandle, OnHostWindowEvent))
                {
                    _debug.Event(
                        $"position: following HWND {hostHandle.ToInt64():X} "
                        + $"({(_hostSampler.Events is null ? "no WinEvent hook" : "WinEvent hook armed")})");
                }

                _positionScheduler.Reset();
                _observedDockRevision = -1;
                _dockSnapshotHost = default;
            }
        }
        else if (_hostSampler.Window != IntPtr.Zero)
        {
            _hostSampler.Unbind();
            _positionScheduler.Reset();
            _observedDockRevision = -1;
            _dockSnapshotHost = default;
        }

        ApplySettleTransition(_positionScheduler.Advance(nowUtc));
        ApplyAccurateCadence();
        EnsurePositionTimerState();
        ReportPositionPerformance(nowUtc);
    }

    /// <summary>
    /// Keeps the accurate path's cadence in step with what is happening. This is the only place the
    /// cross-process read rate is raised, and it is raised for a resize and not for a move.
    /// </summary>
    private void ApplyAccurateCadence()
    {
        // A usable reference means the accurate path has something cheap to re-read. Without one it would
        // have to walk the tree on every poll, so the cadence stays slow however fast the host is moving.
        // The flag is the tracker's *live* state, not its last published snapshot.
        var wanted = PositionFastPathRules.AccurateIntervalFor(
            _positionScheduler.Mode,
            _positionScheduler.ShapeChangeInBurst,
            _dockTracker.HasCheapPath);
        if (_dockTracker.AccurateRefreshIntervalMs != wanted)
        {
            _dockTracker.AccurateRefreshIntervalMs = wanted;
        }

        // During a burst the reference rectangle moves on every read, and the tracker's rule would turn
        // each of those into a full tree traversal. A moving window does not need one: the rectangles
        // are translated, which is exact for a move and first-order for a resize, and the forced resync
        // when the burst ends confirms the settled layout with two walks. This is what keeps a burst at
        // *fewer* traversals than the old idle cadence, not more.
        var walkOnMove = _positionScheduler.Mode != PositionUpdateMode.Burst;
        if (_dockTracker.WalkOnReferenceMove != walkOnMove)
        {
            _dockTracker.WalkOnReferenceMove = walkOnMove;
        }
    }

    private void ApplySettleTransition(SettleTransition transition)
    {
        switch (transition)
        {
            case SettleTransition.EnterSettling:
                // §17: the host stopped moving. Ask for one full discovery pass now instead of waiting
                // for the next scheduled one, so maximize/restore is corrected immediately.
                _dockTracker.RequestResync();
                _debug.Event("position: motion stopped, forcing an accurate resync");
                break;
            case SettleTransition.Settled:
                _debug.Event("position: settled, back to the idle cadence");
                break;
        }
    }

    private void EnsurePositionTimerState()
    {
        var wanted = _positionScheduler.Mode != PositionUpdateMode.Idle
            && _lifecycle.SubsystemsRunning
            && Volatile.Read(ref _disposed) == 0;
        if (wanted && !_positionTimerRunning)
        {
            _positionTimerRunning = true;
            var interval = PositionFastPathRules.BurstIntervalFor(_positionScheduler.Mode);
            if (_positionTimer.Interval != interval)
            {
                _positionTimer.Interval = interval;
            }

            _positionTimer.Start();
        }
        else if (!wanted && _positionTimerRunning)
        {
            _positionTimerRunning = false;
            _positionTimer.Stop();
        }

        if (_positionTimerRunning)
        {
            var interval = PositionFastPathRules.BurstIntervalFor(_positionScheduler.Mode);
            if (_positionTimer.Interval != interval)
            {
                _positionTimer.Interval = interval;
            }
        }
    }

    /// <summary>
    /// One WinEvent naming the host window. Deliberately does the minimum: record that the host moved
    /// and let the scheduler decide when the next write is owed. Reading the geometry and writing the
    /// window happen in <see cref="FastPathDriver.Step"/>, which is rate limited — so the event rate
    /// cannot become the write rate, and there is no queue of past positions to replay.
    /// </summary>
    private void OnHostWindowEvent(HostWindowEventKind kind)
    {
        if (Volatile.Read(ref _disposed) != 0 || !_lifecycle.SubsystemsRunning)
        {
            return;
        }

        var nowUtc = DateTime.UtcNow;
        _positionScheduler.OnHostEvent(nowUtc);

        // Suppress the walk-on-reference-move *now*, not on the next pipeline update: between the first
        // event of a burst and the next timer frame the tracker could still walk on the reference's
        // movement, which is exactly the traversal a burst is supposed to avoid.
        ApplyAccurateCadence();
        EnsurePositionTimerState();

        if (!_form.Visible)
        {
            // §16: a host that is being dragged is by definition the foreground interaction, so the
            // strip must not wait out the idle tick before it appears. Rate limited, so this is at most
            // a handful of extra ticks during a burst.
            RequestImmediateTick();
            return;
        }

        FastPathDriver.Step(this, _positionScheduler, nowUtc, force: false);
    }

    private void OnPositionTimerTick()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var nowUtc = DateTime.UtcNow;
        ApplySettleTransition(_positionScheduler.Advance(nowUtc));
        if (_positionScheduler.Dirty)
        {
            // The timer is already the pacing, so the frame it produces bypasses the rate limit: this is
            // the frame that catches up on every event folded since the last write.
            FastPathDriver.Step(this, _positionScheduler, nowUtc, force: true);
        }

        ApplyAccurateCadence();
        EnsurePositionTimerState();
        ReportPositionPerformance(nowUtc);
    }

    /// <summary>
    /// Runs one ordinary tick now, but no more often than <see cref="ImmediateTickIntervalMs"/>. Only
    /// used to make the strip appear promptly at the start of a drag.
    /// </summary>
    private void RequestImmediateTick()
    {
        if (_inTick)
        {
            return;
        }

        var nowUtc = DateTime.UtcNow;
        if ((nowUtc - _lastImmediateTickUtc).TotalMilliseconds < ImmediateTickIntervalMs)
        {
            return;
        }

        _lastImmediateTickUtc = nowUtc;
        Tick();
    }

    private const int ImmediateTickIntervalMs = 150;

    // ------------------------------------------------------- IFastPathTarget (the real window)

    bool IFastPathTarget.CanTrack =>
        _form.Visible && _currentTarget is not null && _hostSampler.Window != IntPtr.Zero;

    IntRect IFastPathTarget.CurrentWindowBounds => new(
        _form.Left,
        _form.Top,
        _form.Width,
        _form.Height);

    bool IFastPathTarget.TrySampleHost(out HostGeometrySample sample) => _hostSampler.TrySample(out sample);

    void IFastPathTarget.CommitHostGeometry() => _hostSampler.Commit();

    void IFastPathTarget.MoveWindowTo(IntRect bounds)
    {
        if (_form.CurrentLayout is not { } layout)
        {
            return;
        }

        var translated = layout with { WindowBounds = bounds };
        if (_form.ApplyLayoutPositionOnly(translated))
        {
            _lastDesiredWindowBounds = bounds;
            return;
        }

        // The strip's own shape changed along with the move (a responsive variant, an expanded panel):
        // that is a real re-layout, not a move, so hand it to the accurate path.
        RelayoutFromFastPath();
    }

    void IFastPathTarget.RelayoutFromAccuratePath() => RelayoutFromFastPath();

    void IFastPathTarget.HideForUnusableHost() => HideForUnusableHost();

    /// <summary>
    /// A shape change mid-burst: the placement has to be recomputed from the host's real geometry.
    /// The strip is placed through the same docking rules as always, only with the position-only write
    /// allowed — a resize that keeps the strip's own shape therefore costs no repaint either.
    /// </summary>
    private void RelayoutFromFastPath()
    {
        if (Volatile.Read(ref _disposed) != 0 || !_hostSampler.TryBuildHostInfo(out var host))
        {
            HideForUnusableHost();
            return;
        }

        var target = new CodexWindowTarget(host);
        _currentTarget = target;
        PlaceStrip(target, fastPath: true);
    }

    /// <summary>
    /// Summarises the position pipeline. During motion and settling this is once a second; when idle it
    /// is every ten seconds and only if a counter actually moved, so a machine that is not being touched
    /// does not grow the log.
    /// </summary>
    private void ReportPositionPerformance(DateTime nowUtc)
    {
        if (!_debug.Enabled)
        {
            return;
        }

        var mode = _positionScheduler.Mode;
        var modeChanged = mode != _lastReportedPositionMode;
        var countersChanged = _positionScheduler.Notes != _lastReportedNotes;
        var due = modeChanged
            || (mode == PositionUpdateMode.Idle
                ? _positionDiagnostics && (nowUtc - _lastPositionReportUtc).TotalSeconds >= 10
                : (nowUtc - _lastPositionReportUtc).TotalSeconds >= 1);
        if (!due)
        {
            return;
        }

        if (mode == PositionUpdateMode.Idle && !modeChanged && !countersChanged)
        {
            return;
        }

        _lastPositionReportUtc = nowUtc;
        _lastReportedPositionMode = mode;
        _lastReportedNotes = _positionScheduler.Notes;

        var actual = HostWindowSampler.TryReadWindowRect(_form.Handle, out var liveActual)
            ? liveActual
            : new IntRect(_form.Left, _form.Top, _form.Width, _form.Height);
        var desired = _lastDesiredWindowBounds;
        var errorX = desired.IsEmpty ? 0 : actual.X - desired.X;
        var errorY = desired.IsEmpty ? 0 : actual.Y - desired.Y;
        _positionScheduler.NotePositionError(errorX, errorY);

        var fastReads = _dockTracker.FastReadCount;
        var walks = _dockTracker.WalkCount;
        var counters = _positionScheduler.TakeDelta();
        var report = new DebugDiagnostics.PositionPerformanceReport(
            mode,
            counters.WinEvents,
            counters.FastUpdates,
            counters.MoveOnlyWrites,
            counters.RelayoutWrites,
            _form.SetWindowPosCallCount - _lastReportedSetWindowPos,
            counters.CoalescedEvents,
            counters.RateLimitedFrames,
            counters.SkippedUnchanged,
            Math.Max(0, fastReads - _lastReportedFastReads),
            Math.Max(0, walks - _lastReportedWalks),
            counters.OffscreenChanges,
            PositionFastPathRules.BurstIntervalFor(mode),
            _dockTracker.AccurateRefreshIntervalMs,
            Age(_positionScheduler.LastHostActivityUtc, nowUtc),
            _dockTracker.MillisecondsSinceLastRead,
            _hostSampler.WindowBounds,
            desired,
            actual,
            errorX,
            errorY,
            _dockTracker.Latest.DescribeSource(),
            _dockTracker.WalkOnReferenceMove,
            _hostSampler.Events?.LastError);

        // The deltas are what a "one drag" measurement means, so they are booked after being reported.
        _lastReportedFastReads = fastReads;
        _lastReportedWalks = walks;
        _lastReportedSetWindowPos = _form.SetWindowPosCallCount;
        _debug.WritePositionPerformance(DebugDiagnostics.BuildPositionPerformance(report));
    }

    private static double Age(DateTime then, DateTime nowUtc) =>
        then == DateTime.MinValue ? -1 : Math.Max(0, (nowUtc - then).TotalMilliseconds);

    private void UpdateLifecycleMenuText()
    {
        var codex = _watcher.Current;
        var codexText = codex.IsRunning
            ? $"Codex：运行中 · PID {codex.ProcessId} · {codex.Package}"
            : "Codex：未运行";
        var barText = "状态条：" + _lifecycle.StateSummary
            + (_lifecycle.State == CodexLifecycleState.Attaching
                ? $"（第 {_lifecycle.AttachAttempt} 次尝试）"
                : string.Empty);

        var signature = codexText + "\u001f" + barText;
        if (signature == _lifecycleMenuText)
        {
            return;
        }

        _lifecycleMenuText = signature;
        _statusCodexItem.Text = codexText;
        _statusBarItem.Text = barText;

        // Only when there is no metric to show: once a conversation is readable the tooltip carries
        // the conversation id and its token count instead, which is more useful than the state name.
        if (_lastSnapshot is null)
        {
            _trayIcon.Text = TrimTrayText(
                codex.IsRunning ? "Codex Statusbar — Active" : "Codex Statusbar — Waiting for Codex");
        }
    }

    private void WriteLifecycleBlock()
    {
        if (!_debug.Enabled)
        {
            return;
        }

        var codex = _watcher.Current;
        _debug.WriteLifecycle(DebugDiagnostics.BuildLifecycle(new DebugDiagnostics.LifecycleReport(
            _modeLabel,
            "primary",
            _startupRegistryEnabled ? "enabled" : "disabled",
            _executablePath.Length > 0 ? StartupManager.BuildCommand(_executablePath) : "(unknown exe)",
            _lifecycle.StateName,
            codex.IsRunning,
            codex.ProcessId,
            codex.Package,
            codex.Version,
            _currentTarget?.HostWindow.Handle ?? IntPtr.Zero,
            _lifecycle.AttachAttempt,
            _lifecycle.IpcConnected,
            _lifecycle.SessionReady,
            _lifecycle.UiaReady,
            _form.Visible,
            _watcher.LastDecision,
            _watcher.DetectCount,
            _watcher.MillisecondsSinceLastDetect,
            _watcher.ErrorCount,
            _watcher.LastError)));
    }

    private void ToggleStartWithWindows(bool enabled)
    {
        if (_executablePath.Length == 0)
        {
            _startWithWindowsItem.Checked = _startupRegistryEnabled;
            return;
        }

        var result = StartupCoordinator.SetEnabled(_settings, _executablePath, enabled);
        _settings.Save(_settingsPath);
        _startupRegistryEnabled = result.RegistryEnabled;
        _startupOutcome = "startup registration: " + result.Outcome
            + (result.Error is { Length: > 0 } error ? " — " + error : string.Empty);
        _debug.Event(_startupOutcome);

        if (_startWithWindowsItem.Checked != result.RegistryEnabled)
        {
            _startWithWindowsItem.Checked = result.RegistryEnabled;
        }

        UpdateLifecycleMenuText();
        WriteLifecycleBlock();
    }

    private void AttachNow()
    {
        _debug.Event("manual attach requested from the tray");
        _watcher.DetectNow();
        _lifecycle.RequestImmediateAttach();
        Tick();
    }

    private void RestartOverlay()
    {
        _debug.Event("restart requested from the tray");
        var executable = _executablePath.Length > 0 ? _executablePath : Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return;
        }

        try
        {
            var arguments = Environment.GetCommandLineArgs()
                .Skip(1)
                .Where(argument => !argument.Equals("--restart-wait", StringComparison.OrdinalIgnoreCase))
                .ToList();
            arguments.Add("--restart-wait");
            Process.Start(new ProcessStartInfo(executable, string.Join(' ', arguments))
            {
                UseShellExecute = false
            });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or IOException or InvalidOperationException)
        {
            _debug.Event("restart failed: " + exception.Message);
            return;
        }

        ExitOverlay();
    }

    private void Tick()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        // A WinEvent callback runs on this same thread and may re-enter the tick (it asks for an
        // immediate tick so a drag does not start with an invisible strip). The guard makes that either
        // impossible or harmless, depending on where the message pump happens to be.
        if (_inTick)
        {
            return;
        }

        _inTick = true;
        try
        {
            TickCore();
        }
        finally
        {
            _inTick = false;
            UpdatePositionPipeline();
        }
    }

    private void TickCore()
    {
        _lifecycle.OverlayVisible = _form.Visible;

        // The lifecycle decides first whether anything Codex-specific runs at all this tick. While
        // Codex is absent this is the entire tick: a process check, two tray labels and a return.
        var previousState = _lifecycle.State;
        var nowUtc = DateTime.UtcNow;
        _lifecycle.Observe(
            _watcher.Current,
            ipcConnected: _pendingRouteStatus.IsConnected,
            sessionReady: !string.IsNullOrWhiteSpace(_pendingThreadId)
                || _monitor?.ActiveThreadId is not null,
            uiaReady: _dockTracker.Latest.Source != ComposerReferenceSource.None,
            nowUtc: nowUtc);
        ApplyLifecycleTickInterval();
        UpdateLifecycleMenuText();
        if (_lifecycle.State != previousState)
        {
            WriteLifecycleBlock();
            _lastLifecycleBlockUtc = nowUtc;
        }
        else if (nowUtc - _lastLifecycleBlockUtc > TimeSpan.FromSeconds(10))
        {
            // A periodic block even when nothing changes: the "Detections" heartbeat line is how a
            // stalled watcher becomes visible in the log instead of looking like a healthy idle one.
            WriteLifecycleBlock();
            _lastLifecycleBlockUtc = nowUtc;
        }

        if (!_lifecycle.SubsystemsRunning)
        {
            // WAITING_FOR_CODEX: no IPC reconnect loop, no rollout scan, no UIA, no Codex theme poll.
            _dockTracker.Enabled = false;
            return;
        }

        RequestBackgroundPoll();

        // Codex's appearance setting can be changed while the overlay runs; the source caches the
        // config read, so this polls cheaply about every ten seconds.
        if (++_themeRefreshTicks >= 30)
        {
            _themeRefreshTicks = 0;
            RefreshEffectiveTheme();
        }

        if (_pendingRouteStatus.Version != _observedRouteVersion)
        {
            _observedRouteVersion = _pendingRouteStatus.Version;
            UpdateSessionMenuText();
        }

        if (_activeRouteThread.ObserveAndCollapse(_pendingRouteStatus, _interaction))
        {
            StopOutsideClickPolling();
        }

        var activeThreadChanged = !string.Equals(
            _pendingThreadId,
            _observedThreadId,
            StringComparison.OrdinalIgnoreCase);
        if (_pendingSessionVersion != _observedSessionVersion)
        {
            _observedSessionVersion = _pendingSessionVersion;
            _observedThreadId = _pendingThreadId;
            _lastSnapshot = null;
            var shortPendingId = string.IsNullOrWhiteSpace(_pendingThreadId)
                ? "等待识别"
                : OverlayPresentationBuilder.ShortThreadId(_pendingThreadId);
            _pinSessionMenuItem.Enabled = !string.IsNullOrWhiteSpace(_pendingThreadId);
            _presentation = OverlayPresentationBuilder.CreateWaiting(
                $"等待会话 {shortPendingId} 的 token 数据…",
                _settings.CollapsedPrimaryField,
                _settings.CollapsedSecondaryField,
                _settings.VisibleFields);
            _form.SetPresentation(_presentation);
            UpdateSessionMenuText();
        }

        var snapshot = _pendingSnapshot;
        if (snapshot is not null && snapshot != _lastSnapshot)
        {
            _lastSnapshot = snapshot;
            _debug.Write(snapshot);
            RefreshPresentation();
            var shortId = OverlayPresentationBuilder.ShortThreadId(snapshot.ThreadId);
            _pinSessionMenuItem.Enabled = true;
            _trayIcon.Text = TrimTrayText(
                $"Codex {shortId} · {OverlayPresentationBuilder.FormatTokenCount(snapshot.TotalTokens)} tokens");
            UpdateSessionMenuText();
        }

        if (_manualAttachment.IsEditing)
        {
            if (_currentTarget is null
                || !CodexWindowLocator.TryRefreshKnownCodexTarget(
                    _currentTarget,
                    out var refreshedTarget))
            {
                CancelManualEditing(restoreFocus: false, relayout: false);
                CollapseAndHide();
                return;
            }

            _currentTarget = refreshedTarget;
            if (_manualAttachment.ShouldApplyStaticDraft)
            {
                ApplyEditDraftLayout(refreshedTarget);
            }
            UpdateManualMenuState();
            return;
        }

        if (_manuallyHidden || !CodexWindowLocator.TryGetForegroundCodexTarget(out var target))
        {
            CollapseAndHide();
            UpdateManualMenuState();
            return;
        }

        if (activeThreadChanged)
        {
            _interaction.CollapseForHostChange();
            StopOutsideClickPolling();
        }

        _currentTarget = target;
        ApplyLayout(target);
        UpdateManualMenuState();
    }

    private void RequestBackgroundPoll()
    {
        if (Volatile.Read(ref _disposed) != 0
            || Interlocked.CompareExchange(ref _pollInFlight, 1, 0) != 0)
        {
            return;
        }

        // No rollout reader means no Codex is attached: there is nothing to scan and no state_5.sqlite
        // query worth making. Release the guard so the next attached tick can poll.
        var monitor = _monitor;
        if (monitor is null || !_lifecycle.SubsystemsRunning)
        {
            Interlocked.Exchange(ref _pollInFlight, 0);
            return;
        }

        var uiScheduler = TaskScheduler.FromCurrentSynchronizationContext();
        _ = Task.Run(() =>
            {
                var routeStatus = _routeMonitor.GetStatus();
                if (!monitor.PinActiveSession)
                {
                    if (!string.IsNullOrWhiteSpace(routeStatus.ThreadId))
                    {
                        monitor.PreferredThreadId = routeStatus.ThreadId;
                    }
                    else if (!routeStatus.IsConnected)
                    {
                        monitor.PreferredThreadId = null;
                    }
                }
                var snapshot = monitor.Poll();
                return (
                    Snapshot: snapshot,
                    Version: monitor.ActiveSessionVersion,
                    ThreadId: monitor.ActiveThreadId,
                    RouteStatus: routeStatus);
            })
            .ContinueWith(task =>
            {
                try
                {
                    if (Volatile.Read(ref _disposed) == 0 && task.Status == TaskStatus.RanToCompletion)
                    {
                        _pendingSnapshot = task.Result.Snapshot;
                        _pendingSessionVersion = task.Result.Version;
                        _pendingThreadId = task.Result.ThreadId;
                        _pendingRouteStatus = task.Result.RouteStatus;
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _pollInFlight, 0);
                }
            }, CancellationToken.None, TaskContinuationOptions.None, uiScheduler);
    }

    private void RefreshPresentation()
    {
        _presentation = _lastSnapshot is null
            ? OverlayPresentationBuilder.CreateWaiting(
                "正在寻找当前 Codex 会话…",
                _settings.CollapsedPrimaryField,
                _settings.CollapsedSecondaryField,
                _settings.VisibleFields)
            : OverlayPresentationBuilder.Create(
                _lastSnapshot,
                _settings.CollapsedPrimaryField,
                _settings.CollapsedSecondaryField,
                _settings.VisibleFields);
        _form.SetPresentation(_presentation);
    }

    private void ApplyLayout(CodexWindowTarget target)
    {
        // A minimised or hidden Codex reports itself at Windows' (-32000, -32000) sentinel. Docking to
        // geometry like that produces absurd numbers and a "hidden because the budget is negative"
        // verdict, which would send anyone diagnosing a future Codex update down the wrong path, so it
        // is recognised explicitly and the strip simply hides.
        if (OverlayLayoutCalculator.IsUnusableRect(target.HostWindow.WindowBounds)
            || OverlayLayoutCalculator.IsUnusableRect(target.HostWindow.ExtendedFrameBounds))
        {
            HideForUnusableHost(target);
            return;
        }

        PlaceStrip(target, fastPath: false);
    }

    /// <summary>
    /// Places the strip for <paramref name="target"/>'s current geometry.
    ///
    /// <para><paramref name="fastPath"/> is the whole difference between the two channels: on the fast
    /// path a placement that did not change the strip's shape is written with a position-only
    /// <c>SetWindowPos</c> and no repaint at all, while the accurate path always rebuilds the surface.
    /// Both arrive here so the docking rules — the ladder, the responsive budget, the display clamp —
    /// exist exactly once.</para>
    /// </summary>
    private void PlaceStrip(CodexWindowTarget target, bool fastPath)
    {
        // Docked mode replaces the manual position model entirely: the placement comes from the live
        // UI Automation rectangle, never from a saved coordinate.
        var dock = ResolveDock(target);
        _lastDock = dock ?? ComposerDockSnapshot.Empty;
        Size? dockedSize = null;
        if (dock is not null)
        {
            dockedSize = ResolveDockedCapsuleSize(target, dock);
            if (dockedSize is null)
            {
                // Nothing fits between the composer's own buttons and the Context indicator. Hiding is
                // the honest outcome: a shortened strip is still three metrics, but text printed over
                // the permission chip is just damage.
                _interaction.HideForSpace();
                StopOutsideClickPolling();
                _form.Hide();
                if (!fastPath)
                {
                    ReportPosition(target, null, null);
                }

                return;
            }
        }

        Point? manualTopLeft = null;
        if (dock is null && _settings.ManualPlacementEnabled)
        {
            var snapshot = SnapshotFromSettings();
            var targets = CreateAttachmentTargets(target);
            manualTopLeft = ResolveManualTopLeft(target, snapshot, targets);
            if (manualTopLeft is null)
            {
                return;
            }

            if (_settings.PositionMode == OverlayPositionMode.FollowCodex
                && _anchorTargetState.ObserveAndCollapse(
                    target.HostWindow.Handle.ToInt64(),
                    snapshot.MainAttachment.ReferencePoint,
                    _interaction))
            {
                StopOutsideClickPolling();
            }
        }
        else if (dock is null
            && _anchorTargetState.ObserveAndCollapse(
                target.HostWindow.Handle.ToInt64(),
                AttachmentReferencePoint.TopLeft,
                _interaction))
        {
            StopOutsideClickPolling();
        }

        var layout = OverlayLayoutCalculator.Calculate(
            CreateLayoutRequest(target.HostWindow, manualTopLeft, dock, dockedSize));
        if (_interaction.State == OverlayVisualState.Expanded
            && layout.State != OverlayVisualState.Expanded)
        {
            _interaction.CollapseForExpandedLayoutFailure();
            StopOutsideClickPolling();
            layout = OverlayLayoutCalculator.Calculate(
                CreateLayoutRequest(target.HostWindow, manualTopLeft, dock, dockedSize));
        }

        if (layout.State == OverlayVisualState.HiddenForSpace)
        {
            _interaction.HideForSpace();
            StopOutsideClickPolling();
        }
        else
        {
            _interaction.RestoreAfterSpace();
        }

        // §9: position and render are separate. If the strip kept its shape, the move is the entire
        // update — no re-measure, no re-render, no blit — and everything below (which exists to keep the
        // surface and the hit region honest) is already correct.
        if (fastPath && _form.ApplyLayoutPositionOnly(layout))
        {
            _lastDesiredWindowBounds = layout.WindowBounds;
            return;
        }

        _form.ApplyLayout(layout);
        ReportPosition(target, manualTopLeft, layout);
        if (layout.State == OverlayVisualState.HiddenForSpace)
        {
            _form.Hide();
        }
        else if (!_form.Visible)
        {
            _form.Show();
        }

        // The handle only exists once the form is shown, so the surface has to be (re)built after that
        // — a layered window with no surface renders nothing at all.
        _form.RefreshSurface();

        UpdateOutsideClickPolling();
        _lastDesiredWindowBounds = layout.WindowBounds;
    }

    private void HideForUnusableHost(CodexWindowTarget? target = null)
    {
        _interaction.HideForSpace();
        StopOutsideClickPolling();
        _form.Hide();
        _lastDock = ComposerDockSnapshot.Empty;
        _responsiveLevel = 0;
        if (target is not null)
        {
            ReportPosition(target, null, null);
        }
    }

    /// <summary>
    /// Logs the resolved position, but only when it actually changes, so a 350 ms poll does not fill
    /// the log with identical lines. Also records whether the display clamp kicked in — the distinction
    /// between the saved position and the shown one is the whole point of the clamp.
    /// </summary>
    private void ReportPosition(
        CodexWindowTarget target,
        Point? requestedTopLeft,
        OverlayLayoutResult? layout)
    {
        if (!_debug.Enabled)
        {
            return;
        }

        IntRect? strip = null;
        var clamped = false;
        if (layout is { } placed)
        {
            // The capsule's own position, translated to screen coordinates, is exactly what the
            // requested top-left became — so any difference means the display clamp moved it.
            var placedTopLeft = new Point(
                placed.WindowBounds.X + placed.CapsuleBounds.X,
                placed.WindowBounds.Y + placed.CapsuleBounds.Y);
            clamped = requestedTopLeft is { } requested
                && (placedTopLeft.X != requested.X || placedTopLeft.Y != requested.Y);
            strip = new IntRect(
                placedTopLeft.X,
                placedTopLeft.Y,
                placed.CapsuleBounds.Width,
                placed.CapsuleBounds.Height);
        }

        var dockedReport = BuildDockedReport(target);
        var signature = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{_settings.PositionMode}|{_settings.MainAttachment.ReferencePoint}|"
            + $"{_settings.MainAttachment.OffsetXDip:0.###}|{_settings.MainAttachment.OffsetYDip:0.###}|"
            + $"{target.HostWindow.WindowBounds.X},{target.HostWindow.WindowBounds.Y},"
            + $"{target.HostWindow.WindowBounds.Width}x{target.HostWindow.WindowBounds.Height}|"
            + $"{strip?.X},{strip?.Y},{strip?.Width}x{strip?.Height}|"
            + $"{target.HostWindow.Dpi}|{clamped}|{_effectiveTheme}|{_settings.ThemePreference}|"
            + $"{_settings.TransparentBackground}|{_lastDock.Source}|{_lastDock.ReferenceRect}|"
            + $"{_responsiveLevel}|{_settings.ContextGapDip:0.###}|{_settings.TextShadow}");
        if (signature == _lastPositionSignature)
        {
            return;
        }

        _lastPositionSignature = signature;
        _debug.WritePosition(DebugDiagnostics.BuildPosition(
            ManualAttachmentRules.SanitizeMain(_settings.MainAttachment),
            _settings.PositionMode,
            target.HostWindow.WindowBounds,
            strip,
            requestedTopLeft,
            clamped,
            target.HostWindow.Dpi,
            _effectiveTheme == OverlayThemeKind.Light ? "Light" : "Dark",
            _settings.ThemePreference == OverlayThemePreference.Auto
                ? $"Auto -> {_effectiveThemeSource}"
                : _settings.ThemePreference.ToString(),
            _settings.TransparentBackground,
            dockedReport,
            _settings.TextShadow));
    }

    /// <summary>
    /// The numbers behind the geometry acceptance criteria: how far the reference's left edge is, how
    /// far the strip may reach before it covers the composer's own buttons, and which rung of the
    /// verbosity ladder is in use.
    /// </summary>
    private DebugDiagnostics.DockedPositionReport? BuildDockedReport(CodexWindowTarget target)
    {
        if (_settings.PositionMode != OverlayPositionMode.ComposerContextLeft
            || _lastDock.Source == ComposerReferenceSource.None)
        {
            return null;
        }

        var dpi = target.HostWindow.Dpi;
        var visibleHost = OverlayLayoutCalculator.ResolveVisibleHost(target.HostWindow);
        return new DebugDiagnostics.DockedPositionReport(
            _lastDock,
            OverlayLayoutCalculator.ResolveReferenceLeft(_lastDock, visibleHost, dpi),
            OverlayLayoutCalculator.ResolveRowCenterY(_lastDock, visibleHost, dpi),
            OverlayLayoutCalculator.ResolveStripLeftLimit(_lastDock, visibleHost, dpi),
            _responsiveLevel,
            OverlaySettings.SanitizeContextGap(_settings.ContextGapDip),
            _form.MeasureCapsuleWidthsDip());
    }

    private OverlayLayoutRequest CreateLayoutRequest(
        CodexWindowInfo hostWindow,
        Point? manualTopLeft = null,
        ComposerDockSnapshot? dock = null,
        Size? capsuleSize = null) => new(
        hostWindow,
        _settings.AnchorMode,
        _interaction.State == OverlayVisualState.Expanded,
        _presentation.ExpandedRows.Count,
        _presentation.ShowContextProgress,
        manualTopLeft,
        _settings.OverlayScalePercent,
        capsuleSize,
        dock,
        OverlaySettings.SanitizeContextGap(_settings.ContextGapDip));

    /// <summary>
    /// The dock reference for this frame, or null when the strip is not in the docked mode.
    ///
    /// <para>When UI Automation has nothing yet — and only then — the snapshot degrades to the
    /// window-level rung, which is still bottom-right anchored with measured DIP offsets rather than
    /// a fraction of the window.</para>
    /// </summary>
    private ComposerDockSnapshot? ResolveDock(CodexWindowTarget target)
    {
        if (_settings.PositionMode != OverlayPositionMode.ComposerContextLeft)
        {
            return null;
        }

        var handle = target.HostWindow.Handle.ToInt64();
        var snapshot = ObserveDockSnapshot(handle, target.HostWindow.WindowBounds);
        if (snapshot is null)
        {
            return ComposerDockSnapshot.Empty with
            {
                Source = ComposerReferenceSource.Window,
                WindowHandle = handle
            };
        }

        return ReAnchorToCurrentHost(snapshot, target.HostWindow.WindowBounds);
    }

    /// <summary>
    /// The newest published dock snapshot, with each half paired to the host rectangle it was actually
    /// measured against.
    ///
    /// <para>Pairing is the piece that makes the rest of this correct, and it has to happen twice
    /// because a snapshot is not homogeneous. Every rectangle in it is an absolute screen coordinate, so
    /// "Context indicator at x=1708" only means something as "57 px left of the host's right edge". But
    /// the reference rectangle is re-read on every poll while the row, the composer card and the left
    /// cluster are only re-read by a full walk — so they are measured at different moments and need
    /// different anchors: a fast read re-anchors the reference at the current host rectangle, a walk
    /// re-anchors the siblings.</para>
    /// </summary>
    private ComposerDockSnapshot? ObserveDockSnapshot(long handle, IntRect hostBounds)
    {
        var nowUtc = DateTime.UtcNow;
        var revision = _dockTracker.Revision;
        if (revision != _observedDockRevision)
        {
            _observedDockRevision = revision;
            _dockSnapshot = _dockTracker.Latest;
            _dockSnapshotHost = hostBounds;
            _lastDockRevisionChangeUtc = nowUtc;
        }

        var walks = _dockTracker.WalkCount;
        if (walks != _observedWalkCount)
        {
            _observedWalkCount = walks;

            // This revision came from a full discovery pass, so the siblings were measured now.
            _siblingAnchorHost = hostBounds;
        }

        if (_dockSnapshot.Source == ComposerReferenceSource.None
            || _dockSnapshot.WindowHandle != handle
            || OverlayLayoutCalculator.IsUnusableRect(_dockSnapshot.ReferenceRect))
        {
            return null;
        }

        return _dockSnapshot;
    }

    /// <summary>
    /// Re-anchors a snapshot's rectangles from the host rectangles they were measured against to the
    /// host rectangle now.
    ///
    /// <para>A pure move is answered exactly by this: the composer translates rigidly with its window,
    /// and so does every rectangle measured inside it. That is why dragging Codex no longer needs to
    /// wait for a UI Automation read at all — the wait was never necessary, it was an artefact of
    /// treating measurements as absolute positions. A resize is a first-order prediction, corrected by
    /// the accurate path and confirmed by the resync when the burst ends.</para>
    /// </summary>
    private ComposerDockSnapshot ReAnchorToCurrentHost(ComposerDockSnapshot snapshot, IntRect hostBounds)
    {
        var referenceAnchor = _dockSnapshotHost;
        var siblingAnchor = _siblingAnchorHost.IsEmpty ? referenceAnchor : _siblingAnchorHost;
        var shiftReference = !referenceAnchor.IsEmpty && referenceAnchor != hostBounds;
        var shiftSiblings = !siblingAnchor.IsEmpty && siblingAnchor != hostBounds;
        if (!shiftReference && !shiftSiblings)
        {
            return snapshot;
        }

        return snapshot with
        {
            ReferenceRect = shiftReference
                ? PositionFastPathRules.ReAnchor(snapshot.ReferenceRect, referenceAnchor, hostBounds)
                : snapshot.ReferenceRect,
            RowRect = shiftSiblings
                ? PositionFastPathRules.ReAnchor(snapshot.RowRect, siblingAnchor, hostBounds)
                : snapshot.RowRect,
            ComposerRect = shiftSiblings
                ? PositionFastPathRules.ReAnchor(snapshot.ComposerRect, siblingAnchor, hostBounds)
                : snapshot.ComposerRect,
            LeftClusterRect = shiftSiblings
                ? PositionFastPathRules.ReAnchor(snapshot.LeftClusterRect, siblingAnchor, hostBounds)
                : snapshot.LeftClusterRect
        };
    }

    /// <summary>
    /// The strip's size for this frame, together with the verbosity level that fits.
    ///
    /// <para>The ladder is walked widest-first against the room the composer actually leaves between
    /// its own left-hand controls and the Context indicator, so the full strip is shown at every
    /// normal width and the abbreviations only appear when Codex is dragged genuinely narrow.</para>
    /// </summary>
    private Size? ResolveDockedCapsuleSize(CodexWindowTarget target, ComposerDockSnapshot dock)
    {
        var dpi = target.HostWindow.Dpi;
        var scale = ManualAttachmentRules.SanitizeScale(_settings.OverlayScalePercent);
        var widths = _form.MeasureCapsuleWidthsDip();
        var availableDip = AvailableStripWidthDip(target, dock);

        var chosen = -1;
        for (var index = 0; index < widths.Length; index++)
        {
            if (widths[index] <= availableDip)
            {
                chosen = index;
                break;
            }
        }

        // The budget is assembled from several UI Automation rectangles, and Codex relayouts the whole
        // composer when its sidebar collapses or expands. During that frame a child's rectangle can
        // still be the pre-relayout one, which makes the cluster boundary look further left than it is
        // and the budget look bigger — measured at the 1100 px breakpoint, where it briefly reported
        // 243 DIP instead of the settled 127. Getting *shorter* on a stale frame is harmless; getting
        // longer is what would print the full strip over the permission chip, so a level is only ever
        // raised once the reference rectangle has stopped moving.
        var referenceMoved = dock.ReferenceRect != _lastDockReferenceRect;
        if (referenceMoved && chosen > _responsiveLevel)
        {
            chosen = _responsiveLevel;
        }

        _lastDockReferenceRect = dock.ReferenceRect;

        _responsiveLevel = chosen < 0 ? widths.Length : chosen;
        _form.SetCapsuleVariant(chosen < 0 ? widths.Length - 1 : chosen);
        return chosen < 0
            ? null
            : OverlayLayoutCalculator.GetCapsuleSizeForText(widths[chosen], dpi, scale);
    }

    /// <summary>The width budget for the strip, in DIP: from the reference leftwards to the composer's own buttons.</summary>
    private double AvailableStripWidthDip(CodexWindowTarget target, ComposerDockSnapshot dock)
    {
        var dpi = target.HostWindow.Dpi;
        var visibleHost = OverlayLayoutCalculator.ResolveVisibleHost(target.HostWindow);
        var referenceLeft = OverlayLayoutCalculator.ResolveReferenceLeft(dock, visibleHost, dpi);
        var leftLimit = OverlayLayoutCalculator.ResolveStripLeftLimit(dock, visibleHost, dpi);
        var gap = ManualAttachmentRules.DipToPixels(
            OverlaySettings.SanitizeContextGap(_settings.ContextGapDip),
            dpi);
        return ManualAttachmentRules.PixelsToDip(referenceLeft - gap - leftLimit, dpi);
    }

    /// <summary>
    /// The strip's top-left corner for the current frame. In <see cref="OverlayPositionMode.FollowCodex"/>
    /// this is <c>windowAnchorPoint + savedOffset</c> — a pure read of the saved attachment, so no
    /// number of moves or resizes can shift it. <see cref="OverlayPositionMode.FixedScreen"/> ignores
    /// the window entirely.
    /// </summary>
    private Point? ResolveManualTopLeft(
        CodexWindowTarget target,
        ManualPlacementSnapshot snapshot,
        AttachmentTargetBounds targets)
    {
        if (_settings.PositionMode == OverlayPositionMode.FixedScreen)
        {
            return _settings.FixedScreenPosition;
        }

        return ManualAttachmentCoordinator.ResolveTopLeft(
            snapshot,
            targets,
            CapsuleSizeFor(target));
    }

    /// <summary>
    /// The collapsed strip's real size. Measured from the text whenever possible, because the window
    /// now hugs its glyphs: resolving an anchor against the old fixed 330 DIP constant while the
    /// layout used the measured width would offset the strip by exactly the difference.
    /// </summary>
    private Size CapsuleSizeFor(CodexWindowTarget target, int? scalePercent = null)
    {
        var scale = ManualAttachmentRules.SanitizeScale(scalePercent ?? _settings.OverlayScalePercent);
        var widths = _form.MeasureCapsuleWidthsDip();
        if (widths.Length > 0)
        {
            var variant = Math.Clamp(_form.CapsuleVariantIndex, 0, widths.Length - 1);
            return OverlayLayoutCalculator.GetCapsuleSizeForText(widths[variant], target.HostWindow.Dpi, scale);
        }

        return OverlayLayoutCalculator.GetManualCapsuleSize(
            target.HostWindow.Dpi,
            scale,
            target.HostWindow.WorkingArea);
    }

    /// <summary>The capsule's real on-screen rectangle, as currently rendered.</summary>
    private IntRect CurrentCapsuleScreenBounds()
    {
        var layout = _form.CurrentLayout
            ?? throw new InvalidOperationException("编辑布局尚未建立。");
        return new IntRect(
            _form.Left + layout.CapsuleBounds.X,
            _form.Top + layout.CapsuleBounds.Y,
            layout.CapsuleBounds.Width,
            layout.CapsuleBounds.Height);
    }

    /// <summary>
    /// <c>Ctrl+Alt+Shift+P</c>: enter position editing, or lock the strip back down. Works from any
    /// application, which matters because the strip is click-through while locked.
    /// </summary>
    private void ToggleManualEditing()
    {
        if (_manualAttachment.IsEditing)
        {
            SaveManualEditing();
            return;
        }

        BeginManualEditing();
    }

    private void BeginManualEditing()
    {
        if (_manualAttachment.IsEditing || _currentTarget is null)
        {
            if (_currentTarget is null
                && CodexWindowLocator.TryGetForegroundCodexTarget(out var foreground))
            {
                _currentTarget = foreground;
            }
        }

        if (_manualAttachment.IsEditing || _currentTarget is null)
        {
            return;
        }

        if (!CodexWindowLocator.TryRefreshKnownCodexTarget(
            _currentTarget,
            out var refreshedTarget))
        {
            _currentTarget = null;
            UpdateManualMenuState();
            return;
        }
        _currentTarget = refreshedTarget;

        // Adjusting the position means leaving the dock, so the manual attachment is seeded from where
        // the strip actually is right now. Without this the draft would resolve from whatever anchor
        // was last saved (often the old top-centre default) and the strip would jump on the first
        // frame of the gesture.
        if (_settings.PositionMode == OverlayPositionMode.ComposerContextLeft)
        {
            if (_form.CurrentLayout is not null && _form.Visible)
            {
                _settings.MainAttachment = ManualAttachmentCalculator.Capture(
                    _currentTarget.HostWindow.WindowBounds,
                    CurrentCapsuleScreenBounds(),
                    _currentTarget.HostWindow.Dpi);
            }

            _settings.PositionMode = OverlayPositionMode.FollowCodex;
            _settings.ManualPlacementEnabled = true;
            _dockTracker.Enabled = false;
            _settings.Save(_settingsPath);
        }

        _interaction.CollapseForHostChange();
        StopOutsideClickPolling();
        _settingsSnapshotBeforeEdit = SnapshotFromSettings();
        _saveFailureNotified = false;
        var transition = _manualAttachment.BeginEdit(
            _settingsSnapshotBeforeEdit,
            CreateAttachmentTargets(_currentTarget),
            CapsuleSizeFor(_currentTarget));
        ApplyEditTransition(_currentTarget, transition, applyLayout: true);
        _form.BeginEditMode(transition.Draft.ScalePercent);
        if (!_form.Visible)
        {
            _form.Show();
        }
        UpdateManualMenuState();
    }

    private void HandleEditPreviewChanged(
        object? sender,
        OverlayEditPreviewEventArgs eventArgs)
    {
        _ = sender;
        if (!_manualAttachment.IsEditing || _currentTarget is null)
        {
            return;
        }

        var targets = CreateAttachmentTargets(_currentTarget);
        _manualAttachment.BeginGesturePreview();
        ManualAttachmentTransition transition;
        if (eventArgs.Kind == OverlayEditGestureKind.Move)
        {
            var capsuleScreen = CurrentCapsuleScreenBounds();
            if (_settings.PositionMode == OverlayPositionMode.FixedScreen)
            {
                // Fixed mode: the strip is where the pointer left it, full stop. Nothing is derived
                // from the Codex window, so an absolute screen position is all we record.
                _settings.FixedScreenPosition = new Point(capsuleScreen.X, capsuleScreen.Y);
                _manualAttachment.EndGesturePreview();
                UpdateManualMenuState();
                return;
            }

            transition = OverlayEditMoveDispatcher.Dispatch(
                _manualAttachment,
                targets,
                eventArgs,
                capsuleScreen,
                point => IsCursorOnKnownHost(_currentTarget!, point),
                isCompletion: false);
            ApplyEditTransition(
                _currentTarget,
                transition,
                OverlayEditPreviewLayoutPolicy.ShouldApplyLayout(eventArgs.Kind, transition));
        }
        else
        {
            transition = _manualAttachment.PreviewResize(
                targets,
                eventArgs.FixedTopLeft,
                eventArgs.ScalePercent,
                CurrentCollapsedDisplay());
            ApplyEditTransition(_currentTarget, transition, applyLayout: true);
        }
        UpdateManualMenuState();
    }

    private void HandleEditGestureCompleted(
        object? sender,
        OverlayEditPreviewEventArgs eventArgs)
    {
        _ = sender;
        if (!_manualAttachment.IsEditing || _currentTarget is null)
        {
            return;
        }

        var targets = CreateAttachmentTargets(_currentTarget);
        _manualAttachment.EndGesturePreview();
        var capsuleScreen = CurrentCapsuleScreenBounds();
        if (eventArgs.Kind == OverlayEditGestureKind.Move
            && _settings.PositionMode == OverlayPositionMode.FixedScreen)
        {
            _settings.FixedScreenPosition = new Point(capsuleScreen.X, capsuleScreen.Y);
            UpdateManualMenuState();
            return;
        }

        var transition = eventArgs.Kind == OverlayEditGestureKind.Move
            ? OverlayEditMoveDispatcher.Dispatch(
                _manualAttachment,
                targets,
                eventArgs,
                capsuleScreen,
                point => IsCursorOnKnownHost(_currentTarget!, point),
                isCompletion: true)
            : _manualAttachment.PreviewResize(
                targets,
                eventArgs.FixedTopLeft,
                eventArgs.ScalePercent,
                CurrentCollapsedDisplay());
        ApplyEditTransition(
            _currentTarget,
            transition,
            applyLayout: true);
        UpdateManualMenuState();
    }

    private void SaveManualEditing()
    {
        if (!_manualAttachment.IsEditing)
        {
            return;
        }
        if (!CanCommitEditPosition)
        {
            NotifySaveFailure("请先将状态条拖到 Codex 主窗口上。");
            return;
        }

        var original = _settingsSnapshotBeforeEdit ?? SnapshotFromSettings();
        var draft = _manualAttachment.Draft with { Enabled = true };
        ApplySnapshotToSettings(draft);
        if (_settings.PositionMode == OverlayPositionMode.FixedScreen)
        {
            var capsuleScreen = CurrentCapsuleScreenBounds();
            _settings.FixedScreenPosition = new Point(capsuleScreen.X, capsuleScreen.Y);
        }

        if (!_settings.TrySave(_settingsPath))
        {
            ApplySnapshotToSettings(original);
            NotifySaveFailure("无法保存设置，请检查设置文件权限后重试。");
            return;
        }

        var committed = _manualAttachment.Commit();
        ApplySnapshotToSettings(committed.Draft);
        FinishManualEditing(restoreFocus: true, relayout: true);
    }

    private void CancelManualEditing(
        bool restoreFocus = true,
        bool relayout = true)
    {
        if (!_manualAttachment.IsEditing)
        {
            return;
        }

        var cancelled = _manualAttachment.Cancel();
        ApplySnapshotToSettings(cancelled.Draft);
        FinishManualEditing(restoreFocus, relayout);
    }

    private void ResetManualPlacement()
    {
        if (_manualAttachment.IsEditing)
        {
            return;
        }

        _settings.ManualPlacementEnabled = true;
        _settings.MainAttachment = ManualAttachmentRules.DefaultMainAttachment;
        _settings.OverlayScalePercent = ManualAttachmentRules.DefaultScalePercent;
        if (!_settings.TrySave(_settingsPath))
        {
            _trayIcon.ShowBalloonTip(
                3000,
                "Codex Token 状态条",
                "无法保存重置后的设置。",
                ToolTipIcon.Warning);
        }

        _interaction.CollapseForHostChange();
        StopOutsideClickPolling();
        UpdateManualMenuState();
        if (_currentTarget is not null && !_manuallyHidden)
        {
            ApplyLayout(_currentTarget);
        }
    }

    private void ApplyEditDraftLayout(CodexWindowTarget target)
    {
        if (!_manualAttachment.IsEditing)
        {
            return;
        }

        var transition = new ManualAttachmentTransition(
            _manualAttachment.Draft,
            IsEditing: true,
            CanCommitEditPosition,
            RequiresPersist: false,
            ShouldCollapse: true,
            HighlightBounds: _manualAttachment.ShouldShowStaticHighlight
                ? target.HostWindow.WindowBounds
                : null,
            ResolvedTopLeft: _settings.PositionMode == OverlayPositionMode.FixedScreen
                ? _settings.FixedScreenPosition
                : ManualAttachmentCoordinator.ResolveTopLeft(
                    _manualAttachment.Draft,
                    CreateAttachmentTargets(target),
                    CapsuleSizeFor(target, _manualAttachment.Draft.ScalePercent)));
        ApplyEditTransition(target, transition, applyLayout: true);
    }

    /// <summary>
    /// Fixed-screen mode does not need the pointer to be over the Codex window, so it can always be
    /// committed; follow mode still refuses to save a position that was never dropped on the window.
    /// </summary>
    private bool CanCommitEditPosition =>
        _settings.PositionMode == OverlayPositionMode.FixedScreen || _manualAttachment.CanSave;

    private void ApplyEditTransition(
        CodexWindowTarget target,
        ManualAttachmentTransition transition,
        bool applyLayout)
    {
        if (transition.HighlightBounds is IntRect highlight && !highlight.IsEmpty)
        {
            _targetHighlight.ShowTarget(highlight);
        }
        else
        {
            _targetHighlight.ClearTarget();
        }

        if (!applyLayout || transition.ResolvedTopLeft is not Point topLeft)
        {
            return;
        }

        _interaction.CollapseForHostChange();
        StopOutsideClickPolling();
        var layout = OverlayLayoutCalculator.Calculate(new OverlayLayoutRequest(
            target.HostWindow,
            _settings.AnchorMode,
            RequestExpanded: false,
            _presentation.ExpandedRows.Count,
            _presentation.ShowContextProgress,
            topLeft,
            ScalePercent: transition.Draft.ScalePercent));
        _form.ApplyLayout(layout);
        if (layout.State == OverlayVisualState.HiddenForSpace)
        {
            _form.Hide();
        }
        else if (!_form.Visible)
        {
            _form.Show();
        }
    }

    private void FinishManualEditing(bool restoreFocus, bool relayout)
    {
        var focusTarget = _currentTarget;
        _targetHighlight.ClearTarget();
        _form.EndEditMode();
        _interaction.CollapseForHostChange();
        StopOutsideClickPolling();
        _settingsSnapshotBeforeEdit = null;
        _saveFailureNotified = false;
        UpdateManualMenuState();

        if (relayout && _currentTarget is not null && !_manuallyHidden)
        {
            ApplyLayout(_currentTarget);
        }

        if (restoreFocus
            && focusTarget is not null
            && CodexWindowLocator.TryRefreshKnownCodexTarget(focusTarget, out var refreshed))
        {
            _currentTarget = refreshed;
            SetForegroundWindow(refreshed.HostWindow.Handle);
        }
    }

    private void NotifySaveFailure(string message)
    {
        if (_saveFailureNotified)
        {
            return;
        }

        _saveFailureNotified = true;
        _trayIcon.ShowBalloonTip(
            3000,
            "Codex Token 状态条",
            message,
            ToolTipIcon.Warning);
    }

    private CollapsedDisplayMode CurrentCollapsedDisplay() =>
        _form.CurrentLayout?.CollapsedDisplay ?? CollapsedDisplayMode.TwoFields;

    private ManualPlacementSnapshot SnapshotFromSettings() => new(
        _settings.ManualPlacementEnabled,
        ManualAttachmentRules.SanitizeMain(_settings.MainAttachment),
        ManualAttachmentRules.SanitizeScale(_settings.OverlayScalePercent));

    private void ApplySnapshotToSettings(ManualPlacementSnapshot snapshot)
    {
        _settings.ManualPlacementEnabled = snapshot.Enabled;
        _settings.MainAttachment = ManualAttachmentRules.SanitizeMain(snapshot.MainAttachment);
        _settings.OverlayScalePercent = ManualAttachmentRules.SanitizeScale(snapshot.ScalePercent);
    }

    private static AttachmentTargetBounds CreateAttachmentTargets(CodexWindowTarget target) => new(
        target.HostWindow.Handle.ToInt64(),
        target.HostWindow.WindowBounds,
        target.HostWindow.WorkingArea,
        target.HostWindow.Dpi);

    private bool IsCursorOnKnownHost(CodexWindowTarget target, Point point) =>
        CodexWindowLocator.IsPointOnKnownHost(
            target,
            point,
            new HashSet<long>
            {
                _form.Handle.ToInt64(),
                _targetHighlight.Handle.ToInt64()
            });

    private void HandleCapsuleClicked(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ToggleDetailPanel();
    }

    /// <summary>
    /// Opens or closes the detail panel.
    ///
    /// <para>While locked the strip is completely click-through — glyph pixels included — so the panel
    /// can no longer be opened by clicking the numbers. This is the same toggle the click used to
    /// call, reached from the tray instead; the panel itself is unchanged.</para>
    /// </summary>
    private void ToggleDetailPanel()
    {
        if (!_interaction.OnCapsuleMouseUp() || _currentTarget is null)
        {
            return;
        }

        if (!_interaction.ShouldPollOutsideClicks)
        {
            StopOutsideClickPolling();
        }
        ApplyLayout(_currentTarget);
    }

    private void PollOutsidePointer()
    {
        if (!_interaction.ShouldPollOutsideClicks)
        {
            StopOutsideClickPolling();
            return;
        }

        if (!PointerInput.TryGetCursorPosition(out var position))
        {
            return;
        }

        if (_interaction.OnPointerSample(
            PointerInput.ReadPressedButtons(),
            _form.ContainsScreenPoint(position)))
        {
            StopOutsideClickPolling();
            if (_currentTarget is not null)
            {
                ApplyLayout(_currentTarget);
            }
        }
    }

    private void UpdateOutsideClickPolling()
    {
        if (_interaction.ShouldPollOutsideClicks && !_manuallyHidden)
        {
            _outsideClickTimer.Start();
        }
        else
        {
            StopOutsideClickPolling();
        }
    }

    private void StopOutsideClickPolling() => _outsideClickTimer.Stop();

    private void CollapseAndHide()
    {
        _interaction.CollapseForHostChange();
        StopOutsideClickPolling();
        _form.Hide();
    }

    private void UpdateSessionMenuText()
    {
        var threadId = _lastSnapshot?.ThreadId ?? _pendingThreadId;
        var shortId = string.IsNullOrWhiteSpace(threadId)
            ? "等待识别"
            : OverlayPresentationBuilder.ShortThreadId(threadId);
        _sessionMenuItem.Text = $"会话：{shortId}{RouteStatusSuffix(_pendingRouteStatus)}";
    }

    private void ApplyTheme(OverlayThemePalette palette)
    {
        _autoThemePalette = palette;
        ApplyEffectiveTheme();
    }

    /// <summary>
    /// Resolves the effective palette. <c>Auto</c> follows <b>Codex's own</b> appearance setting, falling
    /// back to the Windows app theme only when Codex has not recorded one; <c>Dark</c> / <c>Light</c>
    /// force the text colours outright.
    /// </summary>
    private void ApplyEffectiveTheme()
    {
        var codexKind = _codexTheme.Current;
        var kind = _settings.ThemePreference switch
        {
            OverlayThemePreference.Dark => OverlayThemeKind.Dark,
            OverlayThemePreference.Light => OverlayThemeKind.Light,
            _ => codexKind switch
            {
                CodexThemeKind.Light => OverlayThemeKind.Light,
                CodexThemeKind.Dark => OverlayThemeKind.Dark,
                _ => _autoThemePalette == OverlayThemePalette.For(OverlayThemeKind.Light)
                    ? OverlayThemeKind.Light
                    : OverlayThemeKind.Dark
            }
        };

        _effectiveTheme = kind;
        _effectiveThemeSource = _settings.ThemePreference != OverlayThemePreference.Auto
            ? "forced"
            : codexKind switch
            {
                CodexThemeKind.Light or CodexThemeKind.Dark => "Codex config",
                _ => "Windows"
            };

        var palette = OverlayThemePalette.For(kind);
        _form.ApplyTheme(palette);
        _targetHighlight.ApplyTheme(palette);
        UpdateManualMenuState();
    }

    /// <summary>Re-reads Codex's appearance setting and re-applies if it changed.</summary>
    private void RefreshEffectiveTheme()
    {
        var previous = _effectiveTheme;
        ApplyEffectiveTheme();
        _ = previous;
    }

    private static string TrimTrayText(string value) =>
        value.Length <= 63 ? value : value[..63];

    private static string RouteStatusSuffix(ActiveThreadRouteStatus status)
    {
        if (status.ActiveWindowCount > 1)
        {
            return $" · 多窗口 {status.ActiveWindowCount}";
        }
        return status.IsConnected ? " · 已同步" : " · 日志模式";
    }

    private void ExitOverlay()
    {
        // Exit means "stop for this session", never "uninstall". The HKCU Run value is left exactly as
        // it is, so the next logon starts the watcher again unless the user turned that off.
        _debug.Event("exit requested from the tray (startup registration left unchanged)");
        if (_manualAttachment.IsEditing)
        {
            CancelManualEditing(restoreFocus: false, relayout: false);
        }
        CollapseAndHide();
        _timer.Stop();
        _outsideClickTimer.Stop();
        _hotkey.Dispose();
        _trayIcon.Visible = false;
        ExitThread();
        Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            if (_manualAttachment.IsEditing)
            {
                CancelManualEditing(restoreFocus: false, relayout: false);
            }
            _interaction.CollapseForHostChange();
            _timer.Stop();
            _outsideClickTimer.Stop();
            _timer.Dispose();
            _outsideClickTimer.Dispose();
            // Returns false while a WinEvent callback is on the stack; the hook is torn down either way,
            // and Dispose is the last thing that happens in this process.
            _positionTimer.Stop();
            _positionTimer.Dispose();
            _hostSampler.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _hotkey.Dispose();
            _watcher.Dispose();
            _routeMonitor.Dispose();
            _monitor?.Dispose();
            DisposeThemeAndForms();
        }
        base.Dispose(disposing);
    }

    private void DisposeThemeAndForms()
    {
        _dockTracker.Dispose();
        _themeBinding.Dispose();
        _targetHighlight.Dispose();
        _form.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);
}

internal static class OverlayEditMoveDispatcher
{
    public static ManualAttachmentTransition Dispatch(
        ManualAttachmentCoordinator coordinator,
        AttachmentTargetBounds targets,
        OverlayEditPreviewEventArgs eventArgs,
        IntRect capsuleScreen,
        Func<Point, bool> hostSurfaceResolver,
        bool isCompletion)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(eventArgs);
        ArgumentNullException.ThrowIfNull(hostSurfaceResolver);
        if (eventArgs.Kind != OverlayEditGestureKind.Move)
        {
            throw new ArgumentOutOfRangeException(nameof(eventArgs));
        }

        var hostSurfaceHit = hostSurfaceResolver(eventArgs.CursorScreen);
        var capsuleSize = new Size(capsuleScreen.Width, capsuleScreen.Height);
        return isCompletion
            ? coordinator.CompleteMove(
                targets,
                eventArgs.CursorScreen,
                capsuleScreen,
                hostSurfaceHit,
                capsuleSize)
            : coordinator.PreviewMove(
                targets,
                eventArgs.CursorScreen,
                capsuleScreen,
                hostSurfaceHit,
                capsuleSize);
    }
}

internal static class OverlayEditPreviewLayoutPolicy
{
    public static bool ShouldApplyLayout(
        OverlayEditGestureKind kind,
        ManualAttachmentTransition transition)
    {
        ArgumentNullException.ThrowIfNull(transition);
        return kind != OverlayEditGestureKind.Move || !transition.CanSave;
    }
}
