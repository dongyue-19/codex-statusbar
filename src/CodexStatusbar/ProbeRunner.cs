using System.Drawing.Drawing2D;
using System.Globalization;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace CodexStatusbar;

internal static class ProbeRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Process exit code for the probe that just ran; 0 unless a probe reported failure.</summary>
    public static int LastExitCode { get; private set; }

    public static bool TryRun(IReadOnlyList<string> args, string sessionRoot)
    {
        LastExitCode = 0;

        if (args.Count >= 1 && args[0].Equals("--self-test", StringComparison.OrdinalIgnoreCase))
        {
            LastExitCode = SelfTest.Run(args.Count >= 2 ? args[1] : null);
            return true;
        }

        if (args.Count >= 1 && args[0].Equals("--ipc-probe", StringComparison.OrdinalIgnoreCase))
        {
            var seconds = args.Count >= 2
                && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                && parsed > 0
                    ? parsed
                    : 8;
            LastExitCode = RunIpcProbe(seconds);
            return true;
        }

        if (args.Count >= 2 && args[0].Equals("--position-probe", StringComparison.OrdinalIgnoreCase))
        {
            LastExitCode = PositionProbe.Run(args[1]);
            return true;
        }

        if (args.Count >= 2 && args[0].Equals("--render-probe", StringComparison.OrdinalIgnoreCase))
        {
            PrepareWindowProbeDpiAwareness();
            Application.EnableVisualStyles();
            LastExitCode = RenderProbe.Run(args[1]);
            return true;
        }

        if (args.Count >= 2 && args[0].Equals("--show-probe", StringComparison.OrdinalIgnoreCase))
        {
            PrepareWindowProbeDpiAwareness();
            Application.EnableVisualStyles();
            LastExitCode = RenderProbe.RunShowProbe(args[1]);
            return true;
        }

        if (args.Count >= 2 && args[0].Equals("--hotkey-probe", StringComparison.OrdinalIgnoreCase))
        {
            LastExitCode = RunHotkeyProbe(args[1]);
            return true;
        }

        if (args.Count >= 2 && args[0].Equals("--theme-probe", StringComparison.OrdinalIgnoreCase))
        {
            PrepareWindowProbeDpiAwareness();
            WriteJson(args[1], ExecuteThemeProbe());
            return true;
        }

        if (args.Count >= 3 && args[0].Equals("--attachment-probe", StringComparison.OrdinalIgnoreCase))
        {
            WriteJson(args[1], ExecuteAttachmentProbe(args[2]));
            return true;
        }

        if (args.Count >= 3 && args[0].Equals("--window-classification-probe", StringComparison.OrdinalIgnoreCase))
        {
            var request = ReadJson<WindowClassificationProbeRequest>(args[2]);
            WriteJson(args[1], WindowClassificationProbe.Execute(request));
            return true;
        }

        if (args.Count >= 3 && args[0].Equals("--settings-probe", StringComparison.OrdinalIgnoreCase))
        {
            var request = ReadJson<SettingsProbeRequest>(args[2]);
            var result = SettingsProbe.Execute(request);
            WriteJson(args[1], result);
            return true;
        }

        if (args.Count >= 3 && args[0].Equals("--presentation-probe", StringComparison.OrdinalIgnoreCase))
        {
            var request = ReadJson<PresentationProbeRequest>(args[2]);
            var result = PresentationProbe.Execute(request);
            WriteJson(args[1], result);
            return true;
        }

        if (args.Count >= 3 && args[0].Equals("--layout-probe", StringComparison.OrdinalIgnoreCase))
        {
            var result = ExecuteLayoutProbe(args[2]);
            WriteJson(args[1], result);
            return true;
        }

        if (args.Count >= 3 && args[0].Equals("--interaction-probe", StringComparison.OrdinalIgnoreCase))
        {
            var request = ReadJson<InteractionProbeRequest>(args[2]);
            var result = InteractionProbe.Execute(request);
            WriteJson(args[1], result);
            return true;
        }

        if (args.Count >= 3 && args[0].Equals("--form-probe", StringComparison.OrdinalIgnoreCase))
        {
            PrepareWindowProbeDpiAwareness();
            var request = ReadJson<FormProbeRequest>(args[2]);
            var result = ExecuteFormProbe(request);
            WriteJson(args[1], result);
            return true;
        }

        // IPC 探针用于验证 Codex 当前可见任务广播，不启动悬浮条。
        if (args.Count >= 2 && args[0].Equals("--ipc-probe", StringComparison.OrdinalIgnoreCase))
        {
            using var routeMonitor = new CodexIpcActiveThreadMonitor();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            ActiveThreadRouteStatus status;
            do
            {
                Thread.Sleep(100);
                status = routeMonitor.GetStatus();
            }
            while (DateTime.UtcNow < deadline && string.IsNullOrWhiteSpace(status.ThreadId));

            WriteJson(args[1], status);
            return true;
        }

        // 窗口探针用于验证当前交互桌面可见的 Codex 顶层窗口信息。
        if (args.Count >= 2 && args[0].Equals("--window-probe", StringComparison.OrdinalIgnoreCase))
        {
            PrepareWindowProbeDpiAwareness();
            WriteJson(args[1], CodexWindowLocator.GetForegroundWindowProbe());
            return true;
        }

        // 线程切换探针在同一个监视器实例中验证静止日志间的路由切换。
        if (args.Count >= 4 && args[0].Equals("--thread-switch-probe", StringComparison.OrdinalIgnoreCase))
        {
            using var monitor = new TokenLogMonitor(sessionRoot);
            monitor.PreferredThreadId = args[2];
            var first = monitor.Poll(forceFullScan: true);
            var firstVersion = monitor.ActiveSessionVersion;
            monitor.PreferredThreadId = args[3];
            var second = monitor.Poll();
            var secondVersion = monitor.ActiveSessionVersion;
            WriteJson(args[1], new
            {
                FirstSnapshot = first,
                FirstVersion = firstVersion,
                SecondSnapshot = second,
                SecondVersion = secondVersion
            });
            return true;
        }

        // 探针模式用于构建后验证真实会话日志，不启动任何界面。
        if (args.Count >= 2 && args[0].Equals("--probe", StringComparison.OrdinalIgnoreCase))
        {
            using var monitor = new TokenLogMonitor(sessionRoot);
            if (args.Count >= 3 && !args[2].StartsWith("--", StringComparison.Ordinal))
            {
                monitor.PreferredThreadId = args[2];
            }
            WriteJson(args[1], monitor.Poll(forceFullScan: true));
            return true;
        }

        return false;
    }

    /// <summary>
    /// <c>--ipc-probe [seconds]</c>: connects to <c>\\.\pipe\codex-ipc</c> and prints every lifecycle
    /// event and frame, then the resolved active-thread status. This is the diagnostic to run when
    /// Codex is updated and the strip stops following the open conversation.
    /// </summary>
    private static int RunIpcProbe(int seconds)
    {
        Console.Out.WriteLine($"connecting to \\\\.\\pipe\\codex-ipc for {seconds}s ...");
        CodexIpcActiveThreadMonitor.FrameObserver = line => Console.Out.WriteLine("  [ipc] " + line);

        try
        {
            using var monitor = new CodexIpcActiveThreadMonitor();
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(500);
            }

            var status = monitor.GetStatus();
            Console.Out.WriteLine();
            Console.Out.WriteLine("resolved status:");
            Console.Out.WriteLine($"  threadId          = {status.ThreadId ?? "<null>"}");
            Console.Out.WriteLine($"  isConnected       = {status.IsConnected}");
            Console.Out.WriteLine($"  activeWindowCount = {status.ActiveWindowCount}");
            Console.Out.WriteLine($"  version           = {status.Version}");
            Console.Out.WriteLine($"  lastError         = {status.LastError ?? "<none>"}");
            var ok = !string.IsNullOrWhiteSpace(status.ThreadId);
            Console.Out.WriteLine(ok
                ? "RESULT: PASS (a conversation was resolved over IPC)"
                : "RESULT: FAIL (no conversation resolved over IPC)");
            return ok ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Out.WriteLine("RESULT: FAIL " + exception);
            return 1;
        }
        finally
        {
            CodexIpcActiveThreadMonitor.FrameObserver = null;
        }
    }

    internal static void PrepareWindowProbeDpiAwareness()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
    }

    /// <summary>
    /// Verifies that <c>Ctrl+Alt+Shift+P</c> can actually be claimed on this machine (another
    /// application may already own it). Registration is real, then released again.
    /// </summary>
    private static int RunHotkeyProbe(string outputPath)
    {
        using var hotkey = new GlobalHotkey();
        var registered = hotkey.TryRegister();
        hotkey.Dispose();
        using var second = new GlobalHotkey();
        var reacquired = second.TryRegister();
        WriteJson(outputPath, new
        {
            Combination = GlobalHotkey.Description,
            Registered = registered,
            ReacquiredAfterRelease = reacquired
        });
        return registered && reacquired ? 0 : 1;
    }

    private static T ReadJson<T>(string path)
    {
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8), JsonOptions)
            ?? throw new JsonException($"JSON 探针请求为空：{path}");
    }

    private static object ExecuteLayoutProbe(string path)
    {
        var request = ReadJson<LayoutProbeEnvelopeRequest>(path);
        var results = new List<object>(request.Cases.Count);
        foreach (var item in request.Cases)
        {
            var operation = item.TryGetProperty("Operation", out var operationElement)
                ? operationElement.GetString()
                : null;
            if (operation?.Equals("ConvertCaptionBounds", StringComparison.OrdinalIgnoreCase) == true)
            {
                var conversion = item.Deserialize<CaptionBoundsProbeRequest>(JsonOptions)
                    ?? throw new JsonException("标题按钮坐标转换请求为空。");
                results.Add(new CaptionBoundsProbeResult(
                    conversion.Name,
                    CodexWindowLocator.ConvertRelativeToScreen(
                        conversion.WindowBounds,
                        conversion.RelativeBounds)));
                continue;
            }

            var layoutCase = item.Deserialize<LayoutProbeCaseRequest>(JsonOptions)
                ?? throw new JsonException("布局探针案例为空。");
            var layoutResult = LayoutProbe.Execute(new LayoutProbeRequest { Cases = [layoutCase] });
            results.Add(layoutResult.Cases[0]);
        }

        return new { Cases = results };
    }

    private static object ExecuteAttachmentProbe(string path)
    {
        var request = ReadJson<AttachmentProbeEnvelopeRequest>(path);
        var results = new List<object>(request.Cases.Count);
        foreach (var item in request.Cases)
        {
            var operation = item.TryGetProperty("Operation", out var operationElement)
                ? operationElement.GetString()
                : null;
            results.Add(operation switch
            {
                "ReferencePoints" => ExecuteReferencePointsProbe(
                    DeserializeAttachmentCase<AttachmentReferencePointsProbeRequest>(item)),
                "SelectReferencePoint" => ExecuteSelectReferencePointProbe(
                    DeserializeAttachmentCase<AttachmentCapsuleProbeRequest>(item)),
                "CaptureResolve" => ExecuteCaptureResolveProbe(
                    DeserializeAttachmentCase<AttachmentCaptureResolveProbeRequest>(item)),
                "SelectTargets" => ExecuteSelectTargetsProbe(
                    DeserializeAttachmentCase<AttachmentTargetSelectionProbeRequest>(item)),
                "CalculateScales" => ExecuteCalculateScalesProbe(
                    DeserializeAttachmentCase<AttachmentScaleProbeRequest>(item)),
                "EditState" => ExecuteEditStateProbe(
                    DeserializeAttachmentCase<AttachmentEditStateProbeRequest>(item)),
                _ => throw new InvalidOperationException($"未知的手动吸附探针操作：{operation}")
            });
        }

        return new { Cases = results };
    }

    private static T DeserializeAttachmentCase<T>(JsonElement item)
    {
        return item.Deserialize<T>(JsonOptions)
            ?? throw new JsonException("手动吸附探针案例为空。");
    }

    private static object ExecuteReferencePointsProbe(AttachmentReferencePointsProbeRequest request)
    {
        var points = Enum.GetValues<AttachmentReferencePoint>()
            .Select(kind => new
            {
                Kind = kind,
                Point = ManualAttachmentCalculator.ResolveAnchorPoint(
                    request.Target,
                    new WindowAttachment(kind, 0d, 0d),
                    request.Dpi)
            })
            .ToArray();
        var probeCapsule = new IntRect(0, 0, 330, 34);
        var rejectsEmptyTarget = ThrowsArgumentOutOfRange(() =>
        {
            _ = ManualAttachmentCalculator.SelectReferencePoint(default, probeCapsule);
        });
        var rejectsEmptyCapsule = ThrowsArgumentOutOfRange(() =>
        {
            _ = ManualAttachmentCalculator.SelectReferencePoint(request.Target, default);
        });
        var rejectsZeroDpi = ThrowsArgumentOutOfRange(() =>
        {
            _ = ManualAttachmentCalculator.Capture(request.Target, probeCapsule, 0);
        }) && ThrowsArgumentOutOfRange(() =>
        {
            _ = ManualAttachmentCalculator.ResolveAnchorPoint(
                request.Target,
                new WindowAttachment(AttachmentReferencePoint.TopLeft, 0d, 0d),
                0);
        });
        return new
        {
            request.Name,
            Points = points,
            RejectsEmptyTarget = rejectsEmptyTarget,
            RejectsEmptyCapsule = rejectsEmptyCapsule,
            RejectsZeroDpi = rejectsZeroDpi
        };
    }

    private static object ExecuteSelectReferencePointProbe(AttachmentCapsuleProbeRequest request) =>
        new
        {
            request.Name,
            ReferencePoint = ManualAttachmentCalculator.SelectReferencePoint(
                request.Target,
                request.Capsule)
        };

    private static object ExecuteCaptureResolveProbe(AttachmentCaptureResolveProbeRequest request)
    {
        var attachment = ManualAttachmentCalculator.Capture(
            request.Target,
            request.Capsule,
            request.Dpi);
        var size = new Size(request.Capsule.Width, request.Capsule.Height);
        return new
        {
            request.Name,
            Attachment = attachment,
            ResolvedTopLeft = ManualAttachmentCalculator.ResolveTopLeft(
                request.Target,
                attachment,
                size,
                request.Dpi)
        };
    }

    private static object ExecuteSelectTargetsProbe(AttachmentTargetSelectionProbeRequest request) =>
        new
        {
            request.Name,
            Hits = request.Points
                .Select((point, index) => ManualAttachmentCalculator.SelectTarget(
                    request.Targets,
                    point,
                    index < request.HostSurfaceHits.Count && request.HostSurfaceHits[index]))
                .ToArray()
        };

    private static object ExecuteCalculateScalesProbe(AttachmentScaleProbeRequest request) =>
        new
        {
            request.Name,
            Scales = request.Cases
                .Select(item => ManualAttachmentCalculator.CalculateScale(
                    new Size(item.StartWidth, item.StartHeight),
                    item.StartScale,
                    item.DeltaX,
                    item.DeltaY))
                .ToArray()
        };

    private static object ExecuteEditStateProbe(AttachmentEditStateProbeRequest request)
    {
        var inactive = new ManualPlacementEditState();
        var attachmentApplyThrows = ThrowsInvalidOperation(() => inactive.ApplyAttachment(
            ManualAttachmentRules.DefaultMainAttachment));
        var scaleApplyThrows = ThrowsInvalidOperation(() => inactive.ApplyScale(73));
        var commitThrows = ThrowsInvalidOperation(() => { _ = inactive.Commit(); });
        var cancelThrows = ThrowsInvalidOperation(() => { _ = inactive.Cancel(); });

        var commitState = new ManualPlacementEditState();
        commitState.Begin(request.CommitOriginal);
        commitState.ApplyAttachment(request.CommitAttachment);
        commitState.ApplyScale(request.CommitScale);
        var committed = commitState.Commit();

        var cancelState = new ManualPlacementEditState();
        cancelState.Begin(request.CancelOriginal);
        cancelState.ApplyAttachment(request.CancelAttachment);
        cancelState.ApplyScale(request.CancelScale);
        var cancelled = cancelState.Cancel();

        return new
        {
            request.Name,
            ThrowsBeforeBegin = attachmentApplyThrows && scaleApplyThrows && commitThrows && cancelThrows,
            Committed = committed,
            ActiveAfterCommit = commitState.IsActive,
            Cancelled = cancelled,
            CancelOriginal = request.CancelOriginal,
            ActiveAfterCancel = cancelState.IsActive
        };
    }

    private static bool ThrowsInvalidOperation(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static bool ThrowsArgumentOutOfRange(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }
    }

    private static FormProbeResult ExecuteFormProbe(FormProbeRequest request)
    {
        var results = new List<FormProbeCaseResult>(request.Cases.Count);
        foreach (var probeCase in request.Cases)
        {
            using var form = new TokenStripForm();
            var handle = form.Handle;
            var extendedStyle = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
            var classStyle = GetClassLongPtr(handle, GclStyle).ToInt64();

            var normalCapsuleClickCount = 0;
            form.CapsuleClicked += (_, _) => normalCapsuleClickCount++;

            form.ApplyLayout(probeCase.CollapsedLayout);
            var collapsedBounds = IntRect.FromRectangle(form.Bounds);
            var normalRenderDecorations = form.RenderDecorations;
            form.SimulateCapsuleClick(ScreenCenter(
                probeCase.CollapsedLayout.WindowBounds,
                probeCase.CollapsedLayout.CapsuleBounds));

            var setBoundsCount = form.SetBoundsCoreCallCount;
            form.ApplyLayout(probeCase.ExpandedLayout);
            var expandedSetBoundsCoreDelta = form.SetBoundsCoreCallCount - setBoundsCount;
            var expandedBounds = IntRect.FromRectangle(form.Bounds);

            var capsuleCenter = ScreenCenter(
                probeCase.ExpandedLayout.WindowBounds,
                probeCase.ExpandedLayout.CapsuleBounds);
            var panelCenter = ScreenCenter(
                probeCase.ExpandedLayout.WindowBounds,
                probeCase.ExpandedLayout.PanelBounds);
            var topLeft = new Point(
                probeCase.ExpandedLayout.WindowBounds.Left,
                probeCase.ExpandedLayout.WindowBounds.Top);
            var normalMouseActivateResult = SendMessage(
                handle,
                WmMouseActivate,
                IntPtr.Zero,
                IntPtr.Zero).ToInt32();
            var capsuleCenterHitTest = SendHitTest(handle, capsuleCenter);
            var panelCenterHitTest = SendHitTest(handle, panelCenter);
            var topLeftHitTest = SendHitTest(handle, topLeft);
            var expandedRegionMatchesUnion = RegionMatchesLayout(form, probeCase.ExpandedLayout);

            // Click-through, measured rather than assumed. WindowFromPoint skips windows whose region
            // excludes the point, so a point inside the form rectangle but outside capsule-union-panel
            // must resolve to something other than the strip. Short-lived show() is required because
            // WindowFromPoint ignores hidden windows.
            form.Show();
            Application.DoEvents();
            form.TopMost = true;
            form.RefreshSurface();
            Application.DoEvents();
            var capsuleHitPoint = ScreenCenter(
                probeCase.ExpandedLayout.WindowBounds,
                probeCase.ExpandedLayout.CapsuleBounds);
            var outsidePoint = new Point(
                probeCase.ExpandedLayout.WindowBounds.Left + probeCase.ExpandedLayout.WindowBounds.Width - 12,
                probeCase.ExpandedLayout.WindowBounds.Top + probeCase.ExpandedLayout.WindowBounds.Height - 12);
            var capsuleHitHandle = WindowFromPoint(capsuleHitPoint);
            var outsideHitHandle = WindowFromPoint(outsidePoint);
            var insideCapsuleIsSelf = capsuleHitHandle == form.Handle;
            var outsideRegionIsSelf = outsideHitHandle == form.Handle;
            var capsuleHitClass = ClassNameOf(capsuleHitHandle);
            var outsideHitClass = ClassNameOf(outsideHitHandle);
            form.Hide();
            var normalCommandIntercepted = form.SimulateEditCommand(Keys.Enter);

            var beginEditRejectsExpanded = ThrowsInvalidOperation(() =>
                form.BeginEditMode(probeCase.ExpandedLayout.ScalePercent));

            form.ApplyLayout(probeCase.CollapsedLayout);
            var editCapsuleClickCount = 0;
            form.CapsuleClicked += (_, _) => editCapsuleClickCount++;
            form.BeginEditMode(probeCase.CollapsedLayout.ScalePercent);
            var editHandle = form.Handle;
            var editExtendedStyle = GetWindowLongPtr(editHandle, GwlExStyle).ToInt64();
            var editMouseActivateResult = SendMessage(
                editHandle,
                WmMouseActivate,
                IntPtr.Zero,
                IntPtr.Zero).ToInt32();
            form.SimulateCapsuleClick(ScreenCenter(
                probeCase.CollapsedLayout.WindowBounds,
                probeCase.CollapsedLayout.CapsuleBounds));
            var editResizeHandle100 = IntRect.FromRectangle(form.EditResizeHandleBounds);
            var editIsCollapsed = form.CurrentLayout?.State == OverlayVisualState.Collapsed;
            var editRenderDecorations100 = form.RenderDecorations;

            OverlayEditPreviewEventArgs? movePreview = null;
            var editGestureCompletionCount = 0;
            form.EditPreviewChanged += (_, eventArgs) => movePreview = eventArgs;
            form.EditGestureCompleted += (_, _) => editGestureCompletionCount++;
            var moveStart = ScreenCenter(
                probeCase.CollapsedLayout.WindowBounds,
                probeCase.CollapsedLayout.CapsuleBounds);
            var moveCurrent = new Point(moveStart.X + 30, moveStart.Y + 20);
            var moveStartSize = form.Size;
            form.SimulateEditDrag(moveStart, moveCurrent);
            var movePreviewBounds = IntRect.FromRectangle(form.Bounds);
            form.SimulateEditGestureCompleted(moveCurrent);
            var capturedMovePreview = movePreview;

            var saveRequestCount = 0;
            var cancelRequestCount = 0;
            form.EditSaveRequested += (_, _) => saveRequestCount++;
            form.EditCancelRequested += (_, _) => cancelRequestCount++;
            form.SimulateEditCommand(Keys.Enter);
            form.SimulateEditCommand(Keys.Escape);

            var minimumResize = ExecuteResizeProbe(form, probeCase.CollapsedLayout, -2000);
            var maximumResize = ExecuteResizeProbe(form, probeCase.CollapsedLayout, 2000);
            var lostMoveCapture = ExecuteLostCaptureProbe(
                probeCase.CollapsedLayout,
                OverlayEditGestureKind.Move);
            var lostResizeCapture = ExecuteLostCaptureProbe(
                probeCase.CollapsedLayout,
                OverlayEditGestureKind.Resize);
            var cancelCapture = ExecuteCancelledCaptureProbe(probeCase.CollapsedLayout);
            var disposeCapture = ExecuteDisposedCaptureProbe(probeCase.CollapsedLayout);

            form.ApplyLayout(probeCase.Collapsed60Layout);
            var editResizeHandle60 = IntRect.FromRectangle(form.EditResizeHandleBounds);
            var editRenderDecorations60 = form.RenderDecorations;
            form.ApplyLayout(probeCase.Collapsed130Layout);
            var editResizeHandle130 = IntRect.FromRectangle(form.EditResizeHandleBounds);
            var editRenderDecorations130 = form.RenderDecorations;

            form.EndEditMode();
            var restoredHandle = form.Handle;
            var restoredExtendedStyle = GetWindowLongPtr(restoredHandle, GwlExStyle).ToInt64();
            var restoredMouseActivateResult = SendMessage(
                restoredHandle,
                WmMouseActivate,
                IntPtr.Zero,
                IntPtr.Zero).ToInt32();

            using var highlight = new AttachmentTargetHighlightForm();
            var highlightHandle = highlight.Handle;
            var highlightExtendedStyle = GetWindowLongPtr(highlightHandle, GwlExStyle).ToInt64();
            var highlightTarget = new IntRect(300, 240, 420, 260);
            var highlightSetBoundsCount = highlight.SetBoundsCoreCallCount;
            highlight.ShowTarget(highlightTarget);
            var highlightSetBoundsCoreDelta = highlight.SetBoundsCoreCallCount - highlightSetBoundsCount;
            var highlightBounds = IntRect.FromRectangle(highlight.Bounds);
            var highlightDeviceDpi = highlight.DeviceDpi <= 0 ? 96 : highlight.DeviceDpi;
            var highlightExpectedRingThicknessPixels = Math.Max(
                1,
                (int)Math.Round(
                    2 * highlightDeviceDpi / 96d,
                    MidpointRounding.AwayFromZero));
            var highlightRing = HighlightHasRingRegion(
                highlight,
                highlightExpectedRingThicknessPixels);
            var highlightHitTest = SendHitTest(highlight.Handle, new Point(301, 241));
            highlight.ClearTarget();
            var highlightHiddenAfterClear = !highlight.Visible;

            results.Add(new FormProbeCaseResult(
                probeCase.Name,
                (extendedStyle & WsExToolWindow) != 0,
                (extendedStyle & WsExNoActivate) != 0,
                (extendedStyle & WsExTransparent) != 0,
                // Per-pixel alpha requires WS_EX_LAYERED; the class drop shadow was removed in this
                // round because it is drawn around the window *region* and would outline the now
                // invisible rectangle instead of the glyphs.
                (extendedStyle & WsExLayered) != 0,
                (classStyle & CsDropShadow) != 0,
                normalMouseActivateResult,
                capsuleCenterHitTest,
                panelCenterHitTest,
                topLeftHitTest,
                collapsedBounds,
                expandedBounds,
                expandedSetBoundsCoreDelta,
                expandedRegionMatchesUnion,
                insideCapsuleIsSelf,
                outsideRegionIsSelf,
                capsuleHitClass,
                outsideHitClass,
                normalCapsuleClickCount,
                normalCommandIntercepted,
                normalRenderDecorations,
                beginEditRejectsExpanded,
                (editExtendedStyle & WsExToolWindow) != 0,
                (editExtendedStyle & WsExNoActivate) != 0,
                editMouseActivateResult,
                editIsCollapsed,
                editCapsuleClickCount,
                editResizeHandle60,
                editResizeHandle100,
                editResizeHandle130,
                editRenderDecorations60,
                editRenderDecorations100,
                editRenderDecorations130,
                capturedMovePreview,
                movePreviewBounds,
                moveStartSize.Width == movePreviewBounds.Width
                    && moveStartSize.Height == movePreviewBounds.Height,
                minimumResize,
                maximumResize,
                lostMoveCapture,
                lostResizeCapture,
                cancelCapture,
                disposeCapture,
                editGestureCompletionCount,
                saveRequestCount,
                cancelRequestCount,
                (restoredExtendedStyle & WsExNoActivate) != 0,
                restoredMouseActivateResult,
                OverlayRenderMetrics.Create(96, 60),
                OverlayRenderMetrics.Create(96, 100),
                OverlayRenderMetrics.Create(96, 130),
                (highlightExtendedStyle & WsExToolWindow) != 0,
                (highlightExtendedStyle & WsExNoActivate) != 0,
                (highlightExtendedStyle & WsExTransparent) != 0,
                highlight.ShowInTaskbar,
                highlightSetBoundsCoreDelta,
                highlightBounds,
                highlightDeviceDpi,
                highlightExpectedRingThicknessPixels,
                highlightRing,
                highlightHitTest,
                highlightHiddenAfterClear,
                highlight.Region is null));
        }

        return new FormProbeResult(results);
    }

    private static OverlayEditPreviewEventArgs ExecuteResizeProbe(
        TokenStripForm form,
        OverlayLayoutResult collapsedLayout,
        int delta)
    {
        form.ApplyLayout(collapsedLayout);
        OverlayEditPreviewEventArgs? preview = null;
        form.EditPreviewChanged += CapturePreview;
        var resizeHandle = form.EditResizeHandleBounds;
        var start = form.PointToScreen(new Point(
            resizeHandle.Left + Math.Max(0, resizeHandle.Width / 2),
            resizeHandle.Top + Math.Max(0, resizeHandle.Height / 2)));
        var current = new Point(start.X + delta, start.Y + delta);
        form.SimulateEditResize(start, current);
        form.EditPreviewChanged -= CapturePreview;
        form.SimulateEditGestureCompleted(current);
        return preview ?? throw new InvalidOperationException("缩放模拟未产生预览事件。");

        void CapturePreview(object? sender, OverlayEditPreviewEventArgs eventArgs) => preview = eventArgs;
    }

    private static EditCaptureLossProbeResult ExecuteLostCaptureProbe(
        OverlayLayoutResult collapsedLayout,
        OverlayEditGestureKind kind)
    {
        using var form = CreateEditingForm(collapsedLayout);
        OverlayEditPreviewEventArgs? preview = null;
        OverlayEditPreviewEventArgs? completed = null;
        var completionCount = 0;
        form.EditPreviewChanged += (_, eventArgs) => preview = eventArgs;
        form.EditGestureCompleted += (_, eventArgs) =>
        {
            completionCount++;
            completed = eventArgs;
        };

        var start = kind == OverlayEditGestureKind.Move
            ? ScreenCenter(collapsedLayout.WindowBounds, collapsedLayout.CapsuleBounds)
            : ResizeHandleCenter(form);
        var current = new Point(start.X + 31, start.Y + 19);
        if (kind == OverlayEditGestureKind.Move)
        {
            form.SimulateEditDrag(start, current);
        }
        else
        {
            form.SimulateEditResize(start, current);
        }

        var activeBeforeLoss = form.IsEditGestureActive;
        form.SimulateEditCaptureLost();
        var activeAfterLoss = form.IsEditGestureActive;
        var captureAfterLoss = form.Capture;
        form.SimulateEditCaptureLost();
        form.SimulateEditGestureCompleted(new Point(current.X + 5, current.Y + 5));
        var activeAfterRepeatedSignals = form.IsEditGestureActive;

        return new EditCaptureLossProbeResult(
            kind,
            preview,
            completed,
            completionCount,
            activeBeforeLoss,
            activeAfterLoss,
            activeAfterRepeatedSignals,
            captureAfterLoss,
            0);
    }

    private static EditCaptureLossProbeResult ExecuteCancelledCaptureProbe(
        OverlayLayoutResult collapsedLayout)
    {
        using var form = CreateEditingForm(collapsedLayout);
        OverlayEditPreviewEventArgs? preview = null;
        OverlayEditPreviewEventArgs? completed = null;
        var completionCount = 0;
        var cancelCount = 0;
        form.EditPreviewChanged += (_, eventArgs) => preview = eventArgs;
        form.EditGestureCompleted += (_, eventArgs) =>
        {
            completionCount++;
            completed = eventArgs;
        };
        form.EditCancelRequested += (_, _) => cancelCount++;

        var start = ScreenCenter(collapsedLayout.WindowBounds, collapsedLayout.CapsuleBounds);
        var current = new Point(start.X + 17, start.Y + 11);
        form.SimulateEditDrag(start, current);
        var activeBeforeCancel = form.IsEditGestureActive;
        form.SimulateEditCommand(Keys.Escape);
        form.EndEditMode();

        return new EditCaptureLossProbeResult(
            OverlayEditGestureKind.Move,
            preview,
            completed,
            completionCount,
            activeBeforeCancel,
            form.IsEditGestureActive,
            form.IsEditGestureActive,
            form.Capture,
            cancelCount);
    }

    private static EditCaptureLossProbeResult ExecuteDisposedCaptureProbe(
        OverlayLayoutResult collapsedLayout)
    {
        var form = CreateEditingForm(collapsedLayout);
        OverlayEditPreviewEventArgs? preview = null;
        OverlayEditPreviewEventArgs? completed = null;
        var completionCount = 0;
        form.EditPreviewChanged += (_, eventArgs) => preview = eventArgs;
        form.EditGestureCompleted += (_, eventArgs) =>
        {
            completionCount++;
            completed = eventArgs;
        };

        var start = ResizeHandleCenter(form);
        var current = new Point(start.X + 13, start.Y + 13);
        form.SimulateEditResize(start, current);
        var activeBeforeDispose = form.IsEditGestureActive;
        form.Dispose();

        return new EditCaptureLossProbeResult(
            OverlayEditGestureKind.Resize,
            preview,
            completed,
            completionCount,
            activeBeforeDispose,
            form.IsEditGestureActive,
            form.IsEditGestureActive,
            form.Capture,
            0);
    }

    private static TokenStripForm CreateEditingForm(OverlayLayoutResult collapsedLayout)
    {
        var form = new TokenStripForm();
        _ = form.Handle;
        form.ApplyLayout(collapsedLayout);
        form.BeginEditMode(collapsedLayout.ScalePercent);
        return form;
    }

    private static Point ResizeHandleCenter(TokenStripForm form)
    {
        var resizeHandle = form.EditResizeHandleBounds;
        return form.PointToScreen(new Point(
            resizeHandle.Left + Math.Max(0, resizeHandle.Width / 2),
            resizeHandle.Top + Math.Max(0, resizeHandle.Height / 2)));
    }

    private static bool HighlightHasRingRegion(
        AttachmentTargetHighlightForm form,
        int expectedThicknessPixels)
    {
        if (form.Region is null || form.ClientSize.Width < 8 || form.ClientSize.Height < 8)
        {
            return false;
        }

        var outer = new Rectangle(Point.Empty, form.ClientSize);
        using var expected = new Region(outer);
        if (form.ClientSize.Width > expectedThicknessPixels * 2
            && form.ClientSize.Height > expectedThicknessPixels * 2)
        {
            expected.Exclude(Rectangle.Inflate(
                outer,
                -expectedThicknessPixels,
                -expectedThicknessPixels));
        }

        using var graphics = form.CreateGraphics();
        return form.Region.Equals(expected, graphics);
    }

    private static object ExecuteThemeProbe()
    {
        var backgroundErrors = new List<string>();
        object? registryValue = 0;
        UserPreferenceChangedEventHandler? registeredHandler = null;
        UserPreferenceChangedEventHandler? capturedHandler = null;
        var subscribeCount = 0;
        var unsubscribeCount = 0;
        var sourceChangedCount = 0;
        var sourceChangedThread = -1;

        var source = new WindowsOverlayThemeSource(
            () => registryValue,
            handler =>
            {
                subscribeCount++;
                registeredHandler = handler;
                capturedHandler = handler;
            },
            handler =>
            {
                unsubscribeCount++;
                if (ReferenceEquals(registeredHandler, handler))
                {
                    registeredHandler = null;
                }
            });
        source.Changed += (_, _) =>
        {
            sourceChangedCount++;
            sourceChangedThread = Environment.CurrentManagedThreadId;
        };
        var sourceInitialDark = source.Current == OverlayThemeKind.Dark
            && subscribeCount == 1;
        registryValue = 1;
        var sourceChangeTask = RunCaptured(
            () => capturedHandler?.Invoke(
                source,
                new UserPreferenceChangedEventArgs(UserPreferenceCategory.General)),
            backgroundErrors);
        var sourceChangeCompleted = PumpUntil(() => sourceChangeTask.IsCompleted);
        var sourceChangedOnce = sourceChangeCompleted
            && source.Current == OverlayThemeKind.Light
            && sourceChangedCount == 1
            && sourceChangedThread != Environment.CurrentManagedThreadId;

        registryValue = 2;
        var sameSourceTask = RunCaptured(
            () => capturedHandler?.Invoke(
                source,
                new UserPreferenceChangedEventArgs(UserPreferenceCategory.General)),
            backgroundErrors);
        var sameSourceCompleted = PumpUntil(() => sameSourceTask.IsCompleted);
        var sourceSameKindIgnored = sameSourceCompleted && sourceChangedCount == 1;

        source.Dispose();
        source.Dispose();
        registryValue = 0;
        var postDisposeSourceTask = RunCaptured(
            () => capturedHandler?.Invoke(
                source,
                new UserPreferenceChangedEventArgs(UserPreferenceCategory.General)),
            backgroundErrors);
        var postDisposeSourceCompleted = PumpUntil(() => postDisposeSourceTask.IsCompleted);
        var sourcePostDisposeIgnored = postDisposeSourceCompleted
            && sourceChangedCount == 1
            && source.Current == OverlayThemeKind.Light;

        using var dispatcher = new Control();
        _ = dispatcher.Handle;
        var uiThreadId = Environment.CurrentManagedThreadId;
        var applied = new List<(Color Background, int ThreadId)>();
        var fake = new ProbeOverlayThemeSource(OverlayThemeKind.Dark);
        var binding = new OverlayThemeBinding(
            dispatcher,
            fake,
            palette => applied.Add((palette.Background, Environment.CurrentManagedThreadId)));
        var bindingInitialDark = applied.Count == 1
            && applied[0].Background == OverlayThemePalette.For(OverlayThemeKind.Dark).Background
            && applied[0].ThreadId == uiThreadId;

        var lightTask = RunCaptured(
            () => fake.Set(OverlayThemeKind.Light, forceEvent: true),
            backgroundErrors);
        var lightTaskCompleted = PumpUntil(() => lightTask.IsCompleted);
        var lightApplied = PumpUntil(() => applied.Count == 2);
        var bindingBackgroundLightOnUiThread = lightTaskCompleted
            && lightApplied
            && applied[1].Background == OverlayThemePalette.For(OverlayThemeKind.Light).Background
            && applied[1].ThreadId == uiThreadId;

        var sameBindingTask = RunCaptured(
            () => fake.Set(OverlayThemeKind.Light, forceEvent: true),
            backgroundErrors);
        var sameBindingTaskCompleted = PumpUntil(() => sameBindingTask.IsCompleted);
        Application.DoEvents();
        var bindingSameKindIgnored = sameBindingTaskCompleted && applied.Count == 2;

        var queuedTask = RunCaptured(
            () => fake.Set(OverlayThemeKind.Dark, forceEvent: true),
            backgroundErrors);
        var queuedTaskCompleted = WaitWithoutPumping(() => queuedTask.IsCompleted);
        binding.Dispose();
        binding.Dispose();
        Application.DoEvents();
        var bindingQueuedCallbackCancelledOnDispose = queuedTaskCompleted && applied.Count == 2;

        using var themedForm = new TokenStripForm();
        using var themedHighlight = new AttachmentTargetHighlightForm();
        _ = themedForm.Handle;
        _ = themedHighlight.Handle;
        var themedPresentation = OverlayPresentationBuilder.CreateWaiting(
            "theme-probe",
            DisplayField.Total,
            DisplayField.ContextPercent,
            DisplayField.Total | DisplayField.ContextPercent);
        themedForm.SetPresentation(themedPresentation);
        themedForm.BeginEditMode(ManualAttachmentRules.DefaultScalePercent);
        var themedBounds = themedForm.Bounds;
        var themedLayout = themedForm.CurrentLayout;
        var themedPresentationReference = themedForm.CurrentPresentation;
        var themedEditState = themedForm.IsEditMode;
        var themedVisibility = themedForm.Visible;
        var themedHighlightBounds = themedHighlight.Bounds;
        var themedHighlightVisibility = themedHighlight.Visible;
        var formApplyCount = 0;
        var formApplyThreads = new List<int>();
        var formSource = new ProbeOverlayThemeSource(OverlayThemeKind.Dark);
        var formBinding = new OverlayThemeBinding(
            themedHighlight,
            formSource,
            palette =>
            {
                formApplyCount++;
                formApplyThreads.Add(Environment.CurrentManagedThreadId);
                themedForm.ApplyTheme(palette);
                themedHighlight.ApplyTheme(palette);
            });
        var darkPalette = OverlayThemePalette.For(OverlayThemeKind.Dark);
        var lightPalette = OverlayThemePalette.For(OverlayThemeKind.Light);
        var formsInitialDark = formApplyCount == 1
            && themedForm.CurrentThemePalette == darkPalette
            && themedHighlight.CurrentThemePalette == darkPalette
            && themedForm.BackColor == darkPalette.Background
            && themedForm.ForeColor == darkPalette.Value
            && themedHighlight.BackColor == Color.Fuchsia
            && themedHighlight.TransparencyKey == Color.Fuchsia;

        var formLightTask = RunCaptured(
            () => formSource.Set(OverlayThemeKind.Light, forceEvent: true),
            backgroundErrors);
        var formLightTaskCompleted = PumpUntil(() => formLightTask.IsCompleted);
        var formLightApplied = PumpUntil(() => formApplyCount == 2);
        var formsBackgroundLightOnUiThread = formLightTaskCompleted
            && formLightApplied
            && themedForm.CurrentThemePalette == lightPalette
            && themedHighlight.CurrentThemePalette == lightPalette
            && themedForm.BackColor == lightPalette.Background
            && themedForm.ForeColor == lightPalette.Value
            && formApplyThreads.All(threadId => threadId == uiThreadId);
        var formsStateUnchanged = themedForm.Bounds == themedBounds
            && ReferenceEquals(themedForm.CurrentLayout, themedLayout)
            && ReferenceEquals(themedForm.CurrentPresentation, themedPresentationReference)
            && themedForm.IsEditMode == themedEditState
            && themedForm.Visible == themedVisibility
            && themedHighlight.Bounds == themedHighlightBounds
            && themedHighlight.Visible == themedHighlightVisibility;

        var formSameTask = RunCaptured(
            () => formSource.Set(OverlayThemeKind.Light, forceEvent: true),
            backgroundErrors);
        var formSameTaskCompleted = PumpUntil(() => formSameTask.IsCompleted);
        Application.DoEvents();
        var formsSameKindIgnored = formSameTaskCompleted && formApplyCount == 2;

        themedForm.EndEditMode();
        var formDarkTask = RunCaptured(
            () => formSource.Set(OverlayThemeKind.Dark, forceEvent: true),
            backgroundErrors);
        var formDarkTaskCompleted = PumpUntil(() => formDarkTask.IsCompleted);
        var formDarkApplied = PumpUntil(() => formApplyCount == 3);
        var formsSurviveTokenHandleRecreation = formDarkTaskCompleted
            && formDarkApplied
            && themedForm.CurrentThemePalette == darkPalette
            && themedHighlight.CurrentThemePalette == darkPalette
            && formApplyThreads.All(threadId => threadId == uiThreadId);

        formBinding.Dispose();
        formBinding.Dispose();
        var postDisposeFormTask = RunCaptured(
            formSource.RaiseCapturedEvent,
            backgroundErrors);
        var postDisposeFormCompleted = PumpUntil(() => postDisposeFormTask.IsCompleted);
        Application.DoEvents();
        var formsPostDisposeIgnored = postDisposeFormCompleted
            && formApplyCount == 3
            && themedForm.CurrentThemePalette == darkPalette
            && themedHighlight.CurrentThemePalette == darkPalette;

        return new
        {
            Cases = new[]
            {
                new
                {
                    Name = "theme-lifecycle",
                    Supported = true,
                    SourceInitialDark = sourceInitialDark,
                    SourceChangedOnce = sourceChangedOnce,
                    SourceSameKindIgnored = sourceSameKindIgnored,
                    SourceUnsubscribedOnce = unsubscribeCount == 1 && registeredHandler is null,
                    SourceDisposeIdempotent = unsubscribeCount == 1,
                    SourcePostDisposeIgnored = sourcePostDisposeIgnored,
                    BindingInitialDark = bindingInitialDark,
                    BindingBackgroundLightOnUiThread = bindingBackgroundLightOnUiThread,
                    BindingSameKindIgnored = bindingSameKindIgnored,
                    BindingQueuedCallbackCancelledOnDispose = bindingQueuedCallbackCancelledOnDispose,
                    BindingUnsubscribedBeforeSourceDispose = fake.UnsubscribedBeforeDispose,
                    FormsSupported = true,
                    FormsInitialDark = formsInitialDark,
                    FormsBackgroundLightOnUiThread = formsBackgroundLightOnUiThread,
                    FormsStateUnchanged = formsStateUnchanged,
                    FormsSameKindIgnored = formsSameKindIgnored,
                    FormsSurviveTokenHandleRecreation = formsSurviveTokenHandleRecreation,
                    FormsPostDisposeIgnored = formsPostDisposeIgnored,
                    NoBackgroundException = backgroundErrors.Count == 0
                }
            }
        };
    }

    private static Task RunCaptured(Action action, List<string> errors) => Task.Run(() =>
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            lock (errors)
            {
                errors.Add(exception.ToString());
            }
        }
    });

    private static bool PumpUntil(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 3000;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(1);
        }
        Application.DoEvents();
        return condition();
    }

    private static bool WaitWithoutPumping(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 3000;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(1);
        }
        return condition();
    }

    private static Point ScreenCenter(IntRect windowBounds, IntRect clientBounds) =>
        new(
            windowBounds.X + clientBounds.X + (clientBounds.Width / 2),
            windowBounds.Y + clientBounds.Y + (clientBounds.Height / 2));

    private static int SendHitTest(IntPtr handle, Point screenPoint)
    {
        var packed = unchecked((screenPoint.Y << 16) | (screenPoint.X & 0xffff));
        return SendMessage(handle, WmNcHitTest, IntPtr.Zero, (IntPtr)packed).ToInt32();
    }

    private static bool RegionMatchesLayout(TokenStripForm form, OverlayLayoutResult layout)
    {
        // Plain rectangles: the rounded corners used to come from the capsule fill, which the
        // transparent presentation no longer draws. The region is now purely the input area.
        using var path = new GraphicsPath();
        if (!layout.CapsuleBounds.IsEmpty)
        {
            path.AddRectangle(layout.CapsuleBounds.ToRectangle());
        }
        if (!layout.PanelBounds.IsEmpty)
        {
            path.AddRectangle(layout.PanelBounds.ToRectangle());
        }

        using var expected = new Region(path);
        using var graphics = form.CreateGraphics();
        return form.Region?.Equals(expected, graphics) == true;
    }

    private static GraphicsPath CreateRoundedRectanglePath(Rectangle rectangle, int radius)
    {
        var path = new GraphicsPath();
        var clampedRadius = Math.Clamp(radius, 0, Math.Min(rectangle.Width, rectangle.Height) / 2);
        if (clampedRadius == 0)
        {
            path.AddRectangle(rectangle);
            return path;
        }

        var diameter = clampedRadius * 2;
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static int ScaleDip(int dip, uint dpi) =>
        (int)Math.Round(dip * dpi / 96d, MidpointRounding.AwayFromZero);

    private static IntPtr GetWindowLongPtr(IntPtr windowHandle, int index) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(windowHandle, index)
            : (IntPtr)GetWindowLong32(windowHandle, index);

    private static IntPtr GetClassLongPtr(IntPtr windowHandle, int index) =>
        IntPtr.Size == 8
            ? GetClassLongPtr64(windowHandle, index)
            : (IntPtr)unchecked((int)GetClassLong32(windowHandle, index));

    internal static void WriteJson(string path, object? value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    private sealed class LayoutProbeEnvelopeRequest
    {
        public IReadOnlyList<JsonElement> Cases { get; init; } = [];
    }

    private sealed class AttachmentProbeEnvelopeRequest
    {
        public IReadOnlyList<JsonElement> Cases { get; init; } = [];
    }

    private class AttachmentProbeCaseRequest
    {
        public string Name { get; init; } = string.Empty;
    }

    private sealed class AttachmentReferencePointsProbeRequest : AttachmentProbeCaseRequest
    {
        public IntRect Target { get; init; }
        public uint Dpi { get; init; }
    }

    private class AttachmentCapsuleProbeRequest : AttachmentProbeCaseRequest
    {
        public IntRect Target { get; init; }
        public IntRect Capsule { get; init; }
    }

    private sealed class AttachmentCaptureResolveProbeRequest : AttachmentCapsuleProbeRequest
    {
        public uint Dpi { get; init; }
    }

    private sealed class AttachmentTargetSelectionProbeRequest : AttachmentProbeCaseRequest
    {
        public required AttachmentTargetBounds Targets { get; init; }
        public IReadOnlyList<Point> Points { get; init; } = [];
        public IReadOnlyList<bool> HostSurfaceHits { get; init; } = [];
    }

    private sealed class AttachmentScaleProbeRequest : AttachmentProbeCaseRequest
    {
        public IReadOnlyList<AttachmentScaleProbeCase> Cases { get; init; } = [];
    }

    private sealed class AttachmentScaleProbeCase
    {
        public int StartWidth { get; init; }
        public int StartHeight { get; init; }
        public int StartScale { get; init; }
        public int DeltaX { get; init; }
        public int DeltaY { get; init; }
    }

    private sealed class AttachmentEditStateProbeRequest : AttachmentProbeCaseRequest
    {
        public required ManualPlacementSnapshot CommitOriginal { get; init; }
        public required WindowAttachment CommitAttachment { get; init; }
        public int CommitScale { get; init; }
        public required ManualPlacementSnapshot CancelOriginal { get; init; }
        public required WindowAttachment CancelAttachment { get; init; }
        public int CancelScale { get; init; }
    }

    private sealed class CaptionBoundsProbeRequest
    {
        public string Name { get; init; } = string.Empty;
        public IntRect WindowBounds { get; init; }
        public IntRect RelativeBounds { get; init; }
    }

    private sealed record CaptionBoundsProbeResult(string Name, IntRect ScreenBounds);

    private sealed class FormProbeRequest
    {
        public IReadOnlyList<FormProbeCaseRequest> Cases { get; init; } = [];
    }

    private sealed class FormProbeCaseRequest
    {
        public string Name { get; init; } = string.Empty;
        public required OverlayLayoutResult CollapsedLayout { get; init; }
        public required OverlayLayoutResult Collapsed60Layout { get; init; }
        public required OverlayLayoutResult Collapsed130Layout { get; init; }
        public required OverlayLayoutResult ExpandedLayout { get; init; }
    }

    private sealed record FormProbeResult(IReadOnlyList<FormProbeCaseResult> Cases);

    private sealed class ProbeOverlayThemeSource : IOverlayThemeSource
    {
        private readonly object _gate = new();
        private EventHandler? _changed;
        private EventHandler? _lastSubscribedHandler;
        private OverlayThemeKind _current;
        private int _sequence;
        private bool _disposed;

        public ProbeOverlayThemeSource(OverlayThemeKind current)
        {
            _current = current;
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

        public bool UnsubscribedBeforeDispose { get; private set; }

        public event EventHandler? Changed
        {
            add
            {
                lock (_gate)
                {
                    _changed += value;
                    _lastSubscribedHandler = value;
                }
            }
            remove
            {
                lock (_gate)
                {
                    _changed -= value;
                    _sequence++;
                }
            }
        }

        public void Set(OverlayThemeKind kind, bool forceEvent)
        {
            EventHandler? changed;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                var kindChanged = kind != _current;
                _current = kind;
                if (!forceEvent && !kindChanged)
                {
                    return;
                }
                changed = _changed;
            }

            changed?.Invoke(this, EventArgs.Empty);
        }

        public void RaiseCapturedEvent()
        {
            EventHandler? captured;
            lock (_gate)
            {
                captured = _lastSubscribedHandler;
            }
            captured?.Invoke(this, EventArgs.Empty);
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
                _sequence++;
                UnsubscribedBeforeDispose = _changed is null && _sequence == 2;
                _changed = null;
            }
        }
    }

    private sealed record EditCaptureLossProbeResult(
        OverlayEditGestureKind ExpectedKind,
        OverlayEditPreviewEventArgs? Preview,
        OverlayEditPreviewEventArgs? Completed,
        int CompletionCount,
        bool ActiveBeforeInterruption,
        bool ActiveAfterInterruption,
        bool ActiveAfterRepeatedSignals,
        bool CaptureAfterInterruption,
        int CancelRequestCount);

    private sealed record FormProbeCaseResult(
        string Name,
        bool WsExToolWindowPresent,
        bool WsExNoActivatePresent,
        bool WsExTransparentPresent,
        bool WsExLayeredPresent,
        bool CsDropShadowPresent,
        int MouseActivateResult,
        int CapsuleCenterHitTest,
        int PanelCenterHitTest,
        int TopLeftHitTest,
        IntRect CollapsedBounds,
        IntRect ExpandedBounds,
        int ExpandedSetBoundsCoreDelta,
        bool ExpandedRegionMatchesUnion,
        bool InsideCapsuleWindowFromPointIsSelf,
        bool OutsideRegionWindowFromPointIsSelf,
        string CapsuleHitWindowClass,
        string OutsideHitWindowClass,
        int NormalCapsuleClickCount,
        bool NormalCommandIntercepted,
        OverlayRenderDecorationState NormalRenderDecorations,
        bool BeginEditRejectsExpanded,
        bool EditWsExToolWindowPresent,
        bool EditWsExNoActivatePresent,
        int EditMouseActivateResult,
        bool EditIsCollapsed,
        int EditCapsuleClickCount,
        IntRect EditResizeHandle60,
        IntRect EditResizeHandle100,
        IntRect EditResizeHandle130,
        OverlayRenderDecorationState EditRenderDecorations60,
        OverlayRenderDecorationState EditRenderDecorations100,
        OverlayRenderDecorationState EditRenderDecorations130,
        OverlayEditPreviewEventArgs? MovePreview,
        IntRect MovePreviewBounds,
        bool MovePreservedSize,
        OverlayEditPreviewEventArgs MinimumResizePreview,
        OverlayEditPreviewEventArgs MaximumResizePreview,
        EditCaptureLossProbeResult LostMoveCapture,
        EditCaptureLossProbeResult LostResizeCapture,
        EditCaptureLossProbeResult CancelCapture,
        EditCaptureLossProbeResult DisposeCapture,
        int EditGestureCompletionCount,
        int SaveRequestCount,
        int CancelRequestCount,
        bool RestoredWsExNoActivatePresent,
        int RestoredMouseActivateResult,
        OverlayRenderMetrics Metrics60,
        OverlayRenderMetrics Metrics100,
        OverlayRenderMetrics Metrics130,
        bool HighlightWsExToolWindowPresent,
        bool HighlightWsExNoActivatePresent,
        bool HighlightWsExTransparentPresent,
        bool HighlightShowInTaskbar,
        int HighlightSetBoundsCoreDelta,
        IntRect HighlightBounds,
        int HighlightDeviceDpi,
        int HighlightExpectedRingThicknessPixels,
        bool HighlightHasRingRegion,
        int HighlightHitTest,
        bool HighlightHiddenAfterClear,
        bool HighlightRegionCleared);

    private const int GwlExStyle = -20;
    private const int GclStyle = -26;
    private const long WsExTransparent = 0x00000020;
    private const long WsExLayered = 0x00080000;

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private static IntPtr WindowFromPoint(Point point) =>
        WindowFromPoint(new NativePoint { X = point.X, Y = point.Y });

    private static string ClassNameOf(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return "(none)";
        }

        var buffer = new StringBuilder(256);
        var length = GetClassName(windowHandle, buffer, buffer.Capacity);
        return length <= 0 ? "(unknown)" : buffer.ToString(0, length);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr windowHandle, StringBuilder buffer, int maximum);
    private const long WsExToolWindow = 0x00000080;
    private const long WsExNoActivate = 0x08000000;
    private const long CsDropShadow = 0x00020000;
    private const int WmMouseActivate = 0x0021;
    private const int WmNcHitTest = 0x0084;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "GetClassLongW")]
    private static extern uint GetClassLong32(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static extern IntPtr GetClassLongPtr64(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(
        IntPtr windowHandle,
        int message,
        IntPtr wParam,
        IntPtr lParam);
}
