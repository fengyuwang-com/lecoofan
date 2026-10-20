"""一次性快照：把 EC RAM 一段读出来存成二进制/文本，供前后 diff。
只读，不写；一次跑完就退出（不要做成循环，避免和高频 EC 访问撞车）。

用法: python tools_ec_snap.py <outfile> [lo] [hi]
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

if __name__ == "__main__":
    outfile = sys.argv[1] if len(sys.argv) > 1 else "ec_snap.txt"
    lo = int(sys.argv[2], 0) if len(sys.argv) > 2 else 0x0400
    hi = int(sys.argv[3], 0) if len(sys.argv) > 3 else 0x0500
    rows = []
    with ec_lock():
        for a in range(lo, hi + 1):
            v = read_reg(a)
            rows.append("0x%04X 0x%02X %3d" % (a, v, v))
    with open(outfile, "w", encoding="utf-8") as f:
        f.write("\n".join(rows) + "\n")
    print("wrote", outfile, len(rows), "bytes")
