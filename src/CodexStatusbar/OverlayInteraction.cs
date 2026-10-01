using System.Drawing;
using System.Runtime.InteropServices;

namespace CodexStatusbar;

[Flags]
internal enum PointerButtons
{
    None = 0,
    Left = 1,
    Right = 2,
    Middle = 4
}

internal sealed class OverlayInteractionState
{
    private PointerButtons _previousButtons;

    public OverlayVisualState State { get; private set; } = OverlayVisualState.Collapsed;
    public bool ShouldPollOutsideClicks => State == OverlayVisualState.Expanded;
    public bool IsWaitingForOpeningClickRelease { get; private set; }

    public bool OnCapsuleMouseUp()
    {
        if (State == OverlayVisualState.HiddenForSpace)
        {
            return false;
        }

        _previousButtons = PointerButtons.None;
        if (State == OverlayVisualState.Expanded)
        {
            State = OverlayVisualState.Collapsed;
            IsWaitingForOpeningClickRelease = false;
            return true;
        }

        State = OverlayVisualState.Expanded;
        IsWaitingForOpeningClickRelease = true;
        return true;
    }

    public bool OnPointerSample(PointerButtons pressedButtons, bool pointerInsideOverlay)
    {
        if (State != OverlayVisualState.Expanded)
        {
            return false;
        }

        if (IsWaitingForOpeningClickRelease)
        {
            _previousButtons = pressedButtons;
            if (pressedButtons == PointerButtons.None)
            {
                IsWaitingForOpeningClickRelease = false;
            }
            return false;
        }

        var pressedEdges = pressedButtons & ~_previousButtons;
        _previousButtons = pressedButtons;
        if (pressedEdges != PointerButtons.None && !pointerInsideOverlay)
        {
            State = OverlayVisualState.Collapsed;
            return true;
        }
        return false;
    }

    public bool CollapseForHostChange() => Collapse();

    public bool CollapseForExpandedLayoutFailure() => Collapse();

    public bool HideForSpace()
    {
        if (State == OverlayVisualState.HiddenForSpace)
        {
            return false;
        }

        State = OverlayVisualState.HiddenForSpace;
        _previousButtons = PointerButtons.None;
        IsWaitingForOpeningClickRelease = false;
        return true;
    }

    public bool RestoreAfterSpace()
    {
        if (State != OverlayVisualState.HiddenForSpace)
        {
            return false;
        }

        State = OverlayVisualState.Collapsed;
        return true;
    }

    private bool Collapse()
    {
        if (State == OverlayVisualState.Collapsed)
        {
            return false;
        }

        State = OverlayVisualState.Collapsed;
        _previousButtons = PointerButtons.None;
        IsWaitingForOpeningClickRelease = false;
        return true;
    }
}

internal sealed class ActiveRouteThreadState
{
    private string? _observedThreadId;

    public bool ObserveAndCollapse(
        ActiveThreadRouteStatus routeStatus,
        OverlayInteractionState interaction)
    {
        ArgumentNullException.ThrowIfNull(routeStatus);
        ArgumentNullException.ThrowIfNull(interaction);
        if (string.IsNullOrWhiteSpace(routeStatus.ThreadId))
        {
            return false;
        }

        if (_observedThreadId is null)
        {
            _observedThreadId = routeStatus.ThreadId;
            return false;
        }

        if (string.Equals(
            _observedThreadId,
            routeStatus.ThreadId,
            StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _observedThreadId = routeStatus.ThreadId;
        return interaction.CollapseForHostChange();
    }
}

internal sealed class OverlayAnchorTargetState
{
    private OverlayAnchorTargetIdentity? _observed;

    public bool ObserveAndCollapse(
        long hostHandle,
        AttachmentReferencePoint referencePoint,
        OverlayInteractionState interaction)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        var current = new OverlayAnchorTargetIdentity(
            hostHandle,
            referencePoint);
        if (_observed is null)
        {
            _observed = current;
            return false;
        }
        if (_observed == current)
        {
            return false;
        }
        _observed = current;
        return interaction.CollapseForHostChange();
    }
}

internal sealed record OverlayAnchorTargetIdentity(
    long HostHandle,
    AttachmentReferencePoint ReferencePoint);

internal static class PointerInput
{
    private const int VkLeftButton = 0x01;
    private const int VkRightButton = 0x02;
    private const int VkMiddleButton = 0x04;

    public static PointerButtons ReadPressedButtons()
    {
        var buttons = PointerButtons.None;
        if (IsPressed(VkLeftButton))
        {
            buttons |= PointerButtons.Left;
        }
        if (IsPressed(VkRightButton))
        {
            buttons |= PointerButtons.Right;
        }
        if (IsPressed(VkMiddleButton))
        {
            buttons |= PointerButtons.Middle;
        }
        return buttons;
    }

    public static bool TryGetCursorPosition(out Point position) => GetCursorPos(out position);

    private static bool IsPressed(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point position);
}

internal sealed class InteractionProbeRequest
{
    public IReadOnlyList<InteractionProbeCaseRequest> Cases { get; init; } = [];
}

internal sealed class InteractionProbeCaseRequest
{
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<InteractionProbeEventRequest> Events { get; init; } = [];
}

internal sealed class InteractionProbeEventRequest
{
    public string Operation { get; init; } = string.Empty;
    public int? PressedButtons { get; init; }
    public bool? PointerInsideOverlay { get; init; }
    public string? RouteThreadId { get; init; }
    public int? RouteActiveWindowCount { get; init; }
    public bool? RouteIsConnected { get; init; }
    public long? RouteVersion { get; init; }
    public string? RouteLastError { get; init; }
    public long? HostHandle { get; init; }
    public int? ReferencePoint { get; init; }
}

internal sealed record InteractionProbeEventResult(
    OverlayVisualState State,
    bool ShouldPollOutsideClicks,
    bool IsWaitingForOpeningClickRelease,
    bool StateChanged);

internal sealed record InteractionProbeCaseResult(
    string Name,
    IReadOnlyList<InteractionProbeEventResult> Events);

internal sealed record InteractionProbeResult(IReadOnlyList<InteractionProbeCaseResult> Cases);

internal static class InteractionProbe
{
    public static InteractionProbeResult Execute(InteractionProbeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cases = request.Cases.Select(ExecuteCase).ToArray();
        return new InteractionProbeResult(cases);
    }

    private static InteractionProbeCaseResult ExecuteCase(InteractionProbeCaseRequest probeCase)
    {
        var interaction = new OverlayInteractionState();
        var activeRouteThread = new ActiveRouteThreadState();
        var anchorTarget = new OverlayAnchorTargetState();
        var events = probeCase.Events.Select(probeEvent =>
        {
            var stateChanged = probeEvent.Operation switch
            {
                "CapsuleMouseUp" => interaction.OnCapsuleMouseUp(),
                "PointerSample" => interaction.OnPointerSample(
                    ReadButtons(probeEvent),
                    probeEvent.PointerInsideOverlay ?? throw new ArgumentException("PointerSample 操作需要 PointerInsideOverlay。", nameof(probeEvent))),
                "CollapseForHostChange" => interaction.CollapseForHostChange(),
                "CollapseForExpandedLayoutFailure" => interaction.CollapseForExpandedLayoutFailure(),
                "HideForSpace" => interaction.HideForSpace(),
                "RestoreAfterSpace" => interaction.RestoreAfterSpace(),
                "DataOnlyUpdate" => false,
                "ObserveActiveRouteThread" => activeRouteThread.ObserveAndCollapse(
                    ReadRouteStatus(probeEvent),
                    interaction),
                "ObserveAnchorTarget" => anchorTarget.ObserveAndCollapse(
                    probeEvent.HostHandle
                        ?? throw new ArgumentException("ObserveAnchorTarget 操作需要 HostHandle。", nameof(probeEvent)),
                    (AttachmentReferencePoint)(probeEvent.ReferencePoint ?? (int)AttachmentReferencePoint.TopLeft),
                    interaction),
                _ => throw new ArgumentException($"不支持的交互探针操作：{probeEvent.Operation}", nameof(probeEvent))
            };
            return new InteractionProbeEventResult(
                interaction.State,
                interaction.ShouldPollOutsideClicks,
                interaction.IsWaitingForOpeningClickRelease,
                stateChanged);
        }).ToArray();
        return new InteractionProbeCaseResult(probeCase.Name, events);
    }

    private static ActiveThreadRouteStatus ReadRouteStatus(InteractionProbeEventRequest probeEvent)
    {
        if (!probeEvent.RouteActiveWindowCount.HasValue
            || !probeEvent.RouteIsConnected.HasValue
            || !probeEvent.RouteVersion.HasValue)
        {
            throw new ArgumentException(
                "ObserveActiveRouteThread 操作需要完整 route status。",
                nameof(probeEvent));
        }

        return new ActiveThreadRouteStatus(
            probeEvent.RouteThreadId,
            probeEvent.RouteActiveWindowCount.Value,
            probeEvent.RouteIsConnected.Value,
            probeEvent.RouteVersion.Value,
            probeEvent.RouteLastError);
    }

    private static PointerButtons ReadButtons(InteractionProbeEventRequest probeEvent)
    {
        if (!probeEvent.PressedButtons.HasValue)
        {
            throw new ArgumentException("PointerSample 操作需要 PressedButtons。", nameof(probeEvent));
        }

        const PointerButtons supported = PointerButtons.Left | PointerButtons.Right | PointerButtons.Middle;
        var buttons = (PointerButtons)probeEvent.PressedButtons.Value;
        if ((buttons & ~supported) != PointerButtons.None)
        {
            throw new ArgumentOutOfRangeException(nameof(probeEvent), "PointerSample 包含不支持的按键标志。");
        }

        return buttons;
    }
}
