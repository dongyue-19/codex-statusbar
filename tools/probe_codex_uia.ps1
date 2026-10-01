<#
.SYNOPSIS
  Read-only investigation of the official Codex Desktop UI Automation tree.

.DESCRIPTION
  Finds the Codex (ChatGPT.exe) main window, walks its UIA subtree and prints every
  element that overlaps the bottom band of the window (where the composer toolbar
  lives). Nothing is written, clicked or modified.

  Chromium only materialises its accessibility tree once a UIA client asks for it,
  so the first walk can come back nearly empty - the script walks twice and reports
  both passes so that difference is visible rather than guessed.

.EXAMPLE
  pwsh -File tools\probe_codex_uia.ps1
  pwsh -File tools\probe_codex_uia.ps1 -BottomBandDip 140 -Depth 30 -Json out.json
#>
[CmdletBinding()]
param(
    [int]$BottomBandDip = 160,
    [int]$Depth = 40,
    [string]$Json,
    [int]$WindowHandle = 0,
    [switch]$Full
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class UiaNative {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    // DWM extended frame bounds (attribute 9) - the rect the app itself sees.
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    public static bool MakeDpiAware() { return SetProcessDpiAwarenessContext(new IntPtr(-4)); }
}
'@

[void][UiaNative]::MakeDpiAware()

function Get-ExtendedFrame([IntPtr]$h) {
    $r = New-Object UiaNative+RECT
    $hr = [UiaNative]::DwmGetWindowAttribute($h, 9, [ref]$r, 16)
    if ($hr -ne 0) { [void][UiaNative]::GetWindowRect($h, [ref]$r) }
    $r
}

function Find-CodexWindow {
    if ($WindowHandle -ne 0) { return [IntPtr]$WindowHandle }
    $best = [IntPtr]::Zero; $bestArea = 0
    foreach ($p in Get-Process ChatGPT -ErrorAction SilentlyContinue) {
        foreach ($h in @($p.MainWindowHandle)) {
            if ($h -eq [IntPtr]::Zero) { continue }
            $r = Get-ExtendedFrame $h
            $area = ($r.Right - $r.Left) * ($r.Bottom - $r.Top)
            if ($area -gt $bestArea) { $bestArea = $area; $best = $h }
        }
    }
    if ($best -eq [IntPtr]::Zero) { throw "No Codex (ChatGPT.exe) window found." }
    $best
}

$hwnd = Find-CodexWindow
$frame = Get-ExtendedFrame $hwnd
$classSb = New-Object System.Text.StringBuilder 256
[void][UiaNative]::GetClassName($hwnd, $classSb, 256)
$ownerPid = 0
[void][UiaNative]::GetWindowThreadProcessId($hwnd, [ref]$ownerPid)

Write-Host "=== Codex window ==="
Write-Host ("hwnd      = 0x{0:X}" -f $hwnd.ToInt64())
Write-Host "class     = $($classSb.ToString())"
Write-Host "pid       = $ownerPid"
Write-Host "frame     = ($($frame.Left),$($frame.Top)) $($frame.Right - $frame.Left)x$($frame.Bottom - $frame.Top)   [DWM extended frame bounds]"
Write-Host "foreground= $([UiaNative]::GetForegroundWindow() -eq $hwnd)"
Write-Host ""

$bandTop = $frame.Bottom - ($BottomBandDip * 144 / 96)
$winRect = @{ Left = $frame.Left; Top = $frame.Top; Right = $frame.Right; Bottom = $frame.Bottom }

$rows = New-Object System.Collections.Generic.List[object]

function Walk($el, $depth, $path) {
    if ($null -eq $el -or $depth -gt $Depth) { return }
    $ci = $null
    try { $ci = $el.Current } catch { return }
    $r = $ci.BoundingRectangle
    $inBand = ($r.Bottom -gt $bandTop) -and ($r.Right -gt $frame.Left) -and ($r.Left -lt $frame.Right) -and ($r.Bottom -gt $frame.Top)
    $name = $ci.Name
    if ($inBand -or $Full) {
        $rows.Add([pscustomobject]@{
            Depth      = $depth
            ControlType= ($ci.ControlType.ProgrammaticName -replace '^ControlType\.','')
            AutomationId = $ci.AutomationId
            ClassName  = $ci.ClassName
            Name       = $name
            X          = [int]$r.X
            Y          = [int]$r.Y
            W          = [int]$r.Width
            H          = [int]$r.Height
            Offscreen  = $ci.IsOffscreen
            Enabled    = $ci.IsEnabled
            Path       = $path
        })
    }
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $child = $null
    try { $child = $walker.GetFirstChild($el) } catch { return }
    $i = 0
    while ($null -ne $child -and $i -lt 400) {
        $ct = ''
        try { $ct = $child.Current.ControlType.ProgrammaticName -replace '^ControlType\.','' } catch {}
        $nm = ''
        try { $nm = $child.Current.Name } catch {}
        $tag = if ($nm) { $ct + '(' + ($nm -replace '\s+',' ').Substring(0, [Math]::Min(24, ($nm -replace '\s+',' ').Length)) + ')' } else { $ct }
        Walk $child ($depth + 1) ($path + '/' + $tag)
        try { $child = $walker.GetNextSibling($child) } catch { break }
        $i++
    }
}

for ($pass = 1; $pass -le 2; $pass++) {
    $rows.Clear()
    $root = $null
    try { $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd) } catch { }
    if ($null -eq $root) { Write-Host "PASS $pass : FromHandle returned null"; continue }
    $rootName = ''
    try { $rootName = $root.Current.Name } catch {}
    $rootType = ''
    try { $rootType = $root.Current.ControlType.ProgrammaticName } catch {}
    Write-Host "=== PASS $pass : root Name='$rootName' ControlType=$rootType, bandTop=$bandTop ==="
    Walk $root 0 'root'
    Write-Host "elements in bottom band: $($rows.Count)"
    $rows | Sort-Object Y, X | Format-Table -AutoSize Depth, ControlType, AutomationId, X, Y, W, H, Name | Out-String -Width 400 | Write-Host
    if ($rows.Count -gt 0) { break }
    Start-Sleep -Milliseconds 700
}

if ($Json) {
    $payload = [pscustomobject]@{
        Window = [pscustomobject]@{
            Handle = ('0x{0:X}' -f $hwnd.ToInt64()); Class = $classSb.ToString(); Pid = $ownerPid
            Frame = [pscustomobject]@{ X = $frame.Left; Y = $frame.Top; W = $frame.Right - $frame.Left; H = $frame.Bottom - $frame.Top }
            BandTop = $bandTop
        }
        Elements = $rows
    }
    $payload | ConvertTo-Json -Depth 6 | Set-Content -Path $Json -Encoding UTF8
    Write-Host "wrote $Json"
}