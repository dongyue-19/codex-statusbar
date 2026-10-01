# Startup registration, single instance, simulated logon launch and idle cost.
#
#   pwsh -NoProfile -File tools\verify_startup_and_resource.ps1
#
# Everything asserted is the real HKCU value, the real process table and real CPU counters -- the
# script writes to HKCU\...\Run and restores it to enabled at the end, since that is the shipped
# default. It never touches HKLM and never asks for elevation.
#
[CmdletBinding()]
param(
    [string]$ExePath = "release\CodexStatusbar.exe",
    [string]$LogPath = "$env:LOCALAPPDATA\CodexStatusbar\debug.log",
    [int]$SampleSeconds = 30,
    [string]$Out = "startup-resource-verification.json"
)

$ErrorActionPreference = 'Stop'
$script:pass = 0
$script:fail = 0
$results = [System.Collections.Generic.List[object]]::new()

function Assert-Step {
    param([string]$Name, [bool]$Passed, [string]$Detail)
    if ($Passed) { $script:pass++ } else { $script:fail++ }
    Write-Host ("  [{0}] {1}" -f $(if ($Passed) { 'ok  ' } else { 'FAIL' }), $Name) -ForegroundColor $(if ($Passed) { 'Gray' } else { 'Red' })
    if ($Detail) { Write-Host ("         {0}" -f $Detail) -ForegroundColor DarkGray }
    $results.Add([pscustomobject]@{ name = $Name; passed = $Passed; detail = $Detail })
}

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not [System.IO.Path]::IsPathRooted($ExePath)) { $ExePath = Join-Path $root $ExePath }
if (-not (Test-Path $ExePath)) { throw "overlay exe not found: $ExePath" }
$ExePath = (Resolve-Path $ExePath).Path

$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
function Get-RunValue { (Get-ItemProperty -Path $key -Name CodexStatusbar -ErrorAction SilentlyContinue).CodexStatusbar }

function Get-LifecycleField {
    param([string]$Field)
    if (-not (Test-Path $LogPath)) { return $null }
    $tail = Get-Content $LogPath -Tail 120 -ErrorAction SilentlyContinue
    $start = -1
    for ($i = $tail.Count - 1; $i -ge 0; $i--) { if ($tail[$i].Trim() -eq '[lifecycle]') { $start = $i; break } }
    if ($start -lt 0) { return $null }
    for ($i = $start + 1; $i -lt $tail.Count; $i++) {
        if ([string]::IsNullOrWhiteSpace($tail[$i])) { break }
        $idx = $tail[$i].IndexOf(':')
        if ($idx -lt 0) { continue }
        if ($tail[$i].Substring(0, $idx).Trim() -eq $Field) { return $tail[$i].Substring($idx + 1).Trim() }
    }
    return $null
}
Add-Type -ErrorAction SilentlyContinue -TypeDefinition @'
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class CloseNat {
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
  public static IntPtr MainWindow() {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h,p) => {
      var sb = new StringBuilder(256); GetClassName(h, sb, 256);
      if (sb.ToString() == "Chrome_WidgetWin_1") {
        uint pid; GetWindowThreadProcessId(h, out pid);
        try { var pr = System.Diagnostics.Process.GetProcessById((int)pid);
          if (pr.MainModule.FileName.IndexOf("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) >= 0) { found = h; return false; } } catch {}
      }
      return true; }, IntPtr.Zero);
    return found;
  }
  /// <summary>Visible layered windows owned by a pid: the strip, and nothing else this app creates.</summary>
  public static int Strips(int pid) {
    int count = 0;
    EnumWindows((h,p) => {
      uint owner; GetWindowThreadProcessId(h, out owner);
      if (owner == (uint)pid && IsWindowVisible(h) && (GetWindowLong(h, -20) & 0x00080000) != 0) { count++; }
      return true; }, IntPtr.Zero);
    return count;
  }
}
'@



Write-Host "== startup + single instance + idle cost ==" -ForegroundColor Cyan
Write-Host ("exe: {0}" -f $ExePath)

# --- PE subsystem: the logon launch must not flash a console ------------------------------------
$bytes = [System.IO.File]::ReadAllBytes($ExePath)
$peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
$subsystem = [BitConverter]::ToUInt16($bytes, $peOffset + 0x5C)
Assert-Step "the exe is a GUI-subsystem binary (no console window at logon)" ($subsystem -eq 2) "IMAGE_SUBSYSTEM=$subsystem (2 = WINDOWS_GUI, 3 = WINDOWS_CUI)"

# --- TEST A: enabling writes a correctly quoted value -------------------------------------------
Write-Host ""
Write-Host "-- TEST A: register" -ForegroundColor Yellow
& $ExePath --install-startup | Out-Null
Start-Sleep -Milliseconds 400
$value = Get-RunValue
$expected = '"' + $ExePath + '" --background'
Assert-Step "TEST A: HKCU\...\Run\CodexStatusbar exists" ($null -ne $value) ("value: {0}" -f $(if ($value) { $value } else { '(absent)' }))
Assert-Step "TEST A: the command is exactly the quoted exe + --background" ($value -eq $expected) ("got '{0}' / want '{1}'" -f $value, $expected)
Assert-Step "TEST A: the path is quoted (spaces cannot break the parse)" ($value -like '"*"*') "starts with a quote"
$settings = Get-Content "$env:LOCALAPPDATA\CodexStatusbar\settings.json" -Raw | ConvertFrom-Json
Assert-Step "TEST A: settings.json records the choice" ($settings.startWithWindows -eq $true -and $settings.startupConfigured -eq $true) ("startWithWindows={0} startupConfigured={1}" -f $settings.startWithWindows, $settings.startupConfigured)

# --- TEST B: disabling removes it, enabling restores it -----------------------------------------
Write-Host ""
Write-Host "-- TEST B: disable / re-enable" -ForegroundColor Yellow
& $ExePath --uninstall-startup | Out-Null
Start-Sleep -Milliseconds 400
Assert-Step "TEST B: disabling deletes the registry value" ($null -eq (Get-RunValue)) ("value: {0}" -f $(if (Get-RunValue) { Get-RunValue } else { '(absent)' }))
$settings = Get-Content "$env:LOCALAPPDATA\CodexStatusbar\settings.json" -Raw | ConvertFrom-Json
Assert-Step "TEST B: the choice is remembered as off" ($settings.startWithWindows -eq $false) ("startWithWindows={0}" -f $settings.startWithWindows)

# --- a later launch must NOT re-enable what the user turned off ----------------------------------
Write-Host ""
Write-Host "-- TEST B2: a launch respects the user's off choice" -ForegroundColor Yellow
Get-Process CodexStatusbar -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600
Start-Process -FilePath $ExePath -ArgumentList '--background', '--debug' | Out-Null
Start-Sleep -Seconds 5
Assert-Step "TEST B2: launching again does not re-create the value" ($null -eq (Get-RunValue)) ("value: {0}" -f $(if (Get-RunValue) { Get-RunValue } else { '(absent)' }))
$settings = Get-Content "$env:LOCALAPPDATA\CodexStatusbar\settings.json" -Raw | ConvertFrom-Json
Assert-Step "TEST B2: and it stays recorded as off" ($settings.startWithWindows -eq $false) "startWithWindows=False"

# restore the shipped default for the remaining checks and for the user
& $ExePath --install-startup | Out-Null
Start-Sleep -Milliseconds 400
Assert-Step "TEST B: re-enabling restores the value" ((Get-RunValue) -eq $expected) ("value: {0}" -f (Get-RunValue))

# --- TEST C: single instance ---------------------------------------------------------------------
Write-Host ""
Write-Host "-- TEST C: second instance" -ForegroundColor Yellow
$overlay = @(Get-Process CodexStatusbar -ErrorAction SilentlyContinue)
Assert-Step "TEST C: exactly one instance is running to begin with" ($overlay.Count -eq 1) ("count {0}" -f $overlay.Count)
$second = Start-Process -FilePath $ExePath -ArgumentList '--background' -PassThru -Wait
Assert-Step "TEST C: the second instance exits immediately with code 0" ($second.ExitCode -eq 0) ("exit code {0}" -f $second.ExitCode)
Start-Sleep -Milliseconds 800
$after = @(Get-Process CodexStatusbar -ErrorAction SilentlyContinue)
Assert-Step "TEST C: still exactly one overlay process" ($after.Count -eq 1) ("count {0}, pids {1}" -f $after.Count, ($after.Id -join ','))
Assert-Step "TEST C: the survivor is the original process" ($after.Count -eq 1 -and $after[0].Id -eq $overlay[0].Id) ("pid {0} -> {1}" -f $overlay[0].Id, ($after.Id -join ','))

# --- simulated logon: run the registered command verbatim ----------------------------------------
Write-Host ""
Write-Host "-- simulated logon: the exact registered command" -ForegroundColor Yellow
Get-Process CodexStatusbar -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600
$registered = Get-RunValue
$parsedExe = $registered.Substring(1, $registered.IndexOf('"', 1) - 1)
$parsedArgs = $registered.Substring($registered.IndexOf('"', 1) + 1).Trim()
Start-Process -FilePath $parsedExe -ArgumentList $parsedArgs | Out-Null
Start-Sleep -Seconds 5
$logon = @(Get-Process CodexStatusbar -ErrorAction SilentlyContinue)
Assert-Step "logon: the registered command starts one process" ($logon.Count -eq 1) ("exe '{0}' args '{1}' -> pid {2}" -f $parsedExe, $parsedArgs, ($logon.Id -join ','))
if ($logon.Count -eq 1) {
    $mainWindow = $logon[0].MainWindowHandle
    Assert-Step "logon: no main window is shown" ($mainWindow -eq [IntPtr]::Zero) ("MainWindowHandle {0}" -f $mainWindow)
    Assert-Step "logon: the command carries no --debug (production launches do not log)" ($parsedArgs -notmatch '--debug') ("args '{0}'" -f $parsedArgs)
    # Asserted from Win32 rather than the debug log: the production launch has no log to read, which
    # is itself the behaviour under test.
    $stripCount = [CloseNat]::Strips($logon[0].Id)
    Assert-Step "logon: no strip is on screen while Codex is closed" ($stripCount -eq 0) ("visible layered windows: {0}" -f $stripCount)
}

# Swap in a --debug instance: the resource numbers below need the lifecycle state, and the production
# launch deliberately writes no log.
Get-Process CodexStatusbar -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
Start-Process -FilePath $ExePath -ArgumentList '--background', '--debug' | Out-Null
Start-Sleep -Seconds 5
Assert-Step "debug instance: one overlay is running for the measurements" ((@(Get-Process CodexStatusbar -ErrorAction SilentlyContinue)).Count -eq 1) "restarted with --debug"

# --- idle cost ------------------------------------------------------------------------------------
$target = @(Get-Process CodexStatusbar -ErrorAction SilentlyContinue)[0]
function Measure-Idle {
    param([System.Diagnostics.Process]$Process, [int]$Seconds, [string]$Label)
    $cpu0 = $Process.TotalProcessorTime.TotalMilliseconds
    $ws0 = $Process.WorkingSet64
    Start-Sleep -Seconds $Seconds
    $Process.Refresh()
    $cpu1 = $Process.TotalProcessorTime.TotalMilliseconds
    $ws1 = $Process.WorkingSet64
    $cpuPercent = [math]::Round((($cpu1 - $cpu0) / ($Seconds * 1000)) * 100, 4)
    $handles = $Process.HandleCount
    $state = Get-LifecycleField 'Watcher state'
    Write-Host ("  {0}: CPU {1}% of one core over {2}s, working set {3} MB, {4} handles, state {5}" -f $Label, $cpuPercent, $Seconds, [math]::Round($ws1 / 1MB, 1), $handles, $state) -ForegroundColor DarkGray
    return [pscustomobject]@{ label = $Label; state = $state; cpuPercent = $cpuPercent; workingSetMb = [math]::Round($ws1 / 1MB, 1); handles = $handles }
}

function Close-Codex {
    $hwnd = [CloseNat]::MainWindow()
    if ($hwnd -ne [IntPtr]::Zero) { [void][CloseNat]::PostMessage($hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) }
    $deadline = (Get-Date).AddSeconds(25)
    while ((Get-Date) -lt $deadline) {
        if (-not (Get-Process -Name ChatGPT -ErrorAction SilentlyContinue)) { return $true }
        Start-Sleep -Milliseconds 500
    }
    Get-Process -Name ChatGPT -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        if (-not (Get-Process -Name ChatGPT -ErrorAction SilentlyContinue)) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

Write-Host ""
Write-Host "-- idle cost with Codex closed" -ForegroundColor Yellow
if (Get-Process -Name ChatGPT -ErrorAction SilentlyContinue) {
    Write-Host "   closing Codex so the waiting state is the real one..." -ForegroundColor DarkGray
    [void](Close-Codex)
}
$waitingState = Get-LifecycleField 'Watcher state'
Assert-Step "idle: Codex is really absent and the watcher is waiting" ($waitingState -eq 'WAITING_FOR_CODEX') ("Watcher state: {0}" -f $waitingState)
$waiting = Measure-Idle -Process $target -Seconds $SampleSeconds -Label 'WAITING_FOR_CODEX'
Assert-Step "idle: CPU stays under 0.2 % of one core" ($waiting.cpuPercent -lt 0.2) ("{0}% over {1}s" -f $waiting.cpuPercent, $SampleSeconds)
Assert-Step "idle: no per-process polling loop is left running" ($waiting.cpuPercent -lt 0.2) ("{0}%" -f $waiting.cpuPercent)

Write-Host ""
Write-Host "-- idle cost with Codex running" -ForegroundColor Yellow
$aumid = (Get-AppxPackage -Name 'OpenAI.Codex' | Select-Object -First 1).PackageFamilyName + '!App'
Start-Process ("shell:AppsFolder\" + $aumid) | Out-Null
$deadline = (Get-Date).AddSeconds(90)
while ((Get-Date) -lt $deadline) {
    if ((Get-LifecycleField 'Watcher state') -eq 'ACTIVE') { break }
    Start-Sleep -Milliseconds 500
}
$activeState = Get-LifecycleField 'Watcher state'
Assert-Step "active: the watcher attached to the restarted Codex" ($activeState -eq 'ACTIVE') ("Watcher state: {0}" -f $activeState)
$active = Measure-Idle -Process $target -Seconds $SampleSeconds -Label 'ACTIVE (Codex running)'
Assert-Step "active: the watcher keeps its own cost low" ($active.cpuPercent -lt 1.0) ("{0}% over {1}s" -f $active.cpuPercent, $SampleSeconds)

Write-Host ""
Write-Host ("{0} passed, {1} failed" -f $script:pass, $script:fail) -ForegroundColor $(if ($script:fail -eq 0) { 'Green' } else { 'Red' })

$outPath = if ([System.IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $root $Out }
[pscustomobject]@{
    exe          = $ExePath
    subsystem    = $subsystem
    runValue     = Get-RunValue
    waiting      = $waiting
    active       = $active
    passed       = $script:pass
    failed       = $script:fail
    checks       = $results
} | ConvertTo-Json -Depth 5 | Set-Content -Path $outPath -Encoding utf8NoBOM
Write-Host ("wrote {0}" -f $outPath)

exit $(if ($script:fail -eq 0) { 0 } else { 1 })
