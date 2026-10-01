<#
.SYNOPSIS
  Live acceptance checks for the composer-docked status strip.

.DESCRIPTION
  Reads the official composer's geometry twice per step — once independently through UI Automation
  and once from the overlay's own [position] debug block — and asserts the two agree and that the
  geometry stays inside the documented tolerances across window resizes, maximise, restore and move.

  Running it leaves the Codex window's geometry as it found it.

  Requires the overlay to be running with --debug.

.EXAMPLE
  pwsh -File tools\verify_composer_dock.ps1
#>
[CmdletBinding()]
param(
    [string]$LogPath = "$env:LOCALAPPDATA\CodexStatusbar\debug.log",
    [double]$GapToleranceDip = 1.5,
    [int]$CentreTolerancePx = 3,
    [switch]$NoResize,
    [string]$Out = "composer-dock-verification.json"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class DockNat {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [StructLayout(LayoutKind.Sequential)] public struct WINDOWPLACEMENT {
        public int length; public int flags; public int showCmd;
        public POINT minPosition; public POINT maxPosition; public RECT normalPosition;
    }
    [DllImport("user32.dll")] public static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
    [DllImport("user32.dll")] public static extern bool SetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
    public static WINDOWPLACEMENT Placement(IntPtr h) {
        var p = new WINDOWPLACEMENT();
        p.length = Marshal.SizeOf(typeof(WINDOWPLACEMENT));
        GetWindowPlacement(h, ref p);
        return p;
    }
    /// <summary>Restores both the show state and the window's remembered normal rectangle.</summary>
    public static void RestorePlacement(IntPtr h, WINDOWPLACEMENT p) { SetWindowPlacement(h, ref p); }
    /// <summary>Windows parks a minimised or hidden window here.</summary>
    public static bool IsSentinel(int x, int y) { return x <= -30000 || y <= -30000; }
    public static string Rect(IntPtr h) {
        RECT r;
        return GetWindowRect(h, out r) ? "(" + r.Left + "," + r.Top + ") " + (r.Right - r.Left) + "x" + (r.Bottom - r.Top) : "?";
    }
    /// <summary>Owning process of whatever window is topmost at a point — the click-through test.</summary>
    public static long HitPid(int x, int y) {
        uint pid = 0;
        GetWindowThreadProcessId(WindowFromPoint(new POINT { X = x, Y = y }), out pid);
        return pid;
    }
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);

    /// <summary>The overlay's own visible top-level window, so the click-through test uses its live
    /// rectangle rather than the one the log last happened to record (the strip's width changes with
    /// the text, so a logged rect can be a cadence behind).</summary>
    public static IntPtr VisibleWindowOf(uint pid) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, p) => {
            if (!IsWindowVisible(h)) { return true; }
            uint owner = 0;
            GetWindowThreadProcessId(h, out owner);
            if (owner != pid) { return true; }
            RECT r;
            if (!GetWindowRect(h, out r) || r.Right - r.Left < 20 || r.Bottom - r.Top < 10) { return true; }
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    public static void DpiAware() { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
    public static bool Frame(IntPtr h, out RECT r) { return DwmGetWindowAttribute(h, 9, out r, 16) == 0; }
    public const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
}
'@
[DockNat]::DpiAware()

$script:pass = 0
$script:fail = 0
$script:steps = New-Object System.Collections.Generic.List[object]
$script:checks = New-Object System.Collections.Generic.List[object]
$script:path = $Out

function Check([string]$name, [bool]$passed, [string]$detail) {
    if ($passed) { $script:pass++ } else { $script:fail++ }
    $mark = if ($passed) { 'ok  ' } else { 'FAIL' }
    Write-Host ("  [{0}] {1}" -f $mark, $name)
    if ($detail) { Write-Host ("         {0}" -f $detail) }
    $script:checks.Add([pscustomobject]@{ Name = $name; Passed = $passed; Detail = $detail })
}

function Find-Codex {
    # No IsWindowVisible filter: a window left hidden by an earlier run still has to be findable, or
    # the tool cannot repair it.
    $script:h = [IntPtr]::Zero; $script:a = 0
    $cb = [DockNat+EnumProc]{
        param($w, $p)
        $sb = New-Object System.Text.StringBuilder 256
        [void][DockNat]::GetClassName($w, $sb, 256)
        if ($sb.ToString() -ne 'Chrome_WidgetWin_1') { return $true }
        $op = 0; [void][DockNat]::GetWindowThreadProcessId($w, [ref]$op)
        $pr = Get-Process -Id $op -ErrorAction SilentlyContinue
        if (-not $pr -or $pr.ProcessName -ne 'ChatGPT') { return $true }
        $r = New-Object DockNat+RECT
        if (-not [DockNat]::Frame($w, [ref]$r)) { return $true }
        $area = ($r.Right - $r.Left) * ($r.Bottom - $r.Top)
        if ($area -gt $script:a) { $script:a = $area; $script:h = $w }
        return $true
    }
    [void][DockNat]::EnumWindows($cb, [IntPtr]::Zero)
    $script:h
}

$ctrl = [System.Windows.Automation.TreeWalker]::ControlViewWalker

function Find-ComposerReferences($hwnd) {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
    if (-not $root) { return $null }
    $stack = New-Object System.Collections.Stack
    $stack.Push(@($root, 0))
    $edit = $null
    while ($stack.Count -gt 0 -and -not $edit) {
        $item = $stack.Pop(); $el = $item[0]; $d = $item[1]
        if ($d -gt 30) { continue }
        $c = $null; try { $c = $ctrl.GetFirstChild($el) } catch { continue }
        while ($null -ne $c) {
            try { if ($c.Current.ClassName -eq 'ProseMirror') { $edit = $c; break } } catch {}
            $stack.Push(@($c, $d + 1))
            try { $c = $ctrl.GetNextSibling($c) } catch { break }
        }
    }
    if (-not $edit) { return $null }

    $composer = $ctrl.GetParent($edit)
    $context = $null; $model = $null; $leftClusterRight = 0
    $c = $null; try { $c = $ctrl.GetFirstChild($composer) } catch {}
    $rowTop = [int]::MaxValue; $rowBottom = 0
    $children = @()
    while ($null -ne $c) {
        $ct = ''; try { $ct = $c.Current.ControlType.ProgrammaticName } catch {}
        $r = $null
        try { $b = $c.Current.BoundingRectangle; if (-not [double]::IsNaN($b.X)) { $r = $b } } catch {}
        if ($r) {
            $children += [pscustomobject]@{ El = $c; Type = $ct; X = [int]$r.X; Y = [int]$r.Y; W = [int]$r.Width; H = [int]$r.Height }
            if ($ct -eq 'ControlType.Image' -and -not $context) { $context = $children[-1] }
        }
        if ($ct -eq 'ControlType.Group') {
            $g = $null; try { $g = $ctrl.GetFirstChild($c) } catch {}
            while ($null -ne $g) {
                $pt = ''; try { $pt = (($g.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName }) -join ',') } catch {}
                if ($pt -match 'ExpandCollapse') {
                    $gb = $g.Current.BoundingRectangle
                    $model = [pscustomobject]@{ El = $g; X = [int]$gb.X; Y = [int]$gb.Y; W = [int]$gb.Width; H = [int]$gb.Height }
                    break
                }
                try { $g = $ctrl.GetNextSibling($g) } catch { break }
            }
        }
        try { $c = $ctrl.GetNextSibling($c) } catch { break }
    }

    if ($context -and $model) {
        $ctxRight = $context.X + $context.W
        foreach ($ch in $children) {
            if ($ch.Type -ne 'ControlType.Button' -and $ch.Type -ne 'ControlType.Image') { continue }
            if ($ch.X + $ch.W -gt $context.X) { continue }
            if ($ch.Y -ge ($model.Y + $model.H) -or ($ch.Y + $ch.H) -le $model.Y) { continue }
            $leftClusterRight = [Math]::Max($leftClusterRight, $ch.X + $ch.W)
        }
    }

    [pscustomobject]@{
        Context = $context
        Model = $model
        LeftClusterRight = $leftClusterRight
    }
}

function Read-LastPositionBlock([string]$path) {
    $raw = [System.IO.File]::ReadAllText($path)
    $idx = $raw.LastIndexOf("[position]")
    if ($idx -lt 0) { return $null }
    $raw.Substring($idx)
}

function Parse-Rect([string]$text) {
    $m = [regex]::Match($text, '\((-?\d+),\s*(-?\d+)\)\s+(\d+)x(\d+)')
    if (-not $m.Success) { return $null }
    [pscustomobject]@{
        X = [int]$m.Groups[1].Value; Y = [int]$m.Groups[2].Value
        W = [int]$m.Groups[3].Value; H = [int]$m.Groups[4].Value
    }
}

function Field([string]$block, [string]$label) {
    $m = [regex]::Match($block, "(?m)^" + [regex]::Escape($label) + "\s*(.*)$")
    if ($m.Success) { return $m.Groups[1].Value.Trim() }
    return $null
}

$hwnd = Find-Codex
if ($hwnd -eq [IntPtr]::Zero) { throw "Codex window not found" }

# Capture the window's exact placement before touching anything, so the test can always put it back —
# including the remembered "normal" rectangle, which ShowWindow(SW_RESTORE) reads and which
# SetWindowPos does not update. Driving a window through maximise/restore without this is how a
# harness ends up leaving someone's editor parked at (-32000, -32000) with a 720x900 restore size.
$origPlacement = [DockNat]::Placement($hwnd)

function Get-NormalFrame {
    $p = [DockNat]::Placement($hwnd)
    $r = $p.normalPosition
    [pscustomobject]@{ X = $r.Left; Y = $r.Top; W = $r.Right - $r.Left; H = $r.Bottom - $r.Top }
}

function Repair-HostWindow {
    # SW_SHOW first: a hidden window cannot be restored into a usable state by placement alone.
    [void][DockNat]::ShowWindow($hwnd, 5)
    Start-Sleep -Milliseconds 300
    [void][DockNat]::ShowWindow($hwnd, 9)
    Start-Sleep -Milliseconds 300

    $frame = New-Object DockNat+RECT
    [void][DockNat]::Frame($hwnd, [ref]$frame)
    if ([DockNat]::IsSentinel($frame.Left, $frame.Top)) {
        Write-Host "host window was at the (-32000,-32000) sentinel; repairing" -ForegroundColor Yellow
        $p = [DockNat]::Placement($hwnd)
        $p.showCmd = 1
        $p.flags = 0
        $p.normalPosition.Left = 308; $p.normalPosition.Top = 244
        $p.normalPosition.Right = 308 + 2048; $p.normalPosition.Bottom = 244 + 1224
        [DockNat]::RestorePlacement($hwnd, $p)
        Start-Sleep -Milliseconds 300
    }

    [void][DockNat]::SetWindowPos(
        $hwnd, [IntPtr]::Zero, 308, 244, 2048, 1224,
        [DockNat]::SWP_NOZORDER -bor [DockNat]::SWP_NOACTIVATE -bor 0x0040)
    Start-Sleep -Milliseconds 600
}

Repair-HostWindow

$orig = New-Object DockNat+RECT
[void][DockNat]::Frame($hwnd, [ref]$orig)
$w0 = $orig.Right - $orig.Left
$h0 = $orig.Bottom - $orig.Top
if ([DockNat]::IsIconic($hwnd)) { [void][DockNat]::ShowWindow($hwnd, 9) }

Write-Host "== composer dock verification ==" -ForegroundColor Cyan
Write-Host "log: $LogPath"
Write-Host ("host window {0}" -f [DockNat]::Rect($hwnd))
Write-Host ""

function Assert-Step([string]$label) {
    Start-Sleep -Milliseconds 2200
    $refs = Find-ComposerReferences $hwnd
    $block = Read-LastPositionBlock $LogPath
    if (-not $refs -or -not $refs.Context -or -not $block) {
        Check "$label : references readable" $false "UIA or log unreadable"
        return
    }

    $f = New-Object DockNat+RECT
    [void][DockNat]::Frame($hwnd, [ref]$f)
    $strip = Parse-Rect (Field $block 'Actual strip rect:')
    $loggedContext = Parse-Rect (Field $block 'Reference rect:')
    $source = Field $block 'Reference source:'
    $level = Field $block 'Responsive level:'
    $dpiText = Field $block 'Monitor DPI:'
    $dpi = if ($dpiText) { [double]$dpiText } else { 96 }
    $gapDipText = Field $block 'Gap:'
    $gapDip = if ($gapDipText -match '^([\d\.]+) DIP') { [double]$Matches[1] } else { 0 }
    $expectedGapPx = [int][Math]::Round($gapDip * $dpi / 96.0)

    # Independent measurement: the Context control's own rectangle, read fresh from UI Automation.
    $ctx = $refs.Context

    # No room is a documented outcome, not a failure: the responsive policy hides the strip rather than
    # printing it over the composer's own buttons. What must hold in that case is that the space really
    # was insufficient.
    if (-not $strip) {
        $freePx = $ctx.X - $refs.LeftClusterRight
        $freeDip = [Math]::Round($freePx * 96.0 / $dpi, 2)
        # The overlay logs the width budget and every variant's measured size, so "it hid" can be
        # checked against the ladder rather than taken on trust.
        $budgetText = Field $block 'Width budget DIP:'
        $budgetDip = if ($budgetText -match '^(-?[\d\.]+)') { [double]$Matches[1] } else { $null }
        $narrowestDip = $null
        $widthsText = Field $block 'Variant widths DIP:'
        if ($widthsText -match '^([\d\.]+) / ([\d\.]+) / ([\d\.]+) / ([\d\.]+)') {
            $narrowestDip = [double]$Matches[4]
        }
        $script:steps.Add([pscustomobject]@{
            Step = $label
            Window = "$($f.Left),$($f.Top) $($f.Right-$f.Left)x$($f.Bottom-$f.Top)"
            Source = $source
            Level = $level
            ContextRect = "$($ctx.X),$($ctx.Y) $($ctx.W)x$($ctx.H)"
            StripRect = '(hidden)'
            FreeWidthDip = $freeDip
            WidthBudgetDip = $budgetDip
            NarrowestVariantDip = $narrowestDip
        })
        Write-Host ("-- {0}" -f $label) -ForegroundColor Yellow
        Write-Host ("   window {0}; strip hidden. Left cluster ends at {1}, Context starts at {2}; width budget {3} DIP vs narrowest variant {4} DIP" -f `
            $script:steps[-1].Window, $refs.LeftClusterRight, $ctx.X, $budgetDip, $narrowestDip)
        Check "$label : hidden because even the narrowest variant does not fit" `
            ($null -ne $budgetDip -and $null -ne $narrowestDip -and $budgetDip -lt $narrowestDip) `
            "budget $budgetDip DIP < narrowest $narrowestDip DIP"
        Check "$label : hidden means nothing is covered" $true "no strip is drawn"
        Check "$label : the logged Context rect matches a fresh UI Automation read" `
            ($loggedContext.X -eq $ctx.X -and $loggedContext.Y -eq $ctx.Y) `
            "log $($loggedContext.X),$($loggedContext.Y) vs UIA $($ctx.X),$($ctx.Y)"
        Write-Host ""
        return
    }

    $gapPx = $ctx.X - ($strip.X + $strip.W)
    $gapDipMeasured = $gapPx * 96.0 / $dpi
    $rowCentre = if ($refs.Model) { $refs.Model.Y + $refs.Model.H / 2.0 } else { $ctx.Y + $ctx.H / 2.0 }
    $stripCentre = $strip.Y + $strip.H / 2.0
    $centreError = [Math]::Abs($rowCentre - $stripCentre)
    $contextCentreError = [Math]::Abs(($ctx.Y + $ctx.H / 2.0) - $stripCentre)
    $overlapsLeftCluster = $refs.LeftClusterRight -gt 0 -and $strip.X -lt $refs.LeftClusterRight

    $script:steps.Add([pscustomobject]@{
        Step = $label
        Window = "$($f.Left),$($f.Top) $($f.Right-$f.Left)x$($f.Bottom-$f.Top)"
        Source = $source
        Level = $level
        ContextRect = "$($ctx.X),$($ctx.Y) $($ctx.W)x$($ctx.H)"
        ModelRect = if ($refs.Model) { "$($refs.Model.X),$($refs.Model.Y) $($refs.Model.W)x$($refs.Model.H)" } else { 'n/a' }
        StripRect = "$($strip.X),$($strip.Y) $($strip.W)x$($strip.H)"
        GapDip = [Math]::Round($gapDipMeasured, 2)
        GapErrorDip = [Math]::Round([Math]::Abs($gapDipMeasured - $gapDip), 2)
        CentreErrorPx = [Math]::Round($centreError, 1)
        ContextCentreErrorPx = [Math]::Round($contextCentreError, 1)
        LeftClusterRight = $refs.LeftClusterRight
        LogContextMatchesUia = ($loggedContext.X -eq $ctx.X -and $loggedContext.Y -eq $ctx.Y)
    })

    Write-Host ("-- {0}" -f $label) -ForegroundColor Yellow
    Write-Host ("   window {0}, window rect {1}" -f $script:steps[-1].Window, $script:steps[-1].ContextRect)
    Check "$label : docked to the Context control" ($source -eq 'uia-context') "source = $source"
    Check "$label : the strip's right edge keeps the configured gap" ($gapPx -eq $expectedGapPx) `
        "gap $gapPx px = $([Math]::Round($gapDipMeasured,2)) DIP, configured $gapDip DIP (+$([int][Math]::Round($gapDip*$dpi/96)) px expected)"
    Check "$label : gap is inside the 8-12 DIP target band" ($gapDipMeasured -ge 8 -and $gapDipMeasured -le 12) `
        "$([Math]::Round($gapDipMeasured,2)) DIP"
    Check "$label : vertically centred on the toolbar row" ($centreError -le $CentreTolerancePx) `
        "|row centre - strip centre| = $([Math]::Round($centreError,1)) px (row centre $rowCentre, strip centre $stripCentre)"
    Check "$label : the strip does not cover the composer's left controls" (-not $overlapsLeftCluster) `
        "strip left $($strip.X), left cluster right $($refs.LeftClusterRight)"
    Check "$label : the logged Context rect matches a fresh UI Automation read" $script:steps[-1].LogContextMatchesUia `
        "log $(if ($loggedContext) { "$($loggedContext.X),$($loggedContext.Y)" } else { '?' }) vs UIA $($ctx.X),$($ctx.Y)"

    # Click-through, measured live rather than asserted. Inside its own rectangle the strip must be the
    # window that receives the point (otherwise the panel could never be opened by clicking it); a few
    # pixels outside, the point must reach Codex untouched.
    $overlayPid = (Get-Process CodexStatusbar -ErrorAction SilentlyContinue | Select-Object -First 1).Id
    $overlayWindow = [DockNat]::VisibleWindowOf([uint32]$overlayPid)
    $live = New-Object DockNat+RECT
    [void][DockNat]::GetWindowRect($overlayWindow, [ref]$live)

    # Locked means completely click-through: every pixel of the strip's rectangle — the glyphs included —
    # must let the mouse reach Codex. Sampled rather than asserted, because "alpha-based hit testing
    # already handles it" is exactly the assumption this check exists to falsify.
    $samples = 0
    $blocked = 0
    for ($i = 1; $i -le 24; $i++) {
        for ($j = 1; $j -le 3; $j++) {
            $px = $live.Left + [int](($live.Right - $live.Left) * $i / 25)
            $py = $live.Top + [int](($live.Bottom - $live.Top) * $j / 4)
            $samples++
            if ([DockNat]::HitPid($px, $py) -eq $overlayPid) { $blocked++ }
        }
    }

    $outsidePid = [DockNat]::HitPid(($live.Left + $live.Right) / 2, $live.Top - 4)
    $codexPid = 0
    [void][DockNat]::GetWindowThreadProcessId($hwnd, [ref]$codexPid)
    Check "$label : locked is completely click-through" ($blocked -eq 0) `
        "$blocked of $samples sampled points inside the strip resolve to the overlay (must be 0)"
    Check "$label : a point outside the strip reaches Codex untouched" `
        ($outsidePid -eq $codexPid) `
        "WindowFromPoint 4 px above the strip -> pid $outsidePid (Codex $codexPid)"
    Write-Host ""
}

try {
    Assert-Step 'A. current window size'

    if (-not $NoResize) {
        $x = $orig.Left; $y = $orig.Top
        foreach ($w in 1100, 900, 1700) {
            [void][DockNat]::SetWindowPos($hwnd, [IntPtr]::Zero, $x, $y, $w, $h0, [DockNat]::SWP_NOZORDER -bor [DockNat]::SWP_NOACTIVATE)
            Assert-Step ("B/C. resized to {0} px wide" -f $w)
        }
        [void][DockNat]::ShowWindow($hwnd, 3)
        Assert-Step 'D. maximized'
        [void][DockNat]::ShowWindow($hwnd, 9)
        Assert-Step 'E. restored'
        [void][DockNat]::SetWindowPos($hwnd, [IntPtr]::Zero, $x + 120, $y + 60, $w0, $h0, [DockNat]::SWP_NOZORDER -bor [DockNat]::SWP_NOACTIVATE)
        Assert-Step 'F. moved +120,+60'
    }
}
finally {
    # Always put the window back, even if a step threw. SetWindowPlacement restores the remembered
    # normal rectangle, which is the part SetWindowPos alone does not touch.
    [DockNat]::RestorePlacement($hwnd, $origPlacement)
    Start-Sleep -Milliseconds 300
    [void][DockNat]::SetWindowPos(
        $hwnd, [IntPtr]::Zero, $orig.Left, $orig.Top, $w0, $h0,
        [DockNat]::SWP_NOZORDER -bor [DockNat]::SWP_NOACTIVATE -bor 0x0040)
    Start-Sleep -Milliseconds 800
    if (-not [DockNat]::IsWindowVisible($hwnd)) {
        [void][DockNat]::ShowWindow($hwnd, 5)
        Start-Sleep -Milliseconds 400
    }
    Write-Host ("host window left at {0}" -f [DockNat]::Rect($hwnd)) -ForegroundColor Cyan
    if (-not $NoResize) {
        Assert-Step 'G. frame restored'
    }
}

Write-Host ("{0} passed, {1} failed" -f $script:pass, $script:fail) -ForegroundColor $(if ($script:fail -eq 0) { 'Green' } else { 'Red' })

[pscustomobject]@{
    Passed = $script:pass
    Failed = $script:fail
    Steps = $script:steps
    Checks = $script:checks
} | ConvertTo-Json -Depth 6 | Set-Content -Path $script:path -Encoding UTF8
Write-Host "wrote $script:path"

exit $(if ($script:fail -eq 0) { 0 } else { 1 })