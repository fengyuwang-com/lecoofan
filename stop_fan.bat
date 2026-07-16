@echo off
REM FengFanControl — 恢复出厂风扇控制
REM               Restore to factory EC fan control (one-click safety net)
REM
REM 双击运行。任何时候觉得风扇异常，先跑这个。
REM Double-click to restore. Run this FIRST if you suspect any issue.

setlocal
set PYTHON=C:\Users\a8881\AppData\Local\Programs\Python\Python312\python.exe
set RESTORE=%~dp0restore_fan.py

title FengFanControl — Restore Factory

echo FengFanControl — Restore Factory Fan Control
echo =============================================
echo.
echo This will:
echo   1. Stop the FengFanControl daemon (if running)
echo   2. Release EC register 0x1809 back to firmware
echo   3. Verify EC has resumed auto-control
echo.
echo Press Ctrl+C now to cancel, or any key to continue...
pause >nul
echo.

"%PYTHON%" "%RESTORE%"

echo.
echo =============================================
echo If fan is still loud after restore:
echo - Wait 10-30 seconds for EC to stabilize
echo - The EC may need time to re-calibrate
echo =============================================
pause
