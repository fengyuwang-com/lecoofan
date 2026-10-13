@echo off
rem ============================================================================
rem  LecooFan uninstaller entry (double-click, or invoked by Add/Remove Programs)
rem  Self-elevating: if not admin, relaunch THIS batch file elevated and wait
rem  for it (SFX packages clean up their extraction folder on exit).
rem  Same reason as install.bat: no ArgumentList array, spaces in the path are
rem  handled by cmd quoting rather than Start-Process quoting.
rem
rem  NOTE: keep this file pure ASCII - cmd.exe reads .bat as ANSI (GBK here).
rem ============================================================================
net session >nul 2>&1
if %errorlevel% neq 0 (
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs -Wait"
    exit /b %ERRORLEVEL%
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall-LecooFan.ps1"
exit /b %ERRORLEVEL%
