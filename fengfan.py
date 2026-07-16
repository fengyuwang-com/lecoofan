"""
FengFanControl — N175L (IT5570 EC) fan control daemon

Reads CPU temp from EC HRAM+0x70, applies a temperature→PWM curve,
continuously writes to EC register 0x1809 to override the EC's auto-control.

Usage:
    python fengfan.py                # Foreground with log
    python fengfan.py --quiet        # Background (silent)
    python fengfan.py --status       # Show current EC state and exit
    python fengfan.py --test         # Quick 10s test run
"""

import ctypes
import time
import sys
import os
import json
import signal

# ─── Version ──────────────────────────────────────────────────────────────────
VERSION = "1.0.0"

# ─── Default fan curve ───────────────────────────────────────────────────────
# Temperature (°C) → PWM value (0x00-0xFF)
# EC floor: ~0x18 (1700 RPM), EC soft cap: ~0x78 (5200 RPM)
# Below 0x18: EC clamps up. Above 0x78: EC clamps down.
FAN_CURVE = [
    (45, 0x18),   # <45°C: absolute minimum (1700 RPM, near-silent)
    (55, 0x30),   # 55°C: ~3100 RPM (moderate airflow)
    (65, 0x50),   # 65°C: ~4000 RPM (balanced)
    (72, 0x60),   # 72°C: ~4400 RPM (warm)
    (78, 0x70),   # 78°C: ~4900 RPM (hot)
    (85, 0x78),   # 85°C+: max effective PWM (5200 RPM)
]

POLL_INTERVAL = 1.0         # Seconds between temperature checks
DLL_PATH = r"C:\Program Files\LecooControlCenter\inpoutx64.dll"
PID_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "fengfan.pid")


# ─── EC Access Layer ─────────────────────────────────────────────────────────

class EC:
    """EC register access via inpoutx64 kernel driver (IT5570 at port 0x4E)."""

    def __init__(self, dll_path=DLL_PATH):
        self.dll = ctypes.windll.LoadLibrary(dll_path)
        self.dll.IsInpOutDriverOpen.restype = ctypes.c_uint32
        self.dll.Inp32.restype = ctypes.c_ubyte
        self.dll.Inp32.argtypes = [ctypes.c_int]
        self.dll.Out32.argtypes = [ctypes.c_int, ctypes.c_int]
        self.port = 0x4E  # IT5570 Super I/O
        if not self.dll.IsInpOutDriverOpen():
            raise RuntimeError("inpoutx64 driver not loaded (run as Admin?)")

    def _out(self, p, v):
        self.dll.Out32(p, v & 0xFF)

    def _in(self, p):
        return self.dll.Inp32(p) & 0xFF

    def _ec_enter(self):
        for b in (0x87, 0x01, 0x55, 0x55):
            self._out(self.port, b)

    def _ec_exit(self):
        self._out(self.port, 0x02)
        self._out(self.port + 1, 0x02)

    def read_reg(self, addr):
        """Read a byte from an absolute EC register address."""
        self._ec_enter()
        self._out(self.port, 0x2E); self._out(self.port + 1, 0x11)
        self._out(self.port, 0x2F); self._out(self.port + 1, (addr >> 8) & 0xFF)
        self._out(self.port, 0x2E); self._out(self.port + 1, 0x10)
        self._out(self.port, 0x2F); self._out(self.port + 1, addr & 0xFF)
        self._out(self.port, 0x2E); self._out(self.port + 1, 0x12)
        self._out(self.port, 0x2F); val = self._in(self.port + 1)
        self._ec_exit()
        return val

    def write_reg(self, addr, val):
        """Write a byte to an absolute EC register address."""
        self._ec_enter()
        self._out(self.port, 0x2E); self._out(self.port + 1, 0x11)
        self._out(self.port, 0x2F); self._out(self.port + 1, (addr >> 8) & 0xFF)
        self._out(self.port, 0x2E); self._out(self.port + 1, 0x10)
        self._out(self.port, 0x2F); self._out(self.port + 1, addr & 0xFF)
        self._out(self.port, 0x2E); self._out(self.port + 1, 0x12)
        self._out(self.port, 0x2F); self._out(self.port + 1, val & 0xFF)
        self._ec_exit()

    def read_ram(self, offset):
        """Read a byte from EC HRAM (Host RAM) at given offset."""
        return self.read_reg(0x0400 + offset)

    def read_cpu_temp(self):
        """CPU temperature from EC HRAM+0x70."""
        return self.read_ram(0x70)

    def read_fan_rpm(self):
        """Fan RPM from EC HRAM+0x76/0x77 (MSB/LSB 16-bit counter)."""
        msb = self.read_ram(0x76)
        lsb = self.read_ram(0x77)
        return (msb << 8) | lsb

    def get_fan_pwm(self):
        """Read current PWM value from fan control register 0x1809."""
        return self.read_reg(0x1809)

    def set_fan_pwm(self, pwm):
        """Write PWM value to fan control register 0x1809."""
        self.write_reg(0x1809, pwm & 0xFF)

    def release_fan(self):
        """Release manual fan control by writing 0x00 to 0x1809.

        The EC firmware will resume auto-control within 1-2 seconds.
        """
        self.write_reg(0x1809, 0x00)


# ─── Curve Logic ─────────────────────────────────────────────────────────────

def temp_to_pwm(temp_c, curve=None):
    """Interpolate PWM value from temperature based on the fan curve."""
    if curve is None:
        curve = FAN_CURVE
    if temp_c <= curve[0][0]:
        return curve[0][1]
    if temp_c >= curve[-1][0]:
        return curve[-1][1]
    for i in range(len(curve) - 1):
        t_low, pwm_low = curve[i]
        t_high, pwm_high = curve[i + 1]
        if t_low <= temp_c <= t_high:
            ratio = (temp_c - t_low) / (t_high - t_low) if t_high != t_low else 0
            return int(pwm_low + ratio * (pwm_high - pwm_low))
    return curve[-1][1]


# ─── PID File Management ────────────────────────────────────────────────────

def write_pid():
    with open(PID_FILE, "w", encoding="utf-8") as f:
        f.write(str(os.getpid()))

def read_pid():
    if not os.path.exists(PID_FILE):
        return None
    with open(PID_FILE, "r", encoding="utf-8") as f:
        try:
            return int(f.read().strip())
        except (ValueError, OSError):
            return None

def is_running():
    pid = read_pid()
    if pid is None:
        return False
    try:
        os.kill(pid, 0)  # Signal 0 = existence check (Windows supported in Py3)
        return True
    except (OSError, PermissionError):
        return False

def remove_pid():
    if os.path.exists(PID_FILE):
        os.unlink(PID_FILE)


# ─── Status ──────────────────────────────────────────────────────────────────

def show_status(ec=None):
    """Print current EC and fan status."""
    close = False
    if ec is None:
        ec = EC()
        close = True
    try:
        temp = ec.read_cpu_temp()
        pwm = ec.get_fan_pwm()
        rpm = ec.read_fan_rpm()
        print(f"CPU Temp:     {temp} C")
        print(f"PWM (0x1809): 0x{pwm:02X} ({pwm})")
        print(f"Fan Speed:    {rpm} RPM")
        running = is_running()
        print(f"Daemon:       {'RUNNING' if running else 'STOPPED'}")
        if running:
            print(f"PID file:     {PID_FILE} (pid={read_pid()})")
    finally:
        if close:
            pass  # No explicit close needed for ctypes


# ─── Main Loop ───────────────────────────────────────────────────────────────

def run_loop(ec, quiet=False):
    """Run the fan control loop until interrupted."""
    print(f"FengFanControl v{VERSION}")
    print(f"Curve: {FAN_CURVE}")
    print(f"Poll interval: {POLL_INTERVAL}s")
    print("Press Ctrl+C to stop.\n")

    write_pid()
    last_pwm = -1
    tick = 0

    try:
        while True:
            temp = ec.read_cpu_temp()
            pwm_target = temp_to_pwm(temp)
            ec.set_fan_pwm(pwm_target)
            actual_pwm = ec.get_fan_pwm()
            rpm = ec.read_fan_rpm()

            if pwm_target != last_pwm or tick % 10 == 0:
                if not quiet:
                    print(f"  temp={temp}C PWM=0x{pwm_target:02X} (actual=0x{actual_pwm:02X}) RPM={rpm}")
                last_pwm = pwm_target

            tick += 1
            time.sleep(POLL_INTERVAL)

    except KeyboardInterrupt:
        print("\nStopped by user.")
    except Exception as e:
        print(f"\nERROR: {e}", file=sys.stderr)
    finally:
        remove_pid()


# ─── CLI Entry Point ────────────────────────────────────────────────────────

def main():
    if "--status" in sys.argv:
        try:
            show_status()
        except Exception as e:
            print(f"ERROR: {e}", file=sys.stderr)
            return 1
        return 0

    if "--test" in sys.argv:
        print(f"[TEST] FengFanControl v{VERSION} — 10-second test")
        try:
            ec = EC()
        except Exception as e:
            print(f"[FAIL] EC init: {e}", file=sys.stderr)
            return 1
        print(f"[OK] EC connected (IT5570 at port 0x4E)")
        print(f"[OK] inpoutx64 DLL: {DLL_PATH}")
        show_status(ec)
        print(f"\nRunning for 10 seconds (overriding fan every 1s)...")
        run_loop(ec, quiet=False)
        print(f"[TEST] Complete.")
        return 0

    quiet = "--quiet" in sys.argv

    try:
        ec = EC()
    except Exception as e:
        print(f"ERROR: {e}", file=sys.stderr)
        if not quiet:
            input("Press Enter to exit...")
        return 1

    run_loop(ec, quiet=quiet)
    return 0


if __name__ == "__main__":
    sys.exit(main())
