"""跨进程 EC 互斥（和 lecoofan_gui.cs 里的同名互斥体对齐）。

EC 事务（enter 87 01 55 55 → 读写索引/数据口 → exit 02 02）一旦和别的进程
交错，就会把值写到别的寄存器上 —— 22:56 那次硬断电就是这么来的。
C# 侧用的是命名互斥体 Local\LecooFanEcAccess，Python 侧必须抢同一把锁。

用法:
    from ec_lock import ec_lock
    with ec_lock():
        ...读写 EC...
"""
import ctypes
from ctypes import wintypes

MUTEX_NAME = "Local\\LecooFanEcAccess"
WAIT_OBJECT_0 = 0x00000000
WAIT_ABANDONED = 0x00000080
WAIT_TIMEOUT = 0x00000102

_kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
_kernel32.CreateMutexW.restype = wintypes.HANDLE
_kernel32.CreateMutexW.argtypes = [ctypes.c_void_p, wintypes.BOOL, wintypes.LPCWSTR]
_kernel32.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
_kernel32.WaitForSingleObject.restype = wintypes.DWORD
_kernel32.ReleaseMutex.argtypes = [wintypes.HANDLE]
_kernel32.CloseHandle.argtypes = [wintypes.HANDLE]


class _EcLock(object):
    def __init__(self, timeout_ms=3000):
        self.timeout_ms = timeout_ms
        self.handle = None

    def __enter__(self):
        self.handle = _kernel32.CreateMutexW(None, False, MUTEX_NAME)
        if not self.handle:
            raise OSError("CreateMutexW failed: %d" % ctypes.get_last_error())
        r = _kernel32.WaitForSingleObject(self.handle, self.timeout_ms)
        if r == WAIT_TIMEOUT:
            _kernel32.CloseHandle(self.handle)
            self.handle = None
            raise TimeoutError("EC 锁等不到（%d ms），有别的进程正在访问 EC" % self.timeout_ms)
        if r not in (WAIT_OBJECT_0, WAIT_ABANDONED):
            _kernel32.CloseHandle(self.handle)
            self.handle = None
            raise OSError("WaitForSingleObject 返回 0x%X" % r)
        return self

    def __exit__(self, exc_type, exc, tb):
        if self.handle:
            _kernel32.ReleaseMutex(self.handle)
            _kernel32.CloseHandle(self.handle)
            self.handle = None
        return False


def ec_lock(timeout_ms=3000):
    return _EcLock(timeout_ms)
