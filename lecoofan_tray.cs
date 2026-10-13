using System;
using System.Diagnostics;
using System.IO;
using System.Text;

// LecooFan 启动桩 —— 开机自启动的"可见入口"。
//
// 真正干活的是计划任务 \LecooFan（管理员权限、无声、直接进托盘）；
// 本程序不带应用程序清单（asInvoker），登录时被注册表 Run 键拉起后，
// 只是去触发那个计划任务。这样：
//   1. "任务管理器 → 启动应用"里能看到 LecooFan 这个启动项（可启用/禁用）；
//   2. 全程无窗口、无 UAC——提权从头到尾由计划任务负责。
//
// 计划任务不存在或被禁用时，退化为直接启动 LecooFanGUI.exe（会有一次 UAC）。
static class TrayLauncher
{
    const string TaskName = "LecooFan";

    static string LogPath()
    {
        try { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lecoofan_tray.log"); }
        catch { return "lecoofan_tray.log"; }
    }

    static void Log(string msg)
    {
        try
        {
            File.AppendAllText(LogPath(),
                string.Format("[{0:yyyy-MM-dd HH:mm:ss}] {1}\r\n", DateTime.Now, msg), Encoding.UTF8);
        }
        catch { }
    }

    /// <summary>无窗口跑一条命令，返回退出码。</summary>
    static int RunHidden(string exe, string args)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            Process p = Process.Start(psi);
            string o = p.StandardOutput.ReadToEnd();
            string e = p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            // 退出码必须在 Dispose 之前取：Dispose 之后再读 ExitCode 会抛
            // "没有与此对象关联的进程"，被 catch 当成命令失败（曾经因此误判任务不存在）
            int code;
            try { code = p.ExitCode; } catch { code = -1; }
            try { p.Dispose(); } catch { }
            if (o.Trim().Length > 0) Log("  | " + o.Trim());
            if (e.Trim().Length > 0) Log("  ! " + e.Trim());
            return code;
        }
        catch (Exception ex)
        {
            Log(exe + " ERROR: " + ex.Message);
            return -1;
        }
    }

    static void StartGui()
    {
        string gui = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LecooFanGUI.exe");
        if (!File.Exists(gui)) { Log("找不到 " + gui); return; }
        try
        {
            // UseShellExecute=true 才会走清单里的 requireAdministrator 提权
            ProcessStartInfo psi = new ProcessStartInfo(gui, "--tray");
            psi.UseShellExecute = true;
            Process.Start(psi);
            Log("已直接启动 " + gui + " --tray（这次有一次 UAC）");
        }
        catch (Exception ex) { Log("启动 GUI 失败: " + ex.Message); }
    }

    static int Main(string[] args)
    {
        Log("启动桩运行" + (args.Length > 0 ? " " + string.Join(" ", args) : ""));

        int rc = RunHidden("schtasks", "/Query /TN \"" + TaskName + "\"");
        if (rc != 0)
        {
            Log("计划任务 " + TaskName + " 不存在");
            StartGui();
            return 0;
        }

        rc = RunHidden("schtasks", "/Run /TN \"" + TaskName + "\"");
        if (rc != 0)
        {
            // 任务被禁用（用户主动关的）等：不再强启，交给任务本身去判断
            Log("触发计划任务失败（可能已被禁用），本次不强启");
            return 2;
        }

        Log("已触发计划任务 " + TaskName + "（若已在运行，本次请求被忽略）");
        return 0;
    }
}
