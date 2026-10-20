// LecooFan GUI — N175L (Ultra 5 125H / IT5570 EC) CPU & fan tuning console.
//
// Build (uses the .NET Framework compiler that ships with Windows — no installs):
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe ^
//       /out:LecooFanGUI.exe /win32manifest:lecoofan_gui.manifest ^
//       /win32icon:assets\lecoofan.ico /resource:assets\lecoofan.ico,LecooFan.AppIcon.ico ^
//       /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll lecoofan_gui.cs lecoofan_ui.cs
//   (/win32icon 给 exe 自己，/resource 给窗口标题栏+任务栏：WinForms 窗体默认图标是 .NET 那个白板，
//    不显式设 Form.Icon 就永远不会用 exe 的图标。图标由 assets/make_icon.py 从 logo.svg 生成。)
//
// What it does
//   1. CPU modes  : writes the power scheme directly through powrprof (no powercfg windows).
//   2. Fan        : optional software curve that overrides the EC (inpoutx64.dll), same
//                   protocol as lecoofan.py; released back to EC auto control on exit.
//   3. Telemetry  : EC temperature / RPM / PWM + per-core MHz from perf counters.
//   4. Stress test: 10 s all-thread load so you can hear/measure a mode before committing.
//
// Measured facts for this machine (see README "CPU 静音限速"):
//   * PROCTHROTTLEMAX and PROCFREQMAX are IGNORED by this firmware.
//   * Only 处理器性能提升模式 (boost) = 0 and 处理器性能自主模式 (HWP) = 0 are enforced.
//   * Silent load profile: ~2.0 GHz / ~54-68 C / ~2200-2800 RPM (vs 4.3 GHz / 83 C / 4400 RPM).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Management;
using Microsoft.Win32;

namespace LecooFanGui
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Power scheme control (powrprof.dll)
    // ─────────────────────────────────────────────────────────────────────────
    static class Powr
    {
        public static readonly Guid SUB_PROC  = new Guid("54533251-82be-4824-96c1-47b60b740d00");
        public static readonly Guid BOOST     = new Guid("be337238-0d82-4146-a960-4f3749d470c7"); // 处理器性能提升模式
        public static readonly Guid EPP       = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6863"); // 能效偏好
        public static readonly Guid EPP1      = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6864");
        public static readonly Guid EPP2      = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6865");
        public static readonly Guid SYSCOOL   = new Guid("94d3a615-a899-4ac5-ae2b-e4d8f634367f"); // 系统散热方式
        public static readonly Guid MAXST     = new Guid("bc5038f7-23e0-4960-96da-33abaf5935ec"); // 最大处理器状态 (ignored here)
        public static readonly Guid FREQMAX   = new Guid("75b0ae3f-bce0-45a7-8c89-c9611c25e100"); // 最大频率 (ignored here)
        public static readonly Guid FREQMAX1  = new Guid("75b0ae3f-bce0-45a7-8c89-c9611c25e101");
        public static readonly Guid FREQMAX2  = new Guid("75b0ae3f-bce0-45a7-8c89-c9611c25e102");
        public static readonly Guid AUTON     = new Guid("8baa4a8a-14c6-4451-8e8b-14bdbd197537"); // 自主模式 (HWP)

        [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
        [DllImport("powrprof.dll")] static extern uint PowerReadACValueIndex(IntPtr root, ref Guid s, ref Guid sub, ref Guid set, out uint v);
        [DllImport("powrprof.dll")] static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid s, ref Guid sub, ref Guid set, out uint v);
        [DllImport("powrprof.dll")] static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid s, ref Guid sub, ref Guid set, uint v);
        [DllImport("powrprof.dll")] static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid s, ref Guid sub, ref Guid set, uint v);
        [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr h);

        public static Guid ActiveScheme()
        {
            IntPtr p;
            if (PowerGetActiveScheme(IntPtr.Zero, out p) != 0 || p == IntPtr.Zero)
                throw new Exception("PowerGetActiveScheme 失败");
            try { return (Guid)Marshal.PtrToStructure(p, typeof(Guid)); }
            finally { LocalFree(p); }
        }

        public static uint Read(Guid setting, bool ac, out uint value)
        {
            Guid s = ActiveScheme(), sub = SUB_PROC;
            uint rc = ac ? PowerReadACValueIndex(IntPtr.Zero, ref s, ref sub, ref setting, out value)
                         : PowerReadDCValueIndex(IntPtr.Zero, ref s, ref sub, ref setting, out value);
            return rc;
        }

        public static uint ReadAc(Guid setting)
        {
            uint v; Read(setting, true, out v); return v;
        }

        public static uint ReadDc(Guid setting)
        {
            uint v; Read(setting, false, out v); return v;
        }

        /// <summary>Write AC+DC of one setting on the active scheme and re-apply it.</summary>
        public static void Write(Guid setting, uint ac, uint dc)
        {
            Guid s = ActiveScheme(), sub = SUB_PROC;
            uint rc1 = PowerWriteACValueIndex(IntPtr.Zero, ref s, ref sub, ref setting, ac);
            uint rc2 = PowerWriteDCValueIndex(IntPtr.Zero, ref s, ref sub, ref setting, dc);
            if (rc1 != 0 || rc2 != 0)
                throw new Exception(string.Format("写入设置失败 (AC={0}, DC={1})，需要管理员权限？", rc1, rc2));
            PowerSetActiveScheme(IntPtr.Zero, ref s);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  CPU mode table
    // ─────────────────────────────────────────────────────────────────────────
    enum FanPreset { Auto, Silent }

    class CpuMode
    {
        public string Key, Label, Note;
        public uint Boost, EppAc, EppDc, CoolAc, CoolDc, Auton;
        public FanPreset Fan;                       // 点这个模式时顺手配的风扇行为
        public int RpmCap;                          // Fan == Silent 时的转速上限

        public CpuMode(string key, string label, uint boost, uint eppAc, uint eppDc,
                       uint coolAc, uint coolDc, uint auton, string note,
                       FanPreset fan, int rpmCap)
        {
            Key = key; Label = label; Boost = boost; EppAc = eppAc; EppDc = eppDc;
            CoolAc = coolAc; CoolDc = coolDc; Auton = auton; Note = note;
            Fan = fan; RpmCap = rpmCap;
        }

        public void Apply()
        {
            Powr.Write(Powr.BOOST, Boost, Boost);
            Powr.Write(Powr.EPP, EppAc, EppDc);
            Powr.Write(Powr.EPP1, EppAc, EppDc);
            Powr.Write(Powr.EPP2, EppAc, EppDc);
            Powr.Write(Powr.SYSCOOL, CoolAc, CoolDc);
            Powr.Write(Powr.AUTON, Auton, Auton);
            // Keep the knobs this firmware ignores at harmless originals.
            Powr.Write(Powr.MAXST, 99, 99);
            Powr.Write(Powr.FREQMAX, 0, 0);
            Powr.Write(Powr.FREQMAX1, 0, 0);
            Powr.Write(Powr.FREQMAX2, 0, 0);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  EC access (inpoutx64.dll) — IT5570 at port 0x4E, same as lecoofan.py
    // ─────────────────────────────────────────────────────────────────────────
    class Ec
    {
        const string DLL = @"C:\Program Files\LecooControlCenter\inpoutx64.dll";

        [DllImport(DLL)] static extern void Out32(int port, int val);
        [DllImport(DLL)] static extern byte Inp32(int port);
        [DllImport(DLL)] static extern uint IsInpOutDriverOpen();

        public bool Available;
        public string Status = "未连接";
        const int PORT = 0x4E;
        bool dllBroken;

        public Ec()
        {
            // 冷启动时不能先碰 DLL：inpoutx64 的首次打开一旦失败（驱动服务尚未加载），
            // 本进程内再也不会恢复——实测 20 秒内一直返回失败；而先用 SCM 把驱动服务
            // 拉起来再打开，第一次就成功（0 秒）。所以顺序必须是"先起服务，再开驱动"。
            // 2026-10-14 用户报的"开机自启动说未获取管理员权限"就是这个：登录后十几秒
            // 驱动还没加载，旧代码只试一次就永久放弃。这里重试几轮，之后由 UI 定时器补连。
            for (int i = 0; i < 4; i++)
            {
                if (TryOpen()) return;
                if (dllBroken) return;                 // DLL 本身加载不了，重试无意义
                if (i < 3) System.Threading.Thread.Sleep(500);
            }
        }

        /// <summary>尝试（重新）打开 EC 驱动。返回是否可用。</summary>
        public bool TryOpen()
        {
            try
            {
                // 先确保驱动服务在运行。inpoutx64 首开失败后本进程内不会恢复（实测），
                // 所以服务明确起不来时干脆先不碰 DLL，把重试的机会留给后面。
                SvcState svc = EnsureDriverService();
                if (svc == SvcState.Stopped)
                {
                    Available = false;
                    Status = Elevated()
                        ? "inpout 驱动未就绪（自动重试中…）"
                        : "inpout 驱动未加载（需要管理员权限）";
                    return false;
                }
                if (IsInpOutDriverOpen() != 0)
                {
                    Available = true;
                    Status = "已连接 (IT5570 @ 0x4E)";
                    return true;
                }
                Available = false;
                Status = Elevated()
                    ? "inpout 驱动未就绪（自动重试中…）"
                    : "inpout 驱动未加载（需要管理员权限）";
            }
            catch (Exception ex)
            {
                Available = false;
                dllBroken = true;
                Status = "加载 inpoutx64.dll 失败: " + ex.Message;
            }
            return false;
        }

        static bool Elevated()
        {
            try
            {
                using (System.Security.Principal.WindowsIdentity id =
                    System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    return new System.Security.Principal.WindowsPrincipal(id)
                        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
            }
            catch { return false; }
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr OpenSCManager(string machine, string database, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr OpenService(IntPtr scm, string name, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateService(IntPtr scm, string name, string display, uint access,
            uint type, uint startType, uint errControl, string path, string group, IntPtr tag,
            string deps, string account, string password);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool StartService(IntPtr svc, int argc, string[] argv);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool CloseServiceHandle(IntPtr h);

        enum SvcState { Running, Stopped, Unknown }

        [StructLayout(LayoutKind.Sequential)]
        struct SERVICE_STATUS
        {
            public uint type, state, controlsAccepted, win32ExitCode, svcExitCode, checkpoint, waitHint;
        }
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool QueryServiceStatus(IntPtr svc, out SERVICE_STATUS st);

        /// <summary>
        /// 尽力保证 inpoutx64 内核驱动服务处于运行状态（幂等）。
        /// 服务被清理工具删过、但驱动文件还在时，用系统目录的 .sys 重新登记（按需启动）。
        /// 返回服务状态：Running / 存在但没跑起来 / 未知（根本找不到服务）。
        /// </summary>
        static SvcState EnsureDriverService()
        {
            const uint SC_MANAGER_CONNECT = 0x0001, SC_MANAGER_CREATE_SERVICE = 0x0002;
            const uint SERVICE_START = 0x0010, SERVICE_QUERY_STATUS = 0x0004;
            const uint SERVICE_KERNEL_DRIVER = 0x00000001, SERVICE_DEMAND_START = 0x00000003;
            const uint SERVICE_ERROR_NORMAL = 0x00000001, SERVICE_RUNNING = 0x00000004;
            IntPtr scm = IntPtr.Zero, svc = IntPtr.Zero;
            try
            {
                scm = OpenSCManager(null, null, SC_MANAGER_CONNECT | SC_MANAGER_CREATE_SERVICE);
                if (scm == IntPtr.Zero) return SvcState.Unknown;
                svc = OpenService(scm, "inpoutx64", SERVICE_START | SERVICE_QUERY_STATUS);
                if (svc == IntPtr.Zero)
                {
                    string sys = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.System),
                        "drivers\\inpoutx64.sys");
                    if (!System.IO.File.Exists(sys)) return SvcState.Unknown;  // 交给 DLL 自己装
                    svc = CreateService(scm, "inpoutx64", "inpoutx64",
                        SERVICE_START | SERVICE_QUERY_STATUS, SERVICE_KERNEL_DRIVER,
                        SERVICE_DEMAND_START, SERVICE_ERROR_NORMAL, sys, null, IntPtr.Zero, null, null, null);
                    if (svc == IntPtr.Zero) return SvcState.Unknown;
                }
                StartService(svc, 0, null);                    // 已在运行/正在运行都会返回失败，忽略
                SERVICE_STATUS st;
                if (QueryServiceStatus(svc, out st) && st.state == SERVICE_RUNNING)
                    return SvcState.Running;
                return SvcState.Stopped;
            }
            catch { return SvcState.Unknown; }
            finally
            {
                if (svc != IntPtr.Zero) CloseServiceHandle(svc);
                if (scm != IntPtr.Zero) CloseServiceHandle(scm);
            }
        }

        void Out(int p, int v) { Out32(p, v & 0xFF); }
        int In(int p) { return Inp32(p) & 0xFF; }

        void Enter() { int[] seq = { 0x87, 0x01, 0x55, 0x55 }; foreach (int b in seq) Out(PORT, b); }
        void Exit() { Out(PORT, 0x02); Out(PORT + 1, 0x02); }

        // ── EC 事务锁 ────────────────────────────────────────────────────────
        // EC 的 index/data 端口序列（Enter → 写地址 → 读写数据 → Exit）必须整段原子执行，
        // 一旦被插队，EC 看到的地址和数据就是错的，可能写坏状态。
        //   进程内：采样线程（250ms 一次）和 UI 定时器都在读写 EC，不加锁会互相插队。
        //   跨进程：LecooFanGUI 常驻 + CLI（--charge 等）另起进程，也要排队；
        //          厂商 lecoo-ec-daemon 不认这把锁，所以它运行时不要同时跑 CLI。
        // 2026-10-20 那次意外断电（无 1074、无蓝屏、Kernel-Power 41）发生在多进程同时
        // 抢 EC 的那几分钟 —— EC 是主板供电的总管，被写坏它会直接掐电源。所以这把锁是硬要求。
        static readonly object EcSync = new object();
        static readonly Mutex EcCross = NewEcMutex();

        static Mutex NewEcMutex()
        {
            try { return new Mutex(false, @"Local\LecooFanEcAccess"); }
            catch { return null; }
        }

        /// <summary>在锁内跑一次 EC 事务。拿不到跨进程锁就放弃本次操作（宁可少读一次，
        /// 也不能在没有互斥的情况下写 EC —— 那正是把机器写挂的姿势）。</summary>
        T EcCall<T>(Func<T> body)
        {
            bool held = false;
            try
            {
                if (EcCross != null)
                {
                    try { held = EcCross.WaitOne(3000); }
                    catch (AbandonedMutexException) { held = true; }   // 上一个持有者崩了，锁已自动释放
                    if (!held) throw new TimeoutException("EC 正被其它进程占用，本次访问已跳过");
                }
                lock (EcSync) { return body(); }
            }
            finally
            {
                if (held) { try { EcCross.ReleaseMutex(); } catch { } }
            }
        }

        public int ReadReg(int addr)
        {
            return EcCall<int>(delegate
            {
                Enter();
                Out(PORT, 0x2E); Out(PORT + 1, 0x11);
                Out(PORT, 0x2F); Out(PORT + 1, (addr >> 8) & 0xFF);
                Out(PORT, 0x2E); Out(PORT + 1, 0x10);
                Out(PORT, 0x2F); Out(PORT + 1, addr & 0xFF);
                Out(PORT, 0x2E); Out(PORT + 1, 0x12);
                Out(PORT, 0x2F);
                int v = In(PORT + 1);
                Exit();
                return v;
            });
        }

        public void WriteReg(int addr, int val)
        {
            EcCall<int>(delegate
            {
                Enter();
                Out(PORT, 0x2E); Out(PORT + 1, 0x11);
                Out(PORT, 0x2F); Out(PORT + 1, (addr >> 8) & 0xFF);
                Out(PORT, 0x2E); Out(PORT + 1, 0x10);
                Out(PORT, 0x2F); Out(PORT + 1, addr & 0xFF);
                Out(PORT, 0x2E); Out(PORT + 1, 0x12);
                Out(PORT, 0x2F); Out(PORT + 1, val & 0xFF);
                Exit();
                return 0;
            });
        }

        public int ReadRam(int offset) { return ReadReg(0x0400 + offset); }
        public int CpuTemp() { return ReadRam(0x70); }
        public int FanRpm() { return (ReadRam(0x76) << 8) | ReadRam(0x77); }
        public int FanPwm() { return ReadReg(0x1809); }
        public void SetFanPwm(int pwm) { WriteReg(0x1809, pwm & 0xFF); }
        public void ReleaseFan() { WriteReg(0x1809, 0x00); }   // EC resumes auto control

        // ── 电池充电限值（EC FlexiCharger 兼容）────────────────────────────
        // 反向出来的三个寄存器（N175L / IT5570 实测，与联想官方软件同一套）：
        //   0x04B0 bit4 = 限值使能    0x04BB = 停充阈值(%)    0x04BC = 复充阈值(%)
        // 证据：厂商 lecoo-ctrl.exe charge 设成 60% 之后，这三个字节正好是 0x10 / 0x3C / 0x37；
        // 自己直接写这三个字节，厂商 CLI 再回读也是「55% 开始充 / 60% 停充」，两边完全等价。
        // 语义是滞回：电量掉到「复充阈值」以下才继续充，充到「停充阈值」就停。
        public const int REG_CHG_FLAG = 0x04B0, REG_CHG_STOP = 0x04BB, REG_CHG_RESUME = 0x04BC;
        public const int CHG_FLAG_BIT = 0x10;

        /// <summary>写充电限值。stop&lt;=0 表示「不限制」（与厂商 full 档一致：阈值清零）。</summary>
        public void SetChargeLimit(int stop, int resume)
        {
            // 先写阈值再置使能位：使能位一动 EC 就开始按新阈值办事，别让它看到半截值。
            if (stop <= 0)
            {
                WriteReg(REG_CHG_STOP, 0);
                WriteReg(REG_CHG_RESUME, 0);
            }
            else
            {
                WriteReg(REG_CHG_STOP, stop & 0xFF);
                WriteReg(REG_CHG_RESUME, resume & 0xFF);
            }
            // 使能位只在没置位时才写：0x04B0 是位域，别把别的位抹掉，也别反复戳同一个寄存器
            // （反复写 EC 寄存器是 2026-10-20 那次意外断电的嫌疑之一，宁可少写）。
            int flag = ReadReg(REG_CHG_FLAG);
            if ((flag & CHG_FLAG_BIT) == 0) WriteReg(REG_CHG_FLAG, flag | CHG_FLAG_BIT);
        }

        /// <summary>读回充电限值：{停充%, 复充%}。两个都是 0 = 不限制。</summary>
        public int[] ChargeLimit()
        {
            return new int[] { ReadReg(REG_CHG_STOP), ReadReg(REG_CHG_RESUME) };
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Sampler: EC telemetry + (optional) fan curve, one background thread
    // ─────────────────────────────────────────────────────────────────────────
    enum FanMode { Auto, Curve, Silent, Manual }

    class Sampler
    {
        public const int PWM_MIN = 0x18, PWM_MAX = 0x78;   // EC clamps outside this range

        // (temp°C, PWM) — same curve as lecoofan.py, silence first
        static readonly int[,] CURVE = {
            {45, 0x18}, {55, 0x30}, {65, 0x50}, {72, 0x60}, {78, 0x70}, {85, 0x78}
        };

        /// <summary>温度护栏：从 guard 开始每高 1°C 平滑放开 6 级 PWM（不是跳变），宁吵勿热。</summary>
        public static int GuardPwm(int temp, int guard)
        {
            if (temp <= guard) return PWM_MIN;
            int pwm = PWM_MIN + (temp - guard) * 6;
            if (pwm > PWM_MAX) pwm = PWM_MAX;
            return pwm;
        }

        public static int CurvePwm(int temp)
        {
            if (temp <= CURVE[0, 0]) return CURVE[0, 1];
            int n = CURVE.GetLength(0);
            if (temp >= CURVE[n - 1, 0]) return CURVE[n - 1, 1];
            for (int i = 0; i < n - 1; i++)
            {
                int t0 = CURVE[i, 0], p0 = CURVE[i, 1], t1 = CURVE[i + 1, 0], p1 = CURVE[i + 1, 1];
                if (temp >= t0 && temp <= t1)
                {
                    double r = (t1 == t0) ? 0 : (temp - t0) / (double)(t1 - t0);
                    return (int)(p0 + r * (p1 - p0));
                }
            }
            return CURVE[n - 1, 1];
        }

        // --- snapshot read by the UI ---
        public int Temp = -1, Rpm = -1, Pwm = -1;
        public double MaxMhz, AvgMhz, LoadPct;
        public int BaseMhz = 1800;
        public string FreqNote = "";
        public string LastError = "";
        public bool FanTakenOver;
        public int TargetPwm = -1;

        public volatile FanMode Mode = FanMode.Auto;
        public volatile int ManualPwm = 0x30;

        // 静音上限（Silent）：不猜 PWM，直接按实测转速闭环，锁在目标值附近
        public volatile int SilentRpmCap = 2000;
        public volatile int SilentGuardC = 72;      // 超过这个温度就自动放开转速
        public volatile bool SilentLaidOpen;        // 护栏是否正在放开
        int silentPwm = PWM_MIN;                    // 闭环状态：当前写入 0x1809 的值

        Ec ec;
        Thread thread;
        volatile bool run;
        readonly object sync = new object();

        readonly List<PerformanceCounter> coreFreq = new List<PerformanceCounter>();
        PerformanceCounter totalUtil;
        bool countersReady;

        public Sampler(Ec ec) { this.ec = ec; }

        public void Start()
        {
            run = true;
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Start();
        }

        public void Stop()
        {
            run = false;
            if (thread != null) thread.Join(2000);
            ReleaseFan();
        }

        public void ReleaseFan()
        {
            if (ec != null && ec.Available && FanTakenOver)
            {
                try { ec.ReleaseFan(); } catch { }
                FanTakenOver = false;
            }
        }

        void Loop()
        {
            InitCounters();
            while (run)
            {
                try
                {
                    // ---- EC telemetry ----
                    if (ec.Available)
                    {
                        int t = ec.CpuTemp();
                        if (t >= 0 && t <= 120) Temp = t;
                        int p = ec.FanPwm();
                        if (p >= 0) Pwm = p;
                        int r = ec.FanRpm();
                        if (r > 0) Rpm = r;

                        // ---- fan curve / manual override ----
                        FanMode m = Mode;
                        if (m == FanMode.Auto)
                        {
                            if (FanTakenOver) { ec.ReleaseFan(); FanTakenOver = false; TargetPwm = -1; }
                        }
                        else
                        {
                            // 安全网：任何接管模式下温度过高都先把风扇让给 EC（降温后会自动恢复接管）
                            if (Temp >= 92)
                            {
                                if (FanTakenOver) { ec.ReleaseFan(); FanTakenOver = false; }
                                TargetPwm = -1;
                                if (LastError.Length == 0) LastError = "温度达到 92C：已临时交还 EC，降温后自动恢复接管";
                                continue;
                            }
                            int target;
                            if (m == FanMode.Silent)
                            {
                                // ① 转速闭环：实测比上限高就收、低就放（±120 RPM 死区防抖）
                                int cap = SilentRpmCap;
                                if (cap < 1200) cap = 1200;
                                if (cap > 4200) cap = 4200;
                                if (silentPwm < PWM_MIN) silentPwm = PWM_MIN;
                                if (silentPwm > PWM_MAX) silentPwm = PWM_MAX;
                                if (Rpm > 0)
                                {
                                    int err = Rpm - cap;                  // >0 = 比上限还吵
                                    if (err > 120) silentPwm -= (err > 400 ? 3 : 1);
                                    else if (err < -120) silentPwm += (err < -400 ? 3 : 1);
                                }
                                // ② 温度护栏：超温时以护栏曲线为准（比闭环值大就用护栏值），温降后再由闭环收回来
                                int guard = GuardPwm(Temp, SilentGuardC);
                                SilentLaidOpen = Temp > SilentGuardC;
                                if (guard > silentPwm) silentPwm = guard;
                                target = silentPwm;
                            }
                            else target = (m == FanMode.Manual) ? ManualPwm : CurvePwm(Temp);
                            if (target < PWM_MIN) target = PWM_MIN;
                            if (target > PWM_MAX) target = PWM_MAX;
                            ec.SetFanPwm(target);
                            FanTakenOver = true;
                            TargetPwm = target;
                        }
                    }

                    // ---- CPU frequency / load ----
                    // "Actual Frequency" is a real MHz reading and is the only reliable one on
                    // this hybrid CPU: "% Processor Performance" is relative to the highest
                    // core's base clock and reads >150% even with turbo disabled.
                    if (countersReady)
                    {
                        double max = 0, sum = 0;
                        int n = 0;
                        foreach (PerformanceCounter c in coreFreq)
                        {
                            try
                            {
                                double v = c.NextValue();
                                if (v > max) max = v;
                                sum += v; n++;
                            }
                            catch { }
                        }
                        if (n > 0) { MaxMhz = max; AvgMhz = sum / n; }
                        try { if (totalUtil != null) LoadPct = totalUtil.NextValue(); }
                        catch { }
                    }
                }
                catch (Exception ex) { LastError = ex.Message; }
                Thread.Sleep(250);
            }
        }

        void InitCounters()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    if (k != null)
                    {
                        object o = k.GetValue("~MHz");
                        if (o != null) BaseMhz = Convert.ToInt32(o);
                    }
                }
            }
            catch { }

            try
            {
                PerformanceCounterCategory cat = new PerformanceCounterCategory("Processor Information");
                string[] inst = cat.GetInstanceNames();
                foreach (string name in inst)
                {
                    // per-core instances look like "0,0" (group,core); skip the aggregates
                    if (name.IndexOf(',') < 0 || name.IndexOf("_Total") >= 0) continue;
                    try
                    {
                        PerformanceCounter c = new PerformanceCounter("Processor Information", "Actual Frequency", name, true);
                        c.NextValue();
                        coreFreq.Add(c);
                    }
                    catch { }
                }
                try { totalUtil = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total", true); totalUtil.NextValue(); }
                catch { totalUtil = null; }
                countersReady = coreFreq.Count > 0;
                FreqNote = countersReady ? string.Format("{0} 个核心计数器", coreFreq.Count) : "频率计数器不可用";
            }
            catch (Exception ex)
            {
                countersReady = false;
                FreqNote = "性能计数器不可用: " + ex.Message;
            }
        }

        public string Snapshot()
        {
            var sb = new StringBuilder();
            sb.AppendFormat("温度 {0} C | 风扇 {1} RPM | PWM 0x{2:X2} ({2})", Temp, Rpm, Pwm);
            return sb.ToString();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Stress test: N busy threads for a fixed duration
    // ─────────────────────────────────────────────────────────────────────────
    class Stress
    {
        public volatile bool Running;
        public int PeakTemp = -1, PeakRpm = -1;
        public double PeakMhz, SumLoad; public int Samples;

        public void Start(int seconds, Sampler s, Action<string> log, Action done)
        {
            Running = true;
            int threads = Environment.ProcessorCount;
            log(string.Format("压力测试开始：{0} 线程 / {1} 秒", threads, seconds));
            for (int i = 0; i < threads; i++)
            {
                Thread t = new Thread(delegate ()
                {
                    double x = 1.234;
                    Stopwatch sw = Stopwatch.StartNew();
                    while (sw.Elapsed.TotalSeconds < seconds && Running)
                    {
                        for (int j = 0; j < 20000; j++) x = Math.Sqrt(x * 1.0000001 + 3.14159);
                        if (x < 0) x = 1.234;
                    }
                });
                t.IsBackground = true;
                t.Start();
            }
            Thread sampler = new Thread(delegate ()
            {
                Stopwatch sw = Stopwatch.StartNew();
                while (sw.Elapsed.TotalSeconds < seconds)
                {
                    if (s.Temp > PeakTemp) PeakTemp = s.Temp;
                    if (s.Rpm > PeakRpm) PeakRpm = s.Rpm;
                    if (s.MaxMhz > PeakMhz) PeakMhz = s.MaxMhz;
                    SumLoad += s.LoadPct; Samples++;
                    Thread.Sleep(250);
                }
                Running = false;
                Thread.Sleep(1200);
                log(string.Format("测试结束 → 最高核心频率 {0:0} MHz | 最高温度 {1} C | 最高转速 {2} RPM | 平均负载 {3:0}%",
                    PeakMhz, PeakTemp, PeakRpm, Samples > 0 ? SumLoad / Samples : 0));
                done();
            });
            sampler.IsBackground = true;
            sampler.Start();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Main window
    // ─────────────────────────────────────────────────────────────────────────
    class MainForm : Form
    {
        // 模式说明写短一点：每行一条要点，2x2 模式格里两行放得下（格子宽 ~186 设计单位）。
        // 详细说明（EPP/护栏数值、实验档边界）在底部「当前模式」行和「说明 / 边界」对话框里，不在这里重复。
        static readonly CpuMode[] Modes = new CpuMode[]
        {
            new CpuMode("perf",  "高性能",   2,  0,  0, 1, 1, 1, "睿频开 · 风扇 EC 自动\n满载 87°C / ~4400 RPM", FanPreset.Auto, 0),
            new CpuMode("bal",   "平衡(原厂)", 2, 33, 50, 1, 0, 1, "原厂设置 · 风扇 EC 自动\n满载 84°C / ~3400 RPM", FanPreset.Auto, 0),
            new CpuMode("quiet", "安静",     0, 60, 70, 0, 0, 1, "关睿频 · 锁 2000 RPM\n满载 ~65°C / ~1950 RPM", FanPreset.Silent, 2000),
            new CpuMode("deep",  "极静(实验)", 0, 80, 90, 0, 0, 0, "关 HWP · 锁 1600 RPM\n实验档，仅调试用", FanPreset.Silent, 1600),
        };

        // 电池充电限值预设：停充阈值 / 复充阈值（滞回）。
        // 60/55、80/70、95/90、50/40 四组是联想官方 FlexiCharger 自己用的组合，
        // 这里沿用同一套阈值，本机固件才认；40/35 是按同样规则补的一档。
        static readonly string[] ChargeNames = { "不限制 (100%)", "40%", "50%", "60%", "80%", "95%" };
        static readonly int[] ChargeStops = { 0, 40, 50, 60, 80, 95 };
        static readonly int[] ChargeResumes = { 0, 35, 40, 55, 70, 90 };
        const string BattHintText = "写 EC 0x04BB/0x04BC，与联想 FlexiCharger 同一套寄存器。限值存在 EC RAM 里，断电即失效，重新开机自动恢复。";

        static readonly string LogFile = System.IO.Path.Combine(Application.StartupPath, "lecoofan_gui.log");
        static readonly string CfgFile = System.IO.Path.Combine(Application.StartupPath, "lecoofan_gui.cfg");

        // ── 高 DPI 适配 ──
        // 清单里声明了 dpiAware（系统级感知）：Windows 不再位图拉伸，改用真实 DPI 跑窗体。
        // 布局全是绝对坐标，于是按缩放比把坐标整体等比放大（ScaleTree）。
        // 字号是“点”，高 DPI 屏上 Windows 自己会按比例放大 —— 所以出厂点字号一个字节都不动，
        // 坐标和字天然同步放大；谁要是再把字号乘一次缩放比，字就会比布局大一截，全部挤成一团。
        // --dumpui [scale] 的测试模式反过来用：按“目标缩放 / 本机缩放”放大字号，
        // 好在任意 DPI 的机器上精确模拟目标缩放下的最终效果。
        static float DpiScale = 1f;
        static float dpiScaleForTest = 0f;      // >0：--dumpui 指定缩放（测试用，全局覆盖）

        /// <summary>本机真实缩放比（96 DPI = 1）。</summary>
        static float ComputeDpiScale()
        {
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                    return Math.Max(1f, Math.Min(3f, g.DpiX / 96f));
            }
            catch { return 1f; }
        }

        static int S(int v) { return (int)Math.Round(v * DpiScale); }

        /// <summary>主题字体已是成品（出厂点字号 / 或测试模式放大后的），不能再乘一次。</summary>
        static bool IsThemeFont(Font f)
        {
            return f == Ui.FontBase || f == Ui.FontSmall || f == Ui.FontCardTitle
                || f == Ui.FontBig || f == Ui.FontMono;
        }

        static void ScaleTree(Form form, float s, float fontScale)
        {
            foreach (Control c in form.Controls) ScaleOne(c, s, fontScale);
            form.ClientSize = new Size(S(860), S(880));   // 与 BuildUi 里的 860x880 保持一致
        }

        static void ScaleOne(Control c, float s, float fontScale)
        {
            ScaleSubtree(c, s, fontScale);                // 子控件坐标是绝对的，先缩子再缩父
            c.SetBounds(S(c.Left), S(c.Top), S(c.Width), S(c.Height));
            if (fontScale != 1f && c.Font != null && !IsThemeFont(c.Font))
                c.Font = new Font(c.Font.FontFamily, c.Font.Size * fontScale, c.Font.Style);
        }

        static void ScaleSubtree(Control parent, float s, float fontScale)
        {
            foreach (Control c in parent.Controls) ScaleOne(c, s, fontScale);
        }

        Ec ec = new Ec();
        Sampler sampler;
        Stress stress = new Stress();
        string daemonWarning = "";
        bool firstSampleLogged;
        int ecRetryTicks;                          // EC 冷启动补连：每 4 个 UI tick（约 2 秒）重试一次
        int uiTicks;                               // UI 心跳计数（500ms 一格），心跳日志按它计时

        ModeCard[] modeCards;
        Sparkline spark;
        Label lblMode, lblSub, lblTuneHint, lblFanState, lblFreqNote, lblSoakLast;
        Label valTemp, valRpm, valPwm, valMaxMhz, valAvgMhz, valLoad, valSys;
        TrackBar trkEpp, trkPwm;
        Label lblEppVal, lblPwmVal, lblRpmCap;
        CheckBox chkTurbo;
        ComboBox cmbCool, cmbSoakLen, cmbRpmCap;
        RadioButton radFanAuto, radFanCurve, radFanSilent, radFanManual;
        static readonly int[] RpmCaps = { 1600, 1800, 2000, 2200, 2500, 3000 };
        TextBox txtLog;
        Button btnStress, btnSoak, btnFactory;
        // 可缩放窗口：LayoutUi 要重排的控件都留引用（设计坐标仍写在 BuildUi 里，LayoutUi 按比例重算）
        Card cardMode, cardFan, cardTune, cardMon, cardAct, cardSys, cardBatt;
        Label lblEpp, lblCool, lblPwm, lblFanHint, lblSoakHint;
        Label lblBattCap, lblBattState, lblBattHint;
        ComboBox cmbBatt;
        bool battUiReady;                           // 组合框还在装选项时不触发写入
        int chargeLimitStop = -1;                   // -1 = 没配过（绝不碰 EC）；0 = 不限制；>0 = 停充阈值
        bool chargeLimitPending;                   // 配置里有值但 EC 还没连上，连上就补写
        int battSoc = -1;                           // 电量百分比（GetSystemPowerStatus 读）
        bool battCharging = false;                  // 系统认为正在充电（BatteryFlag 的 0x08 位；平滑过的状态位，证据价值低）
        bool battOnAc = false;                      // 交流电在位（ACLineStatus）
        int chargeFixTicks;                         // 限值被改后补写的节流计数
        Button btnApply, btnHelp, btnDir, btnExit;
        readonly List<Label> metricCaps = new List<Label>();
        readonly List<Label> metricVals = new List<Label>();
        bool layoutReady, pendingMaximize;
        bool cliMode;                              // 自检模式：不读也不写用户配置
        Soak soak = new Soak();
        System.Windows.Forms.Timer uiTimer;
        NotifyIcon trayIcon;                       // 托盘模式（--tray / 开机自启动）用
        bool trayMode, allowClose, balloonShown;
        bool trayIconWarned;                       // 托盘图标画失败只记一条日志，不刷屏
        int lastTrayTemp = int.MinValue;
        Size designSize;                           // 缩放后的设计尺寸：窗口被折腾到屏幕外时按它兜底
        static EventWaitHandle showRequest;        // 别的实例发来的「把控制台显示出来」请求
        const string ShowRequestName = "LecooFanGUI.ShowConsole";
        const int ASFW_ANY = -1;
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int dwProcessId);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("user32.dll")] static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        const int WM_SETICON = 0x0080, ICON_SMALL = 0, ICON_BIG = 1;
        const string AppIconResource = "LecooFan.AppIcon.ico";   // build 的 /resource: 标识符，见文件头注释
        Icon appIconBig, appIconSmall;             // 标题栏 + 任务栏用的 Logo（内嵌 assets/lecoofan.ico）
        static readonly DateTime StartedAt = DateTime.Now;       // 心跳日志里的运行时长

        public MainForm()
            : this(false)
        {
        }

        /// <param name="cliMode">命令行自检/脚本模式：不读配置、不碰用户设置</param>
        public MainForm(bool cliMode)
        {
            this.cliMode = cliMode;
            DpiScale = dpiScaleForTest > 0f ? dpiScaleForTest : ComputeDpiScale();
            // 测试模式：字号按“目标缩放 / 本机缩放”放大，等价于真的在目标 DPI 的屏上跑
            float fontScale = dpiScaleForTest > 0f ? DpiScale / ComputeDpiScale() : 1f;
            Ui.SetScaleBaked(DpiScale, fontScale);
            Ui.TouchFonts();                           // BuildUi 之前把主题字体建好
            BuildUi();
            ApplyAppIcon();                            // 标题栏/任务栏换成 Logo（默认是 .NET 那个白板窗体图标）
            ScaleTree(this, DpiScale, fontScale);
            Font = Ui.FontBase;
            LayoutUi();                                // 按实际客户区摆一遍（设计尺寸下与 BuildUi 写的坐标一致）
            layoutReady = true;                        // 之后的 Resize 事件才走 LayoutUi
            designSize = Size;                         // 此刻还是设计尺寸，托盘还原兜底用
            if (!cliMode)
            {
                // 默认开成屏幕中间的小部件（≈ 屏幕面积 1/3，受内容下限钳制）。
                // DefaultClientSize 给的是客户区，赋值给 Size 得把边框标题栏补回去 ——
                // 少补这一下客户区就比下限矮一截，一打开就带滚动条（旧版的毛病）。
                Size cs = DefaultClientSize();
                Size = new Size(cs.Width + (Width - ClientSize.Width),
                                cs.Height + (Height - ClientSize.Height));
            }

            sampler = new Sampler(ec);
            sampler.Start();
            DetectDaemon();
            Log("LecooFan GUI 启动。EC: " + ec.Status
                + (DpiScale != 1f ? string.Format("（DPI 缩放 {0:0.##}×）", DpiScale) : ""));
            if (daemonWarning.Length > 0) Log(daemonWarning);
            RefreshSettingsLine();
            SyncModeButtons();
            if (!cliMode) LoadSettings();          // 恢复上次的 CPU + 风扇配置（托盘启动同样恢复）
            uiTimer = new System.Windows.Forms.Timer();
            uiTimer.Interval = 500;
            uiTimer.Tick += delegate { RefreshUi(); };
            uiTimer.Start();
        }

        // ── 托盘 / 后台运行 ──

        /// <summary>开机自启动/托盘模式：不弹窗口，只在通知区显示图标，配置照样恢复。</summary>
        public void EnableTray()
        {
            trayMode = true;
            BuildTrayIcon();
            ShowInTaskbar = false;
            // 最小化 + 无任务栏按钮的窗口会被 Windows 记到屏幕外的图标位置（-18286,-18286）。
            // 还原时必须先把窗口摆回正常状态再切 ShowInTaskbar，否则那个坏坐标会被当成
            // 「正常位置」写进重建后的句柄 —— 就是 v1.0.1「打不开控制台」的元凶。
            WindowState = FormWindowState.Minimized;
            Log("托盘模式：窗口不自动弹出，双击通知区图标可打开控制台。");
            if (trayIcon != null)
                trayIcon.ShowBalloonTip(5000, "LecooFan 已在后台运行",
                    "控制台已收回托盘。双击图标打开，右键菜单可「交还风扇给 EC」或退出。", ToolTipIcon.Info);
        }

        /// <summary>监听「打开控制台」请求：已有实例时再双击一次快捷方式，由那个实例发来。</summary>
        public void WatchShowRequests()
        {
            try { showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, ShowRequestName); }
            catch { showRequest = null; }   // 名字对象建不出来不影响主功能，只是少这个便利
        }

        void BuildTrayIcon()
        {
            if (trayIcon != null) return;
            trayIcon = new NotifyIcon();
            trayIcon.Icon = SafeFanIcon(-1);
            trayIcon.Text = "LecooFan 控制台";
            trayIcon.Visible = true;
            trayIcon.DoubleClick += delegate { RestoreFromTray(); };
            ContextMenu menu = new ContextMenu();
            menu.MenuItems.Add("打开控制台", delegate { RestoreFromTray(); });
            menu.MenuItems.Add("交还风扇给 EC", delegate { ReleaseFanToEc(); });
            menu.MenuItems.Add("退出", delegate { allowClose = true; Close(); });
            trayIcon.ContextMenu = menu;
        }

        /// <summary>
        /// 托盘图标是装饰品，画不出来也绝不能影响主功能 —— 风扇还在程序手里，界面崩了才是真事故。
        /// 出错只记一条日志（trayIconWarned 保证不刷屏）。
        /// </summary>
        Icon SafeFanIcon(int temp)
        {
            try { return BuildFanIcon(temp); }
            catch (Exception ex)
            {
                if (!trayIconWarned)
                {
                    trayIconWarned = true;
                    Log("错误: 托盘图标绘制失败（已忽略，不影响风扇控制）: " + ex.Message);
                }
                return null;
            }
        }

        void RestoreFromTray()
        {
            // 顺序要紧：先把窗口退出最小化，再切 ShowInTaskbar，最后校正坐标。
            // 反过来的话（旧版就是这么写的），WinForms 会为切换 ShowInTaskbar 重建窗口句柄，
            // 而最小化中的窗口只有任务栏图标位置（没有任务栏按钮就是屏幕外的 -18286,-18286），
            // 那个坏坐标会被当成「正常位置」记进新句柄 —— 窗口"显示成功"却停在屏幕外，
            // 用户看到的就是"打不开控制台"（连任务栏按钮都是点了没反应的）。
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            ShowInTaskbar = true;
            EnsureWindowOnScreen();          // 句柄重建完才是最终坐标，摆正放最后
            Show();
            Activate();
            BringToFront();
            // 前台锁有时只让窗口抬到最上层、不给焦点（SetForegroundWindow 被静默拒绝）。
            // 一步置顶脉冲保证它真的冒到人眼前 —— 用户要求打开控制台时，这就是他要的结果。
            try { TopMost = true; TopMost = false; }
            catch { }
            Log("托盘: 控制台已打开。");
        }

        /// <summary>窗口跑到屏幕外 / 尺寸异常时，按设计尺寸带回主屏居中（托盘还原的兜底）。</summary>
        void EnsureWindowOnScreen()
        {
            Rectangle b = Bounds;
            bool ok = b.Width >= 320 && b.Height >= 320;
            if (ok)
            {
                Rectangle title = new Rectangle(b.X, b.Y, Math.Min(b.Width, 240), 36);   // 标题栏那一条
                ok = false;
                foreach (Screen s in Screen.AllScreens)
                    if (s.WorkingArea.IntersectsWith(title)) { ok = true; break; }
            }
            if (ok) return;
            Size sz = designSize.Width >= 320 ? designSize : new Size(860, 880);
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Size = sz;
            Location = new Point(wa.Left + Math.Max(0, (wa.Width - sz.Width) / 2),
                                 wa.Top + Math.Max(0, (wa.Height - sz.Height) / 2));
            Log("窗口位置或尺寸异常（可能在屏幕外），已按设计尺寸带回主屏居中。");
        }

        void ReleaseFanToEc()
        {
            try
            {
                if (sampler != null) sampler.Mode = FanMode.Auto;
                if (ec != null && ec.Available) ec.ReleaseFan();
                SaveSettings();
                Log("托盘: 风扇已交还 EC 自动控制。");
            }
            catch { }
        }

        // ── 窗口图标（标题栏 + 任务栏）──
        //
        // WinForms 的 Form 默认图标是 .NET 自带的那个白板窗体（wfc.ico），**不是** exe 的 /win32icon，
        // 所以光给 exe 加图标，标题栏和任务栏按钮上还是白板 —— 必须显式把 Logo 塞进 Form.Icon。
        // Logo 是 assets/lecoofan.ico（9 个尺寸的 DIB，见 assets/make_icon.py），由编译参数
        // /resource:...LecooFan.AppIcon.ico 嵌进 exe，和 /win32icon: 用的是同一份文件。

        void ApplyAppIcon()
        {
            byte[] data = ReadAppIconResource();
            if (data == null)
            {
                Log("窗口图标: 没找到内嵌 Logo 资源（exe 编译时漏了 /resource:），标题栏沿用系统默认图标。");
                return;
            }
            try
            {
                appIconBig = new Icon(new System.IO.MemoryStream(data), 32, 32);     // 任务栏 / Alt-Tab
                appIconSmall = new Icon(new System.IO.MemoryStream(data), 16, 16);   // 标题栏左上角
                Icon = appIconBig;
                Log("窗口图标: Logo 已加载（" + IconSizes(data) + " px 共 " + CountIconEntries(data) + " 个尺寸），标题栏/任务栏生效。");
            }
            catch (Exception ex)
            {
                Log("窗口图标: 加载失败，标题栏沿用系统默认图标 — " + ex.Message);
            }
        }

        static byte[] ReadAppIconResource()
        {
            try
            {
                using (System.IO.Stream s = typeof(MainForm).Assembly.GetManifestResourceStream(AppIconResource))
                {
                    if (s == null) return null;
                    byte[] buf = new byte[s.Length];
                    int off = 0;
                    while (off < buf.Length)
                    {
                        int r = s.Read(buf, off, buf.Length - off);
                        if (r <= 0) break;
                        off += r;
                    }
                    return buf;
                }
            }
            catch { return null; }
        }

        /// <summary>读 ICO 目录列出尺寸（顺便验证结构没坏，坏文件在这里就露馅）。</summary>
        static string IconSizes(byte[] ico)
        {
            if (ico == null || ico.Length < 6) return "?";
            int n = CountIconEntries(ico);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < n && 6 + 16 * (i + 1) <= ico.Length; i++)
            {
                int b = ico[6 + 16 * i];
                sb.Append(b == 0 ? "256" : b.ToString());
                if (i < n - 1) sb.Append("/");
            }
            return sb.ToString();
        }

        static int CountIconEntries(byte[] ico)
        {
            return (ico == null || ico.Length < 6) ? 0 : (ico[4] | (ico[5] << 8));
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // WinForms 只会拿 Form.Icon（32px）缩一份给标题栏，边缘发虚；
            // 这里直接用 ICO 里的 16px 条目盖掉 ICON_SMALL，标题栏才是逐像素清晰的。
            if (appIconSmall != null)
                SendMessage(Handle, WM_SETICON, (IntPtr)ICON_SMALL, appIconSmall.Handle);
        }

        /// <summary>进程当前 GDI 对象数 —— 抓句柄泄漏的那把尺子（正常应该稳定不动）。</summary>
        static int GuiObjects()
        {
            try
            {
                using (Process p = Process.GetCurrentProcess())
                    return (int)GetGuiResources(p.Handle, 0);   // 0 = GR_GDIOBJECTS
            }
            catch { return -1; }
        }

        /// <summary>
        /// 托盘 logo：三叶风扇徽标，颜色随温度（凉=品牌蓝/绿，烫=红）。
        /// 注意句柄归属：Bitmap.GetHicon() 出来的 HICON 不归 Icon 对象管，
        /// Icon.FromHandle() 包一层再 Dispose() **不会**销毁它 —— 每次重建漏一个 GDI 句柄，
        /// 一万个上限一到 GetHicon() 就抛「GDI+ 中发生一般性错误」，程序当场崩（实测跑了 12.7 小时踩中）。
        /// 正确姿势：Clone() 拷一份自己持有的图标，然后 DestroyIcon 掉原句柄。
        /// </summary>
        static Icon BuildFanIcon(int temp)
        {
            double t = temp < 0 ? 0.0 : Math.Max(0, Math.Min(1, (temp - 40) / 45.0));
            Color hub = Ui.Blend(Ui.Good, Ui.Bad, 0.15 + t * 0.85);   // 40°C 起渐变，85°C 到红
            Color ring = Ui.Blend(hub, Color.Black, 0.3);
            using (Bitmap bmp = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (SolidBrush blade = new SolidBrush(Color.FromArgb(238, 243, 250)))
                using (SolidBrush hubBrush = new SolidBrush(hub))
                using (Pen ringPen = new Pen(ring, 3f))
                {
                    g.DrawEllipse(ringPen, 2, 2, 29, 29);             // 外圈
                    for (int i = 0; i < 3; i++)                        // 三片扇叶
                    {
                        double a = i * 2 * Math.PI / 3 - Math.PI / 2;
                        float bx = 16 + (float)(Math.Cos(a) * 8), by = 16 + (float)(Math.Sin(a) * 8);
                        g.FillEllipse(blade, bx - 5, by - 5, 10, 10);
                    }
                    g.FillEllipse(hubBrush, 11, 11, 10, 10);           // 轮毂
                    g.FillEllipse(blade, 14, 14, 4, 4);                // 中心铆钉
                }
                IntPtr h = bmp.GetHicon();
                try { return (Icon)Icon.FromHandle(h).Clone(); }        // Clone 出来的 Icon 自己持句柄
                finally { DestroyIcon(h); }                            // 原句柄必须销毁，否则泄漏
            }
        }

        void UpdateTrayIcon()
        {
            if (trayIcon == null || sampler == null) return;
            CpuMode m = CurrentMode();
            string tip = string.Format("LecooFan {0} {1}\u00B0C / {2} RPM",
                m != null ? m.Label : "自定义", sampler.Temp, sampler.Rpm);
            if (tip.Length > 63) tip = tip.Substring(0, 63);
            trayIcon.Text = tip;
            if (sampler.Temp == lastTrayTemp && trayIcon.Tag != null) return;
            lastTrayTemp = sampler.Temp;
            Icon old = trayIcon.Icon;
            Icon fresh = SafeFanIcon(sampler.Temp);
            if (fresh != null)
            {
                trayIcon.Icon = fresh;
                trayIcon.Tag = new object();
                if (old != null) old.Dispose();     // 这份 Icon 自己持句柄，Dispose 真的会释放
            }
        }

        // ── 配置持久化：启动自动恢复上次的 CPU 档位 + 风扇设置 ──

        void SaveSettings()
        {
            if (cliMode) return;                    // --dumpui 等自检路径不许动用户配置
            if (radFanAuto == null) return;         // BuildUi 之前别写
            try
            {
                CpuMode cur = CurrentMode();
                string fan = radFanSilent.Checked ? "silent"
                           : radFanCurve.Checked ? "curve"
                           : radFanManual.Checked ? "manual" : "auto";
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("cpu=" + (cur != null ? cur.Key : "keep"));
                sb.AppendLine("fan=" + fan);
                sb.AppendLine("cap=" + (sampler != null ? sampler.SilentRpmCap : 2000));
                sb.AppendLine("pwm=" + (sampler != null ? sampler.ManualPwm : 0x30));
                sb.AppendLine("epp=" + trkEpp.Value);
                sb.AppendLine("turbo=" + (chkTurbo.Checked ? 1 : 0));
                sb.AppendLine("cool=" + (cmbCool.SelectedIndex == 0 ? 1 : 0));
                // 充电限值：-1 = 没配过（别去动 EC）；0 = 不限制；>0 = 停充阈值
                // 启动早期（还没读到配置）也会走 SaveSettings，那时 chargeLimitStop 还是 -1；
                // 直接写 -1 会把上次配好的限值抹掉，开机补写任务就再也认不出来 —— 保留旧值。
                int charge = chargeLimitStop >= 0 ? chargeLimitStop : ReadCfgCharge();
                sb.AppendLine("charge=" + charge);
                // 窗口大小也记住（托盘/最小化时用还原尺寸，不然下次开机会照搬那个尺寸）。
                // 但「没动过窗口」不写：默认大小是跟屏幕算出来的，写死了换屏幕/换缩放就不自适应了
                // （这条踩过一次：自检留下的 win= 让新默认尺寸失效，一打开就带滚动条）。
                Size ws = WindowState == FormWindowState.Normal ? Size : RestoreBounds.Size;
                if (ws.Width > 0 && ws.Height > 0 && !IsDefaultWindowSize(ws))
                    sb.AppendLine("win=" + ws.Width + "x" + ws.Height);
                sb.AppendLine("max=" + (WindowState == FormWindowState.Maximized ? 1 : 0));
                System.IO.File.WriteAllText(CfgFile, sb.ToString(), Encoding.UTF8);
                // 配置只在用户动作时写（不是每个心跳），所以顺手记一行：排查「改了没生效」时
                // 一眼能看出到底是没写进去，还是写了没读回来
                Log(string.Format("配置已保存: cpu={0} fan={1} cap={2} pwm=0x{3:X2} epp={4} turbo={5} cool={6} charge={7}{8}",
                    cur != null ? cur.Key : "keep", fan, sampler != null ? sampler.SilentRpmCap : 2000,
                    sampler != null ? sampler.ManualPwm : 0x30, trkEpp.Value,
                    chkTurbo.Checked ? 1 : 0, cmbCool.SelectedIndex == 0 ? 1 : 0, charge,
                    sb.ToString().Contains("win=") ? " win=" + ws.Width + "x" + ws.Height : ""));
            }
            catch { }
        }

        void LoadSettings()
        {
            try
            {
                if (!System.IO.File.Exists(CfgFile)) return;
                string cpu = null, fan = null, cap = null, pwm = null, epp = null, turbo = null, cool = null, win = null, max = null, charge = null;
                foreach (string line in System.IO.File.ReadAllLines(CfgFile, Encoding.UTF8))
                {
                    string[] kv = line.Split('=');
                    if (kv.Length != 2) continue;
                    string k = kv[0].Trim(), v = kv[1].Trim();
                    if (k == "cpu") cpu = v;
                    else if (k == "fan") fan = v;
                    else if (k == "cap") cap = v;
                    else if (k == "pwm") pwm = v;
                    else if (k == "epp") epp = v;
                    else if (k == "turbo") turbo = v;
                    else if (k == "cool") cool = v;
                    else if (k == "win") win = v;
                    else if (k == "max") max = v;
                    else if (k == "charge") charge = v;
                }
                int n;

                // 窗口大小/最大化（先夹到「最小尺寸 ~ 主屏工作区」，换屏或改分辨率后不会跑出屏幕）
                if (win != null)
                {
                    string[] wh = win.Split('x');
                    int w, h;
                    if (wh.Length == 2 && int.TryParse(wh[0], out w) && int.TryParse(wh[1], out h))
                    {
                        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                        Size = new Size(Math.Max(MinimumSize.Width, Math.Min(w, wa.Width)),
                                        Math.Max(MinimumSize.Height, Math.Min(h, wa.Height)));
                    }
                }
                pendingMaximize = (max == "1");

                // 微调区滑块的显示状态先恢复（只动 UI，不写电源方案）
                if (epp != null && int.TryParse(epp, out n))
                    trkEpp.Value = Math.Max(0, Math.Min(100, n));
                if (turbo == "1") chkTurbo.Checked = true;
                else if (turbo == "0") chkTurbo.Checked = false;
                if (cool != null && int.TryParse(cool, out n)) cmbCool.SelectedIndex = (n == 1) ? 0 : 1;

                // 1) CPU 档位（连同它配套的风扇预设）
                if (cpu == "perf" || cpu == "bal" || cpu == "quiet" || cpu == "deep")
                {
                    CpuMode m = FindMode(cpu);
                    if (m != null) ApplyMode(m);
                }

                // 2) 风扇：和预设一致就不用动；不一致说明用户后来手动改过，按保存值覆盖
                if (fan == "silent")
                {
                    int r = 2000;
                    if (cap != null && int.TryParse(cap, out n) && n > 0) r = n;
                    cmbRpmCap.SelectedIndex = RpmCapIndex(r);
                    radFanSilent.Checked = true;
                }
                else if (fan == "curve") radFanCurve.Checked = true;
                else if (fan == "manual")
                {
                    if (pwm != null && int.TryParse(pwm, out n))
                        trkPwm.Value = Math.Max(Sampler.PWM_MIN, Math.Min(Sampler.PWM_MAX, n));
                    radFanManual.Checked = true;
                }
                else if (fan == "auto") radFanAuto.Checked = true;

                // 电池充电限值：配置里有就写回 EC（EC RAM 断电即失，重启必须补写）；没有就完全不碰
                if (charge != null && int.TryParse(charge, out n) && n >= 0)
                {
                    chargeLimitStop = n;
                    SyncChargeCombo();
                    if (!cliMode) WriteChargeLimit(n);
                    else chargeLimitPending = false;
                }

                Log(string.Format("已恢复上次配置: cpu={0} fan={1}{2}", cpu ?? "keep", fan ?? "auto",
                    fan == "silent" ? " cap=" + RpmCapValue() + " RPM" : ""));
            }
            catch { }
        }

        void Log(string s)
        {
            string line = string.Format("[{0:HH:mm:ss}] {1}", DateTime.Now, s);
            AppendLogFile(line);
            txtLog.AppendText(line + "\r\n");
            if (txtLog.Lines.Length > 400)
            {
                string[] keep = new string[200];
                Array.Copy(txtLog.Lines, txtLog.Lines.Length - 200, keep, 0, 200);
                txtLog.Lines = keep;
            }
        }

        /// <summary>
        /// 日志文件滚动：超过 1 MB 就把当前文件改名成 .1（旧的 .1 删掉），新日志从头写。
        /// 常驻托盘跑几周，不滚动的话日志会无限长大（排查问题时也不好翻）。
        /// </summary>
        const long LogMaxBytes = 1024 * 1024;
        static void AppendLogFile(string line)
        {
            try
            {
                System.IO.FileInfo fi = new System.IO.FileInfo(LogFile);
                if (fi.Exists && fi.Length > LogMaxBytes)
                {
                    string old = LogFile + ".1";
                    if (System.IO.File.Exists(old)) System.IO.File.Delete(old);
                    System.IO.File.Move(LogFile, old);
                    System.IO.File.AppendAllText(LogFile,
                        string.Format("[{0:HH:mm:ss}] ── 日志超过 1 MB，上一份已滚到 {1} ──\r\n",
                            DateTime.Now, System.IO.Path.GetFileName(old)), Encoding.UTF8);
                }
                System.IO.File.AppendAllText(LogFile, line + "\r\n", Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>
        /// 心跳日志：每 5 分钟一行，把「看一眼就知道有没有变坏」的指标串起来。
        /// 为什么要它：2026-10-14 那次 GDI+ 崩溃（托盘图标句柄泄漏）如果日志里有 GDI 对象数，
        /// 一眼就能看出它从 40 一路爬到一万，而不用等 12.7 小时后弹异常框。
        /// 一行搞定，方便 grep "心跳" 拉趋势。
        /// </summary>
        const int HeartbeatTicks = 600;            // 600 × 500ms = 5 分钟
        void LogHeartbeat()
        {
            string fan;
            if (!ec.Available) fan = "EC 不可用";
            else if (sampler.Mode == FanMode.Silent)
                fan = string.Format("静音上限 {0} (目标 {1}, 实测 {2}{3})",
                    sampler.SilentRpmCap, Hex(sampler.TargetPwm), Hex(sampler.Pwm),
                    sampler.SilentLaidOpen ? ", 护栏已放开" : "");
            else if (sampler.Mode == FanMode.Curve) fan = string.Format("曲线 (目标 {0}, 实测 {1})", Hex(sampler.TargetPwm), Hex(sampler.Pwm));
            else if (sampler.Mode == FanMode.Manual) fan = string.Format("手动 (实测 {0})", Hex(sampler.Pwm));
            else fan = string.Format("EC 自动 (实测 {0})", Hex(sampler.Pwm));
            Log(string.Format("心跳: 温度 {0} / 风扇 {1} / {2} / 最高核心 {3} / 负载 {4} / GDI 对象 {5} / 内存 {6} MB / 已运行 {7:0.0} 小时",
                sampler.Temp >= 0 ? sampler.Temp + "C" : "—",
                sampler.Rpm > 0 ? sampler.Rpm + " RPM" : "—",
                fan,
                sampler.MaxMhz > 0 ? string.Format("{0:0} MHz", sampler.MaxMhz) : "—",
                sampler.LoadPct > 0 ? string.Format("{0:0} %", sampler.LoadPct) : "—",
                GuiObjects(), Environment.WorkingSet / (1024 * 1024),
                (DateTime.Now - StartedAt).TotalHours));
        }

        /// <summary>读不到的寄存器（0xFFFFFFFF）在日志里写成「—」，别让人以为是真值。</summary>
        static string Hex(int v)
        {
            return v >= 0 && v <= 0xFF ? string.Format("0x{0:X2}", v) : "—";
        }

        void BuildUi()
        {
            Text = "LecooFan 控制台 — N175L CPU / 风扇调速";
            ClientSize = new Size(860, 880);          // 设计尺寸：LayoutUi 以它为基准等比重算
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            AutoScaleMode = AutoScaleMode.None;   // 缩放全部由 ScaleTree 手动做，关掉 WinForms 内置的那套
            AutoScroll = true;                    // 缩到布局下限以下时出滚动条，绝不裁切内容
            StartPosition = FormStartPosition.CenterScreen;
            Font = Ui.FontBase;
            BackColor = Ui.WindowBg;
            ForeColor = Ui.Text;
            // 窗口最小尺寸（含边框，所以补上边框厚度）：再小也能用，内容是滚动的
            MinimumSize = new Size(S(560) + (Width - ClientSize.Width),
                                   S(440) + (Height - ClientSize.Height));

            Label title = new Label();
            title.Text = "LecooFan 控制台";
            title.Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold);
            title.ForeColor = Ui.Text;
            title.SetBounds(18, 10, 400, 28);
            Controls.Add(title);

            lblSub = new Label();
            lblSub.SetBounds(18, 36, 820, 16);
            lblSub.ForeColor = Ui.Muted;
            lblSub.Font = Ui.FontSmall;
            Controls.Add(lblSub);

            // ── CPU 模式（卡片式，点击立即生效）──
            cardMode = new Card();
            cardMode.Text = "CPU 模式（点击立即生效）";
            cardMode.SetBounds(16, 56, 408, 212);
            Controls.Add(cardMode);

            modeCards = new ModeCard[Modes.Length];
            for (int i = 0; i < Modes.Length; i++)
            {
                ModeCard c = new ModeCard(Modes[i]);
                c.SetBounds(14 + (i % 2) * 196, 32 + (i / 2) * 74, 186, 66);
                c.Click += ModeClick;
                cardMode.Controls.Add(c);
                modeCards[i] = c;
            }

            lblMode = new Label();
            lblMode.SetBounds(14, 176, 380, 32);
            lblMode.ForeColor = Ui.Accent;
            lblMode.Font = Ui.FontSmall;
            cardMode.Controls.Add(lblMode);

            // ── 微调 ──
            cardTune = new Card();
            cardTune.Text = "微调（自定义）";
            cardTune.SetBounds(16, 456, 408, 168);
            Controls.Add(cardTune);

            lblEpp = new Label(); lblEpp.Text = "EPP"; lblEpp.SetBounds(14, 30, 40, 16); lblEpp.ForeColor = Ui.Muted; cardTune.Controls.Add(lblEpp);
            trkEpp = new TrackBar();
            trkEpp.Minimum = 0; trkEpp.Maximum = 100; trkEpp.TickFrequency = 10;
            trkEpp.SetBounds(54, 24, 200, 45);
            trkEpp.ValueChanged += delegate { lblEppVal.Text = trkEpp.Value.ToString(); };
            cardTune.Controls.Add(trkEpp);
            lblEppVal = new Label(); lblEppVal.SetBounds(260, 30, 50, 16); lblEppVal.ForeColor = Ui.Muted;
            cardTune.Controls.Add(lblEppVal);

            chkTurbo = new CheckBox(); chkTurbo.Text = "睿频 (Boost)"; chkTurbo.SetBounds(14, 80, 150, 20);
            chkTurbo.ForeColor = Ui.Text;
            cardTune.Controls.Add(chkTurbo);

            lblCool = new Label(); lblCool.Text = "散热"; lblCool.SetBounds(180, 82, 40, 16); lblCool.ForeColor = Ui.Muted; cardTune.Controls.Add(lblCool);
            cmbCool = new ComboBox(); cmbCool.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbCool.Items.Add("主动 (先转风扇)");
            cmbCool.Items.Add("被动 (先降频)");
            cmbCool.SetBounds(222, 78, 114, 24);
            cardTune.Controls.Add(cmbCool);

            btnApply = new Button(); btnApply.Text = "应用微调"; btnApply.SetBounds(14, 112, 150, 30);
            Ui.StyleButton(btnApply, Ui.Accent);
            btnApply.Click += delegate { ApplyTune(); };
            cardTune.Controls.Add(btnApply);

            lblTuneHint = new Label();
            lblTuneHint.SetBounds(174, 110, 226, 52);
            lblTuneHint.ForeColor = Ui.Muted;
            lblTuneHint.Font = Ui.FontSmall;
            lblTuneHint.Text = "EPP 越小越偏性能、越大越省电。本机的最大频率/最大状态键被固件忽略（实测无效）。";
            cardTune.Controls.Add(lblTuneHint);

            // ── 电池充电限值（写 EC 0x04BB/0x04BC，和联想 FlexiCharger 同一套寄存器）──
            cardBatt = new Card();
            cardBatt.Text = "电池充电限值";
            cardBatt.SetBounds(16, 632, 408, 112);
            Controls.Add(cardBatt);

            lblBattCap = new Label(); lblBattCap.Text = "充电上限"; lblBattCap.SetBounds(14, 30, 60, 16);
            lblBattCap.ForeColor = Ui.Muted; lblBattCap.Font = Ui.FontSmall;
            cardBatt.Controls.Add(lblBattCap);

            cmbBatt = new ComboBox();
            cmbBatt.DropDownStyle = ComboBoxStyle.DropDownList;
            for (int i = 0; i < ChargeNames.Length; i++) cmbBatt.Items.Add(ChargeNames[i]);
            cmbBatt.SelectedIndex = 0;
            cmbBatt.SetBounds(78, 26, 150, 24);
            cmbBatt.SelectedIndexChanged += delegate { ApplyChargeLimit(); };
            cardBatt.Controls.Add(cmbBatt);

            lblBattState = new Label();
            lblBattState.SetBounds(14, 56, 380, 18);
            lblBattState.ForeColor = Ui.Text;
            lblBattState.AutoEllipsis = true;
            lblBattState.Text = "当前: —";
            cardBatt.Controls.Add(lblBattState);

            lblBattHint = new Label();
            lblBattHint.SetBounds(14, 78, 380, 30);
            lblBattHint.ForeColor = Ui.Muted;
            lblBattHint.Font = Ui.FontSmall;
            lblBattHint.Text = BattHintText;
            cardBatt.Controls.Add(lblBattHint);

            // ── 风扇 ──
            cardFan = new Card();
            cardFan.Text = "风扇控制";
            cardFan.SetBounds(16, 276, 408, 172);
            Controls.Add(cardFan);

            // 四个单选钮用 AutoSize（宽度按文字实测），坐标由 LayoutUi 顺排 ——
            // 旧版写死 x=14/92/176/260 且宽 110，「静音上限」的框铺到 202 压住 x=176 的
            // 「静音曲线」：圆点被盖掉、只剩"音曲线"三个字。宽度写死必然在别的 DPI/字体下复现。
            radFanAuto = new RadioButton(); radFanAuto.Text = "EC 自动"; radFanAuto.AutoSize = true; radFanAuto.SetBounds(14, 26, 74, 20); radFanAuto.Checked = true;
            radFanAuto.ForeColor = Ui.Text;
            radFanAuto.CheckedChanged += delegate { FanModeChanged(); };
            cardFan.Controls.Add(radFanAuto);
            radFanSilent = new RadioButton(); radFanSilent.Text = "静音上限"; radFanSilent.AutoSize = true; radFanSilent.SetBounds(92, 26, 110, 20);
            radFanSilent.ForeColor = Ui.Text;
            radFanSilent.CheckedChanged += delegate { FanModeChanged(); };
            cardFan.Controls.Add(radFanSilent);
            radFanCurve = new RadioButton(); radFanCurve.Text = "静音曲线"; radFanCurve.AutoSize = true; radFanCurve.SetBounds(176, 26, 80, 20);
            radFanCurve.ForeColor = Ui.Text;
            radFanCurve.CheckedChanged += delegate { FanModeChanged(); };
            cardFan.Controls.Add(radFanCurve);
            radFanManual = new RadioButton(); radFanManual.Text = "手动 PWM"; radFanManual.AutoSize = true; radFanManual.SetBounds(260, 26, 110, 20);
            radFanManual.ForeColor = Ui.Text;
            radFanManual.CheckedChanged += delegate { FanModeChanged(); };
            cardFan.Controls.Add(radFanManual);

            // 静音上限的参数：目标转速（模式卡点「安静/极静」会自动带上，也可在这里手动改）
            lblRpmCap = new Label(); lblRpmCap.Text = "转速上限"; lblRpmCap.SetBounds(14, 80, 58, 16);
            lblRpmCap.ForeColor = Ui.Muted; lblRpmCap.Font = Ui.FontSmall;
            cardFan.Controls.Add(lblRpmCap);
            cmbRpmCap = new ComboBox();
            cmbRpmCap.DropDownStyle = ComboBoxStyle.DropDownList;
            foreach (int r in RpmCaps) cmbRpmCap.Items.Add(r + " RPM");
            cmbRpmCap.SelectedIndex = 2;                       // 2000 RPM（安静档默认，静音已贴 EC 地板）
            cmbRpmCap.SetBounds(76, 76, 100, 22);
            cmbRpmCap.Enabled = false;
            cmbRpmCap.SelectedIndexChanged += delegate { ApplyRpmCap(); };
            cardFan.Controls.Add(cmbRpmCap);

            lblFanState = new Label();
            lblFanState.SetBounds(14, 48, 380, 26);            // 两行：第二行留给护栏/守护进程提示
            lblFanState.ForeColor = Ui.Muted;
            lblFanState.Font = Ui.FontSmall;
            lblFanState.Text = "状态: —";
            cardFan.Controls.Add(lblFanState);

            lblPwm = new Label(); lblPwm.Text = "PWM"; lblPwm.SetBounds(14, 100, 40, 16); lblPwm.ForeColor = Ui.Muted; cardFan.Controls.Add(lblPwm);
            trkPwm = new TrackBar();
            trkPwm.Minimum = Sampler.PWM_MIN; trkPwm.Maximum = Sampler.PWM_MAX;
            trkPwm.Value = 0x30; trkPwm.TickFrequency = 8; trkPwm.SetBounds(54, 96, 220, 38);
            trkPwm.ValueChanged += delegate
            {
                sampler.ManualPwm = trkPwm.Value;
                lblPwmVal.Text = string.Format("0x{0:X2}", trkPwm.Value);
            };
            cardFan.Controls.Add(trkPwm);
            lblPwmVal = new Label(); lblPwmVal.SetBounds(282, 100, 60, 16); lblPwmVal.ForeColor = Ui.Muted;
            lblPwmVal.Text = "0x30";
            cardFan.Controls.Add(lblPwmVal);

            lblFanHint = new Label();
            lblFanHint.SetBounds(14, 138, 380, 32);
            lblFanHint.ForeColor = Ui.Warn;
            lblFanHint.Font = Ui.FontSmall;
            lblFanHint.Text = "静音上限：锁住转速（EC 下限 ~1700 RPM，比这更低锁不住）\n" +
                      "护栏 72°C 自动放开转速；关窗口 = 交还 EC 退出";
            cardFan.Controls.Add(lblFanHint);

            // ── 实时监控（大号读数 + 趋势图）──
            cardMon = new Card();
            cardMon.Text = "实时监控";
            cardMon.SetBounds(436, 56, 408, 300);
            Controls.Add(cardMon);

            valTemp = AddMetric(cardMon, "CPU 温度", 14, 30, 180, 21F, Ui.Good);
            valRpm = AddMetric(cardMon, "风扇转速", 204, 30, 190, 21F, Ui.Accent);
            valMaxMhz = AddMetric(cardMon, "最高核心频率", 14, 100, 180, 13F, Ui.Text);
            valAvgMhz = AddMetric(cardMon, "平均频率", 204, 100, 190, 13F, Ui.Text);
            valPwm = AddMetric(cardMon, "风扇 PWM", 14, 146, 180, 12F, Ui.Text);
            valLoad = AddMetric(cardMon, "CPU 负载", 204, 146, 190, 12F, Ui.Text);

            spark = new Sparkline();
            spark.SetBounds(14, 200, 380, 76);
            cardMon.Controls.Add(spark);

            lblFreqNote = new Label();
            lblFreqNote.SetBounds(14, 276, 380, 15);
            lblFreqNote.ForeColor = Ui.Muted;
            lblFreqNote.Font = Ui.FontSmall;
            lblFreqNote.AutoEllipsis = true;      // 文字是运行期填的，一行放不下就省略号，别撑破卡片
            cardMon.Controls.Add(lblFreqNote);

            // ── 系统设置回读条 ──
            cardSys = new Card();
            cardSys.Text = "系统设置回读";
            cardSys.SetBounds(16, 632, 828, 64);
            Controls.Add(cardSys);

            valSys = new Label();
            valSys.SetBounds(14, 26, 800, 34);
            valSys.ForeColor = Ui.Text;
            cardSys.Controls.Add(valSys);

            // ── 操作 / 测试 ──
            cardAct = new Card();
            cardAct.Text = "操作 / 测试";
            cardAct.SetBounds(436, 364, 408, 252);
            Controls.Add(cardAct);

            btnStress = new Button();
            btnStress.Text = "压力测试 10 秒";
            btnStress.SetBounds(14, 30, 180, 36);
            Ui.StyleButton(btnStress, Ui.Accent);
            btnStress.Click += delegate { RunStress(); };
            cardAct.Controls.Add(btnStress);

            cmbSoakLen = new ComboBox();
            cmbSoakLen.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbSoakLen.Items.Add("1 分钟");
            cmbSoakLen.Items.Add("2 分钟");
            cmbSoakLen.Items.Add("5 分钟");
            cmbSoakLen.Items.Add("10 分钟");
            cmbSoakLen.SelectedIndex = 1;
            cmbSoakLen.SetBounds(204, 34, 88, 26);
            cardAct.Controls.Add(cmbSoakLen);

            btnSoak = new Button();
            btnSoak.Text = "稳定性长跑";
            btnSoak.SetBounds(300, 30, 94, 36);
            Ui.StyleButton(btnSoak, Ui.Good);
            btnSoak.Click += delegate { RunSoak(); };
            cardAct.Controls.Add(btnSoak);

            btnFactory = new Button();
            btnFactory.Text = "恢复原厂设置";
            btnFactory.SetBounds(14, 78, 180, 34);
            Ui.StyleButton(btnFactory, Ui.Muted);
            btnFactory.Click += delegate { ApplyMode(Modes[1]); };
            cardAct.Controls.Add(btnFactory);

            btnHelp = new Button();
            btnHelp.Text = "说明 / 边界";
            btnHelp.SetBounds(204, 78, 190, 34);
            Ui.StyleButton(btnHelp, Ui.Muted);
            btnHelp.Click += delegate { ShowHelp(); };
            cardAct.Controls.Add(btnHelp);

            btnDir = new Button();
            btnDir.Text = "打开项目目录";
            btnDir.SetBounds(14, 122, 180, 34);
            Ui.StyleButton(btnDir, Ui.Muted);
            btnDir.Click += delegate
            {
                try { Process.Start("explorer.exe", Application.StartupPath); } catch { }
            };
            cardAct.Controls.Add(btnDir);

            btnExit = new Button();
            btnExit.Text = "退出（并交还风扇）";
            btnExit.SetBounds(204, 122, 190, 34);
            Ui.StyleButton(btnExit, Ui.Bad);
            btnExit.Click += delegate { Close(); };
            cardAct.Controls.Add(btnExit);

            lblSoakHint = new Label();
            lblSoakHint.SetBounds(14, 166, 380, 44);
            lblSoakHint.ForeColor = Ui.Muted;
            lblSoakHint.Font = Ui.FontSmall;
            lblSoakHint.Text = "长跑 = 混合负载（整数/浮点/内存/随机空转）+ 每秒采样，结束时给出温度平台化、" +
                               "风扇抖动、设置漂移等判据，并输出 soak_*.csv 与 soak_*.txt。";
            cardAct.Controls.Add(lblSoakHint);

            lblSoakLast = new Label();
            lblSoakLast.SetBounds(14, 214, 380, 32);
            lblSoakLast.ForeColor = Ui.Good;
            lblSoakLast.Font = Ui.FontSmall;
            lblSoakLast.Text = "最近一次长跑: 尚未运行";
            cardAct.Controls.Add(lblSoakLast);

            txtLog = new TextBox();
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.BackColor = Ui.LogBg;
            txtLog.ForeColor = Ui.LogText;
            txtLog.BorderStyle = BorderStyle.None;
            txtLog.SetBounds(24, 706, 812, 48);
            txtLog.Font = Ui.FontMono;
            Controls.Add(txtLog);
        }

        Label AddMetric(Control parent, string caption, int x, int y, int w, float size, Color color)
        {
            Label cap = new Label();
            cap.Text = caption;
            cap.Font = Ui.FontSmall;
            cap.ForeColor = Ui.Muted;
            cap.SetBounds(x, y, w, 15);
            parent.Controls.Add(cap);

            Label val = new Label();
            val.Font = new Font("Microsoft YaHei UI", size, FontStyle.Bold);
            val.ForeColor = color;
            val.SetBounds(x, y + 14, w, (int)(size * 2.3F));
            val.Text = "—";
            parent.Controls.Add(val);
            // 记下来：窗口缩放时按「左列/右列」两栏重新分配宽度（LayoutMonCard）
            metricCaps.Add(cap);
            metricVals.Add(val);
            return val;
        }

        // ── 可缩放窗口：布局按当前客户区重算 ──
        // 设计尺寸 860x760 下，下面每条公式都精确还原 BuildUi 里写的坐标（注释里就是验算）；
        // 放大：两列平分横向增量，纵向增量按设计比例分给卡片（日志最多吃 1/4 增量），底部两张卡钉在底边；
        // 缩小：每张卡有自己的内容下限，先保底再把余量按比例分；缩到布局下限为止，再小交给窗体滚动条
        // —— 任何尺寸都不裁切、不重叠，放不下的小字提示直接隐藏（不硬塞、不切字）。

        const int DESIGN_W = 860, DESIGN_H = 880;
        // 设计高度（左列 mode/fan/tune 与右列 mon/act）：
        //   风扇卡从 172 提到 184 —— 原生 TrackBar 有最小高度（175% 下 80px ≈ 46 设计单位，
        //   比设计写的 38 大），172 会让 PWM 滑块压住底部提示条；
        //   模式卡 200→208 —— 2x2 模式格的说明是两行，格子高到 67 设计单位才放得下，
        //   差值从微调卡匀出来（168→160，它的下限只有 145），三张卡合计仍是 552，窗口设计尺寸不变。
        const int DESIGN_MODE_H = 208, DESIGN_FAN_H = 184, DESIGN_TUNE_H = 160, DESIGN_BATT_H = 112;
        const int DESIGN_MON_H = 300, DESIGN_ACT_H = 252;
        // 布局下限（设计单位）：再小就滚动，不再压缩内容。667 是左列三卡下限 + 缝 + 底部两卡的合计
        // （130+184+145+16 缝 +56 标题 +8 +64 回读 +10 +48 日志 +6 = 667），
        // 700 留 33 设计单位余量 —— 那点余量正好让模式格在默认窗口下也放得下两行说明。
        const int FLOOR_W = 750, FLOOR_H = 820;
        // 各卡内容下限（设计单位）：低于这个高度内容就得重叠，所以由 FlexHeights 保底
        const int MIN_MODE_H = 130, MIN_FAN_H = 184, MIN_TUNE_H = 145, MIN_BATT_H = 96;
        const int MIN_MON_H = 200, MIN_ACT_H = 200;    // 操作卡 200：三排按钮 156 + 底部结果行 38 不打架

        void LayoutUi()
        {
            int W = Math.Max(ClientSize.Width, S(FLOOR_W));
            int H = Math.Max(ClientSize.Height, S(FLOOR_H));
            AutoScrollMinSize = new Size(W, H);

            int margin = S(16), gap = S(8), topY = S(56);

            // 横向：左列 408、右列 408、中间 12，窗口变宽两列平分
            int leftW = S(408) + (W - S(DESIGN_W)) / 2;
            int rightX = margin + leftW + S(12);
            int rightW = W - margin - rightX;

            // 单选钮先顺排（一行放不下会换行），换行后风扇卡要多占高度
            int fanDy = LayoutFanRadios(leftW);

            // 纵向：日志框贴底（窗口变高最多分它 1/4 增量），系统回读卡压在日志上面
            int logH = S(48) + Math.Max(0, H - S(DESIGN_H)) / 4;
            int logY = H - S(6) - logH;
            int sysY = logY - S(10) - S(64);

            // 两列的高度预算（设计：左列底 624 / 右列底 616，各自离回读卡 8 / 16）
            int lSpace = (sysY - S(8)) - topY - gap * 3;    // 四张卡 + 三道缝
            int rSpace = (sysY - S(16)) - topY - gap;       // 两张卡 + 一道缝

            int[] lh = FlexHeights(lSpace,
                new int[] { S(DESIGN_MODE_H), S(DESIGN_FAN_H), S(DESIGN_TUNE_H), S(DESIGN_BATT_H) },
                new int[] { S(MIN_MODE_H), S(MIN_FAN_H) + fanDy, S(MIN_TUNE_H), S(MIN_BATT_H) });
            int[] rh = FlexHeights(rSpace,
                new int[] { S(DESIGN_MON_H), S(DESIGN_ACT_H) },
                new int[] { S(MIN_MON_H), S(MIN_ACT_H) });
            int modeH = lh[0], fanH = lh[1], tuneH = lh[2], battH = lh[3], monH = rh[0], actH = rh[1];

            int y = topY;
            cardMode.SetBounds(margin, y, leftW, modeH); y = cardMode.Bottom + gap;
            cardFan.SetBounds(margin, y, leftW, fanH);   y = cardFan.Bottom + gap;
            cardTune.SetBounds(margin, y, leftW, tuneH); y = cardTune.Bottom + gap;
            cardBatt.SetBounds(margin, y, leftW, battH);
            y = topY;
            cardMon.SetBounds(rightX, y, rightW, monH);  y = cardMon.Bottom + gap;
            cardAct.SetBounds(rightX, y, rightW, actH);
            cardSys.SetBounds(margin, sysY, W - margin * 2, S(64));
            txtLog.SetBounds(S(24), logY, W - S(48), logH);
            lblSub.SetBounds(S(18), S(36), W - S(36), S(16));

            LayoutModeCard();
            LayoutFanCard(fanDy);
            LayoutTuneCard();
            LayoutBattCard();
            LayoutMonCard();
            LayoutActCard();
            valSys.SetBounds(S(14), S(26), cardSys.ClientSize.Width - S(28), S(34));
        }

        /// <summary>
        /// 按设计高度比例分配 total，但每张卡不低于内容下限：不够的卡先抬到下限，
        /// 余量再按设计比例分给其余的（迭代到稳定）。调用方保证 total ≥ 各下限之和。
        /// </summary>
        static int[] FlexHeights(int total, int[] design, int[] min)
        {
            int n = design.Length;
            int[] h = new int[n];
            bool[] fixedMin = new bool[n];
            for (int pass = 0; pass < 4; pass++)
            {
                int free = total, dsum = 0;
                for (int i = 0; i < n; i++)
                    if (fixedMin[i]) free -= h[i]; else dsum += design[i];
                bool changed = false;
                for (int i = 0; i < n; i++)
                {
                    if (fixedMin[i]) continue;
                    h[i] = dsum > 0 ? free * design[i] / dsum : 0;
                    if (h[i] < min[i]) { h[i] = min[i]; fixedMin[i] = true; changed = true; }
                }
                if (!changed) break;
            }
            return h;
        }

        /// <summary>
        /// 默认窗口大小（客户区）：屏幕工作区面积的 1/3，保持设计宽高比 —— 开出来就是屏幕中间一个小部件
        /// （本机 2560x1516 工作区 → 1386x1225 设备像素，占 43.8% 面积，居中）。
        /// 下限是紧凑下限（比它再小就该出滚动条了），上限是设计尺寸 —— 内容摆不下时以摆得下为准，
        /// 所以是「≈1/3」而不是精确 1/3；用户拖过窗口尺寸后配置里的 win= 优先，这里只负责第一次打开。
        /// </summary>
        Size DefaultClientSize()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;        // 设备像素（manifest 声明了 dpiAware）
            double k = Math.Sqrt((double)wa.Width * wa.Height / 3.0 / ((double)S(DESIGN_W) * S(DESIGN_H)));
            k = Math.Max(k, Math.Max((double)S(FLOOR_W) / S(DESIGN_W), (double)S(FLOOR_H) / S(DESIGN_H)));
            k = Math.Min(k, 1.0);
            return new Size((int)Math.Round(S(DESIGN_W) * k), (int)Math.Round(S(DESIGN_H) * k));
        }

        /// <summary>当前外框尺寸就是「默认开窗尺寸」吗（差 4px 以内算没动过，写配置时用来跳过 win=）。</summary>
        bool IsDefaultWindowSize(Size outer)
        {
            Size cs = DefaultClientSize();
            Size def = new Size(cs.Width + (Width - ClientSize.Width),
                                cs.Height + (Height - ClientSize.Height));
            return Math.Abs(outer.Width - def.Width) <= 4 && Math.Abs(outer.Height - def.Height) <= 4;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (layoutReady) LayoutUi();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // 上次退出时是最大化就还原（托盘模式不弹窗口，别去抢前台）
            if (pendingMaximize && !trayMode) WindowState = FormWindowState.Maximized;
        }

        /// <summary>
        /// 风扇卡四个单选钮：按实测文字宽度顺排，一行放不下自动换行。返回换行多占的高度。
        /// 宽度写死是「静音曲线」被前一个按钮压住的根因（见 BuildUi 里的说明）。
        /// </summary>
        int LayoutFanRadios(int cardW)
        {
            RadioButton[] rads = { radFanAuto, radFanSilent, radFanCurve, radFanManual };
            int x = S(14), y = S(26), maxX = cardW - S(14);
            foreach (RadioButton r in rads)
            {
                Size ps = r.PreferredSize;
                if (x > S(14) && x + ps.Width > maxX) { x = S(14); y += S(24); }
                r.SetBounds(x, y, ps.Width, ps.Height);
                x += ps.Width + S(12);
            }
            return y - S(26);
        }

        /// <summary>
        /// 多行提示条放不放得下（按实测文字高度）：放不下就整条隐藏，宁可少一条提示也不切字。
        /// 注意 LayoutUi 在窗体 Show 之前就会跑，那时控件的 Visible 读出来恒为 false，
        /// 所以判断结果必须用返回值，不能回头去读控件属性。
        /// </summary>
        static bool HintFits(Label l, int w, int h)
        {
            if (w < 40 || h < 20) return false;
            return Ui.TextFits(l.Text, l.Font, w, h);   // 和模式卡说明、布局自检同一把尺子
        }

        /// <summary>CPU 模式卡：2x2 模式格 + 底部当前模式说明（卡片太矮就把说明藏了，模式格吃掉全部高度）。</summary>
        void LayoutModeCard()
        {
            int cw = cardMode.ClientSize.Width, ch = cardMode.ClientSize.Height;
            int cwEach = (cw - S(26) - S(10)) / 2;                    // 内边距 14/12 + 中缝 10 → 设计 186
            bool showLbl = ch >= S(140);                              // 设计 200：说明留 36
            int lblY = showLbl ? ch - S(36) : ch;                     // 设计 164
            int chEach = Math.Max((lblY - S(4) - S(32) - S(8)) / 2, S(30));   // 设计 66；30 是一行标题的下限
            for (int i = 0; i < modeCards.Length; i++)
                modeCards[i].SetBounds(S(14) + (i % 2) * (cwEach + S(10)),
                                       S(32) + (i / 2) * (chEach + S(8)), cwEach, chEach);
            lblMode.Visible = showLbl;
            if (showLbl) lblMode.SetBounds(S(14), lblY, cw - S(28), S(32));
        }

        /// <summary>风扇卡：单选钮（外部已排好）+ 转速上限 + PWM 滑块 + 底部提示。</summary>
        void LayoutFanCard(int dy)
        {
            int cw = cardFan.ClientSize.Width, ch = cardFan.ClientSize.Height;
            lblFanState.SetBounds(S(14), S(48) + dy, cw - S(28), S(26));
            lblRpmCap.SetBounds(S(14), S(80) + dy, S(58), S(16));
            cmbRpmCap.SetBounds(S(76), S(76) + dy, S(100), S(22));
            lblPwm.SetBounds(S(14), S(100) + dy, S(40), S(16));
            lblPwmVal.SetBounds(cw - S(126), S(100) + dy, S(60), S(16));          // 设计 282
            trkPwm.SetBounds(S(54), S(96) + dy, lblPwmVal.Left - S(8) - S(54), S(38));  // 设计 220
            // 原生 TrackBar 比请求的高（本机 175% 下 80px），提示条要排在它下面，不然被压住
            int hintY = Math.Max(trkPwm.Bottom + S(4), ch - S(38));
            int hintH = ch - hintY - S(4);
            bool showHint = HintFits(lblFanHint, cw - S(28), hintH);
            lblFanHint.Visible = showHint;
            if (showHint) lblFanHint.SetBounds(S(14), hintY, cw - S(28), hintH);
        }

        /// <summary>微调卡：EPP 滑块拉长，底部说明随卡片高度伸展（放不下就藏掉说明）。</summary>
        void LayoutTuneCard()
        {
            int cw = cardTune.ClientSize.Width, ch = cardTune.ClientSize.Height;
            lblEpp.SetBounds(S(14), S(30), S(40), S(16));
            lblEppVal.SetBounds(cw - S(148), S(30), S(50), S(16));                // 设计 260
            trkEpp.SetBounds(S(54), S(24), lblEppVal.Left - S(8) - S(54), S(45));
            chkTurbo.SetBounds(S(14), S(80), S(150), S(20));
            lblCool.SetBounds(S(180), S(82), S(40), S(16));
            cmbCool.SetBounds(S(222), S(78), S(114), S(24));
            btnApply.SetBounds(S(14), S(112), S(150), S(30));
            int hintW = cw - S(188), hintH = ch - S(116);                         // 设计 226x52
            bool showHint = HintFits(lblTuneHint, hintW, hintH);
            lblTuneHint.Visible = showHint;
            if (showHint) lblTuneHint.SetBounds(S(174), S(110), hintW, hintH);
        }

        /// <summary>电池卡：上限下拉 + 状态回读 + 说明（说明放不下就整条隐藏，宁可少一句也不切字）。</summary>
        void LayoutBattCard()
        {
            int cw = cardBatt.ClientSize.Width, ch = cardBatt.ClientSize.Height;
            lblBattCap.SetBounds(S(14), S(30), S(60), S(16));
            cmbBatt.SetBounds(S(78), S(26), S(150), S(24));
            lblBattState.SetBounds(S(14), S(56), cw - S(28), S(18));
            int hintY = S(78);
            int hintH = ch - hintY - S(6);
            bool showHint = HintFits(lblBattHint, cw - S(28), hintH);
            lblBattHint.Visible = showHint;
            if (showHint) lblBattHint.SetBounds(S(14), hintY, cw - S(28), hintH);
        }

        /// <summary>实时监控卡：读数两栏平分宽度，趋势图吃掉全部纵向余量（太矮就藏趋势图和脚注）。</summary>
        void LayoutMonCard()
        {
            int cw = cardMon.ClientSize.Width, ch = cardMon.ClientSize.Height;
            int dx = cw - S(408);
            for (int i = 0; i < metricCaps.Count; i++)
            {
                int col = i % 2, row = i / 2;
                // 两栏平分横向增量（设计 14 / 204）：增量只给右栏的话，窄窗口下右栏会压住左栏
                int x = col == 0 ? S(14) : S(204) + dx / 2;
                int w = col == 0 ? S(180) + dx / 2 : cw - S(14) - x;
                int y = row == 0 ? S(30) : (row == 1 ? S(100) : S(146));
                metricCaps[i].SetBounds(x, y, w, S(15));
                metricVals[i].SetBounds(x, y + S(14), w, metricVals[i].Height);
            }
            bool showNote = ch >= S(236);                                         // 设计 276
            lblFreqNote.Visible = showNote;
            if (showNote) lblFreqNote.SetBounds(S(14), ch - S(24), cw - S(28), S(15));
            bool showSpark = ch >= S(248);                                        // 设计 76 高，窗口变高就长高
            spark.Visible = showSpark;
            if (showSpark) spark.SetBounds(S(14), S(200), cw - S(28), ch - S(224));
        }

        /// <summary>操作卡：按钮两列贴边对齐，窄窗口下左边的按钮自动收窄（放不下就藏长跑说明）。</summary>
        void LayoutActCard()
        {
            int cw = cardAct.ClientSize.Width, ch = cardAct.ClientSize.Height;
            int x2 = cw - S(14) - S(94);                                          // 设计 300
            int x1 = x2 - S(8) - S(88);                                           // 设计 204
            btnSoak.SetBounds(x2, S(30), S(94), S(36));
            cmbSoakLen.SetBounds(x1, S(34), S(88), S(26));
            btnStress.SetBounds(S(14), S(30), x1 - S(10) - S(14), S(36));          // 设计 180
            btnHelp.SetBounds(x1, S(78), cw - S(14) - x1, S(34));                  // 设计 190
            btnFactory.SetBounds(S(14), S(78), x1 - S(10) - S(14), S(34));
            btnExit.SetBounds(x1, S(122), btnHelp.Width, S(34));
            btnDir.SetBounds(S(14), S(122), x1 - S(10) - S(14), S(34));
            // 说明排在三排按钮下面、最后一行长跑结果上面，中间的空隙放不下就整条隐藏
            int hintTop = btnDir.Bottom + S(4);
            int hintAvail = (ch - S(38)) - hintTop - S(4);
            bool showHint = HintFits(lblSoakHint, cw - S(28), hintAvail);
            lblSoakHint.Visible = showHint;
            if (showHint) lblSoakHint.SetBounds(S(14), hintTop, cw - S(28), hintAvail);
            lblSoakLast.SetBounds(S(14), ch - S(38), cw - S(28), S(32));           // 设计 214：贴底
        }

        void DetectDaemon()
        {
            try
            {
                string pidFile = System.IO.Path.Combine(Application.StartupPath, "lecoofan.pid");
                if (System.IO.File.Exists(pidFile))
                {
                    string txt = System.IO.File.ReadAllText(pidFile).Trim();
                    int pid;
                    if (int.TryParse(txt, out pid))
                    {
                        try
                        {
                            Process pr = Process.GetProcessById(pid);
                            if (pr != null && !pr.HasExited)
                                daemonWarning = string.Format("注意: python 守护进程 lecoofan.py 正在运行 (pid={0})，它会和本程序的静音曲线抢 EC；建议先停掉它。", pid);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        void ModeClick(object sender, EventArgs e)
        {
            ModeCard c = sender as ModeCard;
            if (c == null) return;
            ApplyMode(c.Mode);
        }

        void ApplyMode(CpuMode m)
        {
            try
            {
                Cursor = Cursors.WaitCursor;
                m.Apply();
                ApplyFanPreset(m);
                Log(string.Format("模式 → {0}  ({1})", m.Label, m.Note));
                SyncModeButtons();
                RefreshSettingsLine();
                SaveSettings();
            }
            catch (Exception ex) { Log("应用失败: " + ex.Message); }
            finally { Cursor = Cursors.Default; }
        }

        /// <summary>当前电源方案匹配到哪个已知档位（自定义设置返回 null）。</summary>
        CpuMode CurrentMode()
        {
            uint boost = Powr.ReadAc(Powr.BOOST);
            uint auton = Powr.ReadAc(Powr.AUTON);
            uint epp = Powr.ReadAc(Powr.EPP);
            foreach (CpuMode m in Modes)
                if (m.Boost == boost && m.Auton == auton && m.EppAc == epp) return m;
            return null;
        }

        void SyncModeButtons()
        {
            uint boost = Powr.ReadAc(Powr.BOOST);
            uint auton = Powr.ReadAc(Powr.AUTON);
            uint epp = Powr.ReadAc(Powr.EPP);
            CpuMode cur = CurrentMode();
            for (int i = 0; i < Modes.Length; i++)
            {
                modeCards[i].Active = (cur == Modes[i]);
                modeCards[i].Invalidate();
            }
            if (spark != null) spark.ClearHistory();
            lblMode.Text = cur != null
                ? string.Format("当前: {0} — {1}", cur.Label, cur.Note)
                : string.Format("当前: 自定义/其他 (Boost={0}, EPP={1}, 自主={2})", boost, epp, auton);
        }

        /// <summary>
        /// 自检：依次点击 4 张模式卡，验证每套「CPU + 风扇」整套预设都落地
        /// （电源键回读 + sampler 风扇模式/转速上限 + 单选钮状态），
        /// 每档观察 seconds 秒采样温/转速，结束后还原初始档位并交还 EC。
        /// </summary>
        public string SelfTestModes(int seconds)
        {
            StringBuilder r = new StringBuilder();
            CpuMode before = CurrentMode();
            r.AppendLine(string.Format("before: cpu={0} boost={1} epp={2} cool={3}",
                before != null ? before.Key : "custom", Powr.ReadAc(Powr.BOOST), Powr.ReadAc(Powr.EPP), Powr.ReadAc(Powr.SYSCOOL)));

            int pass = 0;
            foreach (CpuMode m in Modes)
            {
                ModeClick(modeCards[Array.IndexOf(Modes, m)], EventArgs.Empty);   // 走真实点击链路
                Application.DoEvents();
                Thread.Sleep(300);                       // 等 Apply 后的回读稳定

                uint boost = Powr.ReadAc(Powr.BOOST), epp = Powr.ReadAc(Powr.EPP), auton = Powr.ReadAc(Powr.AUTON);
                FanMode wantFan = m.Fan == FanPreset.Auto ? FanMode.Auto : FanMode.Silent;
                bool ok = boost == m.Boost && epp == m.EppAc && auton == m.Auton
                       && sampler.Mode == wantFan
                       && (wantFan != FanMode.Silent || sampler.SilentRpmCap == m.RpmCap)
                       && radFanSilent.Checked == (wantFan == FanMode.Silent);
                if (ok) pass++;
                r.AppendLine(string.Format("click {0,-5} → boost={1} epp={2} auton={3} | fan={4} cap={5} | temp={6}C rpm={7} : {8}",
                    m.Key, boost, epp, auton, sampler.Mode, sampler.SilentRpmCap,
                    sampler.Temp, sampler.Rpm, ok ? "PASS" : "FAIL"));

                Stopwatch sw = Stopwatch.StartNew();
                double sumT = 0, sumR = 0; int n = 0, maxT = -1, maxR = -1;
                while (sw.Elapsed.TotalSeconds < seconds)
                {
                    Application.DoEvents();
                    Thread.Sleep(250);
                    sumT += sampler.Temp > 0 ? sampler.Temp : 0; sumR += sampler.Rpm > 0 ? sampler.Rpm : 0; n++;
                    if (sampler.Temp > maxT) maxT = sampler.Temp;
                    if (sampler.Rpm > maxR) maxR = sampler.Rpm;
                    r.AppendLine(string.Format("  t={0:0.0}s mode={1} temp={2}C rpm={3} target=0x{4:X2} pwm=0x{5:X2} taken={6}",
                        sw.Elapsed.TotalSeconds, sampler.Mode, sampler.Temp, sampler.Rpm,
                        sampler.TargetPwm, sampler.Pwm, sampler.FanTakenOver));
                }
                r.AppendLine(string.Format("  {0}: {1}s 均温 {2:0.0}C 最高 {3}C | 均转 {4:0}RPM 最高 {5}RPM",
                    m.Label, seconds, n > 0 ? sumT / n : 0, maxT, n > 0 ? sumR / n : 0, maxR));
            }

            radFanAuto.Checked = true;                         // 交还 EC
            Application.DoEvents();
            Thread.Sleep(800);
            if (before != null && CurrentMode() != before)
            {
                before.Apply();
                ApplyFanPreset(before);
                r.AppendLine("restored cpu+fan preset: " + before.Key);
            }
            else r.AppendLine(before != null ? "cpu 未被动过，风扇已交还 EC" : "cpu 是自定义设置，未碰；风扇已交还 EC");
            r.AppendLine(string.Format("MODES: {0} ({1}/{2} 档位整套预设落地)",
                pass == Modes.Length ? "PASS" : "FAIL", pass, Modes.Length));
            return r.ToString();
        }

        /// <summary>
        /// 自检：走一遍用户点「静音上限」的真实事件链（转速下拉 → 单选 → 闭环），
        /// 断言全程不碰 CPU 电源方案，结束后交还 EC，并把测试前的 CPU 档位还原回去。
        /// </summary>
        public string SelfTestSilent(int idx, int seconds)
        {
            StringBuilder r = new StringBuilder();
            if (idx < 0 || idx >= RpmCaps.Length) idx = 1;
            CpuMode before = CurrentMode();
            r.AppendLine(string.Format("before: cpu={0} boost={1} epp={2} cool={3}",
                before != null ? before.Key : "custom", Powr.ReadAc(Powr.BOOST), Powr.ReadAc(Powr.EPP), Powr.ReadAc(Powr.SYSCOOL)));

            cmbRpmCap.SelectedIndex = idx;                     // → ApplyRpmCap
            radFanSilent.Checked = true;                       // → FanModeChanged（只进闭环，不碰电源方案）
            Application.DoEvents();
            bool cpuUntouched = before == null ? CurrentMode() == null : CurrentMode() == before;
            r.AppendLine(string.Format("clicked silent: cap={0} sliderDisabled={1} cpuUntouched={2} cpuNow={3}",
                sampler.SilentRpmCap, !trkPwm.Enabled, cpuUntouched,
                (CurrentMode() != null ? CurrentMode().Key : "custom")));

            Stopwatch sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                Application.DoEvents();
                Thread.Sleep(200);
                r.AppendLine(string.Format("t={0:0.0}s mode={1} cap={2} temp={3}C rpm={4} target=0x{5:X2} pwm=0x{6:X2} taken={7} | {8}",
                    sw.Elapsed.TotalSeconds, sampler.Mode, sampler.SilentRpmCap, sampler.Temp, sampler.Rpm,
                    sampler.TargetPwm, sampler.Pwm, sampler.FanTakenOver, lblFanState.Text));
            }

            radFanAuto.Checked = true;                         // 交还 EC
            Application.DoEvents();
            Thread.Sleep(800);
            r.AppendLine(string.Format("after auto: mode={0} taken={1} rpm={2}", sampler.Mode, sampler.FanTakenOver, sampler.Rpm));
            if (before != null && CurrentMode() != before)
            {
                before.Apply();
                ApplyFanPreset(before);
                r.AppendLine("restored cpu+fan preset: " + before.Key);
            }
            else r.AppendLine(before != null ? "cpu 未被动过，无需还原" : "cpu 是自定义设置，不碰电源方案");
            r.AppendLine(cpuUntouched ? "SILENT: PASS (电源方案未被切换)" : "SILENT: FAIL (电源方案被切换了)");
            return r.ToString();
        }

        static Color ModeColor(string key)
        {
            switch (key)
            {
                case "perf": return Color.FromArgb(255, 226, 200);
                case "bal": return Color.FromArgb(224, 236, 255);
                case "quiet": return Color.FromArgb(214, 245, 220);
                default: return Color.FromArgb(206, 232, 240);
            }
        }

        void ApplyTune()
        {
            try
            {
                Cursor = Cursors.WaitCursor;
                uint epp = (uint)trkEpp.Value, boost = chkTurbo.Checked ? (uint)2 : (uint)0;
                uint cool = (uint)(cmbCool.SelectedIndex == 0 ? 1 : 0);
                Powr.Write(Powr.EPP, epp, epp);
                Powr.Write(Powr.EPP1, epp, epp);
                Powr.Write(Powr.EPP2, epp, epp);
                Powr.Write(Powr.BOOST, boost, boost);
                Powr.Write(Powr.SYSCOOL, cool, cool);
                Log(string.Format("微调已应用: EPP={0}, 睿频={1}, 散热={2}", epp, boost == 0 ? "关" : "开", cool == 1 ? "主动" : "被动"));
                SyncModeButtons();
                RefreshSettingsLine();
                SaveSettings();
            }
            catch (Exception ex) { Log("微调失败: " + ex.Message); }
            finally { Cursor = Cursors.Default; }
        }

        int RpmCapValue()
        {
            int i = cmbRpmCap.SelectedIndex;
            return RpmCaps[i < 0 || i >= RpmCaps.Length ? 1 : i];
        }

        /// <summary>转速上限值 → 下拉框下标（表里没有就退回 1800）。</summary>
        int RpmCapIndex(int rpm)
        {
            for (int i = 0; i < RpmCaps.Length; i++)
                if (RpmCaps[i] == rpm) return i;
            return 1;
        }

        /// <summary>按模式预设配置风扇。点模式卡 = CPU + 风扇整套预设，
        /// 之后手动改风扇算微调，再点模式会按预设重置风扇。</summary>
        void ApplyFanPreset(CpuMode m)
        {
            if (m.Fan == FanPreset.Auto)
            {
                radFanAuto.Checked = true;                   // → FanModeChanged 交还 EC
            }
            else
            {
                cmbRpmCap.SelectedIndex = RpmCapIndex(m.RpmCap);   // → ApplyRpmCap
                radFanSilent.Checked = true;                 // → FanModeChanged 进闭环
            }
        }

        void ApplyRpmCap()
        {
            if (sampler == null) return;
            sampler.SilentRpmCap = RpmCapValue();
            if (radFanSilent.Checked) Log(string.Format("风扇: 转速上限 → {0} RPM", sampler.SilentRpmCap));
            SaveSettings();
        }

        void FanModeChanged()
        {
            if (sampler == null) return;
            bool silent = radFanSilent.Checked;
            cmbRpmCap.Enabled = silent;
            trkPwm.Enabled = !silent;
            if (silent)
            {
                if (daemonWarning.Length > 0) Log("警告: " + daemonWarning);
                sampler.SilentRpmCap = RpmCapValue();
                sampler.Mode = FanMode.Silent;
                Log(string.Format("风扇: 静音上限 {0} RPM（转速闭环，护栏 {1}°C）", sampler.SilentRpmCap, sampler.SilentGuardC));
            }
            else
            {
                if (radFanCurve.Checked)
                {
                    if (daemonWarning.Length > 0) Log("警告: " + daemonWarning);
                    sampler.Mode = FanMode.Curve;
                    Log("风扇: 接管 EC，使用静音曲线");
                }
                else if (radFanManual.Checked)
                {
                    sampler.Mode = FanMode.Manual;
                    sampler.ManualPwm = trkPwm.Value;
                    Log(string.Format("风扇: 手动 PWM 0x{0:X2}", trkPwm.Value));
                }
                else
                {
                    sampler.Mode = FanMode.Auto;
                    Log("风扇: 交还 EC 自动控制");
                }
            }
            SaveSettings();
        }

        void RunStress()
        {
            if (stress.Running) { Log("压力测试进行中…"); return; }
            if (soak.Running) { Log("稳定性长跑进行中，请等它结束。"); return; }
            btnStress.Enabled = false;
            stress = new Stress();
            stress.Start(10, sampler, Log, delegate { btnStress.Enabled = true; });
        }

        static readonly int[] SoakSeconds = { 60, 120, 300, 600 };

        void RunSoak()
        {
            if (soak.Running) { Log("稳定性长跑已在进行中。"); return; }
            if (stress.Running) { Log("压力测试进行中，请等它结束。"); return; }
            int seconds = SoakSeconds[Math.Max(0, Math.Min(SoakSeconds.Length - 1, cmbSoakLen.SelectedIndex))];
            string label = lblMode.Text.Length > 0 ? lblMode.Text : "当前档位";
            FanMode fan = radFanSilent.Checked ? FanMode.Silent
                : (radFanCurve.Checked ? FanMode.Curve : (radFanManual.Checked ? FanMode.Manual : FanMode.Auto));
            btnSoak.Enabled = false;
            btnStress.Enabled = false;
            Thread t = new Thread(delegate ()
            {
                try
                {
                    soak.Run(seconds, sampler, fan, label, new Action<string>(SoakLog), delegate
                    {
                        try
                        {
                            BeginInvoke(new MethodInvoker(delegate
                            {
                                btnSoak.Enabled = true;
                                btnStress.Enabled = true;
                                lblSoakLast.Text = "最近一次长跑: " + LastSoakLine(soak.Verdict) +
                                    "\r\n文件: " + System.IO.Path.GetFileName(soak.SummaryPath) + " / " + System.IO.Path.GetFileName(soak.CsvPath);
                                MessageBox.Show(this, soak.Verdict + "\r\n\r\n文件：\r\n" + soak.SummaryPath + "\r\n" + soak.CsvPath,
                                    "稳定性长跑结果");
                            }));
                        }
                        catch { }
                    });
                }
                catch (Exception ex) { SoakLog("长跑失败: " + ex.Message); }
            });
            t.IsBackground = true;
            t.Start();
        }

        static string LastSoakLine(string verdict)
        {
            foreach (string raw in verdict.Split('\n'))
            {
                string s = raw.Trim();
                if (s.StartsWith("温度")) return s;
            }
            return "已完成";
        }

        // Soak 在后台线程里，日志要切回 UI 线程
        void SoakLog(string s)
        {
            try
            {
                if (InvokeRequired) { BeginInvoke(new Action<string>(SoakLog), new object[] { s }); return; }
                Log(s);
            }
            catch { }
        }

        void ShowHelp()
        {
            MessageBox.Show(this,
                "本程序做的事：\r\n" +
                "1) 直接改写当前电源方案里的 CPU 设置（睿频、EPP、散热方式、HWP 自主模式）。\r\n" +
                "   点「CPU 模式」卡片 = CPU 设置 + 配套风扇一次配好：高性能/平衡 → 风扇 EC 自动；\r\n" +
                "   安静 → 静音上限 1800 RPM；极静 → 1600 RPM。之后手动改风扇算微调，再点模式会按预设重置。\r\n" +
                "2) 可选接管 EC 风扇（写 0x1809），退出时自动交还。\r\n" +
                "   风扇控制共四种：「EC 自动」不动手；「静音上限」按实测转速闭环，锁在选定 RPM 附近；\r\n" +
                "   「静音曲线」按温度查表（45→0x18 / 55→0x30 / 65→0x50 / 72→0x60 / 78→0x70 / 85→0x78）；\r\n" +
                "   「手动 PWM」固定写一个值。接管时每 250ms 写一次 0x1809。\r\n" +
                "   安全边界：EC 自身下限约 1700 RPM（写更低的 PWM 会被抬回来，所以 1800 附近就是地板）；\r\n" +
                "   「静音上限」自带 72°C 护栏，超温自动放开转速，温度≥92°C 一律交还 EC。\r\n" +
                "3) 只读取 EC 温度/转速/PWM，以及 Windows 性能计数器的每核频率。\r\n\r\n" +
                "本机的实测结论（14 进程满载 / 同一跑分）：\r\n" +
                "· 「最大处理器状态」和「处理器最大频率」被固件忽略（设 50% 仍跑到 4.2GHz）。\r\n" +
                "· 真正生效的是「关闭睿频」：满载 84C→71C，~3400→~3200 RPM。\r\n" +
                "· 代价：单核算力约慢 1.9 倍（0.35s→0.66s），全核只慢 ~15%（1.87s→2.22s）。\r\n" +
                "· 「极静」关掉 HWP 自主模式后反而更热更吵（79C / ~4600RPM），不建议日常用。\r\n" +
                "· 频率读数是 Actual Frequency，混合架构上偏保守，判断快慢以跑分为准。\r\n\r\n" +
                "命令行等价做法：quiet_cpu.bat / restore_cpu.bat（同样是改电源方案）。",
                "说明 / 边界");
        }

        void RefreshSettingsLine()
        {
            try
            {
                uint boost = Powr.ReadAc(Powr.BOOST), boostDc = Powr.ReadDc(Powr.BOOST);
                uint epp = Powr.ReadAc(Powr.EPP), eppDc = Powr.ReadDc(Powr.EPP);
                uint cool = Powr.ReadAc(Powr.SYSCOOL), coolDc = Powr.ReadDc(Powr.SYSCOOL);
                uint auton = Powr.ReadAc(Powr.AUTON), mst = Powr.ReadAc(Powr.MAXST), fmax = Powr.ReadAc(Powr.FREQMAX);
                string[] boostNames = { "关", "开", "主动", "激进", "高效开", "高效激进" };
                string bn = boost < boostNames.Length ? boostNames[boost] : boost.ToString();
                string bd = boostDc < boostNames.Length ? boostNames[boostDc] : boostDc.ToString();
                valSys.Text = string.Format(
                    "睿频 {0}/{1} · EPP {2}/{3} · 散热 {4}/{5}\r\n自主模式 {6} · 最大状态 {7} · 最大频率 {8}",
                    bn, bd, epp, eppDc, cool == 1 ? "主动" : "被动", coolDc == 1 ? "主动" : "被动",
                    auton == 1 ? "开" : "关", mst == 0 ? "不限" : mst + "%", fmax == 0 ? "不限" : fmax + "MHz");
                lblSub.Text = string.Format("EC: {0}    ·    当前电源方案 {1}    ·    管理员运行    ·    静音曲线与 python 守护进程不能同时接管 EC",
                    ec.Status, Powr.ActiveScheme().ToString().Substring(0, 8));
                lblSub.ForeColor = ec.Available ? Ui.Muted : Ui.Bad;
                chkTurbo.Checked = (boost != 0);
                trkEpp.Value = (int)(epp > 100 ? 100 : epp);
                cmbCool.SelectedIndex = (cool == 1) ? 0 : 1;
            }
            catch (Exception ex)
            {
                valSys.Text = "读取失败: " + ex.Message;
            }
        }

        void RefreshUi()
        {
            if (sampler == null) return;
            // 别的实例请求「打开控制台」（双击快捷方式 / 再运行一次 exe）：
            // 搭在这条 500ms 的 UI 心跳上处理，不用另开线程
            if (showRequest != null)
            {
                try { if (showRequest.WaitOne(0)) RestoreFromTray(); }
                catch { }
            }
            // EC 冷启动补连：构造时那几轮重试还没连上就每 ~2 秒再试，直到连上为止
            if (!ec.Available && ++ecRetryTicks >= 4)
            {
                ecRetryTicks = 0;
                if (ec.TryOpen())
                {
                    Log("EC 已连接（冷启动重试成功，温度/风扇控制恢复工作）");
                    sampler.LastError = "";
                    if (chargeLimitStop >= 0) WriteChargeLimit(chargeLimitStop);   // 补写充电限值
                }
            }
            if (!firstSampleLogged && sampler.Temp >= 0)
            {
                firstSampleLogged = true;
                Log(string.Format("首帧遥测: 温度 {0}C / 风扇 {1} RPM / PWM 0x{2:X2} / 最高核心 {3:0} MHz",
                    sampler.Temp, sampler.Rpm, sampler.Pwm, sampler.MaxMhz));
            }
            valTemp.Text = sampler.Temp >= 0 ? sampler.Temp + " \u00B0C" : "—";
            valTemp.ForeColor = Ui.TempColor(sampler.Temp);
            valRpm.Text = sampler.Rpm > 0 ? sampler.Rpm + " RPM" : "—";
            valPwm.Text = sampler.Pwm >= 0 ? string.Format("0x{0:X2} ({0})", sampler.Pwm) : "—";
            // 静音模式下 PWM 滑块不许手动调，改成实时显示闭环正在写的值
            if (radFanSilent != null && radFanSilent.Checked && sampler.TargetPwm >= 0)
                lblPwmVal.Text = string.Format("闭环 0x{0:X2}", sampler.TargetPwm);
            valMaxMhz.Text = sampler.MaxMhz > 0 ? string.Format("{0:0} MHz", sampler.MaxMhz) : "—";
            valAvgMhz.Text = sampler.AvgMhz > 0 ? string.Format("{0:0} MHz", sampler.AvgMhz) : "—";
            valLoad.Text = sampler.LoadPct > 0 ? string.Format("{0:0} %", sampler.LoadPct) : "—";
            if (spark != null) spark.Add(sampler.Temp, sampler.Rpm, sampler.Pwm);
            lblFreqNote.Text = sampler.FreqNote.Length > 0 ? sampler.FreqNote : "";
            if (sampler.LastError.Length > 0) { Log("错误: " + sampler.LastError); sampler.LastError = ""; }
            if (++uiTicks % HeartbeatTicks == 0) LogHeartbeat();
            if (uiTicks % 10 == 0)                 // 每 5 秒刷一次电量 + 限值回读
            {
                UpdateBatterySoc();
                RefreshBattState();
            }
            RefreshSettingsLine();
            RefreshFanState();
            UpdateTrayIcon();
        }

        // ── 电池充电限值 ─────────────────────────────────────────────────
        /// <summary>停充阈值 → 复充阈值（滞回）。预设表里有就用预设的，没有就按 stop-5 推。</summary>
        static int ResumeFor(int stop)
        {
            if (stop <= 0) return 0;
            for (int i = 0; i < ChargeStops.Length; i++) if (ChargeStops[i] == stop) return ChargeResumes[i];
            return Math.Max(0, stop - 5);
        }

        int ChargeIndexFor(int stop)
        {
            for (int i = 0; i < ChargeStops.Length; i++) if (ChargeStops[i] == stop) return i;
            return -1;
        }

        /// <summary>把下拉框同步到当前限值（装选项时不触发写入）。</summary>
        void SyncChargeCombo()
        {
            if (cmbBatt == null) return;
            battUiReady = false;
            try
            {
                int idx = ChargeIndexFor(chargeLimitStop);
                if (idx < 0 && chargeLimitStop > 0)
                {
                    for (int i = cmbBatt.Items.Count - 1; i >= 0; i--)
                        if (cmbBatt.Items[i].ToString().StartsWith("自定义")) cmbBatt.Items.RemoveAt(i);
                    cmbBatt.Items.Add("自定义 (" + chargeLimitStop + "%)");
                    idx = cmbBatt.Items.Count - 1;
                }
                cmbBatt.SelectedIndex = idx < 0 ? 0 : idx;
            }
            finally { battUiReady = true; }
        }

        /// <summary>真正写 EC：stop&lt;=0 表示不限制。写完立刻回读确认。</summary>
        void WriteChargeLimit(int stop)
        {
            if (!ec.Available) { chargeLimitPending = true; return; }
            int resume = ResumeFor(stop);
            try
            {
                ec.SetChargeLimit(stop, resume);
                chargeLimitPending = false;
                int[] rb = ec.ChargeLimit();
                Log(string.Format("充电限值: 停充 {0}% / 复充 {1}%（EC 回读 {2}/{3}）",
                    stop, resume, rb[0], rb[1]));
            }
            catch (Exception ex) { Log("写充电限值失败: " + ex.Message); }
        }

        /// <summary>下拉框变了 → 立刻生效（和风扇/电源那几档一样，改了就生效）。</summary>
        void ApplyChargeLimit()
        {
            if (!battUiReady || cliMode || cmbBatt == null) return;
            int idx = cmbBatt.SelectedIndex;
            if (idx < 0 || idx >= ChargeStops.Length) return;
            chargeLimitStop = ChargeStops[idx];
            WriteChargeLimit(chargeLimitStop);
            SaveSettings();
            RefreshBattState();
        }

        /// <summary>联想官方 EC 守护进程在跑的话，它会按自己的状态文件改限值，得提醒一句。</summary>
        static bool VendorDaemonRunning()
        {
            try { return Process.GetProcessesByName("lecoo-ec-daemon").Length > 0; }
            catch { return false; }
        }

        /// <summary>回读 EC 里的限值并显示；配置值和 EC 对不上时补写回去（每 30 秒最多一次）。</summary>
        void RefreshBattState()
        {
            if (lblBattState == null) return;
            if (!ec.Available)
            {
                lblBattState.Text = "EC 未连接 —— 限值暂时写不进去";
                lblBattState.ForeColor = Ui.Bad;
                return;
            }
            try
            {
                int[] rb = ec.ChargeLimit();
                string soc = battSoc >= 0 ? battSoc + "%" : "—";
                string extra = VendorDaemonRunning() ? "  ⚠ 联想官方 EC 守护进程在跑，可能覆盖限值" : "";
                if (rb[0] <= 0)
                {
                    lblBattState.Text = "当前: 不限制（充到 100%）· 电量 " + soc + extra;
                    lblBattState.ForeColor = Ui.Muted;
                }
                else
                {
                    lblBattState.Text = string.Format("当前: 停充 {0}% · 复充 {1}% · 电量 {2}{3}",
                        rb[0], rb[1], soc, extra);
                    lblBattState.ForeColor = Ui.Good;
                }
                // 限值写了固件到底执不执行，只能看行为，而且必须有真实充电电流才有意义
                // （Windows 报的 charging 标志是平滑过的，插电恒为真，不能当证据）。
                if (lblBattHint != null)
                {
                    AcpiBattery ab = AcpiBattery.Get();
                    bool reallyCharging = ab.Ok && ab.ChargeRate > 0;
                    if (rb[0] > 0 && battOnAc && ab.Ok && ab.ChargeRate <= 0 && ab.DischargeRate <= 0)
                    {
                        lblBattHint.Text = "插着电，但真实电流 0 mW：充电器本身没在工作，跟限值无关。"
                            + "先救活充电器（拔插适配器 / 放电唤醒）再验证限值。";
                        lblBattHint.ForeColor = Ui.Bad;
                    }
                    else if (rb[0] > 0 && reallyCharging && battSoc >= rb[0])
                    {
                        lblBattHint.Text = "⚠ 已到限值仍在充电（真实电流 " + ab.ChargeRate +
                            " mW）：限值没被执行。";
                        lblBattHint.ForeColor = Ui.Bad;
                    }
                    else if (rb[0] > 0 && reallyCharging && battSoc >= 0 && battSoc < rb[0])
                    {
                        lblBattHint.Text = "正在充电（" + ab.ChargeRate + " mW），等电量涨到 " +
                            rb[0] + "% 时看是否停充。";
                        lblBattHint.ForeColor = Ui.Muted;
                    }
                    else if (rb[0] > 0 && battSoc >= 0 && battSoc < rb[0])
                    {
                        lblBattHint.Text = "限值已写入 EC，等电量涨到 " + rb[0] + "% 时看是否停充。";
                        lblBattHint.ForeColor = Ui.Muted;
                    }
                    else
                    {
                        lblBattHint.Text = BattHintText;
                        lblBattHint.ForeColor = Ui.Muted;
                    }
                }
                if (chargeLimitStop >= 0 && !chargeLimitPending && !VendorDaemonRunning())
                {
                    int wantResume = ResumeFor(chargeLimitStop);
                    if ((rb[0] != chargeLimitStop || rb[1] != wantResume) && ++chargeFixTicks >= 6)
                    {
                        chargeFixTicks = 0;
                        Log(string.Format("EC 限值与配置不符（{0}/{1} vs {2}/{3}），已补写",
                            rb[0], rb[1], chargeLimitStop, wantResume));
                        WriteChargeLimit(chargeLimitStop);
                    }
                }
            }
            catch (Exception ex) { lblBattState.Text = "读取限值失败: " + ex.Message; }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }
        [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

        /// <summary>ACPI 真实充/放电电流（WMI root\wmi\BatteryStatus）。
        /// Windows 的 BatteryFlag 充电位是**平滑过的状态位**，插着电就恒为真，
        /// 拿它判断「有没有在充电」等于没判断；只有这里的 ChargeRate / DischargeRate 说了算。
        /// WMI 查询要 100~300 ms，缓存 5 秒给 UI 用。</summary>
        class AcpiBattery
        {
            public int ChargeRate = -1;      // mW，-1 = 读不到
            public int DischargeRate = -1;   // mW
            public bool Ok = false;

            static DateTime lastQuery = DateTime.MinValue;
            static AcpiBattery cached = new AcpiBattery();

            public static AcpiBattery Get()
            {
                if ((DateTime.Now - lastQuery).TotalSeconds < 5) return cached;
                lastQuery = DateTime.Now;
                AcpiBattery r = new AcpiBattery();
                try
                {
                    // 注意：WQL 里写 "FROM root\wmi\BatteryStatus" 会报「无效查询」，
                    // 必须显式给命名空间 scope（实测过）。
                    using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                        new ManagementScope(@"\\.\root\wmi"),
                        new ObjectQuery("SELECT ChargeRate, DischargeRate FROM BatteryStatus")))
                    {
                        foreach (ManagementObject o in s.Get())
                        {
                            object cr = o["ChargeRate"];
                            object dr = o["DischargeRate"];
                            if (cr != null) r.ChargeRate = Convert.ToInt32(cr);
                            if (dr != null) r.DischargeRate = Convert.ToInt32(dr);
                            r.Ok = true;
                            break;
                        }
                    }
                }
                catch { }
                cached = r;
                return r;
            }
        }

        /// <summary>电量和充电状态的文字描述（CLI 用来判断限值有没有真被执行）。</summary>
        static string PowerStateText()
        {
            try
            {
                SYSTEM_POWER_STATUS st;
                if (!GetSystemPowerStatus(out st)) return "电量: 读不到";
                string soc = st.BatteryLifePercent <= 100 ? st.BatteryLifePercent + "%" : "—";
                string ac = st.ACLineStatus == 1 ? "交流电在位" : "电池供电";
                string chg = (st.BatteryFlag & 0x08) != 0 ? "，系统报充电中" :
                             ((st.BatteryFlag & 0x10) != 0 ? "，系统报放电中" : "，未充未放");
                // 真正的电流（Windows 那个标志位是平滑过的，只能当参考）
                AcpiBattery ab = AcpiBattery.Get();
                string real = ab.Ok
                    ? (ab.ChargeRate > 0 ? "，真实充电 " + ab.ChargeRate + " mW"
                       : (ab.DischargeRate > 0 ? "，真实放电 " + ab.DischargeRate + " mW"
                                              : "，真实电流 0 mW（没在充也没在放）"))
                    : "";
                return "电量 " + soc + " · " + ac + chg + real;
            }
            catch (Exception ex) { return "电量: 读不到（" + ex.Message + "）"; }
        }

        /// <summary>限值到底有没有被执行：只有在「真有充电电流」的时候，行为才有意义。
        /// Windows 报的 charging 标志是平滑过的，插电恒为真，不能当证据。</summary>
        static string ChargeVerdict(int stop)
        {
            if (stop <= 0) return "判定: 未设置限值（不限制充电）。";
            try
            {
                SYSTEM_POWER_STATUS st;
                if (!GetSystemPowerStatus(out st)) return "判定: 读不到系统电源状态，判断不了。";
                int soc = st.BatteryLifePercent <= 100 ? st.BatteryLifePercent : -1;
                bool discharging = (st.BatteryFlag & 0x10) != 0;
                AcpiBattery ab = AcpiBattery.Get();
                bool onAc = st.ACLineStatus == 1;
                bool reallyCharging = ab.Ok && ab.ChargeRate > 0;

                if (onAc && ab.Ok && ab.ChargeRate <= 0 && ab.DischargeRate <= 0)
                    return "判定: 插着电，但真实电流 0 mW —— 充电器本身没在工作，"
                        + "跟限值没关系（限值也验证不了）。先把充电器救活（拔插适配器 / 放电唤醒）。";

                if (reallyCharging && soc > stop)
                    return "判定: 真实充电 " + ab.ChargeRate + " mW，电量 " + soc + "% 已越过限值 "
                        + stop + "% 还在充 —— 限值没被执行。";

                if (reallyCharging)
                    return "判定: 真实充电 " + ab.ChargeRate + " mW，电量 " + soc + "% 还在限值 "
                        + stop + "% 以下，等涨上去看停不停。";

                if (discharging) return "判定: 电池在放电，暂时不充。";
                if (soc >= 0 && soc <= stop)
                    return "判定: 电量 " + soc + "% 还在限值 " + stop + "% 以下，等涨上去看停不停。";
                if (!ab.Ok)
                    return "判定: 读不到 ACPI 电流，判断不了（Windows 的 charging 标志不可信）。";
                return "判定: 当前没有充电电流，状态待观察。";
            }
            catch (Exception ex) { return "判定: " + ex.Message; }
        }

        /// <summary>电量百分比（系统 API，比 WMI 快得多，UI 心跳里用不卡）。</summary>
        void UpdateBatterySoc()
        {
            try
            {
                SYSTEM_POWER_STATUS st;
                if (GetSystemPowerStatus(out st))
                {
                    if (st.BatteryLifePercent <= 100) battSoc = st.BatteryLifePercent;
                    else battSoc = -1;
                    // BatteryFlag: 0x08 = 充电中, 0x10 = 放电中, 0x80 = 没有电池
                    battCharging = (st.BatteryFlag & 0x08) != 0;
                    battOnAc = st.ACLineStatus == 1;
                }
                else battSoc = -1;
            }
            catch { battSoc = -1; }
        }

        void RefreshFanState()
        {
            string s;
            if (!ec.Available) s = "EC 不可用，风扇控制已禁用。温度/转速请用 python lecoofan.py --status 查看。";
            else if (sampler.Mode == FanMode.Silent)
                s = string.Format("静音上限 {0} RPM — 实测 {1} RPM / PWM 0x{2:X2}{3}",
                    sampler.SilentRpmCap, sampler.Rpm, sampler.Pwm,
                    sampler.SilentLaidOpen ? string.Format("  ← 超过护栏 {0}°C，已放开转速", sampler.SilentGuardC) : "");
            else if (sampler.FanTakenOver) s = string.Format("已接管 EC  (目标 0x{0:X2}, 实测 0x{1:X2})", sampler.TargetPwm, sampler.Pwm);
            else s = "EC 自动控制 (本程序未接管)";
            if (daemonWarning.Length > 0) s += "\r\n⚠ python 守护进程在运行，会和本程序抢 EC";
            lblFanState.Text = s;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (trayMode && !allowClose)
            {
                // 托盘/自启动模式：关窗口只是收回后台，配置和风扇控制继续跑
                e.Cancel = true;
                Hide();
                ShowInTaskbar = false;
                if (!balloonShown)
                {
                    balloonShown = true;
                    if (trayIcon != null) trayIcon.ShowBalloonTip(6000, "LecooFan 仍在后台运行",
                        "控制台已收回托盘；双击图标随时打开，右键菜单可「交还风扇给 EC」或退出。", ToolTipIcon.Info);
                }
                return;
            }
            SaveSettings();
            try
            {
                Log(string.Format("退出: 保存配置完毕，风扇交还 EC 自动控制（退出前状态: 温度 {0} / {1} RPM / GDI 对象 {2} / 已运行 {3:0.0} 小时）。",
                    sampler != null && sampler.Temp >= 0 ? sampler.Temp + "C" : "—",
                    sampler != null && sampler.Rpm > 0 ? sampler.Rpm.ToString() : "—",
                    GuiObjects(), (DateTime.Now - StartedAt).TotalHours));
                soak.Running = false;
                if (sampler != null) { sampler.Mode = FanMode.Auto; sampler.Stop(); }
                if (trayIcon != null) { trayIcon.Visible = false; trayIcon.Dispose(); }
            }
            catch { }
            base.OnFormClosing(e);
        }

        // ── command line entry (scriptable self-test, no window) ──
        static int e2base()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    if (k != null) { object o = k.GetValue("~MHz"); if (o != null) return Convert.ToInt32(o); }
                }
            }
            catch { }
            return 1800;
        }

        static CpuMode FindMode(string key)
        {
            foreach (CpuMode m in Modes) if (string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase)) return m;
            return null;
        }

        static int problems;

        static void DumpTree(Control root, Control parent, int depth, StringBuilder sb, ref int problems)
        {
            foreach (Control c in parent.Controls)
            {
                string name = (c.Text != null && c.Text.Length > 0) ? c.Text : c.GetType().Name;
                if (name.Length > 28) name = name.Substring(0, 28);
                name = name.Replace("\r", " ").Replace("\n", " | ");
                sb.AppendLine(string.Format("{0}{1,-14} x={2,4} y={3,4} w={4,4} h={5,3}{6}  {7}{8}",
                    new string(' ', depth * 2), c.GetType().Name, c.Left, c.Top, c.Width, c.Height,
                    c.Font != null ? string.Format(" {0:0.#}pt", c.Font.SizeInPoints) : "", name,
                    c.Visible ? "" : "   (隐藏)"));
                // 隐藏的控件坐标是上一轮的，别拿它算溢出/重叠（布局按尺寸主动隐藏提示条是正常行为）
                if (!c.Visible) continue;
                // 窗体开了 AutoScroll 时，内容按「布局尺寸」摆（可以比客户区大，超出部分滚动可见），
                // 所以根窗体拿 AutoScrollMinSize 当边界，否则会把滚动条兜底当成溢出误报
                Size psz = parent.ClientSize;
                Form pf = parent as Form;
                if (parent == root && pf != null && pf.AutoScroll)
                    psz = new Size(Math.Max(psz.Width, pf.AutoScrollMinSize.Width),
                                   Math.Max(psz.Height, pf.AutoScrollMinSize.Height));
                if (c.Left < 0 || c.Top < 0 || c.Right > psz.Width || c.Bottom > psz.Height)
                {
                    sb.AppendLine(string.Format("    !! 超出父容器: {0} (父 {1}) 子底={2}", name, psz, c.Bottom));
                    problems++;
                }
                // 文字放不下检查（Label 允许换行，按钮/单选框按单行算）
                if (c.Text != null && c.Text.Length > 0 && !(c is TextBox) && !(c is ComboBox) && !(c is TrackBar))
                {
                    bool wrap = c is Label;
                    TextFormatFlags flags = wrap
                        ? (TextFormatFlags.WordBreak | TextFormatFlags.NoPadding)
                        : (TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                    Size need = TextRenderer.MeasureText(c.Text, c.Font, new Size(c.Width, int.MaxValue), flags);
                    if (need.Height > c.Height + 2 || (!wrap && need.Width > c.Width + 2))
                    {
                        sb.AppendLine(string.Format("    !! 文字放不下: [{0}] 需要 {1}x{2}, 控件 {3}x{4}",
                            name, need.Width, need.Height, c.Width, c.Height));
                        problems++;
                    }
                }
                if (c.Controls.Count > 0) DumpTree(root, c, depth + 1, sb, ref problems);
            }
            // 同级重叠检查：不止第一层，卡片内部也查 ——
            // 「静音曲线」被前一个单选钮的框压住就是这么漏掉的（旧版只查第一层，
            // 而卡片内部是 Label/RadioButton，谁都不是容器）。
            // 容忍 2px：相邻控件的框经常是「刚好贴着」，压到 3px 以上才算真重叠。
            List<Control> sibs = new List<Control>();
            foreach (Control c in parent.Controls) if (c.Visible) sibs.Add(c);
            for (int i = 0; i < sibs.Count; i++)
                for (int j = i + 1; j < sibs.Count; j++)
                {
                    Rectangle a = sibs[i].Bounds, b = sibs[j].Bounds;
                    a.Inflate(-2, -2); b.Inflate(-2, -2);
                    Rectangle hit = Rectangle.Intersect(a, b);
                    if (hit.Width >= 3 && hit.Height >= 3)
                    {
                        string n1 = (sibs[i].Text != null && sibs[i].Text.Length > 0) ? sibs[i].Text : sibs[i].GetType().Name;
                        string n2 = (sibs[j].Text != null && sibs[j].Text.Length > 0) ? sibs[j].Text : sibs[j].GetType().Name;
                        sb.AppendLine(string.Format("    !! 同级重叠: [{0}] 与 [{1}] 压住 {2}x{3}px",
                            n1.Replace("\n", " "), n2.Replace("\n", " "), hit.Width, hit.Height));
                        problems++;
                    }
                }
        }

        /// <summary>PowerShell 单引号字符串字面量：路径里的单引号翻倍转义。</summary>
        static string PsQ(string s) { return "'" + s.Replace("'", "''") + "'"; }

        /// <summary>跑外部工具（schtasks / powershell，设置开机自启动用）。返回退出码，输出文本经 out 带回。</summary>
        static int RunTool(string exe, string args, out string output)
        {
            try
            {
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi);
                string o = p.StandardOutput.ReadToEnd(), er = p.StandardError.ReadToEnd();
                p.WaitForExit();
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("$ " + exe + " " + args + "   → exit=" + p.ExitCode);
                if (o.Length > 0) sb.AppendLine(o.Trim());
                if (er.Length > 0) sb.AppendLine(er.Trim());
                output = sb.ToString();
                return p.ExitCode;
            }
            catch (Exception ex)
            {
                output = exe + " ERROR: " + ex.Message;
                return -1;
            }
        }

        static void RunCli(string[] args)
        {
            StringBuilder sb = new StringBuilder();
            List<string> frames = new List<string>();
            string cmd = args[0].ToLowerInvariant();
            string outFile = null;
            for (int i = 1; i < args.Length; i++) if (args[i].StartsWith("/") && args[i].Length > 1) outFile = args[i].Substring(1);
            try
            {
                if (cmd == "--apply" && args.Length >= 2)
                {
                    CpuMode m = FindMode(args[1]);
                    if (m == null) sb.AppendLine("unknown mode: " + args[1]);
                    else { m.Apply(); sb.AppendLine("applied " + m.Key + " (" + m.Label + ")"); }
                    sb.AppendLine(string.Format("readback: boost={0} epp={1}/{2} cool={3}/{4} auton={5} maxst={6} freqmax={7}",
                        Powr.ReadAc(Powr.BOOST), Powr.ReadAc(Powr.EPP), Powr.ReadDc(Powr.EPP),
                        Powr.ReadAc(Powr.SYSCOOL), Powr.ReadDc(Powr.SYSCOOL), Powr.ReadAc(Powr.AUTON),
                        Powr.ReadAc(Powr.MAXST), Powr.ReadAc(Powr.FREQMAX)));
                }
                else if (cmd == "--report")
                {
                    Ec e = new Ec();
                    Sampler s = new Sampler(e);
                    s.Start();
                    Thread.Sleep(3500);
                    sb.AppendLine("ec: " + e.Status);
                    sb.AppendLine(string.Format("temp={0}C rpm={1} pwm=0x{2:X2} maxcore={3:0}MHz avg={4:0}MHz load={5:0}%",
                        s.Temp, s.Rpm, s.Pwm, s.MaxMhz, s.AvgMhz, s.LoadPct));
                    sb.AppendLine("sampler note: " + s.FreqNote + " | last error: " + s.LastError);
                    sb.AppendLine(string.Format("settings: boost={0} epp={1}/{2} cool={3}/{4} auton={5} maxst={6} freqmax={7}",
                        Powr.ReadAc(Powr.BOOST), Powr.ReadAc(Powr.EPP), Powr.ReadDc(Powr.EPP),
                        Powr.ReadAc(Powr.SYSCOOL), Powr.ReadDc(Powr.SYSCOOL), Powr.ReadAc(Powr.AUTON),
                        Powr.ReadAc(Powr.MAXST), Powr.ReadAc(Powr.FREQMAX)));
                    s.Stop();
                }
                else if (cmd == "--fan-curve")
                {
                    int secs = args.Length >= 2 ? int.Parse(args[1]) : 10;
                    Ec e = new Ec();
                    Sampler s = new Sampler(e);
                    s.Mode = FanMode.Curve;
                    s.Start();
                    for (int w = 0; w < 20 && s.Temp < 0; w++) Thread.Sleep(100);   // wait for the first EC sample
                    for (int i = 0; i < secs * 4; i++)
                    {
                        Thread.Sleep(250);
                        sb.AppendLine(string.Format("t={0:0.0}s temp={1}C target=0x{2:X2} pwm=0x{3:X2} rpm={4} taken={5}",
                            i * 0.25, s.Temp, s.TargetPwm, s.Pwm, s.Rpm, s.FanTakenOver));
                    }
                    s.Mode = FanMode.Auto;
                    s.Stop();
                    Thread.Sleep(2000);
                    sb.AppendLine(string.Format("after release: pwm=0x{0:X2} rpm={1} (EC auto control resumed)", e.FanPwm(), e.FanRpm()));
                }
                else if (cmd == "--diag")
                {
                    Ec e = new Ec();
                    sb.AppendLine("ec: " + e.Status);
                    try { sb.AppendLine("temp=" + e.CpuTemp()); } catch (Exception ex) { sb.AppendLine("temp ERROR: " + ex.GetType().Name + ": " + ex.Message); }
                    try { sb.AppendLine("rpm=" + e.FanRpm()); } catch (Exception ex) { sb.AppendLine("rpm ERROR: " + ex.GetType().Name + ": " + ex.Message); }
                    try { sb.AppendLine("pwm=" + e.FanPwm()); } catch (Exception ex) { sb.AppendLine("pwm ERROR: " + ex.GetType().Name + ": " + ex.Message); }
                    try
                    {
                        PerformanceCounterCategory cat = new PerformanceCounterCategory("Processor Information");
                        string[] inst = cat.GetInstanceNames();
                        sb.AppendLine("Processor Information instances (" + inst.Length + "): " + string.Join(", ", inst));
                        PerformanceCounter[] cs = cat.GetCounters(inst.Length > 0 ? inst[0] : "_Total");
                        sb.AppendLine("counters in category: " + string.Join(", ",
                            Array.ConvertAll<PerformanceCounter, string>(cs, delegate (PerformanceCounter c) { return c.CounterName; })));

                        // sample both frequency readings for 3 s and report the peaks
                        List<PerformanceCounter> act = new List<PerformanceCounter>();
                        List<PerformanceCounter> perf = new List<PerformanceCounter>();
                        foreach (string n in inst)
                        {
                            if (n.IndexOf(',') < 0) continue;
                            try { act.Add(new PerformanceCounter("Processor Information", "Actual Frequency", n, true)); } catch { }
                            try { perf.Add(new PerformanceCounter("Processor Information", "% Processor Performance", n, true)); } catch { }
                        }
                        double maxAct = 0, maxPerf = 0;
                        for (int k = 0; k < 12; k++)
                        {
                            foreach (PerformanceCounter c in act) { try { double v = c.NextValue(); if (v > maxAct) maxAct = v; } catch { } }
                            foreach (PerformanceCounter c in perf) { try { double v = c.NextValue(); if (v > maxPerf) maxPerf = v; } catch { } }
                            Thread.Sleep(250);
                        }
                        sb.AppendLine(string.Format("peak Actual Frequency = {0:0} MHz | peak % Processor Performance = {1:0}% × base {2} = {3:0} MHz",
                            maxAct, maxPerf, e2base(), maxPerf * e2base() / 100.0));
                        sb.AppendLine(string.Format("cores: {0} actual / {1} perf counters", act.Count, perf.Count));
                        foreach (PerformanceCounter c in act) { try { c.Dispose(); } catch { } }
                        foreach (PerformanceCounter c in perf) { try { c.Dispose(); } catch { } }
                    }
                    catch (Exception ex) { sb.AppendLine("perfcounter ERROR: " + ex.GetType().Name + ": " + ex.Message); }
                }
                else if (cmd == "--dumpui")
                {
                    // 布局自检：建好窗体 → 输出控件树绝对坐标 → 检测卡片重叠/子控件溢出
                    // 第二个参数可指定 DPI 缩放（如 1.5 / 2），验证高分辨率屏上的等比布局
                    if (args.Length >= 2)
                    {
                        float sc;
                        if (float.TryParse(args[1], out sc) && sc > 0f) dpiScaleForTest = sc;
                    }
                    MainForm f = new MainForm(true);
                    f.Show();
                    Application.DoEvents();
                    // 四种尺寸都要过：设计尺寸、紧凑下限（默认开出来的大小）、放大（近似最大化）、
                    // 最小窗口（此时内容按下限布局 + 滚动条）—— 每个尺寸都不重叠/不切字才算布局过关
                    int[] ws = { DESIGN_W, FLOOR_W, 1500, 560 }, hs = { DESIGN_H, FLOOR_H, 1000, 440 };
                    string[] names = { "设计尺寸", "紧凑下限(默认大小)", "放大", "最小窗口(滚动)" };
                    sb.AppendLine(string.Format("屏幕工作区 {0}  设计 {1}x{2}  紧凑下限 {3}x{4}  默认开窗 {5}",
                        Screen.PrimaryScreen.WorkingArea, S(DESIGN_W), S(DESIGN_H),
                        S(FLOOR_W), S(FLOOR_H), f.DefaultClientSize()));
                    for (int i = 0; i < ws.Length; i++)
                    {
                        // 目标尺寸直接设 ClientSize：高 DPI 下 AutoScroll 会预留一条滚动条宽度
                        // （本机 30px，等于 S(17)），实际客户区会比目标大一点 —— 所以下面按「实际客户区」测，
                        // 别按 ws/hs 想象。反正低于下限的尺寸布局都按下限算，多这 30px 不影响覆盖。
                        f.ClientSize = new Size(S(ws[i]), S(hs[i]));
                        Application.DoEvents();
                        sb.AppendLine(string.Format("── {0} {1}x{2}（实际客户区 {3}x{4}）scale={5:0.##}",
                            names[i], ws[i], hs[i], f.ClientSize.Width, f.ClientSize.Height, DpiScale));
                        problems = 0;
                        DumpTree(f, f, 0, sb, ref problems);
                        // 模式卡的说明是自绘的（DumpTree 看不到），这里单独查：设计尺寸及以上必须完整显示，
                        // 小窗口允许退化成一行/隐藏 —— 退化是设计好的，静默切字不是。
                        if (i == 0 || i == 2)
                            foreach (ModeCard mc in f.modeCards)
                                if (mc != null && !mc.NoteFits())
                                {
                                    sb.AppendLine(string.Format("  [问题] 模式卡「{0}」说明放不下（格子 {1}x{2}）",
                                        mc.Mode.Label, mc.Width, mc.Height));
                                    problems++;
                                }
                        sb.AppendLine(problems == 0 ? "LAYOUT: PASS (无重叠/无溢出)" : "LAYOUT: FAIL (" + problems + " 处问题)");
                    }
                    f.Close();
                }
                else if (cmd == "--heartbeat")
                {
                    // 打印一条心跳日志（内容和每 5 分钟自动写的那条一模一样），
                    // 省得为了看一眼格式等 5 分钟
                    MainForm f = new MainForm(true);
                    f.Show();
                    Application.DoEvents();
                    int t0 = GuiObjects();
                    f.LogHeartbeat();
                    sb.AppendLine(string.Format("（心跳里的 GDI 对象数实测 {0}，与 --tray-icon-test 同一把尺子）", t0));
                    f.Close();
                }
                else if (cmd == "--tray-icon-test")
                {
                    // 托盘图标泄漏自测：反复重建/释放图标，量 GDI 对象数的变化。
                    // 老写法（Icon.FromHandle(bmp.GetHicon()) 之后 Dispose 旧图标）每轮漏一个 HICON，
                    // 攒到一万个上限，GetHicon() 就抛「GDI+ 中发生一般性错误」把程序打死 —— 2026-10-14 实崩。
                    int n = args.Length >= 2 ? int.Parse(args[1]) : 2000;
                    BuildFanIcon(-1).Dispose();       // 先画一个：GDI+ 内部缓存（画笔/画刷）第一次用就建好了，
                    int before = GuiObjects();        // 不预热的话测到的是缓存不是泄漏
                    for (int i = 0; i < n; i++)
                    {
                        Icon ic = MainForm.BuildFanIcon(i % 100);
                        ic.Dispose();
                    }
                    int after = GuiObjects();
                    double per = (after - before) / (double)n;
                    sb.AppendLine(string.Format("托盘图标重建 {0} 次: GDI 对象 {1} → {2} (Δ{3}, 每轮 {4:0.000})",
                        n, before, after, after - before, per));
                    sb.AppendLine(per < 0.05 ? "ICON-LEAK: PASS (无泄漏)" : "ICON-LEAK: FAIL");
                }
                else if (cmd == "--fan-cap" && args.Length >= 2)
                {
                    // 静音上限：锁转速快速验证。可跟 [秒数] [CPU档位] 两个可选参数
                    int rpm = int.Parse(args[1]);
                    int secs = args.Length >= 3 ? int.Parse(args[2]) : 10;
                    int guardC = args.Length >= 5 ? int.Parse(args[4]) : 72;
                    if (args.Length >= 4)
                    {
                        CpuMode m = FindMode(args[3]);
                        if (m != null) { m.Apply(); sb.AppendLine("applied cpu mode " + m.Key + " (" + m.Label + ")"); }
                    }
                    Ec e = new Ec();
                    Sampler s = new Sampler(e);
                    s.SilentRpmCap = rpm;
                    s.SilentGuardC = guardC;
                    s.Mode = FanMode.Silent;
                    s.Start();
                    for (int i = 0; i < secs * 4; i++)
                    {
                        Thread.Sleep(250);
                        sb.AppendLine(string.Format("t={0:0.00}s temp={1}C rpm={2} (上限 {3}, 护栏 {4}C) pwm=0x{5:X2} target=0x{6:X2} laidopen={7} taken={8}",
                            i * 0.25, s.Temp, s.Rpm, s.SilentRpmCap, guardC, s.Pwm, s.TargetPwm, s.SilentLaidOpen, s.FanTakenOver));
                    }
                    s.Mode = FanMode.Auto;
                    s.Stop();
                    Thread.Sleep(1000);
                    sb.AppendLine(string.Format("after release: pwm=0x{0:X2} rpm={1} (EC auto resumed)", e.FanPwm(), e.FanRpm()));
                }
                else if (cmd == "--ui-modes")
                {
                    // 真窗体自检：依次点 4 张模式卡，验证「CPU + 风扇」整套预设都落地
                    int secs = args.Length >= 2 ? int.Parse(args[1]) : 5;
                    MainForm f = new MainForm(true);
                    f.Show();
                    Application.DoEvents();
                    sb.Append(f.SelfTestModes(secs));
                    f.Close();
                    Application.DoEvents();
                    sb.AppendLine("form closed");
                }
                else if (cmd == "--ui-silent" && args.Length >= 2)
                {
                    // 真窗体自检：点一下「静音上限」，看 UI 事件链 + 闭环是否真的生效
                    int rpm = int.Parse(args[1]);
                    int secs = args.Length >= 3 ? int.Parse(args[2]) : 3;
                    MainForm f = new MainForm(true);
                    f.Show();
                    Application.DoEvents();
                    sb.Append(f.SelfTestSilent(Array.IndexOf(RpmCaps, rpm), secs));
                    f.Close();
                    Application.DoEvents();
                    sb.AppendLine("form closed");
                }
                else if (cmd == "--soak" && args.Length >= 2)
                {
                    int seconds = int.Parse(args[1]);
                    string modeKey = args.Length >= 3 ? args[2] : "keep";
                    string fanArg = args.Length >= 4 ? args[3] : "auto";
                    if (modeKey != "keep")
                    {
                        CpuMode m = FindMode(modeKey);
                        if (m == null) { sb.AppendLine("unknown mode: " + modeKey); }
                        else { m.Apply(); sb.AppendLine("applied " + m.Key + " (" + m.Label + ")"); }
                    }
                    Ec e = new Ec();
                    Sampler s = new Sampler(e);
                    s.SilentRpmCap = 2000;                   // fan=silent 时的默认静音上限
                    s.Start();
                    Thread.Sleep(1500);
                    FanMode fm = fanArg == "curve" ? FanMode.Curve
                              : (fanArg == "manual" ? FanMode.Manual
                              : (fanArg == "silent" ? FanMode.Silent : FanMode.Auto));
                    Soak sk = new Soak();
                    int lines = 0;
                    sk.Run(seconds, s, fm, modeKey + "/" + fanArg, delegate (string line)
                    {
                        frames.Add(line);
                        lines++;
                    }, delegate { });
                    foreach (string l in frames) sb.AppendLine(l);
                    sb.AppendLine("csv=" + System.IO.Path.GetFileName(sk.CsvPath));
                    sb.AppendLine("report=" + System.IO.Path.GetFileName(sk.SummaryPath));
                    sb.AppendLine("settings: " + string.Format("boost={0} epp={1}/{2} cool={3}/{4} auton={5}",
                        Powr.ReadAc(Powr.BOOST), Powr.ReadAc(Powr.EPP), Powr.ReadDc(Powr.EPP),
                        Powr.ReadAc(Powr.SYSCOOL), Powr.ReadDc(Powr.SYSCOOL), Powr.ReadAc(Powr.AUTON)));
                }
                else if (cmd == "--install-autostart")
                {
                    // 开机自启动 = 两件事配合：
                    //   ① 计划任务（登录时 / 最高权限 / 直接进托盘，不弹窗口）。用 PowerShell 注册而不是
                    //      schtasks /Create：后者默认电池策略是「拔电源就停」，笔记本离电风扇控制会被杀掉。
                    //   ② 注册表 Run 键 → LecooFanTray.exe（无清单的启动桩）。计划任务不会出现在
                    //      「任务管理器 → 启动应用」列表里，Run 键才会；桩只负责触发那个计划任务，
                    //      自己不提权、不弹 UAC。真正干活的始终是计划任务。
                    const string taskName = "LecooFan";
                    const string chargeTaskName = "LecooFanCharge";
                    const string runKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";
                    const string runName = "LecooFan";
                    // PowerShell 单引号字符串：路径里的单引号要翻倍转义（见 PsQ）
                    string ps =
                        "$a = New-ScheduledTaskAction -Execute " + PsQ(typeof(MainForm).Assembly.Location) + " -Argument '--tray';" +
                        "$t = New-ScheduledTaskTrigger -AtLogOn;" +
                        "$s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit 0 -MultipleInstances IgnoreNew;" +
                        "$p = New-ScheduledTaskPrincipal -UserId " + PsQ(Environment.UserDomainName + "\\" + Environment.UserName) + " -LogonType Interactive -RunLevel Highest;" +
                        "Register-ScheduledTask -TaskName " + PsQ(taskName) +
                        " -Action $a -Trigger $t -Settings $s -Principal $p" +
                        " -Description 'LecooFan GUI: N175L EC fan control + CPU modes (tray autostart)' -Force | Out-Null";
                    string o;
                    int rc = RunTool("powershell", "-NoProfile -NonInteractive -Command \"" + ps + "\"", out o);
                    sb.Append(o);
                    sb.AppendLine(rc == 0
                        ? "已设置开机自启动（计划任务 " + taskName + "：登录时以最高权限启动，直接进托盘，不弹窗口；电池下也继续跑）。"
                        : "计划任务设置失败：请用管理员身份运行 `LecooFanGUI.exe --install-autostart`。");
                    // 启动项（任务管理器"启动应用"里可见）：指向启动桩，不是 GUI 本体——GUI 带
                    // requireAdministrator 清单，非提权拉起会弹 UAC；桩无清单，只去触发计划任务。
                    string stub = System.IO.Path.Combine(
                        System.IO.Path.GetDirectoryName(typeof(MainForm).Assembly.Location), "LecooFanTray.exe");
                    if (System.IO.File.Exists(stub))
                    {
                        int rc2 = RunTool("reg", "add \"" + runKey + "\" /v " + runName + " /t REG_SZ /d \\\"" + stub + "\\\" /f", out o);
                        sb.Append(o);
                        sb.AppendLine(rc2 == 0
                            ? "已添加启动项 " + runName + "（任务管理器 → 启动应用 里可见；触发上面的计划任务，不弹 UAC）。"
                            : "启动项添加失败（不影响计划任务，风扇控制仍会在登录时启动）。");
                    }
                    else
                    {
                        sb.AppendLine("未找到 " + System.IO.Path.GetFileName(stub) + "，跳过启动项（计划任务已装好；用 README 里的编译命令补上启动桩后可以再跑一次本命令）。");
                    }

                    // ② 充电限值的开机任务：EC RAM 断电即失，重启后限值必须重新写一遍。
                    // 单独一个任务，不依赖 GUI 在不在：登录时按配置里的 charge= 写回 EC
                    // （配置里没配就什么都不做，绝不乱写 EC）。
                    // 和上面的 LecooFan 任务同时跑不要紧 —— 两个进程写的是同一组寄存器，
                    // 而且现在有跨进程 EC 锁（Local\LecooFanEcAccess）排队，不会互相插队。
                    string appDir = System.IO.Path.GetDirectoryName(typeof(MainForm).Assembly.Location);
                    string ps2 =
                        "$a = New-ScheduledTaskAction -Execute " + PsQ(typeof(MainForm).Assembly.Location) +
                        " -Argument '--charge-apply /charge_apply.log' -WorkingDirectory " + PsQ(appDir) + ";" +
                        "$t = New-ScheduledTaskTrigger -AtLogOn;" +
                        "$s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit 0 -MultipleInstances IgnoreNew;" +
                        "$p = New-ScheduledTaskPrincipal -UserId " + PsQ(Environment.UserDomainName + "\\" + Environment.UserName) + " -LogonType Interactive -RunLevel Highest;" +
                        "Register-ScheduledTask -TaskName " + PsQ(chargeTaskName) +
                        " -Action $a -Trigger $t -Settings $s -Principal $p" +
                        " -Description 'LecooFan: re-apply battery charge limit after boot (EC RAM is volatile)' -Force | Out-Null";
                    int rc3 = RunTool("powershell", "-NoProfile -NonInteractive -Command \"" + ps2 + "\"", out o);
                    sb.Append(o);
                    sb.AppendLine(rc3 == 0
                        ? "已设置充电限值开机任务（" + chargeTaskName + "：登录时按配置补写 EC 0x04BB/0x04BC；没配限值就什么都不做）。"
                        : "充电限值开机任务设置失败（不影响风扇控制和限值本身，只是重启后要手动补写一次）。");
                }
                else if (cmd == "--uninstall-autostart")
                {
                    const string taskName = "LecooFan";
                    const string chargeTaskName = "LecooFanCharge";
                    const string runKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";
                    const string runName = "LecooFan";
                    string o;
                    int rc = RunTool("schtasks", "/Delete /TN \"" + taskName + "\" /F", out o);
                    sb.Append(o);
                    sb.AppendLine(rc == 0
                        ? "已删除开机自启动（计划任务 " + taskName + "）。"
                        : "删除失败（任务可能本来就不存在，或权限不足）。");
                    int rc4 = RunTool("schtasks", "/Delete /TN \"" + chargeTaskName + "\" /F", out o);
                    sb.Append(o);
                    sb.AppendLine(rc4 == 0
                        ? "已删除充电限值开机任务（" + chargeTaskName + "）。"
                        : "删除充电限值任务失败（可能本来就不存在）。");
                    int rc2 = RunTool("reg", "delete \"" + runKey + "\" /v " + runName + " /f", out o);
                    sb.Append(o);
                    sb.AppendLine(rc2 == 0
                        ? "已删除启动项 " + runName + "（任务管理器 → 启动应用 里不再显示）。"
                        : "启动项删除失败（可能本来就不存在）。");
                }
                else if (cmd == "--charge" && args.Length >= 2)
                {
                    // --charge 60 | --charge off | --charge status
                    Ec e = new Ec();
                    sb.AppendLine("ec: " + e.Status);
                    if (!e.Available)
                    {
                        sb.AppendLine("EC 不可用，放弃（不碰 EC）。用管理员身份运行，并确认 inpout 驱动已加载。");
                    }
                    else
                    {
                        string a = args[1].ToLowerInvariant();
                    if (a == "status")
                    {
                        int[] rb0 = e.ChargeLimit();
                        sb.AppendLine(string.Format("charge limit: stop={0}% resume={1}%", rb0[0], rb0[1]));
                        sb.AppendLine("power: " + PowerStateText());
                        sb.AppendLine(ChargeVerdict(rb0[0]));
                    }
                        else
                        {
                            int stop = 0, resume = 0;
                            if (a == "off" || a == "0") { stop = 0; resume = 0; }
                            else
                            {
                                int pct = int.Parse(a);
                                int idx = -1;
                                for (int i = 0; i < ChargeStops.Length; i++) if (ChargeStops[i] == pct) idx = i;
                                if (idx >= 0) { stop = ChargeStops[idx]; resume = ChargeResumes[idx]; }
                                else { stop = pct; resume = Math.Max(0, pct - 5); }
                            }
                        e.SetChargeLimit(stop, resume);
                        int[] rb = e.ChargeLimit();
                        sb.AppendLine(string.Format("charge limit set: stop={0}% resume={1}% (readback {2}/{3})",
                            stop, resume, rb[0], rb[1]));
                        sb.AppendLine("power: " + PowerStateText());
                        SaveCfgCharge(stop);      // 同步配置：GUI 和开机任务都跟着走
                        }
                    }
                }
                else if (cmd == "--charge-apply")
                {
                    // 开机计划任务用：读配置里的限值写回 EC（EC RAM 断电即失）
                    int stop = ReadCfgCharge();
                    if (stop < 0) sb.AppendLine("charge: 配置里没有限值，跳过（不碰 EC）");
                    else
                    {
                        Ec e = new Ec();
                        if (!e.Available)
                        {
                            sb.AppendLine("charge: EC 不可用（" + e.Status + "），本次不写。开机任务会等下次登录再试。");
                        }
                        else
                        {
                            int resume = stop > 0 ? ResumeFor(stop) : 0;
                            e.SetChargeLimit(stop, resume);
                            int[] rb = e.ChargeLimit();
                            sb.AppendLine(string.Format("charge applied: stop={0}% resume={1}% (readback {2}/{3})",
                                stop, resume, rb[0], rb[1]));
                        }
                    }
                }
                else if (cmd == "--fan-off")
                {
                    Ec e = new Ec();
                    e.ReleaseFan();
                    Thread.Sleep(1500);
                    sb.AppendLine(string.Format("fan released: pwm=0x{0:X2} rpm={1}", e.FanPwm(), e.FanRpm()));
                }
                else
                {
                    sb.AppendLine("usage: LecooFanGUI.exe --apply <perf|bal|quiet|deep> | --report | --fan-off");
                    sb.AppendLine("       LecooFanGUI.exe --fan-cap <rpm> [sec] [cpumode] [guardC]   # 静音上限，锁转速");
                    sb.AppendLine("       LecooFanGUI.exe --ui-modes [sec]   # 真窗体自检：依次点 4 张模式卡验证整套预设");
                    sb.AppendLine("       LecooFanGUI.exe --ui-silent <rpm> [sec]   # 真窗体自检：点一下「静音上限」");
                    sb.AppendLine("       LecooFanGUI.exe --fan-curve <sec> | --soak <sec> [mode] [fan] | --dumpui [scale]");
                    sb.AppendLine("           其中 fan = auto | silent(默认锁 2000 RPM) | curve | manual");
                    sb.AppendLine("       LecooFanGUI.exe --charge <60|off|status>   # 电池充电限值（写 EC 0x04BB/0x04BC）");
                    sb.AppendLine("       LecooFanGUI.exe --charge-apply   # 按配置补写充电限值（开机计划任务用）");
                    sb.AppendLine("       LecooFanGUI.exe --tray     # 后台运行：进通知区托盘，不弹窗口（开机自启动同款）");
                    sb.AppendLine("       LecooFanGUI.exe --install-autostart | --uninstall-autostart   # 设置/取消开机自启动（需管理员）");
                    sb.AppendLine("           同时装两处：计划任务(干活,无声无UAC) + Run启动项 LecooFanTray.exe(任务管理器启动应用里可见)");
                }
            }
            catch (Exception ex) { sb.AppendLine("ERROR: " + ex.Message); }

            try { Console.Write(sb.ToString()); } catch { }
            string path = outFile != null
                ? outFile
                : System.IO.Path.Combine(Application.StartupPath, "gui_cli_report.txt");
            try { System.IO.File.WriteAllText(path, sb.ToString(), Encoding.UTF8); } catch { }
        }

        /// <summary>读配置里的 charge= 值；没有这一行返回 -1（= 没配过）。</summary>
        static int ReadCfgCharge()
        {
            try
            {
                if (!System.IO.File.Exists(CfgFile)) return -1;
                foreach (string line in System.IO.File.ReadAllLines(CfgFile, Encoding.UTF8))
                {
                    string[] kv = line.Split('=');
                    if (kv.Length == 2 && kv[0].Trim() == "charge")
                    {
                        int v;
                        if (int.TryParse(kv[1].Trim(), out v)) return v;
                    }
                }
            }
            catch { }
            return -1;
        }

        /// <summary>只改配置里的 charge= 这一行（CLI 用），其余行原样保留。</summary>
        static void SaveCfgCharge(int stop)
        {
            try
            {
                List<string> lines = System.IO.File.Exists(CfgFile)
                    ? new List<string>(System.IO.File.ReadAllLines(CfgFile, Encoding.UTF8))
                    : new List<string>();
                bool found = false;
                for (int i = 0; i < lines.Count; i++)
                    if (lines[i].Trim().StartsWith("charge=")) { lines[i] = "charge=" + stop; found = true; }
                if (!found) lines.Add("charge=" + stop);
                System.IO.File.WriteAllLines(CfgFile, lines.ToArray(), Encoding.UTF8);
            }
            catch { }
        }

        static Mutex oneInstance;

        /// <summary>
        /// 单实例保护（只守 GUI/托盘入口，--report/--dumpui 等自检路径不走这里）。
        /// 已有实例在跑时返回 false：两个进程同时覆写 0x1809 会互相抢 EC，必须避免。
        /// </summary>
        static bool EnsureSingleInstance()
        {
            try
            {
                bool isNew;
                oneInstance = new Mutex(true, "LecooFanGUI", out isNew);
                return isNew;
            }
            catch { return true; }  // mutex 出问题就别互相挡，照常启动
        }

        /// <summary>请已有实例把控制台显示出来；返回是否通知到了（旧版本实例没有这条通道）。</summary>
        static bool RequestShowExisting()
        {
            try
            {
                using (EventWaitHandle h = EventWaitHandle.OpenExisting(ShowRequestName))
                {
                    AllowSetForegroundWindow(ASFW_ANY);   // 不授权的话对方只能"在后台打开"，窗口不冒头
                    h.Set();
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>MainForm 构造前就能用的日志（单实例提示写在窗口还没建起来时）。</summary>
        static void LogLine(string msg)
        {
            AppendLogFile(string.Format("[{0:HH:mm:ss}] {1}", DateTime.Now, msg));
        }

        /// <summary>未处理异常统一落盘（带堆栈），界面线程的异常吞掉继续跑 —— 风扇还在程序手里，界面崩了才是事故。</summary>
        static void LogException(string where, Exception ex)
        {
            try
            {
                LogLine(string.Format("错误: {0}: {1}: {2}", where, ex.GetType().Name, ex.Message));
                if (ex.StackTrace != null)
                    foreach (string l in ex.StackTrace.Split('\n')) LogLine("    " + l.Trim());
                if (ex.InnerException != null)
                    LogLine("    内部异常: " + ex.InnerException.Message);
            }
            catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            bool tray = false;
            foreach (string a in args) if (a == "--tray") tray = true;

            // 未处理异常一律记日志（带堆栈）：崩溃后能翻出原因，而不是只弹一个「GDI+ 中发生一般性错误」。
            // 界面线程的异常吞掉继续跑 —— 风扇控制还挂在程序身上，界面挂了才是真事故。
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                LogException("非界面线程未处理异常", e.ExceptionObject as Exception);
            };
            if (tray || args.Length == 0)
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                {
                    LogException("界面线程未处理异常（已忽略，程序继续运行）", e.Exception);
                };
            }

            if (tray)
            {
                if (!EnsureSingleInstance())
                {
                    // 托盘模式发现已有实例：静默退出（开机自启动/启动桩触发时不能弹窗）
                    LogLine("检测到已有实例在运行，本次托盘启动直接退出。");
                    return;
                }
                // 开机自启动同款入口：进通知区托盘后台运行，不弹窗口，配置照样自动恢复
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try
                {
                    MainForm f = new MainForm();
                    f.WatchShowRequests();
                    f.EnableTray();
                    Application.Run(f);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("启动失败: " + ex.Message + "\r\n\r\n请确认以管理员身份运行。", "LecooFan GUI");
                }
                return;
            }

            if (args.Length > 0) { RunCli(args); return; }
            if (!EnsureSingleInstance())
            {
                // 已有实例（窗口或托盘）：请它把控制台显示出来。
                // 只有对方是没这条通道的旧版本时才退回干提示（那时候也只好靠托盘图标）
                if (!RequestShowExisting())
                    MessageBox.Show("LecooFan 已经在运行（窗口或托盘）。\r\n托盘里右键图标可以打开控制台。", "LecooFan");
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                MainForm f = new MainForm();
                f.WatchShowRequests();
                Application.Run(f);
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动失败: " + ex.Message + "\r\n\r\n请确认以管理员身份运行。", "LecooFan GUI");
            }
        }
    }
}
