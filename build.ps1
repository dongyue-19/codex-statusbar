# Build and publish CodexStatusbar.
#
#   pwsh -File build.ps1              # build + self-test + publish single-file exe
#   pwsh -File build.ps1 -SkipTest    # build + publish only
#
# Output: dist\CodexStatusbar.exe (self-contained, single file, no .NET runtime needed)

[CmdletBinding()]
param(
    [switch]$SkipTest,
    [string]$Configuration = 'Release',
    [string]$RuntimeIdentifier = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root 'src\CodexStatusbar\CodexStatusbar.csproj'
$dist = Join-Path $root 'dist'

Write-Host "== build ==" -ForegroundColor Cyan
dotnet build $proj -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "build failed" }

if (-not $SkipTest) {
    Write-Host "`n== self test ==" -ForegroundColor Cyan
    $exe = Join-Path $root "src\CodexStatusbar\bin\$Configuration\net10.0-windows\CodexStatusbar.exe"
    if (Test-Path $exe) {
        & $exe --self-test (Join-Path $root 'fixtures')
        if ($LASTEXITCODE -ne 0) { throw "self-test failed ($LASTEXITCODE)" }
        Write-Host "self-test exit code: $LASTEXITCODE"

        Write-Host "`n== position model ==" -ForegroundColor Cyan
        & $exe --position-probe (Join-Path $env:TEMP 'codex-statusbar-position-probe.json')
        if ($LASTEXITCODE -ne 0) { throw "position probe failed ($LASTEXITCODE)" }

        Write-Host "`n== hotkey ==" -ForegroundColor Cyan
        & $exe --hotkey-probe (Join-Path $env:TEMP 'codex-statusbar-hotkey-probe.json')
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Ctrl+Alt+Shift+P could not be registered (another app may own it); the tray menu still works"
        }
    } else {
        Write-Warning "self-test binary not found at $exe"
    }

    Write-Host "`n== fixture expectations ==" -ForegroundColor Cyan
    if (Get-Command python -ErrorAction SilentlyContinue) {
        python (Join-Path $root 'tools\make_fixtures.py')
    } else {
        Write-Warning "python not found; skipped fixture checks"
    }
}

Write-Host "`n== publish ==" -ForegroundColor Cyan
dotnet publish $proj -c $Configuration -r $RuntimeIdentifier --self-contained true -o $dist
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

$exePath = Join-Path $dist 'CodexStatusbar.exe'
if (-not (Test-Path $exePath)) { throw "expected $exePath" }
$size = [math]::Round((Get-Item $exePath).Length / 1MB, 2)
Write-Host "`nOK: $exePath  ($size MB)" -ForegroundColor Green
Write-Host "Run it, or double-click start-monitor.bat"