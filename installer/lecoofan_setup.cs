// lecoofan_setup.cs — LecooFan 安装程序（单文件 EXE 引导器）。
//   · 内嵌安装负载（payload/… 资源，由 build-package.ps1 打进 exe）
//   · 解压到 %TEMP%\LecooFanSetup，以管理员身份调用 Install-LecooFan.ps1
//   · 双击即装（manifest 已申请管理员）；支持 --silent 无界面静默安装
// 编译由 installer/build-package.ps1 完成（csc + setup.manifest + lecoofan.ico + 全部 payload 资源）。
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class Setup
{
    static bool silent;
    static volatile bool busy = true;         // 安装中禁止关窗（FormClosing 用它判断）
    static int exitCode = 1;
    static Form form;
    static Label lblStatus;
    static string workDir;

    [STAThread]
    static int Main(string[] args)
    {
        foreach (string a in args)
            if (a == "--silent" || a == "/silent" || a == "/S" || a == "-s") silent = true;

        if (!IsAdmin())
        {
            if (!silent) MessageBox.Show("安装需要管理员权限。\r\n请右键安装程序 → 以管理员身份运行。",
                "LecooFan 安装程序", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        bool createdNew;
        using (Mutex mx = new Mutex(true, "LecooFanSetupSingleInstance", out createdNew))
        {
            if (!createdNew)
            {
                if (!silent) MessageBox.Show("安装程序已经在运行。", "LecooFan 安装程序",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 1;
            }
            return Run();
        }
    }

    static int Run()
    {
        workDir = Path.Combine(Path.GetTempPath(), "LecooFanSetup");
        try { if (Directory.Exists(workDir)) Directory.Delete(workDir, true); } catch { }
        try { Directory.CreateDirectory(workDir); } catch { }

        if (silent)
        {
            exitCode = Install();
            if (exitCode == 0) Cleanup();
            return exitCode;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        form = BuildForm();
        Thread worker = new Thread(delegate()
        {
            exitCode = Install();
            busy = false;
            while (!form.IsHandleCreated) Thread.Sleep(50);
            try { form.BeginInvoke(new MethodInvoker(form.Close)); } catch { }
        });
        worker.IsBackground = true;
        worker.Start();
        Application.Run(form);

        if (exitCode == 0)
        {
            Cleanup();
            MessageBox.Show("安装完成！\r\n\r\nLecooFan 已在通知区运行，开机自启动已就绪。\r\n卸载：开始菜单 → LecooFan → 卸载 LecooFan。",
                "LecooFan 安装程序", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else
        {
            MessageBox.Show("安装失败（代码 " + exitCode + "）。\r\n\r\n详细日志：\r\n" + Path.Combine(workDir, "install.log"),
                "LecooFan 安装程序", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        return exitCode;
    }

    /// <summary>解压内嵌负载 → 跑 Install-LecooFan.ps1。返回安装脚本退出码（失败时日志留在 %TEMP%\LecooFanSetup\install.log）。</summary>
    static int Install()
    {
        string log = Path.Combine(workDir, "install.log");
        try
        {
            SetStatus("正在解压安装文件…");
            ExtractPayload(workDir);
            SetStatus("正在安装（注册计划任务与自启动）…");

            ProcessStartInfo psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -File \"" + Path.Combine(workDir, "Install-LecooFan.ps1") + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.Default;   // 中文系统上 PS 控制台输出是 GBK
            psi.StandardErrorEncoding = Encoding.Default;

            StringBuilder sb = new StringBuilder();
            object lk = new object();
            using (Process p = Process.Start(psi))
            {
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                { if (e.Data != null) lock (lk) sb.AppendLine(e.Data); };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                { if (e.Data != null) lock (lk) sb.AppendLine(e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();
                try { File.WriteAllText(log, sb.ToString(), Encoding.UTF8); } catch { }
                return p.ExitCode;
            }
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(log, "安装器内部错误:\r\n" + ex.ToString(), Encoding.UTF8); } catch { }
            return 2;
        }
    }

    /// <summary>把 exe 里 payload/… 的资源逐个写盘（构建脚本按 staging 目录逐个 /resource 打进去）。</summary>
    static void ExtractPayload(string dest)
    {
        Assembly asm = Assembly.GetExecutingAssembly();
        string prefix = "payload/";
        int n = 0;
        foreach (string name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string full = Path.Combine(dest, name.Substring(prefix.Length).Replace('/', '\\'));
            string dir = Path.GetDirectoryName(full);
            if (dir.Length > 0 && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            using (Stream src = asm.GetManifestResourceStream(name))
            using (FileStream fs = new FileStream(full, FileMode.Create, FileAccess.Write))
            {
                byte[] buf = new byte[65536];
                int r;
                while ((r = src.Read(buf, 0, buf.Length)) > 0) fs.Write(buf, 0, r);
            }
            n++;
        }
        if (n < 10) throw new Exception("安装包不完整：内嵌文件只有 " + n + " 个（预期 ≥10）");
    }

    static void Cleanup()
    {
        try { Directory.Delete(workDir, true); } catch { }   // 成功才清理；失败留现场和日志
    }

    static void SetStatus(string text)
    {
        if (silent || lblStatus == null) return;
        try { lblStatus.BeginInvoke(new MethodInvoker(delegate() { lblStatus.Text = text; })); }
        catch { }
    }

    static bool IsAdmin()
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

    static Form BuildForm()
    {
        const int W = 400, H = 152;
        Form f = new Form();
        f.Text = "LecooFan 安装程序";
        f.FormBorderStyle = FormBorderStyle.FixedDialog;
        f.MaximizeBox = false;
        f.MinimizeBox = false;
        f.StartPosition = FormStartPosition.CenterScreen;
        f.AutoScaleMode = AutoScaleMode.None;      // DPI 缩放全部手动做（同 GUI 的做法）
        f.ClientSize = new Size(W, H);
        f.BackColor = Color.White;
        f.FormClosing += delegate(object s, FormClosingEventArgs e) { if (busy) e.Cancel = true; };

        Label title = new Label();
        title.Text = "正在安装 LecooFan";
        title.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold);
        title.ForeColor = Color.FromArgb(32, 38, 46);
        title.SetBounds(24, 20, 352, 26);
        f.Controls.Add(title);

        lblStatus = new Label();
        lblStatus.Text = "正在准备…";
        lblStatus.Font = new Font("Microsoft YaHei UI", 9F);
        lblStatus.ForeColor = Color.FromArgb(90, 100, 112);
        lblStatus.SetBounds(24, 52, 352, 20);
        f.Controls.Add(lblStatus);

        Label hint = new Label();
        hint.Text = "安装过程约十几秒，会注册开机自启动并启动托盘。\r\n请勿关闭本窗口，完成后自动退出。";
        hint.Font = new Font("Microsoft YaHei UI", 8.5F);
        hint.ForeColor = Color.FromArgb(140, 148, 158);
        hint.SetBounds(24, 76, 352, 34);
        f.Controls.Add(hint);

        ProgressBar bar = new ProgressBar();
        bar.Style = ProgressBarStyle.Marquee;
        bar.MarqueeAnimationSpeed = 30;
        bar.SetBounds(24, 118, 352, 14);
        f.Controls.Add(bar);

        float sc;
        using (Graphics g = f.CreateGraphics()) sc = g.DpiX / 96f;   // 会顺带创建窗口句柄
        if (sc > 1.01f)
        {
            ScaleTree(f, sc);
            f.ClientSize = new Size((int)Math.Round(W * sc), (int)Math.Round(H * sc));
        }
        return f;
    }

    /// <summary>按 DPI 等比放大控件坐标（字号以"点"声明，由 GDI+ 自己按 DPI 渲染，不乘）。</summary>
    static void ScaleTree(Control root, float s)
    {
        foreach (Control c in root.Controls)
        {
            c.SetBounds((int)Math.Round(c.Left * s), (int)Math.Round(c.Top * s),
                        (int)Math.Round(c.Width * s), (int)Math.Round(c.Height * s));
            ScaleTree(c, s);
        }
    }
}
