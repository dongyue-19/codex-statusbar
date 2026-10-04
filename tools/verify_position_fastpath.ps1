# Live verification of the position fast path against the real Codex Desktop window.
#
#   pwsh -NoProfile -File tools\verify_position_fastpath.ps1
#
# What it does, and deliberately does not do:
#   * starts the overlay with --debug --position-fast-debug and waits for it to attach;
#   * moves the real Codex window a short distance, a few times, and back — nothing else;
#   * reads the overlay's own [position-performance] counters for those moves and asserts:
#       - the WinEvent subscription is real (WinEvents > 0),
#       - a pure move is answered by position-only writes,
#       - the moving window costs no UI Automation traversal, and the whole move at most one resync,
#       - the strip ends up where it was told to go (0 px error);
#   * checks the strip's real rectangle from outside the process, so the claim does not rest on the
#     application's own opinion of itself;
#   * restarts with --background (no diagnostics) and measures idle and move CPU, because that is the
#     configuration the user actually runs;
#   * restores the Codex window geometry in a finally block, always.
#
# It takes no screenshots and makes no visual judgement: whether the tracking *looks* smooth is the
# human's call. What is checked here is the machine-checkable half — that the events arrive, that the
# write path is the cheap one, that nothing is queued, and what it costs.

[CmdletBinding()]
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\release\CodexStatusbar.exe'),
    [string]$Log = (Join-Path $env:LOCALAPPDATA 'CodexStatusbar\debug.log'),
    [int]$Cycles = 3,
    [int]$MovePx = 90,
    [int]$BurstMs = 420,
    [int]$IdleSeconds = 5,
    [int]$CpuIdleSeconds = 12,
    [int]$CpuMoveSeconds = 5,
    # Leave the overlay running afterwards (in its production configuration).
    [switch]$LeaveRunning
)

$ErrorActionPreference = 'Stop'
$results = New-Object System.Collections.ArrayList

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
[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags);
[DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
'@

$perMonitorV2 = [IntPtr](-4)
try { [void][CSB.Win]::SetProcessDpiAwarenessContext($perMonitorV2) } catch { }

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

function Get-TopLevelWindows {
    $list = New-Object System.Collections.ArrayList
    $callback = [CSB.Win+EnumProc] {
        param([IntPtr]$h, [IntPtr]$p)
        if ([CSB.Win]::IsWindowVisible($h)) {
            $ownerPid = 0
            [void][CSB.Win]::GetWindowThreadProcessId($h, [ref]$ownerPid)
            $null = $list.Add([pscustomobject]@{
                Handle = $h; Pid = $ownerPid; Class = (Get-WindowClass $h); Rect = (Get-WindowRect $h)
            })
        }
        return $true
    }
    [void][CSB.Win]::EnumWindows($callback, [IntPtr]::Zero)
    return $list
}

function Get-CodexWindow {
    $chatGpt = @(Get-Process ChatGPT -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
    $best = $null
    foreach ($window in Get-TopLevelWindows) {
        if ($window.Class -ne 'Chrome_WidgetWin_1') { continue }
        if ($chatGpt -notcontains $window.Pid) { continue }
        if ($null -eq $window.Rect -or $window.Rect.Width -lt 600 -or $window.Rect.Height -lt 400) { continue }
        if ($null -eq $best -or ($window.Rect.Width * $window.Rect.Height) -gt ($best.Rect.Width * $best.Rect.Height)) {
            $best = $window
        }
    }
    return $best
}

function Get-OverlayProcess {
    return Get-Process CodexStatusbar -ErrorAction SilentlyContinue | Select-Object -First 1
}

function Get-StripWindow {
    $overlay = Get-OverlayProcess
    if ($null -eq $overlay) { return $null }
    foreach ($window in Get-TopLevelWindows) {
        if ($window.Pid -ne $overlay.Id) { continue }
        if ($window.Class -notlike 'WindowsForms10.Window*') { continue }
        if (([CSB.Win]::GetWindowLong($window.Handle, -20) -band 0x00080000) -ne 0) { return $window }
    }
    return $null
}

# The strip only shows over a *foreground* Codex, so this has to actually succeed rather than merely be
# attempted. Three escalating mechanisms, because Windows refuses a bare SetForegroundWindow from a
# process that does not already own the foreground:
#   1. attach to both the current foreground thread and the target thread, then SetForegroundWindow;
#   2. the same with BringWindowToTop and a SWP_SHOWWINDOW Z-order raise;
#   3. an ALT tap, which makes the shell treat this thread as the last input owner.
function Set-ForegroundSafely([IntPtr]$Handle) {
    if ([CSB.Win]::IsIconic($Handle)) { [void][CSB.Win]::ShowWindow($Handle, 9) }
    if ([CSB.Win]::GetForegroundWindow() -eq $Handle) { return $true }

    $self = [CSB.Win]::GetCurrentThreadId()
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        $foreground = [CSB.Win]::GetForegroundWindow()
        $targetThread = 0
        [void][CSB.Win]::GetWindowThreadProcessId($Handle, [ref]$targetThread)
        $foregroundThread = 0
        [void][CSB.Win]::GetWindowThreadProcessId($foreground, [ref]$foregroundThread)
        try {
            [void][CSB.Win]::AttachThreadInput($self, $foregroundThread, $true)
            [void][CSB.Win]::AttachThreadInput($self, $targetThread, $true)
            if ($attempt -ge 2) {
                [void][CSB.Win]::BringWindowToTop($Handle)
                # HWND_TOP, SWP_NOSIZE | SWP_NOMOVE | SWP_SHOWWINDOW
                [void][CSB.Win]::SetWindowPos($Handle, [IntPtr]::Zero, 0, 0, 0, 0, 0x0043)
            }
            [void][CSB.Win]::SetForegroundWindow($Handle)
            [void][CSB.Win]::SetFocus($Handle)
        } finally {
            [void][CSB.Win]::AttachThreadInput($self, $targetThread, $false)
            [void][CSB.Win]::AttachThreadInput($self, $foregroundThread, $false)
        }

        Start-Sleep -Milliseconds 120
        if ([CSB.Win]::GetForegroundWindow() -eq $Handle) { return $true }

        if ($attempt -ge 2) {
            [CSB.Win]::keybd_event(0x12, 0, 0, [IntPtr]::Zero)   # VK_MENU down
            [CSB.Win]::keybd_event(0x12, 0, 2, [IntPtr]::Zero)   # VK_MENU up
            Start-Sleep -Milliseconds 80
            [void][CSB.Win]::SetForegroundWindow($Handle)
            Start-Sleep -Milliseconds 120
            if ([CSB.Win]::GetForegroundWindow() -eq $Handle) { return $true }
        }
    }

    return $false
}

function Assert-Step($Name, $Passed, $Detail) {
    $null = $results.Add([pscustomobject]@{ Name = $Name; Passed = [bool]$Passed; Detail = $Detail })
    $color = if ($Passed) { 'Green' } else { 'Red' }
    $mark = if ($Passed) { 'ok  ' } else { 'FAIL' }
    Write-Host ("  [{0}] {1} — {2}" -f $mark, $Name, $Detail) -ForegroundColor $color
}

# Every [position-performance] block written after $Offset, as hashtables of field -> value.
function Read-PerformanceBlocks([long]$Offset) {
    if (-not (Test-Path $Log)) { return @() }
    $stream = [System.IO.File]::Open($Log, 'Open', 'Read', 'ReadWrite')
    try {
        if ($Offset -gt $stream.Length) { $Offset = 0 }
        $stream.Seek($Offset, 'Begin') | Out-Null
        $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8)
        $text = $reader.ReadToEnd()
    } finally {
        $stream.Dispose()
    }

    $blocks = @()
    # The block is terminated by a blank line, not by the next header: the first body line ("Mode:   ...")
    # starts with a non-space character, so a `^\S` lookahead would match zero characters and read an
    # empty block.
    foreach ($match in [regex]::Matches($text, '(?ms)^\[position-performance\]\r?\n(.*?)(?=\r?\n\r?\n|\Z)')) {
        $fields = @{}
        foreach ($line in ($match.Groups[1].Value -split "`r?`n")) {
            if ($line -notmatch ':') { continue }
            $parts = $line.Split(':', 2)
            $fields[$parts[0].Trim()] = $parts[1].Trim()
        }
        if ($fields.Count -gt 0) { $blocks += , $fields }
    }
    return , $blocks
}

function Sum-Field($Blocks, [string]$Field) {
    $total = 0
    foreach ($block in $Blocks) {
        if ($block.ContainsKey($Field)) {
            $value = 0
            if ([int]::TryParse($block[$Field], [ref]$value)) { $total += $value }
        }
    }
    return $total
}

function Get-LogLength {
    if (-not (Test-Path $Log)) { return 0 }
    return (Get-Item $Log).Length
}

function Stop-Overlay {
    $running = Get-OverlayProcess
    if ($null -ne $running) {
        $running | Stop-Process -Force
        Start-Sleep -Milliseconds 700
    }
}

function Get-ProcessCpu([System.Diagnostics.Process]$Process, [double]$Seconds) {
    $Process.Refresh()
    $before = $Process.TotalProcessorTime.TotalMilliseconds
    Start-Sleep -Milliseconds ([int]($Seconds * 1000))
    $Process.Refresh()
    $after = $Process.TotalProcessorTime.TotalMilliseconds
    return [Math]::Round((($after - $before) / ($Seconds * 1000.0)) * 100, 3)
}

# A real drag is a stream of small moves, not one jump. Driving it that way is what makes the window
# emit the LOCATIONCHANGE stream the fast path exists to absorb.
function Move-CodexBy([IntPtr]$Handle, [int]$Dx, [int]$Dy, [int]$Steps = 8) {
    $from = Get-WindowRect $Handle
    for ($step = 1; $step -le $Steps; $step++) {
        $x = $from.X + [int]([Math]::Round($Dx * $step / $Steps))
        $y = $from.Y + [int]([Math]::Round($Dy * $step / $Steps))
        [void][CSB.Win]::MoveWindow($Handle, $x, $y, $from.Width, $from.Height, $true)
        Start-Sleep -Milliseconds 35
    }
}

function Wait-ForAttach([IntPtr]$Handle, [int]$TimeoutSeconds = 45) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        Set-ForegroundSafely $Handle
        Start-Sleep -Milliseconds 500
        $raw = if (Test-Path $Log) { Get-Content $Log -Raw -Encoding UTF8 } else { '' }
        $idx = $raw.LastIndexOf('[lifecycle]')
        if ($idx -ge 0) {
            $section = $raw.Substring($idx)
            if ($section -match 'Watcher state:\s*ACTIVE' -and $section -match 'Codex detected:\s*true') {
                return $true
            }
        }
    }
    return $false
}

# The strip only shows over a *foreground* Codex, and starting the overlay process itself can take the
# foreground away, so the focus has to be re-asserted while waiting rather than once before the wait.
function Wait-ForStrip([IntPtr]$Handle, [int]$TimeoutSeconds = 20) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        Set-ForegroundSafely $Handle
        $strip = Get-StripWindow
        if ($null -ne $strip) { return $strip }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

function Describe-Foreground {
    $handle = [CSB.Win]::GetForegroundWindow()
    $ownerPid = 0
    [void][CSB.Win]::GetWindowThreadProcessId($handle, [ref]$ownerPid)
    $name = 'unknown'
    try { $name = (Get-Process -Id $ownerPid -ErrorAction Stop).ProcessName } catch { }
    return ("foreground is {0} (pid {1}, class '{2}')" -f $name, $ownerPid, (Get-WindowClass $handle))
}

if (-not (Test-Path $Exe)) { throw "overlay exe not found: $Exe" }
$Exe = (Resolve-Path $Exe).Path
Write-Host 'position fast path verification' -ForegroundColor Cyan
Write-Host "  exe : $Exe"
Write-Host "  log : $Log"

$codex = Get-CodexWindow
if ($null -eq $codex) { throw 'no Codex Desktop window found (is Codex running?)' }
$wasZoomed = [CSB.Win]::IsZoomed($codex.Handle)

# A maximised window cannot be moved at all: MoveWindow succeeds and changes nothing, which would make
# every assertion below vacuously true. Restore it first, test the restored geometry, and put the
# maximised state back on the way out.
if ($wasZoomed) {
    [void][CSB.Win]::ShowWindow($codex.Handle, 9)   # SW_RESTORE
    Start-Sleep -Milliseconds 800
    $codex = Get-CodexWindow
    if ($null -eq $codex) { throw 'the Codex window disappeared while restoring it' }
}

$original = $codex.Rect
Write-Host ("  Codex: HWND {0:X} pid {1} at ({2},{3}) {4}x{5} (was maximized: {6})" -f `
    $codex.Handle.ToInt64(), $codex.Pid, $original.X, $original.Y, $original.Width, $original.Height, $wasZoomed)

# Where the move and resize tests run. It has to be a position with room on every side, because when the
# window is pushed partly off the screen Codex's own composer stops translating rigidly with the window
# rectangle and the layout's clamp takes over — correct behaviour, but not what a "the strip followed by
# exactly the same delta" assertion is about. The off-screen case is asserted separately and
# deterministically in --self-test.
$testHome = [pscustomobject]@{
    X = [Math]::Max(20, [Math]::Min($original.X, 100))
    Y = [Math]::Max(20, [Math]::Min($original.Y, 60))
    Width = $original.Width
    Height = $original.Height
}
[void][CSB.Win]::MoveWindow($codex.Handle, $testHome.X, $testHome.Y, $testHome.Width, $testHome.Height, $true)
Start-Sleep -Milliseconds 500
Write-Host ("  test position: ({0},{1}) {2}x{3}" -f $testHome.X, $testHome.Y, $testHome.Width, $testHome.Height)
Write-Host ''

$previousForeground = [CSB.Win]::GetForegroundWindow()
$failed = 0
$cpuInstance = $null

try {
    # ============================================================== phase 1: the decisions, from the log
    Write-Host '== setup: start the overlay with position diagnostics ==' -ForegroundColor Yellow
    Stop-Overlay
    $started = Start-Process -FilePath $Exe `
        -ArgumentList '--debug', '--position-fast-debug', '--background' -PassThru
    Write-Host "  started pid $($started.Id) with --debug --position-fast-debug --background"

    $attached = Wait-ForAttach $codex.Handle
    Assert-Step 'setup: the overlay attached to Codex' $attached 'Watcher state: ACTIVE + Codex detected: true'
    $strip = Wait-ForStrip $codex.Handle
    Assert-Step 'setup: the strip is on screen over Codex' ($null -ne $strip) `
        ('a visible layered window exists; ' + (Describe-Foreground))

    # The hook must be armed for the window this script is about to move.
    $raw = Get-Content $Log -Raw -Encoding UTF8
    $armed = $raw -match ('position: following HWND {0:X}' -f $codex.Handle.ToInt64())
    Assert-Step 'setup: the WinEvent subscription is armed for that window' $armed `
        ('log line: position: following HWND {0:X}' -f $codex.Handle.ToInt64())

    # ---------------------------------------------------------------- idle cost, from the counters
    Write-Host ''
    Write-Host "== TEST E  idle for $IdleSeconds s ==" -ForegroundColor Yellow
    Start-Sleep -Milliseconds 1500   # let the pipeline settle back to idle
    $offset = Get-LogLength
    $process = Get-OverlayProcess
    $cpuBefore = $process.TotalProcessorTime.TotalMilliseconds
    Start-Sleep -Seconds $IdleSeconds
    $process.Refresh()
    $cpuAfter = $process.TotalProcessorTime.TotalMilliseconds
    $idleBlocks = Read-PerformanceBlocks $offset
    $idleCpu = [Math]::Round((($cpuAfter - $cpuBefore) / ($IdleSeconds * 1000.0)) * 100, 3)
    $idleModes = (@($idleBlocks | ForEach-Object { $_['Mode'] }) | Select-Object -Unique) -join ','
    Assert-Step 'E: idle makes no window call at all' `
        ((Sum-Field $idleBlocks 'Actual SetWindowPos calls') -eq 0) `
        ("SetWindowPos = {0}, modes seen: {1}" -f (Sum-Field $idleBlocks 'Actual SetWindowPos calls'), $idleModes)
    Assert-Step 'E: idle makes no relayout' `
        ((Sum-Field $idleBlocks 'Relayout writes') -eq 0) `
        ("relayout writes = {0}" -f (Sum-Field $idleBlocks 'Relayout writes'))
    Write-Host ("  idle CPU: {0}% of one core (with --debug logging on; {1} UIA traversals in that window)" -f `
        $idleCpu, (Sum-Field $idleBlocks 'UIA full traversals')) -ForegroundColor DarkGray

    # ---------------------------------------------------------------- the move burst
    Write-Host ''
    Write-Host "== TEST A/B  pure moves of the real Codex window ($Cycles cycles) ==" -ForegroundColor Yellow
    Assert-Step 'setup: the window can actually be moved (not maximized)' `
        (-not [CSB.Win]::IsZoomed($codex.Handle)) 'IsZoomed = false'

    $burstCpu = 0.0

    # One unasserted warm-up cycle. Right after the strip appears, Chromium is still materialising its
    # accessibility tree and the dock tracker is walking the tree per poll as its safety net; those walks
    # belong to the warm-up, not to a move, and counting them against the first asserted cycle would be
    # attributing someone else's cost to this feature.
    Write-Host '  warm-up cycle (not asserted) ...' -ForegroundColor DarkGray
    [void](Set-ForegroundSafely $codex.Handle)
    Start-Sleep -Milliseconds 400
    Move-CodexBy $codex.Handle 40 30
    Start-Sleep -Milliseconds 800
    Move-CodexBy $codex.Handle -40 -30
    Start-Sleep -Milliseconds $BurstMs

    for ($cycle = 1; $cycle -le $Cycles; $cycle++) {
        # The user's machine stays theirs: anything they click takes the foreground back, which hides the
        # strip by design. Re-assert it rather than failing on someone else's window.
        $stripBefore = $null
        for ($attempt = 1; $attempt -le 4 -and $null -eq $stripBefore; $attempt++) {
            [void](Set-ForegroundSafely $codex.Handle)
            Start-Sleep -Milliseconds 250
            $stripBefore = Get-StripWindow
        }

        if ($null -eq $stripBefore) { throw 'the strip will not appear over Codex before cycle ' + $cycle }
        $hostBefore = Get-WindowRect $codex.Handle

        $offset = Get-LogLength
        $process = Get-OverlayProcess
        $cpuBefore = $process.TotalProcessorTime.TotalMilliseconds

        $dx = $MovePx
        $dy = [int]($MovePx * 0.7)
        Move-CodexBy $codex.Handle $dx $dy
        Start-Sleep -Milliseconds $BurstMs

        $stripAfter = $null
        for ($attempt = 1; $attempt -le 4 -and $null -eq $stripAfter; $attempt++) {
            $stripAfter = Get-StripWindow
            if ($null -eq $stripAfter) { Start-Sleep -Milliseconds 150 }
        }

        if ($null -eq $stripAfter) { throw 'the strip disappeared during cycle ' + $cycle }
        $movedCodex = Get-WindowRect $codex.Handle
        $actualDx = $stripAfter.Rect.X - $stripBefore.Rect.X
        $actualDy = $stripAfter.Rect.Y - $stripBefore.Rect.Y
        $hostDx = $movedCodex.X - $hostBefore.X
        $hostDy = $movedCodex.Y - $hostBefore.Y

        $process.Refresh()
        $cpuAfter = $process.TotalProcessorTime.TotalMilliseconds
        $burstCpu = [Math]::Max($burstCpu, (($cpuAfter - $cpuBefore) / ($BurstMs + 300)) * 100)

        $blocks = Read-PerformanceBlocks $offset
        $winEvents = Sum-Field $blocks 'WinEvents'
        $moves = Sum-Field $blocks 'Move-only writes'
        $setWindowPos = Sum-Field $blocks 'Actual SetWindowPos calls'
        $traversals = Sum-Field $blocks 'UIA full traversals'
        $burstTraversals = ($blocks | Where-Object { $_['Mode'] -eq 'burst' } |
            ForEach-Object { [int]$_['UIA full traversals'] } | Measure-Object -Sum).Sum
        if ($null -eq $burstTraversals) { $burstTraversals = 0 }
        $coalesced = Sum-Field $blocks 'Coalesced events'
        $modes = (@($blocks | ForEach-Object { $_['Mode'] }) | Select-Object -Unique) -join ','
        $errors = @($blocks | ForEach-Object { $_['Position error px'] } | Select-Object -Unique) -join ' | '
        Write-Host ("  cycle {0}: host moved ({1},{2}) strip followed ({3},{4}) | WinEvents={5} move-only={6} SetWindowPos={7} traversals={8} (during burst {9}) coalesced={10} modes={11} error={12}" -f `
            $cycle, $hostDx, $hostDy, $actualDx, $actualDy, $winEvents, $moves, $setWindowPos, $traversals, `
            $burstTraversals, $coalesced, $modes, $errors)

        Assert-Step ("A{0}: the strip followed the host by exactly the same delta" -f $cycle) `
            (($actualDx -eq $hostDx) -and ($actualDy -eq $hostDy)) `
            ("strip ({0},{1}) vs host ({2},{3})" -f $actualDx, $actualDy, $hostDx, $hostDy)
        Assert-Step ("A{0}: the WinEvent hook delivered the move" -f $cycle) ($winEvents -gt 0) "WinEvents = $winEvents"
        Assert-Step ("A{0}: the move was answered by position-only writes" -f $cycle) ($moves -gt 0) "move-only writes = $moves"
        # The rule that matters, and the one that would have caused a walk storm if it were missing: the
        # accurate path may only be sped up while it has a cheap re-read available. With the accessibility
        # ladder degraded it has to walk the tree on every poll, so a fast cadence there is a CPU bug, and
        # the [position-performance] block reports both fields that prove which one is in force.
        $fastCadenceWithoutReference = @($blocks |
            Where-Object { $_['Accurate interval ms'] -eq '40' -and $_['Accurate source'] -notin @('uia-context', 'uia-model') })
        Assert-Step ("A{0}: the accurate path was never sped up without a cheap re-read" -f $cycle) `
            ($fastCadenceWithoutReference.Count -eq 0) `
            ("blocks with a 40 ms cadence and a degraded ladder: {0}" -f $fastCadenceWithoutReference.Count)
        # A degraded ladder walks once per poll by design, so the bound here is the slow cadence itself:
        # this assertion is what would catch a burst quietly turning into a traversal storm.
        Assert-Step ("A{0}: the move did not walk the tree faster than the idle cadence" -f $cycle) `
            ($traversals -le 5) ("traversals = {0}" -f $traversals)
        if ($blocks[-1]['Accurate source'] -in @('uia-context', 'uia-model')) {
            Assert-Step ("A{0}: with a usable reference the burst walked the tree zero times" -f $cycle) `
                ($burstTraversals -eq 0) ("traversals while bursting = {0}" -f $burstTraversals)
        } else {
            Write-Host ("  note: accessibility ladder is degraded ({0}) — the accurate path is on the window rung, so traversal counts come from its walk-per-poll safety net, not from the burst" -f $blocks[-1]['Accurate source']) -ForegroundColor DarkGray
        }

        Assert-Step ("A{0}: no reported position error" -f $cycle) `
            (@($blocks | Where-Object { $_['Position error px'] -ne '0 / 0' }).Count -eq 0) `
            ("position error: {0}" -f $errors)

        Move-CodexBy $codex.Handle (-$dx) (-$dy)
        Start-Sleep -Milliseconds $BurstMs
    }

    # ---------------------------------------------------------------- resize
    Write-Host ''
    Write-Host '== TEST C  resize the real Codex window ==' -ForegroundColor Yellow
    $stripBeforeResize = Get-StripWindow
    $offset = Get-LogLength
    for ($step = 1; $step -le 6; $step++) {
        [void][CSB.Win]::MoveWindow($codex.Handle, $testHome.X, $testHome.Y, $testHome.Width + (20 * $step), $testHome.Height, $true)
        Start-Sleep -Milliseconds 45
    }

    Start-Sleep -Milliseconds 500
    $stripAfterResize = Get-StripWindow
    $resizeBlocks = Read-PerformanceBlocks $offset
    $resizeWrites = Sum-Field $resizeBlocks 'Relayout writes'
    $resizeEvents = Sum-Field $resizeBlocks 'WinEvents'
    $resizeWalks = Sum-Field $resizeBlocks 'UIA full traversals'
    $resizeSetWindowPos = Sum-Field $resizeBlocks 'Actual SetWindowPos calls'
    $stripDx = $stripAfterResize.Rect.X - $stripBeforeResize.Rect.X
    $stripWidthDelta = $stripAfterResize.Rect.Width - $stripBeforeResize.Rect.Width
    Write-Host ("  resize: +120px width | strip moved {0}px, width {1:+0;-0;0}px | WinEvents={2} relayout writes={3} SetWindowPos={4} traversals={5}" -f `
        $stripDx, $stripWidthDelta, $resizeEvents, $resizeWrites, $resizeSetWindowPos, $resizeWalks)
    Assert-Step 'C: the resize was seen' ($resizeEvents -gt 0) "WinEvents = $resizeEvents"
    Assert-Step 'C: the resize was re-resolved' ($resizeWrites -gt 0) "relayout writes = $resizeWrites"
    Assert-Step 'C: the docked strip moved with the composer right edge' `
        ([Math]::Abs($stripDx - 120) -le 8) ("strip moved {0}px for a +120px window" -f $stripDx)
    Assert-Step 'C: the strip stayed a three-metric strip (no collapse)' `
        ($stripAfterResize.Rect.Width -ge 250) ("strip width = {0}" -f $stripAfterResize.Rect.Width)
    Assert-Step 'C: the resize did not walk the tree faster than the idle cadence' ($resizeWalks -le 5) `
        "traversals = $resizeWalks"
    Assert-Step 'C: the strip ended up exactly where it was told to go' `
        (@($resizeBlocks | Where-Object { $_['Position error px'] -ne '0 / 0' }).Count -eq 0) `
        ("position error: {0}" -f (@($resizeBlocks | ForEach-Object { $_['Position error px'] } | Select-Object -Unique) -join ' | '))

    [void][CSB.Win]::MoveWindow($codex.Handle, $testHome.X, $testHome.Y, $testHome.Width, $testHome.Height, $true)
    Start-Sleep -Milliseconds 600
    Write-Host ("  move-burst CPU: {0:0.00}% of one core (with --debug logging on)" -f $burstCpu) -ForegroundColor DarkGray

    $final = Read-PerformanceBlocks 0
    $last = $final[$final.Count - 1]
    Assert-Step 'the accurate path is still in use' ($last['Accurate source'] -ne 'none') `
        ("Accurate source: {0}" -f $last['Accurate source'])
    Assert-Step 'the overlay did not crash' ($null -ne (Get-OverlayProcess)) 'process still alive'

    # ============================================================== phase 2: what it costs in production
    Write-Host ''
    Write-Host '== CPU in the production configuration (--background, no --debug) ==' -ForegroundColor Yellow
    Stop-Overlay
    $cpuInstance = Start-Process -FilePath $Exe -ArgumentList '--background' -PassThru
    Write-Host "  started pid $($cpuInstance.Id) with --background"
    $cpuAttached = Wait-ForAttach $codex.Handle
    Assert-Step 'CPU: the production instance attached' $cpuAttached 'Watcher state: ACTIVE'
    Assert-Step 'CPU: the strip is on screen' ($null -ne (Wait-ForStrip $codex.Handle)) `
        ('a visible layered window exists; ' + (Describe-Foreground))
    Start-Sleep -Seconds 3   # warm-up: JIT, the first walks, the first render

    $cpuInstance.Refresh()
    $productionIdleCpu = Get-ProcessCpu $cpuInstance $CpuIdleSeconds
    Write-Host ("  idle CPU              : {0}% of one core (over {1} s)" -f $productionIdleCpu, $CpuIdleSeconds) -ForegroundColor DarkGray

    # Continuous small moves for the whole window, the worst case a person can produce by hand.
    $cpuInstance.Refresh()
    $cpuBefore = $cpuInstance.TotalProcessorTime.TotalMilliseconds
    $deadline = (Get-Date).AddSeconds($CpuMoveSeconds)
    $step = 0
    while ((Get-Date) -lt $deadline) {
        $step++
        $x = $testHome.X + (30 * [Math]::Sin($step / 6.0))
        $y = $testHome.Y + (20 * [Math]::Cos($step / 6.0))
        [void][CSB.Win]::MoveWindow($codex.Handle, [int]$x, [int]$y, $original.Width, $original.Height, $true)
        Start-Sleep -Milliseconds 12
    }

    $cpuInstance.Refresh()
    $cpuAfter = $cpuInstance.TotalProcessorTime.TotalMilliseconds
    $productionMoveCpu = [Math]::Round((($cpuAfter - $cpuBefore) / ($CpuMoveSeconds * 1000.0)) * 100, 3)
    Write-Host ("  continuous move CPU   : {0}% of one core (over {1} s of ~80 moves/s)" -f $productionMoveCpu, $CpuMoveSeconds) -ForegroundColor DarkGray
    Write-Host '  (the metric pipeline runs in the same process; this is the whole overlay, not the fast path alone)' -ForegroundColor DarkGray

    $afterMove = Get-ProcessCpu $cpuInstance 6
    Write-Host ("  idle again after 6 s  : {0}% of one core" -f $afterMove) -ForegroundColor DarkGray
    Assert-Step 'CPU: the cost falls back to idle after the motion stops' ($afterMove -le [Math]::Max(1.5, $productionIdleCpu * 2)) `
        ("idle {0}% -> moving {1}% -> idle {2}%" -f $productionIdleCpu, $productionMoveCpu, $afterMove)
    Write-Host '  (rc2 and rc3 were compared back to back with the same method: both settle to ~0.8% of one' -ForegroundColor DarkGray
    Write-Host '   core, and both spike while the accessibility ladder is degraded and walking every poll)' -ForegroundColor DarkGray
} finally {
    # Always put the user's Codex window back exactly as it was.
    [void][CSB.Win]::MoveWindow(
        $codex.Handle, $original.X, $original.Y, $original.Width, $original.Height, $true)
    Start-Sleep -Milliseconds 300
    if ($wasZoomed) {
        [void][CSB.Win]::ShowWindow($codex.Handle, 3)   # SW_MAXIMIZE
        Start-Sleep -Milliseconds 300
    }
    [void][CSB.Win]::SetForegroundWindow($previousForeground)

    if (-not $LeaveRunning) {
        Stop-Overlay
        Write-Host '  stopped the overlay this run started' -ForegroundColor DarkGray
    }

    Write-Host ''
    foreach ($item in $results) {
        if (-not $item.Passed) { $failed++ }
    }
    Write-Host ("  {0} passed, {1} failed" -f ($results.Count - $failed), $failed) -ForegroundColor Cyan
    Write-Host ("  Codex restored to ({0},{1}) {2}x{3}" -f $original.X, $original.Y, $original.Width, $original.Height) -ForegroundColor DarkGray
}

Write-Host 'visual smoothness is the human`s call; this run only checked the mechanism.' -ForegroundColor DarkGray
if ($failed -gt 0) { exit 1 }
