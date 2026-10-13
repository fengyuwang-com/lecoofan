// LecooFan GUI — N175L (Ultra 5 125H / IT5570 EC) CPU & fan tuning console.
//
// Build (uses the .NET Framework compiler that ships with Windows — no installs):
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe ^
//       /out:LecooFanGUI.exe /win32manifest:lecoofan_gui.manifest ^
//       /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll lecoofan_gui.cs
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
    class CpuMode
    {
        public string Key, Label, Note;
        public uint Boost, EppAc, EppDc, CoolAc, CoolDc, Auton;

        public CpuMode(string key, string label, uint boost, uint eppAc, uint eppDc,
                       uint coolAc, uint coolDc, uint auton, string note)
        {
            Key = key; Label = label; Boost = boost; EppAc = eppAc; EppDc = eppDc;
            CoolAc = coolAc; CoolDc = coolDc; Auton = auton; Note = note;
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

        public Ec()
        {
            try
            {
                if (IsInpOutDriverOpen() == 0)
                {
                    Status = "inpout 驱动未加载 (需要管理员)";
                    return;
                }
                Available = true;
                Status = "已连接 (IT5570 @ 0x4E)";
            }
            catch (Exception ex)
            {
                Status = "加载 inpoutx64.dll 失败: " + ex.Message;
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
        public volatile int SilentRpmCap = 1800;
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
            new CpuMode("perf",  "高性能",   2,  0,  0, 1, 1, 1, "睿频开 + EPP 0 + 主动散热\n满载 87°C / ~4400 RPM"),
            new CpuMode("bal",   "平衡(原厂)", 2, 33, 50, 1, 0, 1, "出厂原始设置\n满载 84°C / ~3400 RPM"),
            new CpuMode("quiet", "安静",     0, 60, 70, 0, 0, 1, "关睿频 + EPP 60/70 + 被动\n满载 71°C / ~3200 RPM"),
            new CpuMode("deep",  "极静(实验)", 0, 80, 90, 0, 0, 0, "关 HWP 自主模式\n实测更热更吵 79°C / ~4600"),
        };

        static readonly string LogFile = System.IO.Path.Combine(Application.StartupPath, "lecoofan_gui.log");

        Ec ec = new Ec();
        Sampler sampler;
        Stress stress = new Stress();
        string daemonWarning = "";
        bool firstSampleLogged;

        ModeCard[] modeCards;
        Sparkline spark;
        Label lblMode, lblSub, lblTuneHint, lblFanState, lblFreqNote, lblSoakLast;
        Label valTemp, valRpm, valPwm, valMaxMhz, valAvgMhz, valLoad, valSys;
        TrackBar trkEpp, trkPwm;
        Label lblEppVal, lblPwmVal, lblRpmCap;
        CheckBox chkTurbo, chkSilentQuiet;
        ComboBox cmbCool, cmbSoakLen, cmbRpmCap;
        RadioButton radFanAuto, radFanCurve, radFanSilent, radFanManual;
        bool quietAppliedBySilent;
        static readonly int[] RpmCaps = { 1600, 1800, 2000, 2200, 2500, 3000 };
        TextBox txtLog;
        Button btnStress, btnSoak, btnFactory;
        Soak soak = new Soak();
        System.Windows.Forms.Timer uiTimer;

        public MainForm()
        {
            BuildUi();
            sampler = new Sampler(ec);
            sampler.Start();
            DetectDaemon();
            Log("LecooFan GUI 启动。EC: " + ec.Status);
            if (daemonWarning.Length > 0) Log(daemonWarning);
            RefreshSettingsLine();
            SyncModeButtons();
            uiTimer = new System.Windows.Forms.Timer();
            uiTimer.Interval = 500;
            uiTimer.Tick += delegate { RefreshUi(); };
            uiTimer.Start();
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
            radFanSilent = new RadioButton(); radFanSilent.Text = "静音上限"; radFanSilent.SetBounds(92, 26, 80, 20);
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

            // 静音上限的两个参数：目标转速 + 顺手把 CPU 也切到安静档
            lblRpmCap = new Label(); lblRpmCap.Text = "转速上限"; lblRpmCap.SetBounds(14, 68, 58, 16);
            lblRpmCap.ForeColor = Ui.Muted; lblRpmCap.Font = Ui.FontSmall;
            cardFan.Controls.Add(lblRpmCap);
            cmbRpmCap = new ComboBox();
            cmbRpmCap.DropDownStyle = ComboBoxStyle.DropDownList;
            foreach (int r in RpmCaps) cmbRpmCap.Items.Add(r + " RPM");
            cmbRpmCap.SelectedIndex = 1;                       // 1800 RPM
            cmbRpmCap.SetBounds(76, 64, 100, 22);
            cmbRpmCap.Enabled = false;
            cmbRpmCap.SelectedIndexChanged += delegate { ApplyRpmCap(); };
            cardFan.Controls.Add(cmbRpmCap);

            chkSilentQuiet = new CheckBox(); chkSilentQuiet.Text = "同步 CPU 安静档"; chkSilentQuiet.SetBounds(186, 65, 190, 20);
            chkSilentQuiet.ForeColor = Ui.Text;
            chkSilentQuiet.Checked = true;
            chkSilentQuiet.Enabled = false;
            cardFan.Controls.Add(chkSilentQuiet);

            lblFanState = new Label();
            lblFanState.SetBounds(14, 47, 380, 15);
            lblFanState.ForeColor = Ui.Muted;
            lblFanState.Font = Ui.FontSmall;
            lblFanState.Text = "状态: —";
            cardFan.Controls.Add(lblFanState);

            Label l3 = new Label(); l3.Text = "PWM"; l3.SetBounds(14, 92, 40, 16); l3.ForeColor = Ui.Muted; cardFan.Controls.Add(l3);
            trkPwm = new TrackBar();
            trkPwm.Minimum = Sampler.PWM_MIN; trkPwm.Maximum = Sampler.PWM_MAX;
            trkPwm.Value = 0x30; trkPwm.TickFrequency = 8; trkPwm.SetBounds(54, 86, 220, 45);
            trkPwm.ValueChanged += delegate
            {
                sampler.ManualPwm = trkPwm.Value;
                lblPwmVal.Text = string.Format("0x{0:X2}", trkPwm.Value);
            };
            cardFan.Controls.Add(trkPwm);
            lblPwmVal = new Label(); lblPwmVal.SetBounds(282, 92, 60, 16); lblPwmVal.ForeColor = Ui.Muted;
            lblPwmVal.Text = "0x30";
            cardFan.Controls.Add(lblPwmVal);

            Label l4 = new Label();
            l4.SetBounds(14, 134, 380, 34);
            l4.ForeColor = Ui.Warn;
            l4.Font = Ui.FontSmall;
            l4.Text = "静音上限：锁住转速（EC 下限 ~1700 RPM，比这更低锁不住）\n" +
                      "护栏 72°C：超温自动放开转速；退出/关闭/温度≥92°C 自动交还 EC";
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
                Log(string.Format("模式 → {0}  ({1})", m.Label, m.Note));
                SyncModeButtons();
                RefreshSettingsLine();
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
        /// 自检：走一遍用户点「静音上限」的真实事件链（转速下拉 → 单选 → 同步 CPU 安静档），
        /// 结束后交还 EC，并把测试前的 CPU 档位还原回去（当前是自定义设置时不动电源方案）。
        /// </summary>
        public string SelfTestSilent(int idx, int seconds)
        {
            StringBuilder r = new StringBuilder();
            if (idx < 0 || idx >= RpmCaps.Length) idx = 1;
            CpuMode before = CurrentMode();
            r.AppendLine(string.Format("before: cpu={0} boost={1} epp={2} cool={3}",
                before != null ? before.Key : "custom", Powr.ReadAc(Powr.BOOST), Powr.ReadAc(Powr.EPP), Powr.ReadAc(Powr.SYSCOOL)));
            bool testSync = before != null;
            if (!testSync) chkSilentQuiet.Checked = false;      // 无法精确还原就不碰电源方案

            cmbRpmCap.SelectedIndex = idx;                     // → ApplyRpmCap
            radFanSilent.Checked = true;                       // → FanModeChanged（含同步 CPU 安静档）
            Application.DoEvents();
            r.AppendLine(string.Format("clicked silent: cap={0} syncQuiet={1} sliderDisabled={2} cpuNow={3}",
                sampler.SilentRpmCap, testSync, !trkPwm.Enabled,
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
            if (testSync && before.Key != "quiet")
            {
                before.Apply();
                r.AppendLine("restored cpu mode: " + before.Key);
            }
            else r.AppendLine(testSync ? "cpu 本来就是 quiet，无需还原" : "跳过同步 CPU 分支（电脑当前是自定义设置，不碰电源方案）");
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
            }
            catch (Exception ex) { Log("微调失败: " + ex.Message); }
            finally { Cursor = Cursors.Default; }
        }

        int RpmCapValue()
        {
            int i = cmbRpmCap.SelectedIndex;
            return RpmCaps[i < 0 || i >= RpmCaps.Length ? 1 : i];
        }

        void ApplyRpmCap()
        {
            if (sampler == null) return;
            sampler.SilentRpmCap = RpmCapValue();
            if (radFanSilent.Checked) Log(string.Format("风扇: 转速上限 → {0} RPM", sampler.SilentRpmCap));
        }

        void FanModeChanged()
        {
            if (sampler == null) return;
            bool silent = radFanSilent.Checked;
            cmbRpmCap.Enabled = silent;
            chkSilentQuiet.Enabled = silent;
            trkPwm.Enabled = !silent;
            if (silent)
            {
                if (daemonWarning.Length > 0) Log("警告: " + daemonWarning);
                sampler.SilentRpmCap = RpmCapValue();
                sampler.Mode = FanMode.Silent;
                Log(string.Format("风扇: 静音上限 {0} RPM（转速闭环，护栏 {1}°C）", sampler.SilentRpmCap, sampler.SilentGuardC));
                // 勾了「同步 CPU 安静档」就顺手把发热源一起压下去，只在一进入本模式时做一次
                if (chkSilentQuiet.Checked && !quietAppliedBySilent)
                {
                    quietAppliedBySilent = true;
                    Log("风扇: 同步应用 CPU 安静档");
                    ApplyMode(FindMode("quiet"));
                }
            }
            else
            {
                quietAppliedBySilent = false;
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
                "2) 可选接管 EC 风扇（写 0x1809），退出时自动交还。\r\n" +
                "   风扇控制共四种：「EC 自动」不动手；「静音上限」按实测转速闭环，锁在选定 RPM 附近；\r\n" +
                "   「静音曲线」按温度查表（45→0x18 / 55→0x30 / 65→0x50 / 72→0x60 / 78→0x70 / 85→0x78）；\r\n" +
                "   「手动 PWM」固定写一个值。接管时每 250ms 写一次 0x1809。\r\n" +
                "   安全边界：EC 自身下限约 1700 RPM（写更低的 PWM 会被抬回来，所以 1800 附近就是地板）；\r\n" +
                "   「静音上限」自带 74°C 护栏，超温自动放开转速，温度≥92°C 一律交还 EC。\r\n" +
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
        }

        void RefreshFanState()
        {
            string s;
            if (!ec.Available) s = "EC 不可用，风扇控制已禁用。温度/转速请用 python lecoofan.py --status 查看。";
            else if (sampler.Mode == FanMode.Silent)
                s = string.Format("状态: 静音上限 {0} RPM — 实测 {1} RPM / PWM 0x{2:X2}{3}",
                    sampler.SilentRpmCap, sampler.Rpm, sampler.Pwm,
                    sampler.SilentLaidOpen ? string.Format("  ← 超过护栏 {0}°C，已放开转速", sampler.SilentGuardC) : "");
            else if (sampler.FanTakenOver) s = string.Format("状态: 已接管 EC  (目标 0x{0:X2}, 实测 0x{1:X2})", sampler.TargetPwm, sampler.Pwm);
            else s = "状态: EC 自动控制 (本程序未接管)";
            if (daemonWarning.Length > 0) s += "\r\n注意: python 守护进程在运行";
            lblFanState.Text = s;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try
            {
                soak.Running = false;
                if (sampler != null) { sampler.Mode = FanMode.Auto; sampler.Stop(); }
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
                sb.AppendLine(string.Format("{0}{1,-14} x={2,4} y={3,4} w={4,4} h={5,3}  {6}",
                    new string(' ', depth * 2), c.GetType().Name, c.Left, c.Top, c.Width, c.Height, name));
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
                    MainForm f = new MainForm();
                    f.Show();
                    Application.DoEvents();
                    sb.AppendLine(string.Format("form client={0}x{1}", f.ClientSize.Width, f.ClientSize.Height));
                    DumpTree(f, f, 0, sb, ref problems);
                    sb.AppendLine(problems == 0 ? "LAYOUT: PASS (无重叠/无溢出)" : "LAYOUT: FAIL (" + problems + " 处问题)");
                    f.Close();
                }
                else if (cmd == "--fan-hold" && args.Length >= 2)
                {
                    // 锁定固定 PWM 一段时间：用来验证"程序被强杀后 EC 是否自动接管"
                    string raw = args[1].Replace("0x", "").Replace("0X", "");
                    int pwm = Convert.ToInt32(raw, 16);
                    int secs = args.Length >= 3 ? int.Parse(args[2]) : 30;
                    Ec e = new Ec();
                    Sampler s = new Sampler(e);
                    s.Mode = FanMode.Manual;
                    s.ManualPwm = pwm;
                    s.Start();
                    for (int i = 0; i < secs * 2; i++)
                    {
                        Thread.Sleep(500);
                        sb.AppendLine(string.Format("t={0:0.0}s temp={1}C target=0x{2:X2} pwm=0x{3:X2} rpm={4} taken={5}",
                            i * 0.5, s.Temp, s.TargetPwm, s.Pwm, s.Rpm, s.FanTakenOver));
                    }
                    s.Mode = FanMode.Auto;
                    s.Stop();
                    sb.AppendLine("hold finished (graceful release)");
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
                else if (cmd == "--ui-silent" && args.Length >= 2)
                {
                    // 真窗体自检：点一下「静音上限」，看 UI 事件链 + 闭环是否真的生效
                    int rpm = int.Parse(args[1]);
                    int secs = args.Length >= 3 ? int.Parse(args[2]) : 3;
                    MainForm f = new MainForm();
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
                    s.Start();
                    Thread.Sleep(1500);
                    FanMode fm = fanArg == "curve" ? FanMode.Curve : (fanArg == "manual" ? FanMode.Manual : FanMode.Auto);
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
                    sb.AppendLine("       LecooFanGUI.exe --ui-silent <rpm> [sec]   # 真窗体自检：点一下「静音上限」");
                    sb.AppendLine("       LecooFanGUI.exe --fan-curve <sec> | --fan-hold <pwm> <sec> | --soak <sec> [mode] [fan] | --dumpui");
                }
            }
            catch (Exception ex) { sb.AppendLine("ERROR: " + ex.Message); }

            try { Console.Write(sb.ToString()); } catch { }
            string path = outFile != null
                ? outFile
                : System.IO.Path.Combine(Application.StartupPath, "gui_cli_report.txt");
            try { System.IO.File.WriteAllText(path, sb.ToString(), Encoding.UTF8); } catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length > 0) { RunCli(args); return; }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动失败: " + ex.Message + "\r\n\r\n请确认以管理员身份运行。", "LecooFan GUI");
            }
        }
    }
}
