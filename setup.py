"""
LecooFan — setup / uninstall script

Installs or removes the Windows Task Scheduler auto-start entry.

Usage:
    python setup.py          # Install auto-start (Task Scheduler)
    python setup.py uninstall # Remove auto-start
    python setup.py --help    # Show this
"""

import sys
import os
import subprocess
import platform

PROJECT_DIR = os.path.dirname(os.path.abspath(__file__))
SCRIPT = os.path.join(PROJECT_DIR, "lecoofan.py")
PYTHON_EXE = sys.executable
TASK_NAME = "LecooFan"
TASK_DESC = "LecooFan — N175L IT5570 EC quiet fan curve daemon"


def check_admin():
    """Return True if running with admin privileges."""
    try:
        import ctypes
        return ctypes.windll.shell32.IsUserAnAdmin() != 0
    except Exception:
        return False


def install():
    if not check_admin():
        print("ERROR: Admin privileges required to install Task Scheduler entry.")
        print("Run this as Administrator (right-click -> Run as Administrator).")
        return False

    action = (
        f'New-ScheduledTaskAction -Execute "{PYTHON_EXE}" '
        f'-Argument "-u \\\"{SCRIPT}\\\" --quiet"'
    )
    trigger = "New-ScheduledTaskTrigger -AtLogOn"
    settings = (
        "New-ScheduledTaskSettingsSet "
        "-AllowStartIfOnBatteries "
        "-DontStopIfGoingOnBatteries "
        "-StartWhenAvailable "
        "-ExecutionTimeLimit 0"  # No time limit (run forever)
    )
    principal = (
        "New-ScheduledTaskPrincipal "
        "-GroupId 'BUILTIN\\Administrators' "
        "-RunLevel Highest"
    )
    ps_command = (
        f"$action = {action}; "
        f"$trigger = {trigger}; "
        f"$settings = {settings}; "
        f"$principal = {principal}; "
        f"$task = New-ScheduledTask "
        f"-Action $action "
        f"-Trigger $trigger "
        f"-Settings $settings "
        f"-Principal $principal "
        f"-Description '{TASK_DESC}'; "
        f"Register-ScheduledTask -TaskName '{TASK_NAME}' "
        f"-InputObject $task -Force"
    )

    print(f"Installing Task Scheduler entry: {TASK_NAME}")
    print(f"  Python: {PYTHON_EXE}")
    print(f"  Script: {SCRIPT}")
    print(f"  Mode:   On-logon, Highest privileges, silent background")

    result = subprocess.run(
        ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
         "-Command", ps_command],
        capture_output=True, text=True, timeout=30
    )

    if result.returncode == 0:
        print("\n[OK] Task installed successfully.")
        print(f"      Task '{TASK_NAME}' will run automatically at next logon.")
        print(f"      To run now:  schtasks /Run /TN {TASK_NAME}")
        print(f"      To disable:  schtasks /Change /TN {TASK_NAME} /DISABLE")
        return True
    else:
        print(f"\n[FAIL] Task installation failed:")
        print(f"  {result.stderr.strip()}")
        return False


def uninstall():
    if not check_admin():
        print("ERROR: Admin privileges required.")
        return False

    print(f"Removing Task Scheduler entry: {TASK_NAME}")
    result = subprocess.run(
        ["powershell", "-NoProfile", "-Command",
         f"Unregister-ScheduledTask -TaskName '{TASK_NAME}' -Confirm:$false"],
        capture_output=True, text=True, timeout=15
    )

    if result.returncode == 0:
        print("[OK] Task removed.")
        return True
    else:
        print(f"[FAIL] {result.stderr.strip()}")
        return False


def main():
    if "--help" in sys.argv or "-h" in sys.argv or len(sys.argv) < 2:
        print(__doc__.strip())
        return 0

    cmd = sys.argv[1]
    if cmd == "uninstall":
        return 0 if uninstall() else 1
    else:
        return 0 if install() else 1


if __name__ == "__main__":
    if len(sys.argv) < 2:
        sys.exit(0 if install() else 1)
    main()
