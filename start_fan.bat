@echo off
REM Windows batch launcher for lecoofan.py (double-click friendly)
REM Edit PYTHON path below if your Python is elsewhere

set PYTHON=C:\Users\a8881\AppData\Local\Programs\Python\Python312\python.exe
set SCRIPT=%~dp0lecoofan.py

title LecooFan Daemon

echo Starting LecooFan v1.0
echo =============================
"%PYTHON%" -u "%SCRIPT%" %*

pause
