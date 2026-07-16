@echo off
REM LecooFan — 一键切换 自定义静音风扇 / 出厂EC控制
REM               Toggle between quiet fan curve and factory EC control
REM
REM 双击运行即可切换。运行时先停止当前的，再启动另一种模式。
REM Double-click to toggle. Stops current mode, starts the other.

setlocal
set PYTHON=C:\Users\a8881\AppData\Local\Programs\Python\Python312\python.exe
set SCRIPT=%~dp0lecoofan.py
set RESTORE=%~dp0restore_fan.py

title LecooFan Toggle

echo LecooFan — Toggle
echo ======================
echo.

REM Check if fengfan daemon is running
tasklist /FI "IMAGENAME eq python.exe" /V 2>nul | findstr /I "fengfan" >nul
if %ERRORLEVEL% EQU 0 (
    echo [*] LecooFan is RUNNING. Switching to FACTORY mode...
    "%PYTHON%" "%RESTORE%"
    if %ERRORLEVEL% EQU 0 (
        echo.
        echo [OK] Switched to factory fan control (EC auto).
    )
    goto :end
)

REM Check if Lecoo daemon is handling fan (any python fan process)
echo [*] LecooFan is STOPPED. Switching to QUIET mode...
start /B "" "%PYTHON%" -u "%SCRIPT%" --quiet
if %ERRORLEVEL% EQU 0 (
    echo [OK] LecooFan started (quiet mode).
) else (
    echo [*] (Background start may take a moment)
)
goto :end

:end
echo.
echo ========================
echo Status: check via: python "%SCRIPT%" --status
echo ========================
timeout /T 3 >nul
