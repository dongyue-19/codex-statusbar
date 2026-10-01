using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;

namespace CodexStatusbar;

internal sealed record CodexWindowTarget(CodexWindowInfo HostWindow);

internal sealed record WindowSurfaceCandidate(
    long Handle,
    uint ProcessId,
    bool IsVisible,
    bool IsMinimized,
    IntRect Bounds)
{
    public bool BoundsReadSucceeded { get; init; } = true;
}

internal static class CodexWindowLocator
{
    private static readonly ConditionalWeakTable<CodexWindowTarget, ConfirmedCodexTargetIdentity>
        ConfirmedTargets = new();
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint GaRoot = 2;
    private const uint GwOwner = 4;
    private const int GwlExStyle = -20;
    private const int DwmwaCaptionButtonBounds = 5;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int SmCxSize = 30;
    private const int SmCySize = 31;
    private const int SmCxSizeFrame = 32;
    private const int SmCySizeFrame = 33;
    private const int SmCxPaddedBorder = 92;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr windowHandle, StringBuilder text, int maximumCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr windowHandle, uint command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassName(
        IntPtr windowHandle,
        StringBuilder className,
        int maximumCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr windowHandle, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRectangle rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        IntPtr processHandle,
        uint flags,
        StringBuilder executablePath,
        ref uint pathLength);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        out NativeRectangle value,
        int valueSize);

    public static bool TryGetForegroundCodexTarget(out CodexWindowTarget target)
    {
        target = null!;
        var windowHandle = GetForegroundWindow();
        windowHandle = GetAncestor(windowHandle, GaRoot);
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(windowHandle, out var processId);
        if (processId == 0)
        {
            return false;
        }

        if (!IsCodexDesktopProcess(processId))
        {
            return false;
        }

        if (!TryGetCodexTarget(windowHandle, processId, out target))
        {
            return false;
        }

        RememberConfirmedTarget(target, processId);
        return true;
    }

    public static bool TryRefreshKnownCodexTarget(
        CodexWindowTarget previous,
        out CodexWindowTarget refreshed)
    {
        ArgumentNullException.ThrowIfNull(previous);
        refreshed = null!;
        var previousHost = previous.HostWindow.Handle;
        if (previousHost == IntPtr.Zero
            || !ConfirmedTargets.TryGetValue(previous, out var confirmed))
        {
            return false;
        }

        GetWindowThreadProcessId(previousHost, out var currentProcessId);
        if (!IsKnownTargetIdentityValid(
                confirmed.HostHandle,
                confirmed.ProcessId,
                previousHost.ToInt64(),
                currentProcessId)
            || !IsCodexDesktopProcess(confirmed.ProcessId))
        {
            return false;
        }

        var expectedProcessId = confirmed.ProcessId;

        if (!TryEnumerateCandidates(expectedProcessId, out var candidates)
            || !TrySelectKnownCodexTarget(
                previousHost,
                expectedProcessId,
                candidates,
                out var selection)
            || !TryReadWindowInfo(selection.Host.Handle, out var hostWindow))
        {
            return false;
        }

        refreshed = new CodexWindowTarget(hostWindow);
        RememberConfirmedTarget(refreshed, expectedProcessId);
        return true;
    }

    internal static bool TrySelectKnownCodexTarget(
        IntPtr previousHostHandle,
        uint expectedProcessId,
        IReadOnlyList<WindowCandidateFacts> candidates,
        out CodexWindowCandidateSelection selection)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        selection = null!;
        if (previousHostHandle == IntPtr.Zero || expectedProcessId == 0)
        {
            return false;
        }

        var sameProcess = candidates
            .Where(item => item.ProcessId == expectedProcessId && item.IsCodexProcess)
            .ToArray();
        var selected = CodexWindowClassifier.Select(sameProcess, previousHostHandle);
        if (selected is null || selected.Host.Handle != previousHostHandle)
        {
            return false;
        }

        selection = selected;
        return true;
    }

    internal static bool IsCandidateReadValid(
        int classNameLength,
        long extendedStyle,
        int styleReadError) =>
        classNameLength > 0 && (extendedStyle != 0 || styleReadError == 0);

    internal static bool IsKnownTargetIdentityValid(
        long confirmedHostHandle,
        uint confirmedProcessId,
        long currentHostHandle,
        uint currentProcessId) =>
        confirmedHostHandle != 0
        && confirmedProcessId != 0
        && confirmedHostHandle == currentHostHandle
        && confirmedProcessId == currentProcessId;

    public static bool IsPointOnKnownHost(
        CodexWindowTarget target,
        Point point,
        IReadOnlySet<long> ignoredHandles)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(ignoredHandles);

        var hostHandle = target.HostWindow.Handle;
        if (hostHandle == IntPtr.Zero
            || !ConfirmedTargets.TryGetValue(target, out var confirmed))
        {
            return false;
        }

        GetWindowThreadProcessId(hostHandle, out var currentHostProcessId);
        if (!IsKnownTargetIdentityValid(
            confirmed.HostHandle,
            confirmed.ProcessId,
            hostHandle.ToInt64(),
            currentHostProcessId))
        {
            return false;
        }

        if (!TryEnumerateWindowSurfaces(out var candidates))
        {
            return false;
        }

        if (!IsUnderlyingWindowKnownHostForCurrentProcess(
            candidates,
            point,
            ignoredHandles,
            confirmed.HostHandle,
            confirmed.ProcessId))
        {
            return false;
        }

        var selectedRoot = GetAncestor(new IntPtr(confirmed.HostHandle), GaRoot);
        if (selectedRoot == IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(selectedRoot, out var selectedProcessId);
        return IsKnownTargetIdentityValid(
            confirmed.HostHandle,
            confirmed.ProcessId,
            selectedRoot.ToInt64(),
            selectedProcessId);
    }

    internal static long? SelectUnderlyingWindowAtPoint(
        IReadOnlyList<WindowSurfaceCandidate> zOrderedCandidates,
        Point point,
        IReadOnlySet<long> ignoredHandles,
        uint ignoredProcessId)
    {
        ArgumentNullException.ThrowIfNull(zOrderedCandidates);
        ArgumentNullException.ThrowIfNull(ignoredHandles);

        foreach (var candidate in zOrderedCandidates)
        {
            if (candidate.Handle == 0
                || ignoredHandles.Contains(candidate.Handle)
                || (ignoredProcessId != 0 && candidate.ProcessId == ignoredProcessId)
                || !candidate.IsVisible
                || candidate.IsMinimized)
            {
                continue;
            }

            if (!candidate.BoundsReadSucceeded)
            {
                return candidate.Handle;
            }

            if (candidate.Bounds.IsEmpty
                || !candidate.Bounds.Contains(point.X, point.Y))
            {
                continue;
            }

            return candidate.Handle;
        }

        return null;
    }

    internal static bool IsUnderlyingWindowKnownHost(
        IReadOnlyList<WindowSurfaceCandidate> zOrderedCandidates,
        Point point,
        IReadOnlySet<long> ignoredHandles,
        long confirmedHostHandle,
        uint confirmedProcessId,
        uint ignoredProcessId)
    {
        var selectedHandle = SelectUnderlyingWindowAtPoint(
            zOrderedCandidates,
            point,
            ignoredHandles,
            ignoredProcessId);
        if (selectedHandle is null
            || confirmedHostHandle == 0
            || confirmedProcessId == 0)
        {
            return false;
        }

        var selected = zOrderedCandidates.First(item => item.Handle == selectedHandle.Value);
        return selected.BoundsReadSucceeded
            && selected.Handle == confirmedHostHandle
            && selected.ProcessId == confirmedProcessId;
    }

    internal static bool IsUnderlyingWindowKnownHostForCurrentProcess(
        IReadOnlyList<WindowSurfaceCandidate> zOrderedCandidates,
        Point point,
        IReadOnlySet<long> ignoredHandles,
        long confirmedHostHandle,
        uint confirmedProcessId) =>
        IsUnderlyingWindowKnownHost(
            zOrderedCandidates,
            point,
            ignoredHandles,
            confirmedHostHandle,
            confirmedProcessId,
            checked((uint)Environment.ProcessId));

    internal static WindowSurfaceCandidate CreateUnreadableSurfaceCandidate(
        long handle,
        uint processId,
        bool windowStillExists,
        bool isVisible,
        bool isMinimized) =>
        new(
            handle,
            processId,
            windowStillExists ? isVisible : true,
            windowStillExists ? isMinimized : false,
            default)
        {
            BoundsReadSucceeded = false
        };

    private static void RememberConfirmedTarget(CodexWindowTarget target, uint processId) =>
        ConfirmedTargets.Add(
            target,
            new ConfirmedCodexTargetIdentity(target.HostWindow.Handle.ToInt64(), processId));

    public static IntRect ConvertRelativeToScreen(IntRect windowBounds, IntRect relativeBounds) => new(
        windowBounds.X + relativeBounds.X,
        windowBounds.Y + relativeBounds.Y,
        relativeBounds.Width,
        relativeBounds.Height);

    public static object GetForegroundWindowProbe()
    {
        var windowHandle = GetAncestor(GetForegroundWindow(), GaRoot);
        if (windowHandle == IntPtr.Zero)
        {
            return new
            {
                Found = false,
                Handle = 0L,
                ForegroundHandle = 0L,
                ProcessId = 0u,
                IsCodex = false,
                Title = string.Empty,
                HostHandle = (long?)null,
                HostWindowBounds = (IntRect?)null,
                WindowBounds = (IntRect?)null,
                ExtendedFrameBounds = (IntRect?)null,
                CaptionButtonBounds = (IntRect?)null,
                WorkingArea = (IntRect?)null,
                Dpi = (uint?)null,
                ChromeMetrics = (WindowChromeMetrics?)null
            };
        }

        GetWindowThreadProcessId(windowHandle, out var processId);
        var title = new StringBuilder(1024);
        GetWindowText(windowHandle, title, title.Capacity);
        var isCodex = processId != 0 && IsCodexDesktopProcess(processId);
        CodexWindowTarget? target = null;
        if (isCodex && TryGetCodexTarget(windowHandle, processId, out var readTarget))
        {
            target = readTarget;
        }

        return new
        {
            Found = true,
            Handle = windowHandle.ToInt64(),
            ForegroundHandle = windowHandle.ToInt64(),
            ProcessId = processId,
            IsCodex = isCodex,
            Title = title.ToString(),
            HostHandle = target?.HostWindow.Handle.ToInt64(),
            HostWindowBounds = target?.HostWindow.WindowBounds,
            WindowBounds = target?.HostWindow.WindowBounds,
            ExtendedFrameBounds = target?.HostWindow.ExtendedFrameBounds,
            CaptionButtonBounds = target?.HostWindow.CaptionButtonBounds,
            WorkingArea = target?.HostWindow.WorkingArea,
            Dpi = target?.HostWindow.Dpi,
            ChromeMetrics = target?.HostWindow.ChromeMetrics
        };
    }

    private static bool TryGetCodexTarget(
        IntPtr foregroundHandle,
        uint processId,
        out CodexWindowTarget target)
    {
        target = null!;
        if (!TryEnumerateCandidates(processId, out var candidates))
        {
            return false;
        }

        var selection = CodexWindowClassifier.Select(candidates, foregroundHandle);
        if (selection is null
            || !TryReadWindowInfo(selection.Host.Handle, out var hostWindow))
        {
            return false;
        }

        target = new CodexWindowTarget(hostWindow);
        return true;
    }

    private static bool TryEnumerateWindowSurfaces(
        out IReadOnlyList<WindowSurfaceCandidate> candidates)
    {
        var collected = new List<WindowSurfaceCandidate>();
        var enumerationSucceeded = EnumWindows((windowHandle, _) =>
        {
            GetWindowThreadProcessId(windowHandle, out var processId);
            var isVisible = IsWindowVisible(windowHandle);
            var isMinimized = IsIconic(windowHandle);
            if (!GetWindowRect(windowHandle, out var nativeBounds))
            {
                collected.Add(CreateUnreadableSurfaceCandidate(
                    windowHandle.ToInt64(),
                    processId,
                    IsWindow(windowHandle),
                    isVisible,
                    isMinimized));
                return true;
            }

            collected.Add(new WindowSurfaceCandidate(
                windowHandle.ToInt64(),
                processId,
                isVisible,
                isMinimized,
                ToIntRect(nativeBounds)));
            return true;
        }, IntPtr.Zero);
        candidates = collected;
        return enumerationSucceeded;
    }

    private static bool TryEnumerateCandidates(
        uint expectedProcessId,
        out IReadOnlyList<WindowCandidateFacts> candidates)
    {
        var collected = new List<WindowCandidateFacts>();
        var enumerationSucceeded = EnumWindows((windowHandle, _) =>
        {
            GetWindowThreadProcessId(windowHandle, out var candidateProcessId);
            if (candidateProcessId != expectedProcessId)
            {
                return true;
            }

            if (TryReadCandidate(windowHandle, candidateProcessId, out var candidate))
            {
                collected.Add(candidate);
            }
            return true;
        }, IntPtr.Zero);
        candidates = collected;
        return enumerationSucceeded;
    }

    private static bool TryReadCandidate(
        IntPtr windowHandle,
        uint processId,
        out WindowCandidateFacts candidate)
    {
        candidate = null!;
        if (!GetWindowRect(windowHandle, out var nativeBounds))
        {
            return false;
        }

        var className = new StringBuilder(256);
        var classNameLength = GetClassName(windowHandle, className, className.Capacity);

        Marshal.SetLastPInvokeError(0);
        var extendedStyle = GetWindowLongPtr(windowHandle, GwlExStyle);
        var styleReadError = Marshal.GetLastPInvokeError();
        if (!IsCandidateReadValid(
            classNameLength,
            extendedStyle.ToInt64(),
            styleReadError))
        {
            return false;
        }

        candidate = new WindowCandidateFacts(
            windowHandle,
            processId,
            IsCodexProcess: true,
            IsWindowVisible(windowHandle),
            IsIconic(windowHandle),
            GetWindow(windowHandle, GwOwner),
            extendedStyle.ToInt64(),
            ToIntRect(nativeBounds),
            className.ToString());
        return true;
    }

    private static bool TryReadWindowInfo(IntPtr windowHandle, out CodexWindowInfo info)
    {
        info = null!;
        if (!GetWindowRect(windowHandle, out var nativeWindowBounds))
        {
            return false;
        }

        var windowBounds = ToIntRect(nativeWindowBounds);
        var dwmResult = DwmGetWindowAttribute(
            windowHandle,
            DwmwaExtendedFrameBounds,
            out var nativeExtendedFrameBounds,
            Marshal.SizeOf<NativeRectangle>());
        var extendedFrameBounds = dwmResult == 0
            ? ToIntRect(nativeExtendedFrameBounds)
            : windowBounds;
        if (extendedFrameBounds.Width < 500 || extendedFrameBounds.Height < 400)
        {
            return false;
        }

        var workingArea = IntRect.FromRectangle(Screen.FromHandle(windowHandle).WorkingArea);
        var dpi = ReadDpi(windowHandle);
        IntRect? captionButtonBounds = null;
        var chromeMetrics = default(WindowChromeMetrics);
        if (dpi > 0)
        {
            captionButtonBounds = ReadCaptionButtonBounds(windowHandle, windowBounds);
            chromeMetrics = ReadChromeMetrics(dpi);
        }

        info = new CodexWindowInfo(
            windowHandle,
            windowBounds,
            extendedFrameBounds,
            captionButtonBounds,
            workingArea,
            dpi,
            chromeMetrics);
        return true;
    }

    private static IntRect? ReadCaptionButtonBounds(IntPtr windowHandle, IntRect windowBounds)
    {
        var result = DwmGetWindowAttribute(
            windowHandle,
            DwmwaCaptionButtonBounds,
            out var nativeRelativeBounds,
            Marshal.SizeOf<NativeRectangle>());
        if (result != 0)
        {
            return null;
        }

        var relativeBounds = ToIntRect(nativeRelativeBounds);
        if (relativeBounds.IsEmpty ||
            relativeBounds.Left < 0 ||
            relativeBounds.Top < 0 ||
            relativeBounds.Right > windowBounds.Width ||
            relativeBounds.Bottom > windowBounds.Height)
        {
            return null;
        }

        return ConvertRelativeToScreen(windowBounds, relativeBounds);
    }

    private static uint ReadDpi(IntPtr windowHandle)
    {
        try
        {
            var dpi = GetDpiForWindow(windowHandle);
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

    private static WindowChromeMetrics ReadChromeMetrics(uint dpi)
    {
        try
        {
            return new WindowChromeMetrics(
                GetSystemMetricsForDpi(SmCxSize, dpi),
                GetSystemMetricsForDpi(SmCySize, dpi),
                GetSystemMetricsForDpi(SmCxSizeFrame, dpi),
                GetSystemMetricsForDpi(SmCySizeFrame, dpi),
                GetSystemMetricsForDpi(SmCxPaddedBorder, dpi));
        }
        catch (EntryPointNotFoundException)
        {
            return default;
        }
    }

    private static IntRect ToIntRect(NativeRectangle rectangle) => new(
        rectangle.Left,
        rectangle.Top,
        rectangle.Right - rectangle.Left,
        rectangle.Bottom - rectangle.Top);

    private static IntPtr GetWindowLongPtr(IntPtr windowHandle, int index) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(windowHandle, index)
            : (IntPtr)GetWindowLong32(windowHandle, index);

    private static bool IsCodexDesktopProcess(uint processId)
    {
        var processHandle = OpenProcess(ProcessQueryLimitedInformation, inheritHandle: false, processId);
        if (processHandle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var capacity = 2048u;
            var path = new StringBuilder((int)capacity);
            if (!QueryFullProcessImageName(processHandle, 0, path, ref capacity))
            {
                return false;
            }

            var executablePath = path.ToString();
            var executableName = Path.GetFileName(executablePath);
            var isKnownExecutableName = executableName.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase)
                || executableName.Equals("Codex.exe", StringComparison.OrdinalIgnoreCase);
            if (!isKnownExecutableName)
            {
                return false;
            }

            // Microsoft Store/MSIX 版仍使用 ChatGPT.exe，但安装包路径可稳定区分 Codex。
            if (executablePath.Contains("\\WindowsApps\\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 兼容未来或企业分发的独立 Codex.exe。
            if (executableName.Equals("Codex.exe", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }
        finally
        {
            CloseHandle(processHandle);
        }
    }

    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool EnumWindowsCallback(IntPtr windowHandle, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private sealed record ConfirmedCodexTargetIdentity(long HostHandle, uint ProcessId);
}
