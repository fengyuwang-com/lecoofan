// LecooFan GUI — 视觉组件 + 稳定性长跑工具（与 lecoofan_gui.cs 一起编译）
//
//   Card       卡片式面板（圆角、细边框、标题带强调色圆点）
//   ModeCard   模式卡（悬停/激活高亮，替代原来的普通按钮）
//   Sparkline  温度 + 转速 迷你趋势图
//   Soak       稳定性长跑：混合负载 + 每秒采样 + CSV + 稳定性判据 + 文字报告
//
// 全文件只用 .NET Framework 自带类库，无需任何外部依赖。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace LecooFanGui
{
    // ─────────────────────────────────────────────────────────────────────────
    //  主题
    // ─────────────────────────────────────────────────────────────────────────
    static class Ui
    {
        public static readonly Color WindowBg = Color.FromArgb(243, 245, 249);
        public static readonly Color CardBg = Color.White;
        public static readonly Color Border = Color.FromArgb(224, 229, 236);
        public static readonly Color Title = Color.FromArgb(66, 74, 86);
        public static readonly Color Muted = Color.FromArgb(130, 140, 152);
        public static readonly Color Text = Color.FromArgb(38, 44, 52);
        public static readonly Color Accent = Color.FromArgb(64, 132, 235);
        public static readonly Color Good = Color.FromArgb(34, 160, 94);
        public static readonly Color Warn = Color.FromArgb(224, 145, 30);
        public static readonly Color Bad = Color.FromArgb(214, 69, 65);
        public static readonly Color LogBg = Color.FromArgb(28, 33, 40);
        public static readonly Color LogText = Color.FromArgb(198, 208, 220);

        static Font _base, _cardTitle, _big, _small, _mono;
        public static Font FontBase { get { if (_base == null) _base = new Font("Microsoft YaHei UI", 9F); return _base; } }
        public static Font FontCardTitle { get { if (_cardTitle == null) _cardTitle = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold); return _cardTitle; } }
        public static Font FontBig { get { if (_big == null) _big = new Font("Microsoft YaHei UI", 19F, FontStyle.Bold); return _big; } }
        public static Font FontSmall { get { if (_small == null) _small = new Font("Microsoft YaHei UI", 8F); return _small; } }
        public static Font FontMono { get { if (_mono == null) _mono = new Font("Consolas", 8.5F); return _mono; } }

        public static Color ModeAccent(string key)
        {
            switch (key)
            {
                case "perf": return Color.FromArgb(233, 92, 61);
                case "bal": return Color.FromArgb(64, 132, 235);
                case "quiet": return Color.FromArgb(34, 160, 94);
                default: return Color.FromArgb(148, 92, 214);
            }
        }

        /// <summary>温度 → 颜色（给读数用）。</summary>
        public static Color TempColor(int t)
        {
            if (t < 0) return Muted;
            if (t < 60) return Good;
            if (t < 75) return Warn;
            return Bad;
        }

        public static GraphicsPath Round(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d - 1, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d - 1, r.Bottom - d - 1, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d - 1, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>做成扁平化的现代按钮外观。</summary>
        public static void StyleButton(Button b, Color accent)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = accent;
            b.FlatAppearance.MouseOverBackColor = Blend(accent, Color.White, 0.86);
            b.FlatAppearance.MouseDownBackColor = Blend(accent, Color.Black, 0.85);
            b.BackColor = Color.White;
            b.ForeColor = accent;
            b.Font = FontCardTitle;
            b.Cursor = Cursors.Hand;
        }

        public static Color Blend(Color a, Color b, double t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  卡片面板
    // ─────────────────────────────────────────────────────────────────────────
    class Card : Panel
    {
        public Card()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.CardBg;
            ForeColor = Ui.Text;
            Font = Ui.FontBase;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Ui.WindowBg);
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = Ui.Round(r, 7))
            {
                using (SolidBrush bg = new SolidBrush(Ui.CardBg)) g.FillPath(bg, p);
                using (Pen pen = new Pen(Ui.Border)) g.DrawPath(pen, p);
            }
            using (SolidBrush dot = new SolidBrush(Ui.Accent))
                g.FillEllipse(dot, 11, 11, 7, 7);
            using (SolidBrush t = new SolidBrush(Ui.Title))
                g.DrawString(Text, Ui.FontCardTitle, t, 24, 8);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  模式卡（替代原来的普通按钮）
    // ─────────────────────────────────────────────────────────────────────────
    class ModeCard : Control
    {
        public CpuMode Mode;
        public bool Active;
        bool hover;

        public ModeCard(CpuMode mode)
        {
            Mode = mode;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Ui.CardBg;
            ForeColor = Ui.Text;
            Cursor = Cursors.Hand;
            Font = Ui.FontBase;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Ui.CardBg);

            Color accent = Ui.ModeAccent(Mode.Key);
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = Ui.Round(r, 7))
            {
                Color fill = Active ? Ui.Blend(accent, Color.White, 0.88)
                            : (hover ? Color.FromArgb(250, 251, 253) : Color.White);
                using (SolidBrush bg = new SolidBrush(fill)) g.FillPath(bg, p);
                using (Pen pen = new Pen(Active ? accent : Ui.Border, Active ? 2f : 1f)) g.DrawPath(pen, p);
            }

            using (SolidBrush bar = new SolidBrush(accent))
                g.FillEllipse(bar, 12, 14, 9, 9);

            using (Font lf = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold))
            using (SolidBrush lb = new SolidBrush(Active ? Ui.Blend(accent, Color.Black, 0.25) : Ui.Text))
                g.DrawString(Mode.Label, lf, lb, 28, 9);

            Rectangle note = new Rectangle(28, 32, Width - 36, Height - 36);
            using (SolidBrush nb = new SolidBrush(Ui.Muted))
            using (StringFormat sf = new StringFormat())
            {
                sf.Trimming = StringTrimming.EllipsisCharacter;
                g.DrawString(Mode.Note, Ui.FontSmall, nb, note, sf);
            }
            if (Active)
            {
                using (Font af = new Font("Microsoft YaHei UI", 7.5F, FontStyle.Bold))
                using (SolidBrush ab = new SolidBrush(accent))
                    g.DrawString("当前", af, ab, Width - 34, 11);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  迷你趋势图：温度（°C）+ 转速（RPM）
    // ─────────────────────────────────────────────────────────────────────────
    class Sparkline : Control
    {
        const int Cap = 900;                 // 900 点 ≈ 7.5 分钟（500ms 采样）
        readonly List<float> temp = new List<float>();
        readonly List<float> rpm = new List<float>();
        readonly List<float> pwm = new List<float>();

        public Sparkline()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
            Font = Ui.FontBase;
        }

        public void Add(float t, float r, float p)
        {
            temp.Add(t); rpm.Add(r); pwm.Add(p);
            while (temp.Count > Cap) { temp.RemoveAt(0); rpm.RemoveAt(0); pwm.RemoveAt(0); }
            Invalidate();
        }

        public void ClearHistory() { temp.Clear(); rpm.Clear(); pwm.Clear(); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            Rectangle plot = new Rectangle(6, 6, Width - 12, Height - 22);
            using (Pen grid = new Pen(Color.FromArgb(238, 241, 246)))
            {
                for (int i = 0; i <= 4; i++)
                {
                    int y = plot.Top + plot.Height * i / 4;
                    g.DrawLine(grid, plot.Left, y, plot.Right, y);
                }
            }

            if (temp.Count < 2)
            {
                using (SolidBrush b = new SolidBrush(Ui.Muted))
                    g.DrawString("趋势图：正在采集…", Ui.FontSmall, b, plot.Left + 4, plot.Top + 4);
                DrawLegend(g, plot);
                return;
            }

            DrawSeries(g, plot, rpm, 0f, 5500f, Color.FromArgb(210, 218, 228), 1.4f);   // 转速（灰）
            DrawSeries(g, plot, temp, 25f, 100f, Ui.Accent, 2.0f);                       // 温度（蓝）
            DrawSeries(g, plot, pwm, 0f, 255f, Color.FromArgb(230, 176, 120), 1.0f);     // PWM（浅橙）

            float lastTemp = temp[temp.Count - 1];
            int ty = plot.Bottom - (int)((lastTemp - 25f) / 75f * plot.Height);
            using (SolidBrush b = new SolidBrush(Ui.Accent))
            using (Font f = new Font("Microsoft YaHei UI", 8F, FontStyle.Bold))
                g.DrawString(string.Format("{0:0}°C", lastTemp), f, b, plot.Right - 46, Math.Max(plot.Top, Math.Min(plot.Bottom - 14, ty - 8)));

            DrawLegend(g, plot);
        }

        void DrawSeries(Graphics g, Rectangle plot, List<float> data, float lo, float hi, Color color, float width)
        {
            int n = data.Count;
            PointF[] pts = new PointF[n];
            for (int i = 0; i < n; i++)
            {
                float x = plot.Left + (n == 1 ? 0 : (float)i / (n - 1) * plot.Width);
                float v = (data[i] - lo) / (hi - lo);
                if (v < 0) v = 0;
                if (v > 1) v = 1;
                pts[i] = new PointF(x, plot.Bottom - v * plot.Height);
            }
            using (Pen pen = new Pen(color, width)) { pen.LineJoin = LineJoin.Round; g.DrawLines(pen, pts); }
        }

        void DrawLegend(Graphics g, Rectangle plot)
        {
            using (Font f = new Font("Microsoft YaHei UI", 7.5F))
            {
                string s = string.Format("温度(蓝)   转速(灰 0-5500)   PWM(橙 0-255)   点数 {0}", temp.Count);
                using (SolidBrush b = new SolidBrush(Ui.Muted))
                    g.DrawString(s, f, b, plot.Left, plot.Bottom + 2);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  稳定性长跑（Soak）：混合负载 + 每秒采样 + CSV + 稳定性判据
    // ─────────────────────────────────────────────────────────────────────────
    class Soak
    {
        public class Row
        {
            public double Elapsed;
            public int Temp = -1, Rpm = -1, Pwm = -1, Target = -1;
            public bool Taken;
            public double MaxMhz, AvgMhz, Load;
        }

        public readonly List<Row> Rows = new List<Row>();
        public volatile bool Running;
        public string CsvPath = "";
        public string SummaryPath = "";
        public string Verdict = "";

        /// <summary>随机但不失控的混合负载：整数/浮点/内存遍历，偶尔短暂空转（考验风扇跟随）。</summary>
        static void Worker(int seed, Func<bool> running)
        {
            Random rnd = new Random(seed);
            double x = 1.234;
            int[] buf = new int[4096];
            while (running())
            {
                int kind = rnd.Next(100);
                Stopwatch slice = Stopwatch.StartNew();
                if (kind < 45)                                                  // 整数流水
                {
                    long a = 12345;
                    while (running() && slice.ElapsedMilliseconds < 1500)
                        for (int i = 0; i < 60000; i++) a = (a * 1103515245 + 12345) & 0x7FFFFFFF;
                }
                else if (kind < 80)                                             // 浮点/超越函数
                {
                    while (running() && slice.ElapsedMilliseconds < 1500)
                        for (int i = 0; i < 30000; i++) x = Math.Sqrt(x * 1.0000001 + 3.14159265);
                }
                else if (kind < 95)                                             // 内存遍历（超 32G 机器常见负载形态）
                {
                    while (running() && slice.ElapsedMilliseconds < 1500)
                        for (int i = 0; i < buf.Length; i++) buf[i] = (buf[i] + i) ^ (buf[(i * 7) % buf.Length]);
                }
                else                                                            // 短暂空转 → 观察风扇是否抖动
                {
                    Thread.Sleep(rnd.Next(150, 600));
                }
                if (x < 0 || double.IsNaN(x)) x = 1.234;
            }
        }

        static double Sum(List<int> v) { double s = 0; foreach (int x in v) s += x; return s; }
        static int Max(List<int> v) { int m = 0; foreach (int x in v) if (x > m) m = x; return m; }
        static int Min(List<int> v) { if (v.Count == 0) return 0; int m = int.MaxValue; foreach (int x in v) if (x < m) m = x; return m; }
        static double Std(List<int> v, double mean)
        {
            if (v.Count == 0) return 0;
            double s = 0; foreach (int x in v) s += (x - mean) * (x - mean);
            return Math.Sqrt(s / v.Count);
        }

        static string Settings()
        {
            try
            {
                return string.Format("boost={0} epp={1}/{2} cool={3}/{4} auton={5} maxst={6} freqmax={7}",
                    Powr.ReadAc(Powr.BOOST), Powr.ReadAc(Powr.EPP), Powr.ReadDc(Powr.EPP),
                    Powr.ReadAc(Powr.SYSCOOL), Powr.ReadDc(Powr.SYSCOOL), Powr.ReadAc(Powr.AUTON),
                    Powr.ReadAc(Powr.MAXST), Powr.ReadAc(Powr.FREQMAX));
            }
            catch (Exception ex) { return "read-error: " + ex.Message; }
        }

        public void Run(int seconds, Sampler sampler, FanMode fanMode, string label,
                        Action<string> log, Action done)
        {
            Running = true;
            Rows.Clear();
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            CsvPath = System.IO.Path.Combine(Application.StartupPath, "soak_" + stamp + ".csv");
            SummaryPath = System.IO.Path.Combine(Application.StartupPath, "soak_" + stamp + ".txt");

            string settingsBefore = Settings();
            int threads = Environment.ProcessorCount;
            log(string.Format("═══ 稳定性长跑开始 ═══ 档位 {0} / 风扇 {1} / {2} 秒 / {3} 线程",
                label, fanMode, seconds, threads));
            log("设置(开始): " + settingsBefore);

            FanMode savedMode = sampler.Mode;
            sampler.Mode = fanMode;

            List<Thread> pool = new List<Thread>();
            for (int i = 0; i < threads; i++)
            {
                int seed = i * 7919 + 13;
                Thread t = new Thread(delegate () { Worker(seed, delegate { return Running; }); });
                t.IsBackground = true;
                t.Start();
                pool.Add(t);
            }

            int errors = 0;
            Stopwatch sw = Stopwatch.StartNew();
            int lastTick = -30;
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                Thread.Sleep(1000);
                Row r = new Row();
                r.Elapsed = Math.Round(sw.Elapsed.TotalSeconds, 1);
                r.Temp = sampler.Temp; r.Rpm = sampler.Rpm; r.Pwm = sampler.Pwm;
                r.Target = sampler.TargetPwm; r.Taken = sampler.FanTakenOver;
                r.MaxMhz = sampler.MaxMhz; r.AvgMhz = sampler.AvgMhz; r.Load = sampler.LoadPct;
                Rows.Add(r);
                if (sampler.LastError.Length > 0) { errors++; log("采样错误: " + sampler.LastError); sampler.LastError = ""; }
                double el = sw.Elapsed.TotalSeconds;
                if (r.Temp >= 0 && (int)el - lastTick >= 30)
                {
                    lastTick = (int)el;
                    log(string.Format("  [{0:0}s/{1}s] {2:0}C  {3}RPM  PWM 0x{4:X2}  最高核心 {5:0}MHz  负载 {6:0}%",
                        el, seconds, r.Temp, r.Rpm, r.Pwm, r.MaxMhz, r.Load));
                }
            }
            Running = false;
            foreach (Thread t in pool) { try { t.Join(3000); } catch { } }
            log("负载已停止，等待 3 秒读取稳态…");
            Thread.Sleep(3000);

            sampler.Mode = savedMode;
            string settingsAfter = Settings();

            Verdict = Analyze(seconds, settingsBefore, settingsAfter, errors, label, fanMode);
            WriteCsv();
            WriteSummary(label, fanMode, seconds, settingsBefore, settingsAfter, errors);
            log(Verdict);
            log("CSV: " + System.IO.Path.GetFileName(CsvPath) + "   报告: " + System.IO.Path.GetFileName(SummaryPath));
            done();
        }

        // 稳定性判据
        string Analyze(int seconds, string before, string after, int errors, string label, FanMode fanMode)
        {
            StringBuilder sb = new StringBuilder();
            int n = Rows.Count;
            if (n < 5) return "数据太少，无法判断。";

            // 跳过开头 2-3 秒的预热样本（EC 第一次采样还没拿到值）
            int s0 = Math.Min(3, Math.Max(0, n / 10));

            // 只用有效样本统计（-1 = 该次没读到，不能参与均值/方差）
            List<int> tv = new List<int>();
            List<int> rv = new List<int>();
            double sumMaxMhz = 0, sumLoad = 0;
            int used = 0;
            for (int i = s0; i < n; i++)
            {
                Row r = Rows[i];
                used++;
                if (r.Temp > 0) tv.Add(r.Temp);
                if (r.Rpm > 0) rv.Add(r.Rpm);
                sumMaxMhz += r.MaxMhz; sumLoad += r.Load;
            }
            int nEff = Math.Max(1, used);
            int nT = Math.Max(1, tv.Count), nR = Math.Max(1, rv.Count);
            double avgT = Sum(tv) / nT, avgR = Sum(rv) / nR;
            int maxT = Max(tv), minT = Min(tv), maxR = Max(rv), minR = Min(rv);

            // 末端 1/4 温度均值：判断是否已经平台化（还在爬升 = 没到稳态）
            int tail = Math.Max(2, tv.Count / 4);
            double tailSum = 0;
            for (int i = Math.Max(0, tv.Count - tail); i < tv.Count; i++) tailSum += tv[i];
            double tailAvgT = tailSum / tail;
            double std = Std(tv, avgT);

            // 风扇换向次数（抖动/啸叫风险）
            int reversals = 0, pwmChanges = 0, pwmJumps = 0;
            int prevDir = 0, prevPwm = -1;
            for (int i = Math.Max(1, s0); i < n; i++)
            {
                int d = Rows[i].Rpm - Rows[i - 1].Rpm;
                if (Math.Abs(d) >= 40)
                {
                    int dir = Math.Sign(d);
                    if (prevDir != 0 && dir != prevDir) reversals++;
                    prevDir = dir;
                }
                if (Rows[i].Target > 0)
                {
                    if (prevPwm >= 0 && Rows[i].Target != prevPwm) pwmChanges++;
                    if (prevPwm >= 0 && Math.Abs(Rows[i].Target - prevPwm) >= 16) pwmJumps++;
                    prevPwm = Rows[i].Target;
                }
            }

            // 接管时的 EC 控制保持率
            int taken = 0, held = 0;
            for (int i = s0; i < n; i++)
            {
                Row r = Rows[i];
                if (r.Taken && r.Target > 0 && r.Pwm > 0)
                {
                    taken++;
                    if (Math.Abs(r.Pwm - r.Target) <= 4) held++;
                }
            }
            double hold = taken > 0 ? 100.0 * held / taken : -1;

            sb.AppendLine("═══ 稳定性结论 ═══");
            sb.AppendLine(string.Format("档位 {0} / 风扇 {1} / 时长 {2}s / 样本 {3}", label, fanMode, seconds, n));
            sb.AppendLine(string.Format("温度 平均 {0:0.0}C 最高 {1:0}C 最低 {2}C 抖动 σ={3:0.00}", avgT, maxT, minT, std));
            sb.AppendLine(string.Format("末端1/4均值 {0:0.0}C（与整体均值差 {1:+0.0;-0.0;0.0}C）", tailAvgT, tailAvgT - avgT));
            sb.AppendLine(string.Format("风扇 平均 {0:0} RPM 最高 {1} 最低 {2}；换向 {3} 次，PWM 目标变化 {4} 次（其中大跳变 {5} 次）",
                avgR, maxR, minR, reversals, pwmChanges, pwmJumps));
            sb.AppendLine(string.Format("频率 最高核心均值 {0:0} MHz；平均负载 {1:0}%", sumMaxMhz / nEff, sumLoad / nEff));
            if (hold >= 0) sb.AppendLine(string.Format("EC 接管期控制保持率 {0:0.0}%（{1}/{2} 次采样落在目标 ±4 内）", hold, held, taken));
            sb.AppendLine();
            sb.AppendLine("判据：");
            sb.AppendLine(string.Format("  [1] 温度平台化（末端均值不高于整体均值 3C）   {0}  ({1:+0.0;-0.0;0.0}C)",
                (tailAvgT - avgT) <= 3.0 ? "PASS" : "WARN", tailAvgT - avgT));
            sb.AppendLine(string.Format("  [2] 无热失控（最高温度 < 90C）                {0}  ({1:0}C)", maxT < 90 ? "PASS" : "FAIL", maxT));
            sb.AppendLine(string.Format("  [3] 温控有余量（最高温度 < 85C）              {0}  ({1:0}C)", maxT < 85 ? "PASS" : "WARN", maxT));
            sb.AppendLine(string.Format("  [4] 风扇不抖（换向 <= 40 次/轮）              {0}  ({1} 次)", reversals <= 40 ? "PASS" : "WARN", reversals));
            sb.AppendLine(string.Format("  [5] 负载达成（平均负载 >= 85%）               {0}  ({1:0}%)", (sumLoad / nEff) >= 85 ? "PASS" : "WARN", sumLoad / nEff));
            sb.AppendLine(string.Format("  [6] 设置未漂移                                {0}", before == after ? "PASS" : "FAIL"));
            if (before != after) sb.AppendLine("      开始: " + before + "\r\n      结束: " + after);
            sb.AppendLine(string.Format("  [7] 采样无异常                                {0}  ({1} 次)", errors == 0 ? "PASS" : "WARN", errors));
            if (hold >= 0)
                sb.AppendLine(string.Format("  [8] 接管保持率 >= 85%（EC 会定期抢回，属正常） {0}  ({1:0.0}%)", hold >= 85 ? "PASS" : "WARN", hold));
            return sb.ToString().TrimEnd();
        }

        void WriteCsv()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("elapsed_s,temp_c,fan_rpm,pwm,pwm_target,taken_over,max_core_mhz,avg_mhz,load_pct");
            foreach (Row r in Rows)
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "{0:0.0},{1},{2},{3},0x{4:X2},{5},{6:0},{7:0},{8:0}",
                    r.Elapsed, r.Temp, r.Rpm, r.Pwm, r.Target < 0 ? 0 : r.Target, r.Taken ? 1 : 0, r.MaxMhz, r.AvgMhz, r.Load));
            System.IO.File.WriteAllText(CsvPath, sb.ToString(), Encoding.UTF8);
        }

        void WriteSummary(string label, FanMode fanMode, int seconds, string before, string after, int errors)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("LecooFan 稳定性长跑报告");
            sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("机器: N175L / Intel Core Ultra 5 125H / 32GB");
            sb.AppendLine(Verdict);
            sb.AppendLine();
            sb.AppendLine("设置(开始): " + before);
            sb.AppendLine("设置(结束): " + after);
            sb.AppendLine("采样异常: " + errors);
            sb.AppendLine("CSV: " + System.IO.Path.GetFileName(CsvPath));
            System.IO.File.WriteAllText(SummaryPath, sb.ToString(), Encoding.UTF8);
        }
    }
}
