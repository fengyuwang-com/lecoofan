@echo off
rem ============================================================================
rem  LecooFan installer entry (double-click, or run from the setup package)
rem  Self-elevating: if not admin, relaunch THIS batch file elevated and wait
rem  for it. The wait matters - SFX packages delete their extraction folder
rem  when this process exits, so the elevated copy must still see the files.
rem
rem  Why not Start-Process -ArgumentList array? Passing a spaced path
rem  (C:\Program Files\...) through that array silently fails on some
rem  systems. Relaunching the bat itself avoids ArgumentList entirely.
rem
rem  NOTE: keep this file pure ASCII - cmd.exe reads .bat as ANSI (GBK here).
rem ============================================================================
net session >nul 2>&1
if %errorlevel% neq 0 (
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs -Wait"
    exit /b %ERRORLEVEL%
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-LecooFan.ps1"
exit /b %ERRORLEVEL%
