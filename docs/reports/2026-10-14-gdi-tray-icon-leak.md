# 崩溃分析：`GDI+ 中发生一般性错误` @ `Bitmap.GetHicon()`（2026-10-14）

## 一句话结论

托盘图标每次重画都**泄漏一个 GDI 句柄**（实际是 3 个），跑了 12.7 小时后撞到 Windows 的
**每进程 10 000 个 GDI 对象上限**，`Bitmap.GetHicon()` 建不出新句柄就抛 `ExternalException (0x80004005)`，
异常从 UI 定时器里冒出来，程序当场死。

## 现场（用户报的弹窗）

```
System.Runtime.InteropServices.ExternalException (0x80004005): GDI+ 中发生一般性错误
   在 System.Drawing.Bitmap.GetHicon()
   在 LecooFanGui.MainForm.BuildFanIcon(Int32 temp)
   在 LecooFanGui.MainForm.UpdateTrayIcon()
   在 LecooFanGui.MainForm.RefreshUi()
   在 System.Windows.Forms.Timer.OnTick(EventArgs e)
```

`GetHicon()` 本身没有任何参数问题（同一张 32×32 位图，之前画了几千次都成功）——
它抛 `0x80004005` 的原因只有一个：**句柄表满了，建不出新的 HICON**。

## 根因

老代码：

```csharp
using (Bitmap bmp = new Bitmap(32, 32))
using (Graphics g = Graphics.FromImage(bmp))
{
    ...
    return Icon.FromHandle(bmp.GetHicon());     // ← 泄漏就在这一行
}
```

`Icon.FromHandle()` 包出来的 `Icon` 对象**不持有**那个 HICON（`ownsHandle = false`），
所以之后对旧图标做的 `old.Dispose()` 并不会销毁句柄 —— 而 `Bitmap`/`Graphics` 的 `using`
也不管 HICON（它属于 user32，不属于 GDI+ 的位图对象）。于是：

- 每次温度变化（`UpdateTrayIcon` 里按整度数变化触发）→ `GetHicon()` 新建 1 个图标句柄
  （外加遮罩位图和彩色位图共 **3 个 GDI 对象**）→ 一个都收不回来。
- 托盘实例从 2026-10-14 09:29 跑到 22:07（12.7 小时），句柄一路爬到 10 000 → 崩。

## 对照实验（可复现）

`--tray-icon-test` 是修好后加进程序里的回归自测（重建 N 次图标，量 GDI 对象数变化）。
下面是两种写法各跑 2 000 次的一次性对照（同一台机器、同一个进程内前后跑）：

| 写法 | 2 000 次重建后 GDI 对象 | 每轮 |
|---|---|---|
| 老：`Icon.FromHandle(bmp.GetHicon())` + `Dispose()` | 15 → **6 015** | **+3.000** |
| 新：`Clone()` + `finally { DestroyIcon(h); }` | 6 015 → 6 015 | **+0.000** |

按 +3/次算，**约 3 300 次重画就到 10 000 上限** —— 12.7 小时里平均每 13.8 秒变一次温度，完全对得上。

修复后的功能等价性也验过：`DestroyIcon` 掉原句柄之后，`Clone()` 出来的图标
`GetIconInfo()` 仍然成功，用 `Graphics.DrawIcon` 画出来的像素指纹与老写法**逐像素一致**
（`Clone()` 在 .NET Framework 里是 `CopyImage` 出一份自己持有的句柄，不是共享）。

## 修复（lecoofan_gui.cs）

1. **句柄归属写对**（`BuildFanIcon`）：

```csharp
IntPtr h = bmp.GetHicon();
try { return (Icon)Icon.FromHandle(h).Clone(); }   // Clone 出来的 Icon 自己持句柄
finally { DestroyIcon(h); }                        // 原句柄必须销毁，否则泄漏
```

2. **画失败不再能杀死程序**（`SafeFanIcon`）：托盘图标绘制包一层 try/catch，
   失败只记一条日志（`trayIconWarned` 防刷屏），风扇控制照常跑。
3. **兜底**：`AppDomain.UnhandledException` + `Application.ThreadException` 都写日志（带调用栈），
   界面线程的异常弹一次提示后继续运行。
4. **能提前发现**：日志每 5 分钟一行心跳，里面带 `GDI 对象` 数和内存 —— 这个泄漏如果有心跳，
   从 40 爬到 10 000 的趋势一眼就能看出来，不用等 12.7 小时后弹异常框。

## 防回归

```
LecooFanGUI.exe --tray-icon-test 5000
→ 托盘图标重建 5000 次: GDI 对象 8 → 9 (Δ1, 每轮 0.000)
→ ICON-LEAK: PASS (无泄漏)
```

`Δ1` 是 GDI+ 首次使用时的内部缓存（预热后测量，不随次数增长）。
只要这个数开始随次数线性增长，就是又漏了。
