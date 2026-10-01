#requires -Version 7
<#
.SYNOPSIS
  Verify that the CodexStatusbar overlay attached to the Codex Desktop window,
  follows it, and cannot steal focus — numerically, without looking at the screen.

.DESCRIPTION
  Checks performed:
    1. Locate the Codex Desktop main window (process ChatGPT.exe / Codex.exe,
       class Chrome_WidgetWin_1, visible, not minimised, >= 500x400, no owner).
    2. Locate the overlay's top-level window (process CodexStatusbar).
    3. Assert the overlay sits at the Codex window's bottom edge (inside or just
       outside), horizontally within the Codex window.
    4. Assert the overlay has WS_EX_TOOLWINDOW and WS_EX_NOACTIVATE set
       (no Alt+Tab entry, never takes keyboard focus).
    5. Assert the overlay is NOT the foreground window after it appears.
    6. Move the Codex window by a small delta, re-measure, assert the overlay moved
       with it, then restore the original geometry.

.PARAMETER Restore
  Restore and foreground the Codex Desktop window first (useful when it is minimised
  to tray, which is its normal state when closed).

.EXAMPLE
  pwsh -File tools\verify_overlay_attach.ps1 -Restore
#>
[CmdletBinding()]
param(
    [switch]$Restore,
    [int]$DeltaX = 40,
    [int]$DeltaY = 30,
    # Poll this many seconds for the overlay to become visible. The overlay HIDES itself while Codex
    # is not the foreground window (intended behaviour), and Windows refuses to let another process
    # raise Codex, so an interactive "click on Codex now" window is the only way to test the visible
    # placement.
    [int]$WaitSeconds = 0
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public class Win {
    public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint c);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_LAYERED    = 0x00080000;
    public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    public class Info {
        public IntPtr Handle;
        public uint Pid;
        public string Class = "";
        public string Title = "";
        public int Left, Top, Right, Bottom;
        public bool Visible, Iconic;
        public IntPtr Owner;
        public int ExStyle;
        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
        public string Describe() {
            return string.Format("hwnd=0x{0:X8} pid={1} cls={2} rect=({3},{4})-({5},{6}) {7}x{8} ex=0x{9:X8} vis={10} iconic={11}",
                (long)Handle, Pid, Class, Left, Top, Right, Bottom, Width, Height, ExStyle, Visible, Iconic);
        }
    }

    public static List<Info> Dump(int[] pids) {
        var res = new List<Info>();
        EnumWindows((h, l) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            foreach (var p in pids) {
                if (p == (int)pid) {
                    var t = new StringBuilder(512); GetWindowText(h, t, 512);
                    var c = new StringBuilder(256); GetClassName(h, c, 256);
                    RECT r; GetWindowRect(h, out r);
                    res.Add(new Info {
                        Handle = h, Pid = pid, Class = c.ToString(), Title = t.ToString(),
                        Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom,
                        Visible = IsWindowVisible(h), Iconic = IsIconic(h),
                        Owner = GetWindow(h, 4), ExStyle = GetWindowLong(h, GWL_EXSTYLE)
                    });
                }
            }
            return true;
        }, IntPtr.Zero);
        return res;
    }

    // The classification rule used by CodexStatusbar: only a real Codex main window.
    public static Info FindCodexMainWindow(int[] pids) {
        foreach (var w in Dump(pids)) {
            if (w.Visible && !w.Iconic && w.Owner == IntPtr.Zero &&
                w.Class == "Chrome_WidgetWin_1" && w.Width >= 500 && w.Height >= 400 &&
                (w.ExStyle & (WS_EX_TOOLWINDOW | WS_EX_LAYERED)) == 0)
                return w;
        }
        return null;
    }
}
'@

function Get-Pids([string]$name) {
    @(Get-Process -Name $name -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
}

$codexPids = @()
foreach ($n in 'ChatGPT', 'Codex') { $codexPids += Get-Pids $n }
if (-not $codexPids) { throw "Codex Desktop is not running (no ChatGPT.exe / Codex.exe process)." }

if ($Restore) {
    Write-Host "Restoring + foregrounding the Codex Desktop window..." -ForegroundColor Cyan
    foreach ($w in [Win]::Dump($codexPids)) {
        if ($w.Class -eq 'Chrome_WidgetWin_1') {
            [void][Win]::ShowWindow($w.Handle, 9)   # SW_RESTORE
            [void][Win]::SetForegroundWindow($w.Handle)
        }
    }
    Start-Sleep -Milliseconds 1200
}

$codex = [Win]::FindCodexMainWindow($codexPids)
if (-not $codex) {
    Write-Host "No attachable Codex main window found. Window state:" -ForegroundColor Yellow
    [Win]::Dump($codexPids) | Where-Object { $_.Class -like 'Chrome_WidgetWin*' } |
        ForEach-Object { Write-Host "   $($_.Describe())" }
    Write-Host "`nCodex Desktop is probably minimised. Re-run with -Restore, or open it." -ForegroundColor Yellow
    exit 2
}
Write-Host "Codex window:   $($codex.Describe())" -ForegroundColor Green

$ovPids = Get-Pids 'CodexStatusbar'
if (-not $ovPids) { throw "CodexStatusbar is not running. Start it first (start-monitor.bat)." }

function Get-OverlayWindow([switch]$IncludeHidden) {
    $all = @([Win]::Dump($ovPids) | Where-Object {
        $_.Width -gt 100 -and $_.Height -gt 10 -and $_.Class -like 'WindowsForms10.Window.8*'
    })
    if (-not $IncludeHidden) { $all = @($all | Where-Object { $_.Visible }) }
    $all | Sort-Object { $_.Width * $_.Height } -Descending | Select-Object -First 1
}

$ov = Get-OverlayWindow
if (-not $ov -and $WaitSeconds -gt 0) {
    Write-Host "The overlay is hidden because Codex is not the foreground window." -ForegroundColor Yellow
    Write-Host "Click on the Codex Desktop window now — waiting up to $WaitSeconds s ..." -ForegroundColor Yellow
    $deadline = (Get-Date).AddSeconds($WaitSeconds)
    while ((Get-Date) -lt $deadline -and -not $ov) {
        Start-Sleep -Milliseconds 400
        $codex = [Win]::FindCodexMainWindow($codexPids)
        $ov = Get-OverlayWindow
    }
}

if (-not $ov) {
    $hidden = Get-OverlayWindow -IncludeHidden
    Write-Host "`nOverlay has no VISIBLE window right now." -ForegroundColor Yellow
    if ($hidden) {
        Write-Host "Its window exists but is hidden (correct while Codex is not foreground):" -ForegroundColor Yellow
        Write-Host "   $($hidden.Describe())"
        $fail = 0
        function CheckStyle([string]$name, [bool]$ok, [string]$detail) {
            if ($ok) { Write-Host "  OK    $name $detail" -ForegroundColor Green }
            else { Write-Host "  FAIL  $name $detail" -ForegroundColor Red; $script:fail++ }
        }
        Write-Host "`n== window style (must be non-intrusive) ==" -ForegroundColor Cyan
        CheckStyle "WS_EX_TOOLWINDOW set (no Alt+Tab entry)" (($hidden.ExStyle -band [Win]::WS_EX_TOOLWINDOW) -ne 0) "(ex=0x$('{0:X8}' -f $hidden.ExStyle))"
        CheckStyle "WS_EX_NOACTIVATE set (never steals focus)" (($hidden.ExStyle -band [Win]::WS_EX_NOACTIVATE) -ne 0) "(ex=0x$('{0:X8}' -f $hidden.ExStyle))"
        Write-Host "`nGeometry cannot be checked while hidden. Re-run with -WaitSeconds 60 and click Codex." -ForegroundColor Yellow
        exit 3
    }
    throw "CodexStatusbar is running (pids $($ovPids -join ',')) but has no overlay window at all."
}
Write-Host "Overlay window: $($ov.Describe())" -ForegroundColor Green

$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail) {
    if ($ok) { Write-Host "  OK    $name $detail" -ForegroundColor Green }
    else { Write-Host "  FAIL  $name $detail" -ForegroundColor Red; $script:fail++ }
}

Write-Host "`n== geometry ==" -ForegroundColor Cyan
$insideBottom = $ov.Bottom -le $codex.Bottom + 2 -and $ov.Top -ge $codex.Bottom - 60
$adjacentBelow = [math]::Abs($ov.Top - $codex.Bottom) -le 8
Check "vertically at the Codex bottom edge" ($insideBottom -or $adjacentBelow) `
      "(codex.bottom=$($codex.Bottom) overlay.top=$($ov.Top) overlay.bottom=$($ov.Bottom))"
Check "horizontally within the Codex window" `
      ($ov.Left -ge $codex.Left - 8 -and $ov.Right -le $codex.Right + 8) `
      "(codex=[$($codex.Left),$($codex.Right)] overlay=[$($ov.Left),$($ov.Right)])"
Check "strip height ~24-40 px" ($ov.Height -ge 18 -and $ov.Height -le 56) "(height=$($ov.Height))"

Write-Host "`n== window style (must be non-intrusive) ==" -ForegroundColor Cyan
Check "WS_EX_TOOLWINDOW set (no Alt+Tab entry)" (($ov.ExStyle -band [Win]::WS_EX_TOOLWINDOW) -ne 0) "(ex=0x$('{0:X8}' -f $ov.ExStyle))"
Check "WS_EX_NOACTIVATE set (never steals focus)" (($ov.ExStyle -band [Win]::WS_EX_NOACTIVATE) -ne 0) "(ex=0x$('{0:X8}' -f $ov.ExStyle))"

$fg = [Win]::GetForegroundWindow()
Check "overlay is not the foreground window" ($fg -ne $ov.Handle) "(fg=0x$('{0:X8}' -f [int64]$fg))"

Write-Host "`n== follows move ==" -ForegroundColor Cyan
$origL, $origT = $codex.Left, $codex.Top
$origOvL, $origOvT = $ov.Left, $ov.Top
[void][Win]::SetWindowPos($codex.Handle, [IntPtr]::Zero, $origL + $DeltaX, $origT + $DeltaY, 0, 0,
    ([Win]::SWP_NOSIZE -bor [Win]::SWP_NOZORDER -bor [Win]::SWP_NOACTIVATE))
Start-Sleep -Milliseconds 900
$moved = [Win]::Dump($codexPids) | Where-Object { $_.Handle -eq $codex.Handle } | Select-Object -First 1
$ovMoved = [Win]::Dump($ovPids) | Where-Object { $_.Handle -eq $ov.Handle } | Select-Object -First 1
if ($moved -and $ovMoved) {
    $dOvX = $ovMoved.Left - $origOvL
    $dOvY = $ovMoved.Top - $origOvT
    Check "overlay moved with the Codex window" `
          ([math]::Abs($dOvX - $DeltaX) -le 4 -and [math]::Abs($dOvY - $DeltaY) -le 4) `
          "(codex moved by $DeltaX,$DeltaY; overlay moved by $dOvX,$dOvY)"
}
# restore original geometry
[void][Win]::SetWindowPos($codex.Handle, [IntPtr]::Zero, $origL, $origT, 0, 0,
    ([Win]::SWP_NOSIZE -bor [Win]::SWP_NOZORDER -bor [Win]::SWP_NOACTIVATE))
Start-Sleep -Milliseconds 400

Write-Host ""
if ($fail -eq 0) { Write-Host "ALL OVERLAY ATTACHMENT CHECKS PASSED" -ForegroundColor Green; exit 0 }
Write-Host "$fail CHECK(S) FAILED" -ForegroundColor Red; exit 1