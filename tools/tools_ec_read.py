"""只读小工具：读 EC 0x04B0 附近的字节，看充电限值相关寄存器现在的真实状态。
只做少量读，不写任何东西；跑之前确保没有别的进程在访问 EC。
"""
import ctypes, sys

DLL = r"C:\Program Files\LecooControlCenter\inpoutx64.dll"
PORT = 0x4E

dll = ctypes.windll.LoadLibrary(DLL)
dll.Inp32.restype = ctypes.c_ubyte
dll.Inp32.argtypes = [ctypes.c_int]
dll.Out32.argtypes = [ctypes.c_int, ctypes.c_int]

def out(p, v): dll.Out32(p, v & 0xFF)
def inp(p): return dll.Inp32(p) & 0xFF

def enter():
    for b in (0x87, 0x01, 0x55, 0x55): out(PORT, b)

def exit_():
    out(PORT, 0x02); out(PORT + 1, 0x02)

def read_reg(addr):
    enter()
    out(PORT, 0x2E); out(PORT + 1, 0x11)
    out(PORT, 0x2F); out(PORT + 1, (addr >> 8) & 0xFF)
    out(PORT, 0x2E); out(PORT + 1, 0x10)
    out(PORT, 0x2F); out(PORT + 1, addr & 0xFF)
    out(PORT, 0x2E); out(PORT + 1, 0x12)
    out(PORT, 0x2F); v = inp(PORT + 1)
    exit_()
    return v

if __name__ == "__main__":
    lo = int(sys.argv[1], 0) if len(sys.argv) > 1 else 0x04A0
    hi = int(sys.argv[2], 0) if len(sys.argv) > 2 else 0x04D0
    print("addr : value")
    for a in range(lo, hi + 1):
        print("0x%04X : 0x%02X  %3d" % (a, read_reg(a), read_reg(a)))
