"""一次性写工具：往 EC 绝对地址写一个字节（带回读确认）。
只用于排查充电限值机制，单次调用即退出，别开循环。

用法: python tools_ec_write.py <addr> <value>
"""
import ctypes, sys, os

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ec_lock import ec_lock

DLL = r"C:\Program Files\LecooControlCenter\inpoutx64.dll"
PORT = 0x4E

dll = ctypes.windll.LoadLibrary(DLL)
dll.Inp32.restype = ctypes.c_ubyte
dll.Inp32.argtypes = [ctypes.c_int]
dll.Out32.argtypes = [ctypes.c_int, ctypes.c_int]

def out(p, v): dll.Out32(p, v & 0xFF)
def inp(p): return dll.Inp32(p) & 0xFF

def read_reg(addr):
    out(PORT, 0x87); out(PORT, 0x01); out(PORT, 0x55); out(PORT, 0x55)
    out(PORT, 0x2E); out(PORT + 1, 0x11)
    out(PORT, 0x2F); out(PORT + 1, (addr >> 8) & 0xFF)
    out(PORT, 0x2E); out(PORT + 1, 0x10)
    out(PORT, 0x2F); out(PORT + 1, addr & 0xFF)
    out(PORT, 0x2E); out(PORT + 1, 0x12)
    out(PORT, 0x2F); v = inp(PORT + 1)
    out(PORT, 0x02); out(PORT + 1, 0x02)
    return v

def write_reg(addr, val):
    out(PORT, 0x87); out(PORT, 0x01); out(PORT, 0x55); out(PORT, 0x55)
    out(PORT, 0x2E); out(PORT + 1, 0x11)
    out(PORT, 0x2F); out(PORT + 1, (addr >> 8) & 0xFF)
    out(PORT, 0x2E); out(PORT + 1, 0x10)
    out(PORT, 0x2F); out(PORT + 1, addr & 0xFF)
    out(PORT, 0x2E); out(PORT + 1, 0x12)
    out(PORT, 0x2F); out(PORT + 1, val & 0xFF)
    out(PORT, 0x02); out(PORT + 1, 0x02)

if __name__ == "__main__":
    addr = int(sys.argv[1], 0)
    val = int(sys.argv[2], 0)
    with ec_lock():
        before = read_reg(addr)
        write_reg(addr, val)
        after = read_reg(addr)
    print("0x%04X: 0x%02X -> wrote 0x%02X -> readback 0x%02X %s" %
          (addr, before, val & 0xFF, after, "OK" if after == (val & 0xFF) else "MISMATCH"))
