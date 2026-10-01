@echo off
setlocal
rem ---------------------------------------------------------------
rem  CodexStatusbar 1.0.0-rc1 - launcher
rem
rem    start-monitor.bat            start the overlay (normal use)
rem    start-monitor.bat --debug    start it and write the debug log
rem
rem  Self-contained build: no .NET runtime installation is required.
rem  Nothing is installed, nothing is patched; this starts one
rem  companion process and nothing else.
rem ---------------------------------------------------------------

set "HERE=%~dp0"
set "EXE=%HERE%CodexStatusbar.exe"

if not exist "%EXE%" (
    echo.
    echo   CodexStatusbar.exe was not found next to this script.
    echo   Expected: "%EXE%"
    echo.
    pause
    exit /b 1
)

rem  Arguments are passed through unchanged, e.g.
rem      start-monitor.bat --debug
rem      start-monitor.bat --no-overlay
start "" "%EXE%" %*
exit /b 0