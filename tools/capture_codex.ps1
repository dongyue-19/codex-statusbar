<#
.SYNOPSIS
  Capture the real Codex Desktop window (plus whatever floats over it) to PNG.

.DESCRIPTION
  Read-only screen capture. Brings the Codex window to the foreground so the
  capture is not occluded, grabs either the whole virtual screen or a rectangle,
  writes a PNG and restores the previously focused window.

  DPI awareness is set to per-monitor-v2 first, otherwise the coordinates read
  from Win32 and the pixels returned by CopyFromScreen are in different spaces.
#>
[CmdletBinding()]
param(
    [string]$Out = "codex-capture.png",
    [string]$Rect,                       # "x,y,w,h" in physical pixels; omit for full window
    [string]$Window = "codex",           # codex | screen
    [switch]$NoActivate,
    [int]$Pad = 0
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class CapNat {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool f);
    [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr h);
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    public static void DpiAware() { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
    public static bool Frame(IntPtr h, out RECT r) { return DwmGetWindowAttribute(h, 9, out r, 16) == 0; }
}
'@
[CapNat]::DpiAware()

function Get-ScreenRect([IntPtr]$h) {
    $r = New-Object CapNat+RECT
    if (-not [CapNat]::Frame($h, [ref]$r)) { [void][CapNat]::GetWindowRect($h, [ref]$r) }
    $r
}

function Find-CodexWindow {
    $script:bestHwnd = [IntPtr]::Zero; $script:bestArea = 0
    $cb = [CapNat+EnumProc]{
        param($h, $p)
        if (-not [CapNat]::IsWindowVisible($h)) { return $true }
        $sb = New-Object System.Text.StringBuilder 256
        [void][CapNat]::GetClassName($h, $sb, 256)
        if ($sb.ToString() -ne 'Chrome_WidgetWin_1') { return $true }
        $ownerPid = 0
        [void][CapNat]::GetWindowThreadProcessId($h, [ref]$ownerPid)
        $proc = Get-Process -Id $ownerPid -ErrorAction SilentlyContinue
        if (-not $proc -or $proc.ProcessName -ne 'ChatGPT') { return $true }
        $r = New-Object CapNat+RECT
        if (-not [CapNat]::Frame($h, [ref]$r)) { return $true }
        $a = ($r.Right - $r.Left) * ($r.Bottom - $r.Top)
        if ($a -gt $script:bestArea) { $script:bestArea = $a; $script:bestHwnd = $h }
        return $true
    }
    [void][CapNat]::EnumWindows($cb, [IntPtr]::Zero)
    $script:bestHwnd
}

$hwnd = Find-CodexWindow
if ($hwnd -eq [IntPtr]::Zero) { throw "Codex window not found" }

$prev = [CapNat]::GetForegroundWindow()
if (-not $NoActivate) {
    if ([CapNat]::IsIconic($hwnd)) { [void][CapNat]::ShowWindow($hwnd, 9) }
    $target = 0; [void][CapNat]::GetWindowThreadProcessId($hwnd, [ref]$target)
    $mine = [CapNat]::GetCurrentThreadId()
    [void][CapNat]::AttachThreadInput($mine, $target, $true)
    [void][CapNat]::SetForegroundWindow($hwnd)
    [void][CapNat]::AttachThreadInput($mine, $target, $false)
    Start-Sleep -Milliseconds 450
}

if ($Window -eq 'screen') {
    $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $x = $b.X; $y = $b.Y; $w = $b.Width; $h = $b.Height
} elseif ($Rect) {
    $p = $Rect.Split(',') | ForEach-Object { [int]$_ }
    $x = $p[0]; $y = $p[1]; $w = $p[2]; $h = $p[3]
} else {
    $r = Get-ScreenRect $hwnd
    $x = $r.Left; $y = $r.Top; $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
}
if ($Pad) { $x -= $Pad; $y -= $Pad; $w += 2 * $Pad; $h += 2 * $Pad }

$bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size($w, $h)), [System.Drawing.CopyPixelOperation]::SourceCopy)
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

if (-not $NoActivate -and $prev -ne [IntPtr]::Zero -and $prev -ne $hwnd) {
    [void][CapNat]::SetForegroundWindow($prev)
}

Write-Host "captured ($x,$y) ${w}x${h} -> $Out"
Write-Host "codex hwnd = 0x$('{0:X}' -f $hwnd.ToInt64())"