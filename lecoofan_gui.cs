// LecooFan GUI — N175L (Ultra 5 125H / IT5570 EC) CPU & fan tuning console.
//
// Build (uses the .NET Framework compiler that ships with Windows — no installs):
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe ^
//       /out:LecooFanGUI.exe /win32manifest:lecoofan_gui.manifest ^
//       /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll lecoofan_gui.cs lecoofan_ui.cs
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

        public int ReadReg(int addr)
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
        }

        public void WriteReg(int addr, int val)
        {
            Enter();
            Out(PORT, 0x2E); Out(PORT + 1, 0x11);
            Out(PORT, 0x2F); Out(PORT + 1, (addr >> 8) & 0xFF);
            Out(PORT, 0x2E); Out(PORT + 1, 0x10);
            Out(PORT, 0x2F); Out(PORT + 1, addr & 0xFF);
            Out(PORT, 0x2E); Out(PORT + 1, 0x12);
            Out(PORT, 0x2F); Out(PORT + 1, val & 0xFF);
            Exit();
        }

        public int ReadRam(int offset) { return ReadReg(0x0400 + offset); }
        public int CpuTemp() { return ReadRam(0x70); }
        public int FanRpm() { return (ReadRam(0x76) << 8) | ReadRam(0x77); }
        public int FanPwm() { return ReadReg(0x1809); }
        public void SetFanPwm(int pwm) { WriteReg(0x1809, pwm & 0xFF); }
        public void ReleaseFan() { WriteReg(0x1809, 0x00); }   // EC resumes auto control
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
        static readonly CpuMode[] Modes = new CpuMode[]
        {
            new CpuMode("perf",  "高性能",   2,  0,  0, 1, 1, 1, "睿频开 + EPP 0 + 主动散热 + 风扇 EC 自动\n满载 87°C / ~4400 RPM，最吵但满血", FanPreset.Auto, 0),
            new CpuMode("bal",   "平衡(原厂)", 2, 33, 50, 1, 0, 1, "出厂原始设置 + 风扇 EC 自动\n满载 84°C / ~3400 RPM", FanPreset.Auto, 0),
            new CpuMode("quiet", "安静",     0, 60, 70, 0, 0, 1, "关睿频 + EPP 60/70 + 被动 + 风扇锁 2000 RPM\n满载约 64-66°C / ~1950 RPM（护栏 72°C 兜底）", FanPreset.Silent, 2000),
            new CpuMode("deep",  "极静(实验)", 0, 80, 90, 0, 0, 0, "关 HWP 自主模式 + 风扇锁 1600 RPM\n实验档：1600 低于 EC 地板 ~1700，实坐地板，仅调试用", FanPreset.Silent, 1600),
        };

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
            form.ClientSize = new Size(S(860), S(760));   // 与 BuildUi 里的 860x760 保持一致
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
        Soak soak = new Soak();
        System.Windows.Forms.Timer uiTimer;
        NotifyIcon trayIcon;                       // 托盘模式（--tray / 开机自启动）用
        bool trayMode, allowClose, balloonShown;
        int lastTrayTemp = int.MinValue;
        Size designSize;                           // 缩放后的设计尺寸：窗口被折腾到屏幕外时按它兜底
        static EventWaitHandle showRequest;        // 别的实例发来的「把控制台显示出来」请求
        const string ShowRequestName = "LecooFanGUI.ShowConsole";
        const int ASFW_ANY = -1;
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int dwProcessId);

        public MainForm()
            : this(false)
        {
        }

        /// <param name="cliMode">命令行自检/脚本模式：不读配置、不碰用户设置</param>
        public MainForm(bool cliMode)
        {
            DpiScale = dpiScaleForTest > 0f ? dpiScaleForTest : ComputeDpiScale();
            // 测试模式：字号按“目标缩放 / 本机缩放”放大，等价于真的在目标 DPI 的屏上跑
            float fontScale = dpiScaleForTest > 0f ? DpiScale / ComputeDpiScale() : 1f;
            Ui.SetScaleBaked(DpiScale, fontScale);
            Ui.TouchFonts();                           // BuildUi 之前把主题字体建好
            BuildUi();
            ScaleTree(this, DpiScale, fontScale);
            Font = Ui.FontBase;
            designSize = Size;                         // 此刻还是设计尺寸，托盘还原兜底用

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
            trayIcon.Icon = BuildFanIcon(-1);
            trayIcon.Text = "LecooFan 控制台";
            trayIcon.Visible = true;
            trayIcon.DoubleClick += delegate { RestoreFromTray(); };
            ContextMenu menu = new ContextMenu();
            menu.MenuItems.Add("打开控制台", delegate { RestoreFromTray(); });
            menu.MenuItems.Add("交还风扇给 EC", delegate { ReleaseFanToEc(); });
            menu.MenuItems.Add("退出", delegate { allowClose = true; Close(); });
            trayIcon.ContextMenu = menu;
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
            Size sz = designSize.Width >= 320 ? designSize : new Size(860, 760);
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

        /// <summary>托盘 logo：三叶风扇徽标，颜色随温度（凉=品牌蓝/绿，烫=红）。</summary>
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
                return Icon.FromHandle(bmp.GetHicon());
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
            trayIcon.Icon = BuildFanIcon(sampler.Temp);
            trayIcon.Tag = new object();
            if (old != null) old.Dispose();         // 旧图标是 HICON 句柄，不释放会泄漏
        }

        // ── 配置持久化：启动自动恢复上次的 CPU 档位 + 风扇设置 ──

        void SaveSettings()
        {
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
                System.IO.File.WriteAllText(CfgFile, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        void LoadSettings()
        {
            try
            {
                if (!System.IO.File.Exists(CfgFile)) return;
                string cpu = null, fan = null, cap = null, pwm = null, epp = null, turbo = null, cool = null;
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
                }
                int n;

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

                Log(string.Format("已恢复上次配置: cpu={0} fan={1}{2}", cpu ?? "keep", fan ?? "auto",
                    fan == "silent" ? " cap=" + RpmCapValue() + " RPM" : ""));
            }
            catch { }
        }

        void Log(string s)
        {
            string line = string.Format("[{0:HH:mm:ss}] {1}", DateTime.Now, s);
            try { System.IO.File.AppendAllText(LogFile, line + "\r\n", Encoding.UTF8); } catch { }
            txtLog.AppendText(line + "\r\n");
            if (txtLog.Lines.Length > 400)
            {
                string[] keep = new string[200];
                Array.Copy(txtLog.Lines, txtLog.Lines.Length - 200, keep, 0, 200);
                txtLog.Lines = keep;
            }
        }

        void BuildUi()
        {
            Text = "LecooFan 控制台 — N175L CPU / 风扇调速";
            ClientSize = new Size(860, 760);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            AutoScaleMode = AutoScaleMode.None;   // 缩放全部由 ScaleTree 手动做，关掉 WinForms 内置的那套
            StartPosition = FormStartPosition.CenterScreen;
            Font = Ui.FontBase;
            BackColor = Ui.WindowBg;
            ForeColor = Ui.Text;

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
            Card cardMode = new Card();
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
            Card cardTune = new Card();
            cardTune.Text = "微调（自定义）";
            cardTune.SetBounds(16, 456, 408, 168);
            Controls.Add(cardTune);

            Label l1 = new Label(); l1.Text = "EPP"; l1.SetBounds(14, 30, 40, 16); l1.ForeColor = Ui.Muted; cardTune.Controls.Add(l1);
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

            Label l2 = new Label(); l2.Text = "散热"; l2.SetBounds(180, 82, 40, 16); l2.ForeColor = Ui.Muted; cardTune.Controls.Add(l2);
            cmbCool = new ComboBox(); cmbCool.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbCool.Items.Add("主动 (先转风扇)");
            cmbCool.Items.Add("被动 (先降频)");
            cmbCool.SetBounds(222, 78, 114, 24);
            cardTune.Controls.Add(cmbCool);

            Button btnApply = new Button(); btnApply.Text = "应用微调"; btnApply.SetBounds(14, 112, 150, 30);
            Ui.StyleButton(btnApply, Ui.Accent);
            btnApply.Click += delegate { ApplyTune(); };
            cardTune.Controls.Add(btnApply);

            lblTuneHint = new Label();
            lblTuneHint.SetBounds(174, 110, 226, 52);
            lblTuneHint.ForeColor = Ui.Muted;
            lblTuneHint.Font = Ui.FontSmall;
            lblTuneHint.Text = "EPP 越小越偏性能、越大越省电。本机的最大频率/最大状态键被固件忽略（实测无效）。";
            cardTune.Controls.Add(lblTuneHint);

            // ── 风扇 ──
            Card cardFan = new Card();
            cardFan.Text = "风扇控制";
            cardFan.SetBounds(16, 276, 408, 172);
            Controls.Add(cardFan);

            radFanAuto = new RadioButton(); radFanAuto.Text = "EC 自动"; radFanAuto.SetBounds(14, 26, 74, 20); radFanAuto.Checked = true;
            radFanAuto.ForeColor = Ui.Text;
            radFanAuto.CheckedChanged += delegate { FanModeChanged(); };
            cardFan.Controls.Add(radFanAuto);
            radFanSilent = new RadioButton(); radFanSilent.Text = "静音上限"; radFanSilent.SetBounds(92, 26, 110, 20);
            radFanSilent.ForeColor = Ui.Text;
            radFanSilent.CheckedChanged += delegate { FanModeChanged(); };
            cardFan.Controls.Add(radFanSilent);
            radFanCurve = new RadioButton(); radFanCurve.Text = "静音曲线"; radFanCurve.SetBounds(176, 26, 80, 20);
            radFanCurve.ForeColor = Ui.Text;
            radFanCurve.CheckedChanged += delegate { FanModeChanged(); };
            cardFan.Controls.Add(radFanCurve);
            radFanManual = new RadioButton(); radFanManual.Text = "手动 PWM"; radFanManual.SetBounds(260, 26, 110, 20);
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

            Label l3 = new Label(); l3.Text = "PWM"; l3.SetBounds(14, 100, 40, 16); l3.ForeColor = Ui.Muted; cardFan.Controls.Add(l3);
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

            Label l4 = new Label();
            l4.SetBounds(14, 138, 380, 32);
            l4.ForeColor = Ui.Warn;
            l4.Font = Ui.FontSmall;
            l4.Text = "静音上限：锁住转速（EC 下限 ~1700 RPM，比这更低锁不住）\n" +
                      "护栏 72°C 超温自动放开转速；关窗口 = 退回托盘继续跑";
            cardFan.Controls.Add(l4);

            // ── 实时监控（大号读数 + 趋势图）──
            Card cardMon = new Card();
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
            cardMon.Controls.Add(lblFreqNote);

            // ── 系统设置回读条 ──
            Card cardSys = new Card();
            cardSys.Text = "系统设置回读";
            cardSys.SetBounds(16, 632, 828, 64);
            Controls.Add(cardSys);

            valSys = new Label();
            valSys.SetBounds(14, 26, 800, 34);
            valSys.ForeColor = Ui.Text;
            cardSys.Controls.Add(valSys);

            // ── 操作 / 测试 ──
            Card cardAct = new Card();
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

            Button btnHelp = new Button();
            btnHelp.Text = "说明 / 边界";
            btnHelp.SetBounds(204, 78, 190, 34);
            Ui.StyleButton(btnHelp, Ui.Muted);
            btnHelp.Click += delegate { ShowHelp(); };
            cardAct.Controls.Add(btnHelp);

            Button btnDir = new Button();
            btnDir.Text = "打开项目目录";
            btnDir.SetBounds(14, 122, 180, 34);
            Ui.StyleButton(btnDir, Ui.Muted);
            btnDir.Click += delegate
            {
                try { Process.Start("explorer.exe", Application.StartupPath); } catch { }
            };
            cardAct.Controls.Add(btnDir);

            Button btnExit = new Button();
            btnExit.Text = "退出（并交还风扇）";
            btnExit.SetBounds(204, 122, 190, 34);
            Ui.StyleButton(btnExit, Ui.Bad);
            btnExit.Click += delegate { Close(); };
            cardAct.Controls.Add(btnExit);

            Label lblSoakHint = new Label();
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
            return val;
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
            RefreshSettingsLine();
            RefreshFanState();
            UpdateTrayIcon();
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
                sb.AppendLine(string.Format("{0}{1,-14} x={2,4} y={3,4} w={4,4} h={5,3}{6}  {7}",
                    new string(' ', depth * 2), c.GetType().Name, c.Left, c.Top, c.Width, c.Height,
                    c.Font != null ? string.Format(" {0:0.#}pt", c.Font.SizeInPoints) : "", name));
                if (c.Left < 0 || c.Top < 0 || c.Right > parent.ClientSize.Width || c.Bottom > parent.ClientSize.Height)
                {
                    sb.AppendLine(string.Format("    !! 超出父容器: {0} (父 {1}) 子底={2}", name, parent.ClientSize, c.Bottom));
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
            if (depth <= 1)
            {
                List<Control> boxes = new List<Control>();
                foreach (Control c in parent.Controls) if (c.Controls.Count > 0 || c is TextBox) boxes.Add(c);
                for (int i = 0; i < boxes.Count; i++)
                    for (int j = i + 1; j < boxes.Count; j++)
                        if (boxes[i].Bounds.IntersectsWith(boxes[j].Bounds))
                        {
                            sb.AppendLine(string.Format("    !! 重叠: [{0}] 与 [{1}]",
                                boxes[i].GetType().Name + ":" + boxes[i].Text, boxes[j].GetType().Name + ":" + boxes[j].Text));
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
                    sb.AppendLine(string.Format("form client={0}x{1} scale={2:0.##}", f.ClientSize.Width, f.ClientSize.Height, DpiScale));
                    if (dpiScaleForTest > 0f && dpiScaleForTest < ComputeDpiScale())
                    {
                        sb.AppendLine("注意: 模拟缩放低于本机真实 DPI 时，原生控件（TrackBar 最小高度随 DPI 走）与字号测量仍按真实 DPI，");
                        sb.AppendLine("      可能报少量 1-2px 的假溢出；只看『超出父容器』里超过 3px 的项即可。");
                    }
                    DumpTree(f, f, 0, sb, ref problems);
                    sb.AppendLine(problems == 0 ? "LAYOUT: PASS (无重叠/无溢出)" : "LAYOUT: FAIL (" + problems + " 处问题)");
                    f.Close();
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
                }
                else if (cmd == "--uninstall-autostart")
                {
                    const string taskName = "LecooFan";
                    const string runKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";
                    const string runName = "LecooFan";
                    string o;
                    int rc = RunTool("schtasks", "/Delete /TN \"" + taskName + "\" /F", out o);
                    sb.Append(o);
                    sb.AppendLine(rc == 0
                        ? "已删除开机自启动（计划任务 " + taskName + "）。"
                        : "删除失败（任务可能本来就不存在，或权限不足）。");
                    int rc2 = RunTool("reg", "delete \"" + runKey + "\" /v " + runName + " /f", out o);
                    sb.Append(o);
                    sb.AppendLine(rc2 == 0
                        ? "已删除启动项 " + runName + "（任务管理器 → 启动应用 里不再显示）。"
                        : "启动项删除失败（可能本来就不存在）。");
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
            try
            {
                System.IO.File.AppendAllText(LogFile, string.Format("[{0:HH:mm:ss}] {1}\r\n", DateTime.Now, msg));
            }
            catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            bool tray = false;
            foreach (string a in args) if (a == "--tray") tray = true;

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
