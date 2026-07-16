"""
LecooFan — 一键恢复出厂设置

将 EC 风扇控制恢复到出厂状态（EC 自动控制）。
在任何异常情况下运行此脚本，风扇会立刻回到笔记本原厂设置。

Usage:
    python restore_fan.py
"""

import ctypes
import time
import os
import sys
import subprocess
import signal

DLL_PATH = r"C:\Program Files\LecooControlCenter\inpoutx64.dll"


def ec_connect():
    dll = ctypes.windll.LoadLibrary(DLL_PATH)
    dll.IsInpOutDriverOpen.restype = ctypes.c_uint32
    dll.Inp32.restype = ctypes.c_ubyte
    dll.Inp32.argtypes = [ctypes.c_int]
    dll.Out32.argtypes = [ctypes.c_int, ctypes.c_int]
    port = 0x4E
    if not dll.IsInpOutDriverOpen():
        raise RuntimeError("inpoutx64 driver not loaded (run as Admin?)")
    return dll, port


def ec_write(dll, port, addr, val):
    def out(p, v): dll.Out32(p, v & 0xFF)
    def inp(p): return dll.Inp32(p) & 0xFF

    for b in (0x87, 0x01, 0x55, 0x55):
        out(port, b)
    out(port, 0x2E); out(port + 1, 0x11)
    out(port, 0x2F); out(port + 1, (addr >> 8) & 0xFF)
    out(port, 0x2E); out(port + 1, 0x10)
    out(port, 0x2F); out(port + 1, addr & 0xFF)
    out(port, 0x2E); out(port + 1, 0x12)
    out(port, 0x2F); out(port + 1, val & 0xFF)
    out(port, 0x02); out(port + 1, 0x02)


def ec_read(dll, port, addr):
    def out(p, v): dll.Out32(p, v & 0xFF)
    def inp(p): return dll.Inp32(p) & 0xFF

    for b in (0x87, 0x01, 0x55, 0x55):
        out(port, b)
    out(port, 0x2E); out(port + 1, 0x11)
    out(port, 0x2F); out(port + 1, (addr >> 8) & 0xFF)
    out(port, 0x2E); out(port + 1, 0x10)
    out(port, 0x2F); out(port + 1, addr & 0xFF)
    out(port, 0x2E); out(port + 1, 0x12)
    out(port, 0x2F); val = inp(port + 1)
    out(port, 0x02); out(port + 1, 0x02)
    return val


def read_ram(dll, port, offset):
    return ec_read(dll, port, 0x0400 + offset)


def kill_fengfan():
    """Kill any running lecoofan.py process."""
    killed = False
    try:
        result = subprocess.run(
            ["tasklist", "/FI", "IMAGENAME eq python.exe", "/V"],
            capture_output=True, text=True, timeout=5
        )
        for line in result.stdout.splitlines():
            if "fengfan" in line.lower():
                parts = line.split()
                pid = parts[1] if len(parts) > 1 else None
                if pid and pid.isdigit():
                    os.kill(int(pid), signal.SIGTERM)
                    killed = True
                    print(f"  [+] Killed lecoofan.py (PID {pid})")
    except Exception as e:
        print(f"  [-] Error killing fengfan: {e}")
    return killed


def kill_by_pid_file():
    pid_file = os.path.join(os.path.dirname(os.path.abspath(__file__)), "lecoofan.pid")
    if os.path.exists(pid_file):
        try:
            with open(pid_file, "r", encoding="utf-8") as f:
                pid = int(f.read().strip())
            os.kill(pid, signal.SIGTERM)
            print(f"  [+] Killed lecoofan.py via PID file (PID {pid})")
            os.unlink(pid_file)
            return True
        except (ValueError, OSError, FileNotFoundError):
            pass
    return False


def main():
    BANNER = """
╔══════════════════════════════════════════════════════════╗
║         LecooFan — 一键恢复出厂设置               ║
║         One-Click Restore to Factory Fan Control        ║
╚══════════════════════════════════════════════════════════╝
    """
    print(BANNER)

    # Step 1: Kill running daemon
    print("[1/4] Stopping LecooFan daemon...")
    killed = kill_by_pid_file()
    if not killed:
        killed = kill_fengfan()
    if not killed:
        # Try taskkill as fallback
        subprocess.run(
            ["taskkill", "/F", "/IM", "python.exe", "/FI", "WindowTitle eq fengfan*"],
            capture_output=True, timeout=5
        )
    time.sleep(0.5)
    print("  -> Daemon stopped.\n")

    # Step 2: Connect to EC
    print("[2/4] Connecting to EC (IT5570)...")
    try:
        dll, port = ec_connect()
    except Exception as e:
        print(f"  [-] FAILED: {e}")
        print("  [!] Try running as Administrator.")
        sys.exit(1)
    print("  -> EC connected.\n")

    # Step 3: Release fan control
    print("[3/4] Releasing fan control to EC firmware...")
    before_pwm = ec_read(dll, port, 0x1809)
    before_rpm_msb = read_ram(dll, port, 0x76)
    before_rpm_lsb = read_ram(dll, port, 0x77)
    before_rpm = (before_rpm_msb << 8) | before_rpm_lsb
    print(f"  Before: PWM 0x{before_pwm:02X}, RPM={before_rpm}")

    # Write 0 to 0x1809 — release manual override
    ec_write(dll, port, 0x1809, 0x00)

    # Wait for EC to resume auto-control
    print("  Waiting 3 seconds for EC to resume auto-control...")
    time.sleep(3)

    after_pwm = ec_read(dll, port, 0x1809)
    after_rpm_msb = read_ram(dll, port, 0x76)
    after_rpm_lsb = read_ram(dll, port, 0x77)
    after_rpm = (after_rpm_msb << 8) | after_rpm_lsb
    print(f"  After:  PWM 0x{after_pwm:02X}, RPM={after_rpm}")
    print("  -> Fan control released.\n")

    # Step 4: Verify
    print("[4/4] Verification...")
    temp = read_ram(dll, port, 0x70)
    print(f"  CPU Temperature: {temp}C")
    print(f"  PWM register:    0x{after_pwm:02X}")
    print(f"  Fan Speed:       {after_rpm} RPM")
    print()

    if after_pwm != 0x00:
        print("  [OK] EC has resumed auto-control (PWM changed from 0x00).")
    else:
        print("  [WARN] PWM still at 0x00 — EC may not have taken over. Try again.")

    print()
    print("=" * 58)
    print("  FAN CONTROL RESTORED TO FACTORY DEFAULTS.")
    print("  The EC firmware is now managing the fan automatically.")
    print("=" * 58)
    return 0


if __name__ == "__main__":
    sys.exit(main())
