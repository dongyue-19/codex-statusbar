<#
.SYNOPSIS
  Check whether the Codex composer UIA references survive window geometry changes.

.DESCRIPTION
  Read-only. Walks the Codex UI Automation tree to locate the composer, the
  Context-usage indicator and the Model selector, then reports for each geometry
  change:

    - the rectangle of the CACHED element (does a held reference stay valid?), and
    - the rectangle from a FRESH tree walk (can it be re-found after a relayout?).

  This decides whether the status strip may hold a reference or must re-discover
  it, which is the difference between an overlay that follows a resize and one
  that freezes. The window geometry is restored at the end.
#>
[CmdletBinding()]
param(
    [string]$Out = "uia-stability.json",
    [switch]$NoResize,
    [string]$RestoreFrame
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class StabNat {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    public static void DpiAware() { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
    public static bool Frame(IntPtr h, out RECT r) { return DwmGetWindowAttribute(h, 9, out r, 16) == 0; }
    public const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
}
'@
[StabNat]::DpiAware()

function Find-Codex {
    $script:h = [IntPtr]::Zero; $script:a = 0
    $cb = [StabNat+EnumProc]{
        param($w, $p)
        if (-not [StabNat]::IsWindowVisible($w)) { return $true }
        $sb = New-Object System.Text.StringBuilder 256
        [void][StabNat]::GetClassName($w, $sb, 256)
        if ($sb.ToString() -ne 'Chrome_WidgetWin_1') { return $true }
        $op = 0; [void][StabNat]::GetWindowThreadProcessId($w, [ref]$op)
        $pr = Get-Process -Id $op -ErrorAction SilentlyContinue
        if (-not $pr -or $pr.ProcessName -ne 'ChatGPT') { return $true }
        $r = New-Object StabNat+RECT
        if (-not [StabNat]::Frame($w, [ref]$r)) { return $true }
        $area = ($r.Right - $r.Left) * ($r.Bottom - $r.Top)
        if ($area -gt $script:a) { $script:a = $area; $script:h = $w }
        return $true
    }
    [void][StabNat]::EnumWindows($cb, [IntPtr]::Zero)
    $script:h
}

function SafeInt([double]$v) {
    if ([double]::IsNaN($v) -or [double]::IsInfinity($v)) { return $null }
    if ($v -gt 2e9) { return 2000000000 }
    if ($v -lt -2e9) { return -2000000000 }
    [int]$v
}

function RectOf($el) {
    if (-not $el) { return $null }
    try { $r = $el.Current.BoundingRectangle } catch { return $null }
    if ($null -eq $r) { return $null }
    $x = SafeInt $r.X; $y = SafeInt $r.Y; $w = SafeInt $r.Width; $h = SafeInt $r.Height
    if ($null -eq $x -or $null -eq $y -or $null -eq $w -or $null -eq $h) { return $null }
    [pscustomobject]@{ X = $x; Y = $y; W = $w; H = $h }
}

function Show-Rect($r) {
    if ($null -eq $r) { return '<none>' }
    "$($r.X),$($r.Y) $($r.W)x$($r.H)"
}

$ctrl = [System.Windows.Automation.TreeWalker]::ControlViewWalker
$hwnd = Find-Codex
$root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)

# --- rediscovery, mirroring what the real implementation would have to do -----
function Find-Refs {
    $edit = $null
    $stack = New-Object System.Collections.Stack
    $stack.Push(@($root, 0))
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
    if (-not $edit) { return [pscustomobject]@{ Composer = $null; Context = $null; Model = $null; Edit = $null } }

    $composer = $ctrl.GetParent($edit)
    $context = $null; $model = $null; $idx = -1; $i = 0
    $c = $null; try { $c = $ctrl.GetFirstChild($composer) } catch {}
    while ($null -ne $c) {
        $ct = ''; try { $ct = $c.Current.ControlType.ProgrammaticName } catch {}
        $nm = ''; try { $nm = $c.Current.Name } catch {}
        if ($ct -eq 'ControlType.Image' -and -not $context) {
            $cls = ''; try { $cls = $c.Current.ClassName } catch {}
            $looksContext = ($nm -like '*上下文用量*') -or ($nm -like '*Context usage*') -or ($cls -like '*codex-description*')
            if ($looksContext) { $context = $c; $idx = $i }
        }
        if ($ct -eq 'ControlType.Group') {
            $g = $null; try { $g = $ctrl.GetFirstChild($c) } catch {}
            while ($null -ne $g) {
                $pt = ''; try { $pt = (($g.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName }) -join ',') } catch {}
                if ($pt -match 'ExpandCollapse') { $model = $g; break }
                try { $g = $ctrl.GetNextSibling($g) } catch { break }
            }
        }
        try { $c = $ctrl.GetNextSibling($c) } catch { break }
        $i++
    }
    [pscustomobject]@{ Composer = $composer; Context = $context; Model = $model; Edit = $edit; ContextIndex = $idx }
}

$refs = Find-Refs
if (-not $refs.Context) { throw "Context indicator not found" }
Write-Host "composer child that holds the Context indicator: index $($refs.ContextIndex) of the composer's ControlView children"
Write-Host ""

$origFrame = New-Object StabNat+RECT
[void][StabNat]::Frame($hwnd, [ref]$origFrame)
if ([StabNat]::IsIconic($hwnd)) { [void][StabNat]::ShowWindow($hwnd, 9) }
if ($RestoreFrame) {
    $p = $RestoreFrame.Split(',') | ForEach-Object { [int]$_ }
    [void][StabNat]::SetWindowPos($hwnd, [IntPtr]::Zero, $p[0], $p[1], $p[2], $p[3], [StabNat]::SWP_NOZORDER -bor [StabNat]::SWP_NOACTIVATE)
    Start-Sleep -Milliseconds 500
    [void][StabNat]::Frame($hwnd, [ref]$origFrame)
}

$steps = @()
function Record($label) {
    $f = New-Object StabNat+RECT
    [void][StabNat]::Frame($hwnd, [ref]$f)
    $cachedContext = RectOf $refs.Context
    $cachedModel = RectOf $refs.Model
    $fresh = Find-Refs
    $script:steps += [pscustomobject]@{
        Step = $label
        Window = "$($f.Left),$($f.Top) $($f.Right-$f.Left)x$($f.Bottom-$f.Top)"
        CachedContext = $cachedContext
        FreshContext = RectOf $fresh.Context
        CachedModel = $cachedModel
        FreshModel = RectOf $fresh.Model
        FreshComposer = RectOf $fresh.Composer
        ContextIndex = $fresh.ContextIndex
        CachedContextAlive = ($null -ne $cachedContext)
        CachedModelAlive = ($null -ne $cachedModel)
    }
}

Record 'baseline'

if (-not $NoResize) {
    $x = $origFrame.Left; $y = $origFrame.Top
    $w0 = $origFrame.Right - $origFrame.Left; $h0 = $origFrame.Bottom - $origFrame.Top
    foreach ($w in 1100, 900, 1700) {
        [void][StabNat]::SetWindowPos($hwnd, [IntPtr]::Zero, $x, $y, $w, $h0, [StabNat]::SWP_NOZORDER -bor [StabNat]::SWP_NOACTIVATE)
        Start-Sleep -Milliseconds 600
        Record "resize ${w}px wide"
    }
    [void][StabNat]::ShowWindow($hwnd, 3)
    Start-Sleep -Milliseconds 800
    Record 'maximized'
    [void][StabNat]::ShowWindow($hwnd, 9)
    Start-Sleep -Milliseconds 800
    Record 'restored'
    [void][StabNat]::SetWindowPos($hwnd, [IntPtr]::Zero, $x + 120, $y + 60, $w0, $h0, [StabNat]::SWP_NOZORDER -bor [StabNat]::SWP_NOACTIVATE)
    Start-Sleep -Milliseconds 600
    Record 'moved +120,+60'
    [void][StabNat]::SetWindowPos($hwnd, [IntPtr]::Zero, $x, $y, $w0, $h0, [StabNat]::SWP_NOZORDER -bor [StabNat]::SWP_NOACTIVATE)
    Start-Sleep -Milliseconds 600
    Record 'frame restored'
}

foreach ($s in $steps) {
    Write-Host ("{0,-20} window {1}" -f $s.Step, $s.Window)
    Write-Host ("{0,-20}   cached context {1,-20} alive={2}" -f '', (Show-Rect $s.CachedContext), $s.CachedContextAlive)
    Write-Host ("{0,-20}   fresh  context {1,-20} (index {2})" -f '', (Show-Rect $s.FreshContext), $s.ContextIndex)
    Write-Host ("{0,-20}   cached model   {1,-20} alive={2}" -f '', (Show-Rect $s.CachedModel), $s.CachedModelAlive)
    Write-Host ("{0,-20}   fresh  model   {1}" -f '', (Show-Rect $s.FreshModel))
    $fc = $s.FreshContext; $fm = $s.FreshModel
    if ($fc -and $fm) { Write-Host ("{0,-20}   model.Left - context.Left = {1}" -f '', ($fm.X - $fc.X)) }
    Write-Host ""
}

$steps | ConvertTo-Json -Depth 5 | Set-Content -Path $Out -Encoding UTF8
Write-Host "wrote $Out"