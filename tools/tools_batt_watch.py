# -*- coding: utf-8 -*-
"""轮询电量 + 充电状态（Windows GetSystemPowerStatus），配合 EC 阈值实验看固件认不认。"""
import ctypes, time, sys

class SPS(ctypes.Structure):
    _fields_ = [("ACLineStatus", ctypes.c_ubyte), ("BatteryFlag", ctypes.c_ubyte),
                ("BatteryLifePercent", ctypes.c_ubyte), ("SystemStatusFlag", ctypes.c_ubyte),
                ("BatteryLifeTime", ctypes.c_long), ("BatteryFullLifeTime", ctypes.c_long)]

g = ctypes.windll.kernel32.GetSystemPowerStatus
secs = int(sys.argv[1]) if len(sys.argv) > 1 else 120
for i in range(secs // 5 + 1):
    s = SPS()
    if g(ctypes.byref(s)):
        flag = s.BatteryFlag
        state = "charging" if flag & 0x08 else ("discharging" if flag & 0x10 else "idle")
        print("t=%3ds soc=%3d%% flag=0x%02X ac=%d %s" % (i*5, s.BatteryLifePercent, flag, s.ACLineStatus, state), flush=True)
    time.sleep(5)
