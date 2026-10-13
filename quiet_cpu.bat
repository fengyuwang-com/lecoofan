@echo off
REM LecooFan - Quiet CPU profile: cap CPU frequency so the laptop stays silent.
REM
REM   quiet_cpu.bat        quiet profile (turbo OFF, EPP 60/70, passive cooling)  <- recommended
REM   quiet_cpu.bat deep   experimental: also disables HWP autonomous mode
REM                        (measured on this machine: hotter and louder, debug only)
REM
REM Restore factory CPU settings with restore_cpu.bat.
REM Details and measured data: README section "CPU 静音限速 / Quiet CPU Limit".
REM The same thing with a UI is LecooFanGUI.exe.

setlocal

REM --- require administrator (powercfg writes need it) ---
fltmc >nul 2>&1
if %errorLevel% neq 0 (
    echo [*] Administrator privileges required - requesting elevation...
    if "%~1"=="" (
        powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    ) else (
        powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs -ArgumentList '%~1'"
    )
    exit /b
)

echo ============================================
echo  LecooFan - Quiet CPU Profile
echo ============================================

REM --- The only hard cap enforced by N175L firmware: turbo/boost off ---
REM NOTE: PROCTHROTTLEMAX and PROCFREQMAX are silently IGNORED on this
REM machine (measured: 50%% still boosted to 4.2 GHz). Only PERFBOOSTMODE
REM = 0 is actually applied by hardware.
powercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PERFBOOSTMODE 0
powercfg /setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR PERFBOOSTMODE 0

REM --- Energy performance preference (HWP EPP, 0-100): higher = efficiency ---
REM deep profile pushes this further; set below before the HWP switch.
set EPP_AC=60
set EPP_DC=70
if /I "%~1"=="deep" (
    set EPP_AC=80
    set EPP_DC=90
)
powercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PERFEPP %EPP_AC%
powercfg /setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR PERFEPP %EPP_DC%
powercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PERFEPP1 %EPP_AC%
powercfg /setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR PERFEPP1 %EPP_DC%
powercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PERFEPP2 %EPP_AC%
powercfg /setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR PERFEPP2 %EPP_DC%

REM --- Silence-first cooling: throttle CPU before the fan ramps (passive) ---
powercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR SYSCOOLPOL 0
powercfg /setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR SYSCOOLPOL 0

REM --- Keep the non-working knobs at harmless originals ---
powercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX 99
powercfg /setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX 99
powercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCFREQMAX 0
powercfg /setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCFREQMAX 0

REM --- HWP autonomous mode ---
if /I "%~1"=="deep" (
    powercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PERFAUTONOMOUS 0
    powercfg /setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR PERFAUTONOMOUS 0
    echo [OK] DEEP profile: turbo OFF + autonomous OFF + EPP 80/90
    echo      WARNING: measured on this machine as 79 C / ~4600 RPM under load,
    echo      i.e. hotter and louder than plain quiet mode. Debug use only.
) else (
    powercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PERFAUTONOMOUS 1
    powercfg /setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR PERFAUTONOMOUS 1
    echo [OK] QUIET profile: turbo OFF + EPP 60/70 + passive cooling
)

powercfg /setactive SCHEME_CURRENT

echo.
echo Applied to the active power scheme - persists across reboot.
echo Measured on this machine, 14-process full load:
echo   quiet:   71 C / ~3200 RPM / single-core bench 0.66s
echo   factory: 84 C / ~3400 RPM / single-core bench 0.35s  (restore_cpu.bat)
echo   perf:    87 C / ~4400 RPM / single-core bench 0.31s  (GUI "高性能")
echo.
echo Revert anytime with restore_cpu.bat
pause
