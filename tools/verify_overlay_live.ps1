# Live verification of the position model against the real Codex Desktop window.
#
#   pwsh -NoProfile -File tools\verify_overlay_live.ps1
#
# What it does:
#   1. finds the real Codex Desktop top-level window;
#   2. brings it to the foreground (the strip only shows over Codex, by design);
#   3. waits for the strip to appear, then screenshots it composited by DWM;
#   4. shrinks, grows and maximises Codex and re-reads the strip's real rectangle each time;
#   5. checks the strip stays at anchor + offset with no cumulative drift;
#   6. restores the original Codex geometry and the previously focused window.
#
# It reads the numbers the overlay itself reports in its [position] debug section, so this validates
# the running application rather than a reimplementation of it.

[CmdletBinding()]
param(
    [string]$Log = (Join-Path $env:LOCALAPPDATA 'CodexStatusbar\debug.log'),
    [string]$OutDir = (Join-Path $env:TEMP 'codex-statusbar-live'),
    [int]$WaitSeconds = 45,
    [int]$SettleMs = 1600,
    # Optional "x,y,w,h" in DWM extended-frame terms, restored on the way out. Use this to put the
    # Codex window back exactly where it was before an earlier run moved it.
    [string]$RestoreFrame = '',
    [switch]$KeepForeground
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Add-Type -Namespace CSB -Name Win -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
public delegate bool EnumProc(IntPtr h, IntPtr p);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
[DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
[DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT value, int size);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
[DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
[DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr h);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
[DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
'@

# Must happen before any window geometry is read. Without it this process is DPI-virtualised, so
# GetWindowRect and CopyFromScreen work in 96-DPI logical units while the overlay reports physical
# pixels — every comparison would be off by the scale factor (and the screenshot would be misplaced).
$perMonitorV2 = [IntPtr](-4)
try {
    if (-not [CSB.Win]::SetProcessDpiAwarenessContext($perMonitorV2)) {
        Add-Type -Namespace CSB -Name Shcore -MemberDefinition @'
[DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int value);
'@
        [void][CSB.Shcore]::SetProcessDpiAwareness(2)
    }
} catch {
    Write-Warning "could not set per-monitor DPI awareness: $($_.Exception.Message)"
}

function Get-ScaleFactor {
    Add-Type -AssemblyName System.Windows.Forms
    $graphics = [System.Drawing.Graphics]::FromImage((New-Object System.Drawing.Bitmap 1, 1))
    $scale = $graphics.DpiX / 96.0
    $graphics.Dispose()
    return $scale
}

function Get-WindowClass([IntPtr]$Handle) {
    $buffer = New-Object System.Text.StringBuilder 256
    $length = [CSB.Win]::GetClassName($Handle, $buffer, 256)
    if ($length -le 0) { return '' }
    return $buffer.ToString(0, $length)
}

function Get-WindowRect([IntPtr]$Handle) {
    $rect = New-Object CSB.Win+RECT
    if (-not [CSB.Win]::GetWindowRect($Handle, [ref]$rect)) { return $null }
    return [pscustomobject]@{
        X = $rect.Left; Y = $rect.Top
        Width = $rect.Right - $rect.Left; Height = $rect.Bottom - $rect.Top
        Right = $rect.Right; Bottom = $rect.Bottom
    }
}

# The application compares against DWM's extended frame bounds, not GetWindowRect, because a framed
# window's GetWindowRect includes the invisible resize border. Match that so the two agree.
function Get-ExtendedFrameRect([IntPtr]$Handle) {
    $rect = New-Object CSB.Win+RECT
    $hr = [CSB.Win]::DwmGetWindowAttribute($Handle, 9, [ref]$rect, 16)
    if ($hr -ne 0) { return (Get-WindowRect $Handle) }
    return [pscustomobject]@{
        X = $rect.Left; Y = $rect.Top
        Width = $rect.Right - $rect.Left; Height = $rect.Bottom - $rect.Top
        Right = $rect.Right; Bottom = $rect.Bottom
    }
}

function Test-RectEqual($A, $B, [int]$Tolerance = 2) {
    if ($null -eq $A -or $null -eq $B) { return $false }
    return (([Math]::Abs($A.X - $B.X) -le $Tolerance) -and
            ([Math]::Abs($A.Y - $B.Y) -le $Tolerance) -and
            ([Math]::Abs($A.Width - $B.Width) -le $Tolerance) -and
            ([Math]::Abs($A.Height - $B.Height) -le $Tolerance))
}

function Get-TopLevelWindows {
    $list = New-Object System.Collections.ArrayList
    $callback = [CSB.Win+EnumProc] {
        param([IntPtr]$h, [IntPtr]$p)
        if ([CSB.Win]::IsWindowVisible($h)) {
            $ownerPid = 0
            [void][CSB.Win]::GetWindowThreadProcessId($h, [ref]$ownerPid)
            $null = $list.Add([pscustomobject]@{
                Handle = $h; Pid = $ownerPid; Class = (Get-WindowClass $h)
                Rect = (Get-WindowRect $h); Frame = (Get-ExtendedFrameRect $h)
            })
        }
        return $true
    }
    [void][CSB.Win]::EnumWindows($callback, [IntPtr]::Zero)
    return $list
}

function Get-CodexWindow([object]$ExpectedFrame = $null) {
    $chatGpt = @(Get-Process ChatGPT -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
    $candidates = @()
    foreach ($window in Get-TopLevelWindows) {
        if ($window.Class -ne 'Chrome_WidgetWin_1') { continue }
        if ($chatGpt -notcontains $window.Pid) { continue }
        if ($null -eq $window.Frame -or $window.Frame.Width -lt 600 -or $window.Frame.Height -lt 400) { continue }
        $candidates += $window
    }

    # Prefer the window whose extended frame bounds are exactly what the overlay says it is following:
    # that guarantees this script is driving the same window the application tracks.
    if ($null -ne $ExpectedFrame) {
        foreach ($window in $candidates) {
            if (Test-RectEqual $window.Frame $ExpectedFrame) { return $window }
        }
    }

    $best = $null
    foreach ($window in $candidates) {
        if ($null -eq $best -or
            ($window.Frame.Width * $window.Frame.Height) -gt ($best.Frame.Width * $best.Frame.Height)) {
            $best = $window
        }
    }
    return $best
}

function Get-StripWindow {
    $overlayPids = @(Get-Process CodexStatusbar -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
    foreach ($window in Get-TopLevelWindows) {
        if ($overlayPids -notcontains $window.Pid) { continue }
        if ($window.Class -notlike 'WindowsForms10.Window*') { continue }
        $exStyle = [CSB.Win]::GetWindowLong($window.Handle, -20)
        if (($exStyle -band 0x00080000) -ne 0) { return $window }
    }
    return $null
}

function Set-ForegroundSafely([IntPtr]$Handle) {
    # Only un-minimise. Calling SW_RESTORE unconditionally would un-maximise a maximised window,
    # which would move the user's Codex window for no reason.
    if ([CSB.Win]::IsIconic($Handle)) { [void][CSB.Win]::ShowWindow($Handle, 9) }
    $foreground = [CSB.Win]::GetForegroundWindow()
    $targetThread = 0
    [void][CSB.Win]::GetWindowThreadProcessId($Handle, [ref]$targetThread)
    $foregroundThread = 0
    [void][CSB.Win]::GetWindowThreadProcessId($foreground, [ref]$foregroundThread)
    $self = [CSB.Win]::GetCurrentThreadId()
    $attached = $false
    try {
        # Attaching to the current foreground thread is what makes SetForegroundWindow permissible
        # from a process that does not own the foreground.
        [void][CSB.Win]::AttachThreadInput($self, $foregroundThread, $true)
        [void][CSB.Win]::AttachThreadInput($self, $targetThread, $true)
        $attached = $true
        [void][CSB.Win]::SetForegroundWindow($Handle)
        [void][CSB.Win]::SetFocus($Handle)
    } finally {
        if ($attached) {
            [void][CSB.Win]::AttachThreadInput($self, $targetThread, $false)
            [void][CSB.Win]::AttachThreadInput($self, $foregroundThread, $false)
        }
    }
}

function Read-PositionBlocks {
    if (-not (Test-Path $Log)) { return @() }
    $text = Get-Content $Log -Raw -Encoding UTF8
    $blocks = @()
    foreach ($match in [regex]::Matches($text, '(?ms)^\[position\]\r?\n(.*?)(?=^Current thread:|^\[position\]|\r?\n\r?\n|\Z)')) {
        $fields = @{}
        foreach ($line in ($match.Groups[1].Value -split "`r?`n")) {
            if ($line -notmatch ':') { continue }
            $parts = $line.Split(':', 2)
            $fields[$parts[0].Trim()] = $parts[1].Trim()
        }
        if ($fields.Count -gt 0) { $blocks += , $fields }
    }
    # The comma matters: returning a one-element array without it unwraps to the element itself.
    return , $blocks
}

function Parse-Point([string]$Text) {
    $m = [regex]::Match($Text, '\((-?\d+),\s*(-?\d+)\)')
    if (-not $m.Success) { return $null }
    return [pscustomobject]@{ X = [int]$m.Groups[1].Value; Y = [int]$m.Groups[2].Value }
}

function Parse-Rect([string]$Text) {
    $m = [regex]::Match($Text, '\((-?\d+),\s*(-?\d+)\)\s+(\d+)x(\d+)')
    if (-not $m.Success) { return $null }
    return [pscustomobject]@{
        X = [int]$m.Groups[1].Value; Y = [int]$m.Groups[2].Value
        Width = [int]$m.Groups[3].Value; Height = [int]$m.Groups[4].Value
        Right = [int]$m.Groups[1].Value + [int]$m.Groups[3].Value
        Bottom = [int]$m.Groups[2].Value + [int]$m.Groups[4].Value
    }
}

function Capture([string]$Path, $Rect, [int]$Pad = 24) {
    Add-Type -AssemblyName System.Drawing
    $x = [Math]::Max(0, $Rect.X - $Pad)
    $y = [Math]::Max(0, $Rect.Y - $Pad)
    $w = $Rect.Width + ($Pad * 2)
    $h = $Rect.Height + ($Pad * 2)
    $bitmap = New-Object System.Drawing.Bitmap $w, $h
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size $w, $h))
    $graphics.Dispose()
    $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

# ---------------------------------------------------------------- run

$results = New-Object System.Collections.ArrayList
function Assert($Name, $Passed, $Detail) {
    $null = $results.Add([pscustomobject]@{ Name = $Name; Passed = [bool]$Passed; Detail = $Detail })
}

Write-Host '== locating Codex Desktop ==' -ForegroundColor Cyan
# Seed from whatever the running overlay last reported, so we drive exactly the window it follows.
$seedBlocks = Read-PositionBlocks
$seedFrame = if ($seedBlocks.Count -gt 0) { Parse-Rect $seedBlocks[-1]['Codex rect'] } else { $null }
if ($null -ne $seedFrame) {
    Write-Host ("  overlay last reported following ({0},{1}) {2}x{3}" -f `
        $seedFrame.X, $seedFrame.Y, $seedFrame.Width, $seedFrame.Height)
}

$codex = Get-CodexWindow $seedFrame
if ($null -eq $codex) {
    Write-Host 'Codex Desktop window not found. Open Codex Desktop and re-run.' -ForegroundColor Red
    exit 2
}
$originalRect = $codex.Rect
$wasZoomed = [CSB.Win]::IsZoomed($codex.Handle)
Write-Host ("  Codex hwnd=0x{0:X} pid={1} frame=({2},{3}) {4}x{5} zoomed={6}" -f `
    $codex.Handle.ToInt64(), $codex.Pid, $codex.Frame.X, $codex.Frame.Y, `
    $codex.Frame.Width, $codex.Frame.Height, $wasZoomed)

$previousForeground = [CSB.Win]::GetForegroundWindow()
$logLength = if (Test-Path $Log) { (Get-Item $Log).Length } else { 0 }

Write-Host '== bringing Codex to the foreground ==' -ForegroundColor Cyan
Set-ForegroundSafely $codex.Handle

$strip = $null
$deadline = (Get-Date).AddSeconds($WaitSeconds)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 400
    $strip = Get-StripWindow
    if ($null -ne $strip) { break }
}
if ($null -eq $strip) {
    Write-Host 'The strip did not become visible within the wait window.' -ForegroundColor Red
    Write-Host 'Is the monitor running?  start-monitor.bat' -ForegroundColor Yellow
    if (-not $KeepForeground) { [void][CSB.Win]::SetForegroundWindow($previousForeground) }
    exit 3
}
Start-Sleep -Milliseconds $SettleMs

$exStyle = [CSB.Win]::GetWindowLong($strip.Handle, -20)
Assert 'strip window is layered (per-pixel alpha)' (($exStyle -band 0x00080000) -ne 0) ('ex=0x{0:X8}' -f $exStyle)
Assert 'strip is a tool window (not in Alt+Tab)' (($exStyle -band 0x00000080) -ne 0) ('ex=0x{0:X8}' -f $exStyle)
Assert 'strip never activates (WS_EX_NOACTIVATE)' (($exStyle -band 0x08000000) -ne 0) ('ex=0x{0:X8}' -f $exStyle)

function Read-Latest {
    $blocks = Read-PositionBlocks
    if ($blocks.Count -eq 0) { return $null }
    return $blocks[-1]
}

$before = Read-Latest
if ($null -eq $before) {
    Write-Host 'No [position] block in the log — start the monitor with --debug.' -ForegroundColor Red
    if (-not $KeepForeground) { [void][CSB.Win]::SetForegroundWindow($previousForeground) }
    exit 4
}

$beforeStrip = Parse-Rect $before['Actual strip rect']
$beforeRequested = Parse-Point $before['Requested strip rect']
$beforeCodex = Parse-Rect $before['Codex rect']
$anchor = $before['Anchor']
$offset = $before['Offset']
$dpi = [int]$before['Monitor DPI']

Assert 'the overlay is following the window this script drives' `
    (Test-RectEqual $beforeCodex $codex.Frame 4) `
    "overlay says ($($beforeCodex.X),$($beforeCodex.Y)) $($beforeCodex.Width)x$($beforeCodex.Height); " +
    "driven window is ($($codex.Frame.X),$($codex.Frame.Y)) $($codex.Frame.Width)x$($codex.Frame.Height)"

Write-Host ''
Write-Host '== TEST 3  before ==' -ForegroundColor Cyan
Write-Host "  Codex rect          : ($($beforeCodex.X), $($beforeCodex.Y)) $($beforeCodex.Width)x$($beforeCodex.Height)"
Write-Host "  Anchor / Offset     : $anchor  $offset   (monitor DPI $dpi)"
Write-Host "  Requested top-left  : ($($beforeRequested.X), $($beforeRequested.Y))"
Write-Host "  Actual strip rect   : ($($beforeStrip.X), $($beforeStrip.Y)) $($beforeStrip.Width)x$($beforeStrip.Height)"
Assert 'TEST 3  strip sits exactly where anchor + offset says' `
    (($beforeStrip.X -eq $beforeRequested.X) -and ($beforeStrip.Y -eq $beforeRequested.Y)) `
    "requested ($($beforeRequested.X),$($beforeRequested.Y)) actual ($($beforeStrip.X),$($beforeStrip.Y))"
Assert 'TEST 3  live window rect matches the strip rectangle the app reports' `
    (($strip.Rect.X -eq $beforeStrip.X) -and ($strip.Rect.Y -eq $beforeStrip.Y) -and `
     ($strip.Rect.Width -eq $beforeStrip.Width) -and ($strip.Rect.Height -eq $beforeStrip.Height)) `
    "hwnd ($($strip.Rect.X),$($strip.Rect.Y)) $($strip.Rect.Width)x$($strip.Rect.Height)"

Write-Host ''
Write-Host '== TEST 1  on-screen capture (DWM composited over Codex) ==' -ForegroundColor Cyan
$shot = Join-Path $OutDir 'onscreen-over-codex.png'
Capture $shot $beforeStrip
Write-Host "  $shot"
Assert 'TEST 1  screenshot captured' (Test-Path $shot) $shot

function Set-CodexState([int]$Command, [string]$Label) {
    [void][CSB.Win]::ShowWindow($codex.Handle, $Command)
    Start-Sleep -Milliseconds $SettleMs
    return (Report-Position $Label)
}

function Set-CodexSize([int]$Width, [int]$Height, [string]$Label) {
    [void][CSB.Win]::MoveWindow($codex.Handle, $originalRect.X, $originalRect.Y, $Width, $Height, $true)
    Start-Sleep -Milliseconds $SettleMs
    return (Report-Position $Label)
}

function Report-Position([string]$Label) {
    $block = Read-Latest
    if ($null -eq $block) { return $null }
    $stripRect = Parse-Rect $block['Actual strip rect']
    $requested = Parse-Point $block['Requested strip rect']
    $codexRect = Parse-Rect $block['Codex rect']
    $m = [regex]::Match($block['Offset'], '\((-?[\d.]+),\s*(-?[\d.]+)\)')
    $offsetX = if ($m.Success) { [double]$m.Groups[1].Value } else { $null }
    $offsetY = if ($m.Success) { [double]$m.Groups[2].Value } else { $null }
    $rightMarginDip = if ($null -ne $offsetX -and $offsetX -lt 0) {
        [Math]::Round(($codexRect.Right - $stripRect.Right) * 96.0 / $dpi, 2)
    } else { $null }

    Write-Host ''
    Write-Host "== $Label ==" -ForegroundColor Cyan
    Write-Host "  Codex rect          : ($($codexRect.X), $($codexRect.Y)) $($codexRect.Width)x$($codexRect.Height)"
    Write-Host "  Anchor / Offset     : $($block['Anchor'])  $($block['Offset'])"
    Write-Host "  Requested top-left  : ($($requested.X), $($requested.Y))"
    Write-Host "  Actual strip rect   : ($($stripRect.X), $($stripRect.Y)) $($stripRect.Width)x$($stripRect.Height)"
    if ($null -ne $rightMarginDip) { Write-Host "  Right margin        : $rightMarginDip DIP" }
    Write-Host "  Clamped             : $($block['Clamped'])"

    Assert "$Label  lands within 2 px of anchor + offset" `
        (([Math]::Abs($stripRect.X - $requested.X) -le 2) -and ([Math]::Abs($stripRect.Y - $requested.Y) -le 2)) `
        "expected ($($requested.X),$($requested.Y)) actual ($($stripRect.X),$($stripRect.Y))"
    Assert "$Label  anchor and offset are unchanged" `
        (($block['Anchor'] -eq $anchor) -and ($block['Offset'] -eq $offset)) `
        "$($block['Anchor']) $($block['Offset'])"
    $liveStrip = Get-WindowRect $strip.Handle
    Assert "$Label  the real window matches the reported rectangle" `
        (($null -ne $liveStrip) -and ($liveStrip.X -eq $stripRect.X) -and ($liveStrip.Y -eq $stripRect.Y) -and `
         ($liveStrip.Width -eq $stripRect.Width) -and ($liveStrip.Height -eq $stripRect.Height)) `
        "hwnd ($($liveStrip.X),$($liveStrip.Y)) $($liveStrip.Width)x$($liveStrip.Height) vs reported ($($stripRect.X),$($stripRect.Y)) $($stripRect.Width)x$($stripRect.Height)"
    return [pscustomobject]@{ Strip = $stripRect; Codex = $codexRect; RightMarginDip = $rightMarginDip }
}

Write-Host ''
Write-Host '== TEST 3  shrink Codex ==' -ForegroundColor Yellow
$small = Set-CodexSize ([int]($originalRect.Width * 0.62)) ([int]($originalRect.Height * 0.62)) 'TEST 3  after shrink'

Write-Host ''
Write-Host '== TEST 4  grow Codex back ==' -ForegroundColor Yellow
$grown = Set-CodexSize $originalRect.Width $originalRect.Height 'TEST 4  after growing back'
Assert 'TEST 4  growing back restores the original position exactly (no drift)' `
    (($grown.Strip.X -eq $beforeStrip.X) -and ($grown.Strip.Y -eq $beforeStrip.Y)) `
    "before ($($beforeStrip.X),$($beforeStrip.Y)) after ($($grown.Strip.X),$($grown.Strip.Y))"

Write-Host ''
Write-Host '== TEST 5  maximize then restore ==' -ForegroundColor Yellow
$maximised = Set-CodexState 3 'TEST 5  maximized'          # SW_MAXIMIZE
$restoredAfterMax = Set-CodexState 9 'TEST 5  restored'    # SW_RESTORE
Assert 'TEST 5  restore after maximize returns to the original position' `
    (($restoredAfterMax.Strip.X -eq $beforeStrip.X) -and ($restoredAfterMax.Strip.Y -eq $beforeStrip.Y)) `
    "before ($($beforeStrip.X),$($beforeStrip.Y)) after ($($restoredAfterMax.Strip.X),$($restoredAfterMax.Strip.Y))"

Write-Host ''
Write-Host '== TEST 7  move Codex ==' -ForegroundColor Yellow
$moveX = [Math]::Max(0, $originalRect.X + 160)
$moveY = [Math]::Max(0, $originalRect.Y + 90)
[void][CSB.Win]::MoveWindow($codex.Handle, $moveX, $moveY, $originalRect.Width, $originalRect.Height, $true)
Start-Sleep -Milliseconds $SettleMs
$movedBlock = Read-Latest
$movedStrip = Parse-Rect $movedBlock['Actual strip rect']
$movedCodex = Parse-Rect $movedBlock['Codex rect']
Write-Host "  Codex rect          : ($($movedCodex.X), $($movedCodex.Y)) $($movedCodex.Width)x$($movedCodex.Height)"
Write-Host "  Actual strip rect   : ($($movedStrip.X), $($movedStrip.Y)) $($movedStrip.Width)x$($movedStrip.Height)"
$dx = $movedStrip.X - $beforeStrip.X
$dy = $movedStrip.Y - $beforeStrip.Y
Write-Host "  Strip delta         : ($dx, $dy)"
Assert 'TEST 7  strip follows the move by exactly the same delta' `
    (($dx -eq ($movedCodex.X - $beforeCodex.X)) -and ($dy -eq ($movedCodex.Y - $beforeCodex.Y))) `
    "strip ($dx,$dy) codex ($($movedCodex.X - $beforeCodex.X),$($movedCodex.Y - $beforeCodex.Y))"
$movedShot = Join-Path $OutDir 'onscreen-over-codex-moved.png'
Capture $movedShot $movedStrip

# restore everything
if ($wasZoomed) {
    [void][CSB.Win]::ShowWindow($codex.Handle, 3)   # SW_MAXIMIZE
} else {
    [void][CSB.Win]::MoveWindow($codex.Handle, $originalRect.X, $originalRect.Y, $originalRect.Width, $originalRect.Height, $true)
}

# An explicit target frame overrides the above: DWM's extended frame excludes the invisible resize
# border, so convert back to a window rectangle using the border widths measured on this very window.
if ($RestoreFrame -ne '') {
    $parts = $RestoreFrame.Split(',')
    if ($parts.Count -eq 4) {
        $fx = [int]$parts[0]; $fy = [int]$parts[1]
        $fw = [int]$parts[2]; $fh = [int]$parts[3]
        $live = Get-WindowRect $codex.Handle
        $frame = Get-ExtendedFrameRect $codex.Handle
        if ($null -ne $live -and $null -ne $frame) {
            $left = $live.X - $frame.X
            $top = $live.Y - $frame.Y
            $right = $live.Right - $frame.Right
            $bottom = $live.Bottom - $frame.Bottom
            [void][CSB.Win]::MoveWindow(
                $codex.Handle,
                $fx - $left,
                $fy - $top,
                $fw + $left + $right,
                $fh + $top + $bottom,
                $true)
            Write-Host ("  restored Codex to frame ({0},{1}) {2}x{3}" -f $fx, $fy, $fw, $fh) -ForegroundColor DarkGray
        }
    }
}

Start-Sleep -Milliseconds 600
if (-not $KeepForeground) { [void][CSB.Win]::SetForegroundWindow($previousForeground) }

Write-Host ''
Write-Host '== summary ==' -ForegroundColor Cyan
$failed = 0
foreach ($item in $results) {
    $mark = if ($item.Passed) { 'ok  ' } else { 'FAIL'; $failed++ }
    Write-Host ("  [{0}] {1} — {2}" -f $mark, $item.Name, $item.Detail)
}
Write-Host ("  {0} passed, {1} failed" -f ($results.Count - $failed), $failed)
Write-Host "  screenshots: $OutDir"
if ($failed -gt 0) { exit 1 }
exit 0