# Live acceptance for the Codex lifecycle: waiting, auto-attach, auto-detach, re-attach.
#
# Everything asserted here is read twice: from the overlay's own [lifecycle] debug block, and
# independently from Win32 (process table + window enumeration). The two must agree, or the check
# fails -- the point is not that the app says it is attached, it is that the strip is on screen.
#
#   pwsh -NoProfile -File tools\verify_lifecycle_live.ps1
#   pwsh -NoProfile -File tools\verify_lifecycle_live.ps1 -ExePath dist\CodexStatusbar.exe -Cycles 3
#
[CmdletBinding()]
param(
    [string]$ExePath = "release\CodexStatusbar.exe",
    [string]$LogPath = "$env:LOCALAPPDATA\CodexStatusbar\debug.log",
    [int]$Cycles = 3,
    [int]$AttachTimeoutSec = 40,
    [int]$DetachTimeoutSec = 30,
    [switch]$NoCodexControl,
    [string]$Out = "lifecycle-verification.json"
)

$ErrorActionPreference = 'Stop'
$script:pass = 0
$script:fail = 0
$results = [System.Collections.Generic.List[object]]::new()

Add-Type @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class LifeNat {
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public class WindowInfo {
        public long Handle;
        public bool Visible;
        public string ClassName = "";
        public int ExStyle;
        public string Rect = "";
    }

    public static List<WindowInfo> Windows(int pid) {
        var list = new List<WindowInfo>();
        EnumWindows((h, p) => {
            uint owner; GetWindowThreadProcessId(h, out owner);
            if (owner == (uint)pid) {
                var sb = new StringBuilder(256);
                GetClassName(h, sb, 256);
                RECT r; GetWindowRect(h, out r);
                list.Add(new WindowInfo {
                    Handle = (long)h,
                    Visible = IsWindowVisible(h),
                    ClassName = sb.ToString(),
                    ExStyle = GetWindowLong(h, -20),
                    Rect = "(" + r.Left + "," + r.Top + ") " + (r.Right - r.Left) + "x" + (r.Bottom - r.Top)
                });
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static IntPtr CodexMainWindow() {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, p) => {
            var sb = new StringBuilder(256);
            GetClassName(h, sb, 256);
            if (sb.ToString() == "Chrome_WidgetWin_1" && IsWindowVisible(h)) {
                uint pid; GetWindowThreadProcessId(h, out pid);
                try {
                    var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                    var path = proc.MainModule.FileName;
                    if (path.IndexOf("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) >= 0) { found = h; return false; }
                } catch { }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    public static void CloseGracefully(IntPtr h) { PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero); } // WM_CLOSE
}
'@

function Assert-Step {
    param([string]$Name, [bool]$Passed, [string]$Detail)
    if ($Passed) { $script:pass++ } else { $script:fail++ }
    $mark = if ($Passed) { 'ok  ' } else { 'FAIL' }
    $colour = if ($Passed) { 'Gray' } else { 'Red' }
    Write-Host ("  [{0}] {1}" -f $mark, $Name) -ForegroundColor $colour
    if ($Detail) { Write-Host ("         {0}" -f $Detail) -ForegroundColor DarkGray }
    $results.Add([pscustomobject]@{ name = $Name; passed = $Passed; detail = $Detail })
}

function Get-LifecycleBlock {
    if (-not (Test-Path $LogPath)) { return $null }
    $tail = Get-Content $LogPath -Tail 120 -ErrorAction SilentlyContinue
    if (-not $tail) { return $null }
    $start = -1
    for ($i = $tail.Count - 1; $i -ge 0; $i--) {
        if ($tail[$i].Trim() -eq '[lifecycle]') { $start = $i; break }
    }
    if ($start -lt 0) { return $null }
    $fields = @{}
    for ($i = $start + 1; $i -lt $tail.Count; $i++) {
        $line = $tail[$i]
        if ([string]::IsNullOrWhiteSpace($line)) { break }
        $idx = $line.IndexOf(':')
        if ($idx -lt 0) { continue }
        $fields[$line.Substring(0, $idx).Trim()] = $line.Substring($idx + 1).Trim()
    }
    return $fields
}

function Get-OverlayState {
    param([int]$ProcessId)
    $windows = [LifeNat]::Windows($ProcessId)
    # The strip is the layered window; the tray/hidden helpers are not on screen.
    $strip = $windows | Where-Object { $_.Visible -and ($_.ExStyle -band 0x00080000) -ne 0 }
    return [pscustomobject]@{
        Total    = $windows.Count
        Visible  = @($windows | Where-Object { $_.Visible }).Count
        Strips   = @($strip).Count
        Strip    = if ($strip) { ($strip | Select-Object -First 1).Rect } else { 'none' }
    }
}

function Get-CodexProcesses {
    foreach ($name in @('ChatGPT', 'Codex')) {
        Get-Process -Name $name -ErrorAction SilentlyContinue
    }
}

function Get-PackageFamily {
    param([int]$ProcessId)
    try {
        Add-Type -ErrorAction SilentlyContinue -TypeDefinition @'
using System; using System.Text; using System.Runtime.InteropServices;
public static class PkgId {
  [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(uint a, bool i, int p);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern int GetPackageFamilyName(IntPtr h, ref int n, StringBuilder s);
  public static string Family(int pid) {
    var h = OpenProcess(0x1000, false, pid);
    if (h == IntPtr.Zero) return "";
    try { int n = 0; int rc = GetPackageFamilyName(h, ref n, null); if (rc != 122) return "";
      var sb = new StringBuilder(n); return GetPackageFamilyName(h, ref n, sb) == 0 ? sb.ToString() : ""; }
    finally { CloseHandle(h); }
  }
}
'@
    } catch { }
    return [PkgId]::Family($ProcessId)
}

function Wait-ForState {
    param([string]$State, [int]$TimeoutSec, [int]$NotPid = 0)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $block = Get-LifecycleBlock
        if ($block -and $block['Watcher state'] -eq $State) { return $block }
        Start-Sleep -Milliseconds 400
    }
    return $null
}

function Start-Codex {
    $aumid = (Get-AppxPackage -Name 'OpenAI.Codex' | Select-Object -First 1).PackageFamilyName + '!App'
    Start-Process ("shell:AppsFolder\" + $aumid) | Out-Null
    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $deadline) {
        if ([LifeNat]::CodexMainWindow() -ne [IntPtr]::Zero) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function Stop-Codex {
    param([int]$WaitSec = 25)
    $hwnd = [LifeNat]::CodexMainWindow()
    if ($hwnd -ne [IntPtr]::Zero) { [LifeNat]::CloseGracefully($hwnd) }
    $deadline = (Get-Date).AddSeconds($WaitSec)
    while ((Get-Date) -lt $deadline) {
        if (-not (Get-CodexProcesses)) { return $true }
        Start-Sleep -Milliseconds 400
    }
    # Only if it refused to close: a user-visible "close" that hangs is still a close.
    Get-CodexProcesses | Stop-Process -Force -ErrorAction SilentlyContinue
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        if (-not (Get-CodexProcesses)) { return $true }
        Start-Sleep -Milliseconds 400
    }
    return $false
}

# --------------------------------------------------------------------------- setup

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not [System.IO.Path]::IsPathRooted($ExePath)) { $ExePath = Join-Path $root $ExePath }
if (-not (Test-Path $ExePath)) { throw "overlay exe not found: $ExePath" }

Write-Host "== lifecycle verification ==" -ForegroundColor Cyan
Write-Host ("exe: {0}" -f $ExePath)
Write-Host ("log: {0}" -f $LogPath)

$overlay = Get-Process -Name CodexStatusbar -ErrorAction SilentlyContinue
if (-not $overlay) {
    Write-Host "starting the overlay with --debug..."
    Start-Process -FilePath $ExePath -ArgumentList '--background', '--debug' | Out-Null
    Start-Sleep -Seconds 4
    $overlay = Get-Process -Name CodexStatusbar -ErrorAction SilentlyContinue
}
if (-not $overlay) { throw "the overlay did not start" }
$overlayPid = $overlay[0].Id
Write-Host ("overlay pid: {0}" -f $overlayPid)

$codexRunning = [bool](Get-CodexProcesses)
Write-Host ("Codex currently running: {0}" -f $codexRunning)
Write-Host ""

# --- CASE 1: watcher up, Codex absent ----------------------------------------------------------
Write-Host "-- CASE 1: Codex not running" -ForegroundColor Yellow
if ($codexRunning -and -not $NoCodexControl) {
    Write-Host "   closing Codex to reach the waiting state..."
    [void](Stop-Codex)
}
$block = Wait-ForState -State 'WAITING_FOR_CODEX' -TimeoutSec $DetachTimeoutSec
Assert-Step "CASE 1: watcher reports WAITING_FOR_CODEX" ($null -ne $block) $(if ($block) { "Codex detected: $($block['Codex detected'])" } else { 'timed out' })
$state = Get-OverlayState -ProcessId $overlayPid
Assert-Step "CASE 1: no strip is on screen while waiting" ($state.Strips -eq 0) ("visible windows {0}, layered+visible {1}" -f $state.Visible, $state.Strips)
Assert-Step "CASE 1: the watcher process is still alive" ((Get-Process -Id $overlayPid -ErrorAction SilentlyContinue) -ne $null) "pid $overlayPid"

# --- CASE 2: Codex starts -> automatic attach ---------------------------------------------------
Write-Host ""
Write-Host "-- CASE 2: starting Codex Desktop" -ForegroundColor Yellow
$startedAt = Get-Date
$started = Start-Codex
Assert-Step "CASE 2: Codex Desktop started" $started "main window found"
$block = Wait-ForState -State 'ACTIVE' -TimeoutSec $AttachTimeoutSec
$attachSeconds = [math]::Round(((Get-Date) - $startedAt).TotalSeconds, 1)
Assert-Step "CASE 2: the watcher reaches ACTIVE with no manual step" ($null -ne $block) $(if ($block) { "after {0}s" -f $attachSeconds } else { "timed out after $AttachTimeoutSec s" })
if ($block) {
    Assert-Step "CASE 2: it identified the OpenAI.Codex package" ($block['Package'] -eq 'OpenAI.Codex') "Package: $($block['Package'])"
    Assert-Step "CASE 2: IPC is connected" ($block['IPC'] -eq 'connected') "IPC: $($block['IPC'])"
}
# the strip needs Codex in the foreground to be placed
Add-Type -ErrorAction SilentlyContinue -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class Fg {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetFocus(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

  // Windows only lets a process take the foreground if it already owns it. Attaching this thread to
  // the *current* foreground thread as well as the target's is what makes the call permissible from
  // a background script -- attaching to the target alone silently fails whenever some other
  // application (a browser, a chat client) owns the foreground.
  public static bool Force(IntPtr h) {
    if (h == IntPtr.Zero) return false;
    if (IsIconic(h)) { ShowWindow(h, 9); }   // SW_RESTORE only when minimised
    uint targetThread = GetWindowThreadProcessId(h, out _);
    IntPtr fg = GetForegroundWindow();
    uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
    uint self = GetCurrentThreadId();
    bool attached = false;
    try {
      if (fgThread != 0 && fgThread != self) { AttachThreadInput(self, fgThread, true); }
      if (targetThread != self) { AttachThreadInput(self, targetThread, true); }
      attached = true;
      SetForegroundWindow(h);
      SetFocus(h);
      return GetForegroundWindow() == h;
    } finally {
      if (attached) {
        if (targetThread != self) { AttachThreadInput(self, targetThread, false); }
        if (fgThread != 0 && fgThread != self) { AttachThreadInput(self, fgThread, false); }
      }
    }
  }

  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool f);
}
'@
$visible = $false
$fgOk = $false
$deadline = (Get-Date).AddSeconds(15)
while ((Get-Date) -lt $deadline) {
    $h = [LifeNat]::CodexMainWindow()
    if ($h -ne [IntPtr]::Zero) { $fgOk = [Fg]::Force($h) }
    Start-Sleep -Milliseconds 700
    $state = Get-OverlayState -ProcessId $overlayPid
    if ($state.Strips -ge 1) { $visible = $true; break }
}
Assert-Step "CASE 2: the strip appears on screen by itself" $visible $(if ($visible) { "strip at $($state.Strip)" } else { "no visible layered window (foreground acquired: $fgOk)" })

# --- CASE 3: Codex closes -> detach, watcher survives --------------------------------------------
Write-Host ""
Write-Host "-- CASE 3: closing Codex Desktop" -ForegroundColor Yellow
$closed = Stop-Codex
Assert-Step "CASE 3: Codex Desktop closed" $closed "no packaged Codex process remains"
$block = Wait-ForState -State 'WAITING_FOR_CODEX' -TimeoutSec $DetachTimeoutSec
Assert-Step "CASE 3: the watcher returns to WAITING_FOR_CODEX" ($null -ne $block) $(if ($block) { "Codex detected: $($block['Codex detected'])" } else { 'timed out' })
$state = Get-OverlayState -ProcessId $overlayPid
Assert-Step "CASE 3: the strip is hidden again" ($state.Strips -eq 0) ("layered+visible {0}" -f $state.Strips)
Assert-Step "CASE 3: the watcher process was NOT terminated" ((Get-Process -Id $overlayPid -ErrorAction SilentlyContinue) -ne $null) "pid $overlayPid still alive"

# --- CASE 4/6: repeat cycles ---------------------------------------------------------------------
$pids = [System.Collections.Generic.List[int]]::new()
for ($cycle = 1; $cycle -le $Cycles; $cycle++) {
    Write-Host ""
    Write-Host ("-- CASE 4/6 cycle {0} of {1}" -f $cycle, $Cycles) -ForegroundColor Yellow
    $started = Start-Codex
    Assert-Step "cycle ${cycle}: Codex started" $started "main window found"
    $block = Wait-ForState -State 'ACTIVE' -TimeoutSec $AttachTimeoutSec
    Assert-Step "cycle ${cycle}: attached automatically" ($null -ne $block) $(if ($block) { "PID $($block['Codex PID'])" } else { 'timed out' })
    if ($block) {
        $pids.Add([int]$block['Codex PID'])
        Assert-Step "cycle ${cycle}: the attach uses the new Codex process" ($block['Codex PID'] -ne '--') "PID $($block['Codex PID'])"
    }

    $h = [LifeNat]::CodexMainWindow()
    if ($h -ne [IntPtr]::Zero) { [void][Fg]::Force($h) }
    $visible = $false
    $fgOk = $false
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        $h2 = [LifeNat]::CodexMainWindow()
        if ($h2 -ne [IntPtr]::Zero) { $fgOk = [Fg]::Force($h2) }
        Start-Sleep -Milliseconds 600
        $state = Get-OverlayState -ProcessId $overlayPid
        if ($state.Strips -ge 1) { $visible = $true; break }
    }
    Assert-Step "cycle ${cycle}: the strip is on screen" $visible ("layered windows: $($state.Strips), strip $($state.Strip), foreground acquired: $fgOk")
    Assert-Step "cycle ${cycle}: only one overlay process exists" ((@(Get-Process -Name CodexStatusbar -ErrorAction SilentlyContinue)).Count -eq 1) ("count {0}" -f (@(Get-Process -Name CodexStatusbar -ErrorAction SilentlyContinue)).Count)

    [void](Stop-Codex)
    $block = Wait-ForState -State 'WAITING_FOR_CODEX' -TimeoutSec $DetachTimeoutSec
    Assert-Step "cycle ${cycle}: detached back to WAITING_FOR_CODEX" ($null -ne $block) $(if ($block) { 'ok' } else { 'timed out' })
    $state = Get-OverlayState -ProcessId $overlayPid
    Assert-Step "cycle ${cycle}: the strip is hidden" ($state.Strips -eq 0) ("layered+visible {0}" -f $state.Strips)
    Assert-Step "cycle ${cycle}: the process survived" ((Get-Process -Id $overlayPid -ErrorAction SilentlyContinue) -ne $null) "pid $overlayPid"
}

# distinct PIDs across cycles: proves no stale Codex process identity is reused
$distinct = @($pids | Sort-Object -Unique)
Assert-Step "each cycle attached to a distinct Codex PID" ($distinct.Count -eq $pids.Count) ("PIDs: " + ($pids -join ', '))

# --- final: window/handle hygiene ---------------------------------------------------------------
$state = Get-OverlayState -ProcessId $overlayPid
Assert-Step "no stale overlay windows accumulated" ($state.Strips -eq 0) ("visible {0}" -f $state.Visible)
$final = Get-LifecycleBlock
Assert-Step "the log shows no watcher error" ($null -ne $final -and $final['Watcher error'] -eq 'none') $(if ($final) { "Watcher error: $($final['Watcher error'])" } else { 'no block' })

Write-Host ""
Write-Host ("{0} passed, {1} failed" -f $script:pass, $script:fail) -ForegroundColor $(if ($script:fail -eq 0) { 'Green' } else { 'Red' })

$outPath = if ([System.IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $root $Out }
[pscustomobject]@{
    exe        = $ExePath
    overlayPid = $overlayPid
    cycles     = $Cycles
    passed     = $script:pass
    failed     = $script:fail
    codexPids  = $pids
    checks     = $results
} | ConvertTo-Json -Depth 5 | Set-Content -Path $outPath -Encoding utf8NoBOM
Write-Host ("wrote {0}" -f $outPath)

exit $(if ($script:fail -eq 0) { 0 } else { 1 })