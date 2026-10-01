<#
.SYNOPSIS
  Responsive sweep: what the docked strip does at each window width, and why.

.DESCRIPTION
  Drives the Codex window through a list of widths and, at each one, records the full geometry the
  overlay resolved — the Context and model rectangles re-read independently through UI Automation, the
  composer rectangle and left-cluster boundary from the overlay's own [position] block, the width
  budget, the selected verbosity level and whether the strip is visible.

  This exists to answer one question: is a non-monotonic result (a narrower window showing the strip
  where a wider one hid it) Codex's own responsive layout freeing up room, or a bug in the reference /
  budget arithmetic? The table is what distinguishes them.

  The window is always restored afterwards, including its remembered normal placement.

.EXAMPLE
  pwsh -File tools\probe_responsive.ps1
#>
[CmdletBinding()]
param(
    [int[]]$Widths = @(800, 900, 1000, 1100, 1200, 1400, 1700),
    [string]$LogPath = "$env:LOCALAPPDATA\CodexStatusbar\debug.log",
    [string]$Out = "responsive-sweep.json",
    [int]$SettleMs = 2500
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class RespNat {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct WINDOWPLACEMENT {
        public int length; public int flags; public int showCmd;
        public POINT minPosition; public POINT maxPosition; public RECT normalPosition;
    }
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
    [DllImport("user32.dll")] public static extern bool SetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    public static void DpiAware() { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
    public static bool Frame(IntPtr h, out RECT r) { return DwmGetWindowAttribute(h, 9, out r, 16) == 0; }
    public static WINDOWPLACEMENT Placement(IntPtr h) {
        var p = new WINDOWPLACEMENT();
        p.length = Marshal.SizeOf(typeof(WINDOWPLACEMENT));
        GetWindowPlacement(h, ref p);
        return p;
    }
    public static void RestorePlacement(IntPtr h, WINDOWPLACEMENT p) { SetWindowPlacement(h, ref p); }
    public static bool IsSentinel(int x, int y) { return x <= -30000 || y <= -30000; }
    public const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
}
'@
[RespNat]::DpiAware()

function Find-Codex {
    # Deliberately does not require IsWindowVisible: a window left hidden by an earlier run still has
    # to be findable, or the tool cannot repair it.
    $script:h = [IntPtr]::Zero; $script:a = 0
    $cb = [RespNat+EnumProc]{
        param($w, $p)
        $sb = New-Object System.Text.StringBuilder 256
        [void][RespNat]::GetClassName($w, $sb, 256)
        if ($sb.ToString() -ne 'Chrome_WidgetWin_1') { return $true }
        $op = 0; [void][RespNat]::GetWindowThreadProcessId($w, [ref]$op)
        $pr = Get-Process -Id $op -ErrorAction SilentlyContinue
        if (-not $pr -or $pr.ProcessName -ne 'ChatGPT') { return $true }
        $r = New-Object RespNat+RECT
        if (-not [RespNat]::Frame($w, [ref]$r)) { return $true }
        $area = ($r.Right - $r.Left) * ($r.Bottom - $r.Top)
        if ($area -gt $script:a) { $script:a = $area; $script:h = $w }
        return $true
    }
    [void][RespNat]::EnumWindows($cb, [IntPtr]::Zero)
    $script:h
}

# Repair whatever state a previous run may have left the window in.
function Repair-HostWindow([IntPtr]$h, [int]$x, [int]$y, [int]$w, [int]$hh) {
    [RespNat]::ShowWindow($h, 5) | Out-Null      # SW_SHOW  — a hidden window is not repairable otherwise
    Start-Sleep -Milliseconds 300
    [RespNat]::ShowWindow($h, 9) | Out-Null      # SW_RESTORE — leave any maximised state first
    Start-Sleep -Milliseconds 300
    $p = [RespNat]::Placement($h)
    $p.showCmd = 1
    $p.flags = 0
    $p.normalPosition.Left = $x; $p.normalPosition.Top = $y
    $p.normalPosition.Right = $x + $w; $p.normalPosition.Bottom = $y + $hh
    [RespNat]::RestorePlacement($h, $p)
    Start-Sleep -Milliseconds 300
    [RespNat]::SetWindowPos($h, [IntPtr]::Zero, $x, $y, $w, $hh, 0x0004 -bor 0x0010 -bor 0x0040)
    Start-Sleep -Milliseconds 600
}

$ctrl = [System.Windows.Automation.TreeWalker]::ControlViewWalker

function Read-ComposerGeometry($hwnd) {
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
    $cb = $composer.Current.BoundingRectangle
    $context = $null; $model = $null; $leftRight = 0
    $c = $null; try { $c = $ctrl.GetFirstChild($composer) } catch {}
    while ($null -ne $c) {
        $ct = ''; try { $ct = $c.Current.ControlType.ProgrammaticName } catch {}
        $r = $null
        try { $b = $c.Current.BoundingRectangle; if (-not [double]::IsNaN($b.X)) { $r = $b } } catch {}
        if ($r) {
            $rec = [pscustomobject]@{ Type = $ct; X = [int]$r.X; Y = [int]$r.Y; W = [int]$r.Width; H = [int]$r.Height }
            if ($ct -eq 'ControlType.Image' -and -not $context) { $context = $rec }
        }
        if ($ct -eq 'ControlType.Group') {
            $g = $null; try { $g = $ctrl.GetFirstChild($c) } catch {}
            while ($null -ne $g) {
                $pt = ''; try { $pt = (($g.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName }) -join ',') } catch {}
                if ($pt -match 'ExpandCollapse') {
                    $gb = $g.Current.BoundingRectangle
                    $model = [pscustomobject]@{ Type = 'ControlType.Button'; X = [int]$gb.X; Y = [int]$gb.Y; W = [int]$gb.Width; H = [int]$gb.Height }
                    break
                }
                try { $g = $ctrl.GetNextSibling($g) } catch { break }
            }
        }
        try { $c = $ctrl.GetNextSibling($c) } catch { break }
    }

    if ($context -and $model) {
        $c2 = $null; try { $c2 = $ctrl.GetFirstChild($composer) } catch {}
        while ($null -ne $c2) {
            $ct = ''; try { $ct = $c2.Current.ControlType.ProgrammaticName } catch {}
            if ($ct -eq 'ControlType.Button' -or $ct -eq 'ControlType.Image') {
                $b = $c2.Current.BoundingRectangle
                $x = [int]$b.X; $y = [int]$b.Y; $w = [int]$b.Width; $h = [int]$b.Height
                if (($x + $w) -le $context.X -and $y -lt ($model.Y + $model.H) -and ($y + $h) -gt $model.Y) {
                    $leftRight = [Math]::Max($leftRight, $x + $w)
                }
            }
            try { $c2 = $ctrl.GetNextSibling($c2) } catch { break }
        }
    }

    [pscustomobject]@{
        Composer = "($([int]$cb.X),$([int]$cb.Y)) $([int]$cb.Width)x$([int]$cb.Height)"
        ComposerLeft = [int]$cb.X
        Context = if ($context) { "($($context.X),$($context.Y)) $($context.W)x$($context.H)" } else { 'n/a' }
        ContextLeft = if ($context) { $context.X } else { 0 }
        Model = if ($model) { "($($model.X),$($model.Y)) $($model.W)x$($model.H)" } else { 'n/a' }
        leftClusterRight = $leftRight
    }
}

function Read-LastPositionBlock([string]$path) {
    $raw = [System.IO.File]::ReadAllText($path)
    $idx = $raw.LastIndexOf("[position]")
    if ($idx -lt 0) { return $null }
    $raw.Substring($idx)
}

function Field([string]$block, [string]$label) {
    $m = [regex]::Match($block, "(?m)^" + [regex]::Escape($label) + "\s*(.*)$")
    if ($m.Success) { return $m.Groups[1].Value.Trim() }
    return $null
}

$hwnd = Find-Codex
if ($hwnd -eq [IntPtr]::Zero) { throw "Codex window not found" }

# Establish a known-good starting state before measuring anything.
Repair-HostWindow $hwnd 308 244 2048 1224
$origPlacement = [RespNat]::Placement($hwnd)

$orig = New-Object RespNat+RECT
[void][RespNat]::Frame($hwnd, [ref]$orig)
$h0 = $orig.Bottom - $orig.Top

Write-Host "== responsive sweep ==" -ForegroundColor Cyan
Write-Host ("host {0},{1} {2}x{3}" -f $orig.Left, $orig.Top, ($orig.Right - $orig.Left), $h0)
Write-Host ""

$rows = New-Object System.Collections.Generic.List[object]

function Sample([string]$label, [int]$width) {
    Start-Sleep -Milliseconds $SettleMs
    $geo = Read-ComposerGeometry $hwnd
    $block = Read-LastPositionBlock $LogPath
    if (-not $geo -or -not $block) { Write-Host "  $label : unreadable"; return }

    $source = Field $block 'Reference source:'
    $level = Field $block 'Responsive level:'
    $budgetText = Field $block 'Width budget DIP:'
    $budget = if ($budgetText -match '^(-?[\d\.]+)') { [double]$Matches[1] } else { $null }
    $stripText = Field $block 'Actual strip rect:'
    $visible = $stripText -and $stripText -ne '--' -and $stripText -notmatch '^--'
    $contextLogged = Field $block 'Reference rect:'
    $leftLimit = Field $block 'Left limit px:'
    $refLeft = Field $block 'Reference left edge px:'

    $row = [pscustomobject]@{
        Width = $width
        Step = $label
        ContextRect = $geo.Context
        ContextLeft = $geo.ContextLeft
        ModelRect = $geo.Model
        ComposerRect = $geo.Composer
        LeftBoundaryPx = $geo.leftClusterRight
        WidthBudgetDip = $budget
        Level = $level
        Visible = [bool]$visible
        StripRect = if ($visible) { $stripText } else { '(hidden)' }
        ReferenceSource = $source
        ReferenceLeftPx = $refLeft
        LeftLimitPx = $leftLimit
        LeftClusterPx = $geo.leftClusterRight
    }
    $script:rows.Add($row)

    Write-Host ("-- {0} px ({1})" -f $width, $label) -ForegroundColor Yellow
    Write-Host ("   context {0}   model {1}" -f $geo.Context, $geo.Model)
    Write-Host ("   composer {0}   left boundary {1}px" -f $geo.Composer, $geo.leftClusterRight)
    Write-Host ("   budget {0} DIP   level {1}   visible {2}   source {3}" -f $budget, $level, $visible, $source)
    Write-Host ""
}

try {
    Sample 'current width' ($orig.Right - $orig.Left)
    foreach ($w in $Widths) {
        [void][RespNat]::SetWindowPos($hwnd, [IntPtr]::Zero, $orig.Left, $orig.Top, $w, $h0, [RespNat]::SWP_NOZORDER -bor [RespNat]::SWP_NOACTIVATE)
        Sample "sweep" $w
    }
}
finally {
    [RespNat]::RestorePlacement($hwnd, $origPlacement)
    Start-Sleep -Milliseconds 300
    [void][RespNat]::SetWindowPos(
        $hwnd, [IntPtr]::Zero, $orig.Left, $orig.Top, ($orig.Right - $orig.Left), $h0,
        [RespNat]::SWP_NOZORDER -bor [RespNat]::SWP_NOACTIVATE -bor 0x0040)
    Start-Sleep -Milliseconds 800
    if (-not [RespNat]::IsWindowVisible($hwnd)) {
        [void][RespNat]::ShowWindow($hwnd, 5)
        Start-Sleep -Milliseconds 400
    }
    $f = New-Object RespNat+RECT
    [void][RespNat]::GetWindowRect($hwnd, [ref]$f)
    Write-Host ("host restored to ({0},{1}) {2}x{3} visible={4}" -f `
        $f.Left, $f.Top, ($f.Right - $f.Left), ($f.Bottom - $f.Top), [RespNat]::IsWindowVisible($hwnd)) -ForegroundColor Cyan
}

Write-Host ""
Write-Host ("{0,-8}{1,-22}{2,-22}{3,-10}{4,-9}{5,-16}{6,-8}{7}" -f 'width', 'context rect', 'model rect', 'budget', 'limit px', 'level', 'visible', 'source')
foreach ($r in $rows) {
    Write-Host ("{0,-8}{1,-22}{2,-22}{3,-10}{4,-9}{5,-16}{6,-8}{7}" -f `
        $r.Width, $r.ContextRect, $r.ModelRect, $r.WidthBudgetDip, $r.LeftLimitPx, $r.Level, $r.Visible, $r.ReferenceSource)
}
Write-Host ""
Write-Host ("{0,-8}{1,-14}{2,-16}{3,-16}{4}" -f 'width', 'composer', 'uia boundary', 'overlay limit', 'note')
foreach ($r in $rows) {
    $note = if ($r.LeftLimitPx -and $r.LeftClusterPx -and [int]$r.LeftLimitPx -ne (0)) {
        $delta = [int]$r.LeftLimitPx - ($r.LeftClusterPx + 6)
        if ([Math]::Abs($delta) -le 8) { 'agree' } else { "overlay $delta px left of UIA" }
    } else { '' }
    Write-Host ("{0,-8}{1,-14}{2,-16}{3,-16}{4}" -f $r.Width, $r.ComposerRect, $r.LeftClusterPx, $r.LeftLimitPx, $note)
}

$rows | ConvertTo-Json -Depth 5 | Set-Content -Path $Out -Encoding UTF8
Write-Host "`nwrote $Out"