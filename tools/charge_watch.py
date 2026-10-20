# -*- coding: utf-8 -*-
"""充电状态实时监视器 —— 判断「到底有没有在充电」用这个，不要用 Windows 的 charging 标志。

Windows 的 SYSTEM_POWER_STATUS.BatteryFlag 里的充电位是平滑过的状态位，
插着电就一直是 true，拿它判断充电等于没判断。
真正可信的是 ACPI root\\wmi\\BatteryStatus 的 ChargeRate（充电功率 mW）和
DischargeRate（放电功率 mW）：两者都为 0 = 既不充也不放（充电器停着）。

同时读 EC：实时电量 0x0493、停充阈值 0x04BB、复充阈值 0x04BC。

用法:
    python charge_watch.py [秒数]      # 默认 300 秒，每 2 秒一行
"""
import ctypes, os, subprocess, sys, time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

DLL = r"C:\Program Files\LecooControlCenter\inpoutx64.dll"
PORT = 0x4E
HRAM = 0x0400

try:
    from ec_lock import ec_lock
except Exception:
    ec_lock = None

_dll = ctypes.windll.LoadLibrary(DLL)
_dll.Inp32.restype = ctypes.c_ubyte
_dll.Inp32.argtypes = [ctypes.c_int]
_dll.Out32.argtypes = [ctypes.c_int, ctypes.c_int]


def _out(p, v):
    _dll.Out32(p, v & 0xFF)


def _inp(p):
    return _dll.Inp32(p) & 0xFF


def _read_reg(addr):
    _out(PORT, 0x87); _out(PORT, 0x01); _out(PORT, 0x55); _out(PORT, 0x55)
    _out(PORT, 0x2E); _out(PORT + 1, 0x11)
    _out(PORT, 0x2F); _out(PORT + 1, (addr >> 8) & 0xFF)
    _out(PORT, 0x2E); _out(PORT + 1, 0x10)
    _out(PORT, 0x2F); _out(PORT + 1, addr & 0xFF)
    _out(PORT, 0x2E); _out(PORT + 1, 0x12)
    _out(PORT, 0x2F); v = _inp(PORT + 1)
    _out(PORT, 0x02); _out(PORT + 1, 0x02)
    return v


def ec_state():
    try:
        if ec_lock:
            with ec_lock():
                return (_read_reg(HRAM + 0x93), _read_reg(HRAM + 0xBB), _read_reg(HRAM + 0xBC))
        return (_read_reg(HRAM + 0x93), _read_reg(HRAM + 0xBB), _read_reg(HRAM + 0xBC))
    except Exception:
        return (-1, -1, -1)


_PS = r'''
$b = Get-WmiObject -Namespace root\wmi -Class BatteryStatus
if ($b) {
  "rate={0} dRate={1} V={2} rem={3} chg={4}" -f $b.ChargeRate,$b.DischargeRate,$b.Voltage,$b.RemainingCapacity,$b.Charging
} else { "no-battery" }
'''


def acpi():
    try:
        r = subprocess.run(["powershell", "-NoProfile", "-Command", _PS],
                           capture_output=True, text=True, encoding="utf-8", errors="replace")
        return r.stdout.strip()
    except Exception as ex:
        return "err:" + str(ex)


class SPS(ctypes.Structure):
    _fields_ = [("ACLineStatus", ctypes.c_ubyte), ("BatteryFlag", ctypes.c_ubyte),
                ("BatteryLifePercent", ctypes.c_ubyte), ("SystemStatusFlag", ctypes.c_ubyte),
                ("BatteryLifeTime", ctypes.c_long), ("BatteryFullLifeTime", ctypes.c_long)]


_g = ctypes.windll.kernel32.GetSystemPowerStatus


def win_status():
    s = SPS()
    if not _g(ctypes.byref(s)):
        return "?"
    return "soc=%d%% flag=0x%02X ac=%d" % (s.BatteryLifePercent, s.BatteryFlag, s.ACLineStatus)


if __name__ == "__main__":
    secs = int(sys.argv[1]) if len(sys.argv) > 1 else 300
    print("t(s)  EC(soc/0x4BB/0x4BC)   ACPI(charge mW / discharge mW / V / rem)   Windows")
    t0 = time.time()
    while True:
        soc, bb, bc = ec_state()
        print("t=%5.1f  %3d/%3d/%3d          %s   %s" %
              (time.time() - t0, soc, bb, bc, acpi(), win_status()), flush=True)
        if time.time() - t0 >= secs:
            break
        time.sleep(2)
