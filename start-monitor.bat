@echo off
setlocal
rem CodexStatusbar launcher - double-click to start the overlay.
rem Self-contained build: no .NET runtime installation is required.

set "ROOT=%~dp0"
set "EXE=%ROOT%dist\CodexStatusbar.exe"

if not exist "%EXE%" (
    set "EXE=%ROOT%src\CodexStatusbar\bin\Release\net10.0-windows\CodexStatusbar.exe"
)

if not exist "%EXE%" (
    echo.
    echo   CodexStatusbar.exe was not found.
    echo   Build it first:
    echo.
    echo     dotnet publish "%ROOT%src\CodexStatusbar\CodexStatusbar.csproj" -c Release -r win-x64 --self-contained true -o "%ROOT%dist"
    echo.
    pause
    exit /b 1
)

rem Pass through any extra arguments, e.g.  start-monitor.bat --debug
start "" "%EXE%" %*
exit /b 0