using System.Runtime.InteropServices;

namespace CodexStatusbar;

internal sealed class OverlayContext : ApplicationContext
{
    private readonly OverlaySettings _settings;
    private readonly CodexIpcActiveThreadMonitor _routeMonitor = new();
    private readonly TokenLogMonitor _monitor;
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
        string? forcedThreadId = null)
    {
        _settingsPath = settingsPath;
        _debug = debug ?? new DebugDiagnostics(false);
        _forcedThreadId = forcedThreadId;
        _monitor = new TokenLogMonitor(sessionRoot);
        if (!string.IsNullOrWhiteSpace(forcedThreadId))
        {
            _monitor.PinActiveSession = true;
            _monitor.PreferredThreadId = forcedThreadId;
        }

        _settings = OverlaySettings.Load(_settingsPath);
        _dockTracker = new ComposerDockTracker(
            () => _currentTarget?.HostWindow.Handle ?? IntPtr.Zero)
        {
            // Only paid for when the strip is actually docked: UI Automation makes Codex build its
            // accessibility tree, which is not free, so the manual mode must not keep asking.
            Enabled = _settings.PositionMode == OverlayPositionMode.ComposerContextLeft
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
        _sessionMenuItem = new ToolStripMenuItem("会话：等待数据") { Enabled = false };
        menu.Items.Add(_sessionMenuItem);
        _pinSessionMenuItem = new ToolStripMenuItem("锁定当前会话") { Enabled = false, CheckOnClick = true };
        _pinSessionMenuItem.CheckedChanged += (_, _) =>
        {
            _monitor.PinActiveSession = _pinSessionMenuItem.Checked;
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

    private void Tick()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
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

        var uiScheduler = TaskScheduler.FromCurrentSynchronizationContext();
        _ = Task.Run(() =>
            {
                var routeStatus = _routeMonitor.GetStatus();
                if (!_monitor.PinActiveSession)
                {
                    if (!string.IsNullOrWhiteSpace(routeStatus.ThreadId))
                    {
                        _monitor.PreferredThreadId = routeStatus.ThreadId;
                    }
                    else if (!routeStatus.IsConnected)
                    {
                        _monitor.PreferredThreadId = null;
                    }
                }
                var snapshot = _monitor.Poll();
                return (
                    Snapshot: snapshot,
                    Version: _monitor.ActiveSessionVersion,
                    ThreadId: _monitor.ActiveThreadId,
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
            _interaction.HideForSpace();
            StopOutsideClickPolling();
            _form.Hide();
            _lastDock = ComposerDockSnapshot.Empty;
            _responsiveLevel = 0;
            ReportPosition(target, null, null);
            return;
        }

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
                ReportPosition(target, null, null);
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
        var latest = _dockTracker.Latest;
        if (latest.Source != ComposerReferenceSource.None
            && latest.WindowHandle == handle
            && !OverlayLayoutCalculator.IsUnusableRect(latest.ReferenceRect))
        {
            return latest;
        }

        return ComposerDockSnapshot.Empty with
        {
            Source = ComposerReferenceSource.Window,
            WindowHandle = handle
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
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _hotkey.Dispose();
            _routeMonitor.Dispose();
            _monitor.Dispose();
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
