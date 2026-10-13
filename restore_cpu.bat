@echo off
REM LecooFan - Restore original CPU power settings (turbo back on).
REM Counterpart of quiet_cpu.bat. Values recorded 2026-10-13 from this
REM machine's Balanced scheme before the quiet profile was applied.

setlocal

REM --- require administrator ---
fltmc >nul 2>&1
if %errorLevel% neq 0 (
    echo [*] Administrator privileges required - requesting elevation...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

echo ============================================
echo  LecooFan - Restore original CPU settings
echo ============================================

REM --- turbo back on (original PERFBOOSTMODE = 2 "High performance") ---
powercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PERFBOOSTMODE 2
powercfg /setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR PERFBOOSTMODE 2

REM --- original EPP (33 AC / 50 DC) for all efficiency classes ---
powercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PERFEPP 33
powercfg /setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR PERFEPP 50
powercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PERFEPP1 33
powercfg /setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR PERFEPP1 50
powercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PERFEPP2 33
powercfg /setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR PERFEPP2 50

REM --- original cooling policy: AC = active (fan first), DC = passive ---
powercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR SYSCOOLPOL 1
powercfg /setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR SYSCOOLPOL 0

REM --- originals for the knobs that Windows ignores on this machine ---
powercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PERFAUTONOMOUS 1
powercfg /setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR PERFAUTONOMOUS 1
powercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX 99
powercfg /setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX 99
powercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCFREQMAX 0
powercfg /setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCFREQMAX 0

powercfg /setactive SCHEME_CURRENT

echo.
echo [OK] Factory CPU settings restored - turbo boost is back on.
pause
