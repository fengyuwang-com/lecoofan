# LecooFan

**Lenovo N175L (Lecoo / 来酷) / ITE IT5570 EC 完整降噪方案**  
**Complete quiet-cooling stack for N175L laptop**

> 让这台笔记本彻底安静下来。从 CPU 降压到风扇曲线，全部管住。  
> Silence this machine. From CPU undervolt to PWM override — the whole stack.

---

## 目录 / Table of Contents

1. [安全警告 / Safety First](#-安全-warning--safety-first)
2. [系统全景 / System Overview](#-系统全景--system-overview)
3. [所有依赖 / All Dependencies](#-所有依赖--all-dependencies)
4. [安装与卸载 / Install & Uninstall](#-安装与卸载--install--uninstall)
5. [ThrottleStop 配置 / ThrottleStop Setup](#-throttlestop-配置--throttlestop-setup)
6. [CPU 静音限速 / Quiet CPU Limit](#-cpu-静音限速--quiet-cpu-limit)
7. [图形控制台 / GUI Console](#-图形控制台--gui-console)
8. [风扇控制 / Fan Control](#-风扇控制--fan-control)
9. [自启动 / Auto-Start](#-自启动--auto-start)
10. [一键恢复出厂 / One-Click Restore](#-一键恢复出厂--one-click-restore)
11. [风扇曲线调优 / Tuning the Curve](#-风扇曲线调优--tuning-the-curve)
12. [故障排除 / Troubleshooting](#-故障排除--troubleshooting)
13. [文件索引 / File Index](#-文件索引--file-index)
14. [技术原理 / How It Works](#-技术原理--how-it-works)

---

## ⚠ 安全警告 / SAFETY FIRST

**任何时候觉得风扇异常 / If you suspect ANY issue:**

```bash
cd C:\Projects\lecoofan
python restore_fan.py
```

或双击 `stop_fan.bat`。

这会立即停止所有自定义控制，让 EC 恢复出厂风扇逻辑。

**CPU 过热会永久损坏硬件。这个工具修改的是硬件层面的风扇控制，有风险。**  
**CPU overheating permanently damages hardware. This tool modifies fan control at the hardware register level. USE AT YOUR OWN RISK.**

---

## 🏗 系统全景 / System Overview

这台笔记本要真正安静，需要 **三个层面** 协同工作：

```
┌─────────────────────────────────────────────────┐
│                  应用层 / User Space              │
│                                                   │
│  ┌─────────────────────┐  ┌──────────────────┐  │
│  │   ThrottleStop      │  │  LecooFan GUI    │  │
│  │   (CPU undervolt    │  │  (fan control +  │  │
│  │    + Speed Shift)   │  │  CPU 4 modes)    │  │
│  │   ThrottleStop.exe  │  │  LecooFanGUI.exe │  │
│  │                     │  │  --tray 托盘模式  │  │
│  └─────────┬───────────┘  └────────┬─────────┘  │
│            │                       │            │
│  ┌─────────▼───────────────────────▼──────────┐ │
│  │        inpoutx64.dll (内核驱动)            │ │
│  │   C:\Program Files\LecooControlCenter\    │ │
│  └─────────┬───────────────────────┬──────────┘ │
├────────────┼───────────────────────┼────────────┤
│   内核层   │                       │            │
│   Kernel   │      MSR + MMIO       │   LPC I/O  │
├────────────┼───────────────────────┼────────────┤
│  硬件层    │                       │            │
│   Hardware │   ┌───────────────────▼────────┐   │
│            │   │      IT5570 EC 芯片        │   │
│            │   │  (8051 微控制器 @ 0x4E)    │   │
│            │   │                            │   │
│            │   │  HRAM 0x0400 ─→ CPU Temp   │   │
│            │   │  REG  0x1809 ─→ Fan PWM    │   │
│            │   │  HRAM 0x0476  ─→ Fan RPM   │   │
│            │   └────────────────────────────┘   │
│            │                                    │
│  ┌─────────▼──────────┐   ┌──────────────────┐  │
│  │  Intel Core Ultra  │   │   PWM→4-pin fan  │  │
│  │  5 125H (14核)     │   │   DC-DC converter│  │
│  └────────────────────┘   └──────────────────┘  │
└─────────────────────────────────────────────────┘
```

### 三层的作用 / Three Layers

| 层 | 工具 | 作用 | 效果 |
|----|------|------|------|
| **1. 降压** | ThrottleStop | CPU core/cache 降压 -50~-100mV | 温度降 5-15°C |
| **2. 节能策略** | ThrottleStop | Speed Shift EPP=80-128, 启用 C-States | 空闲时降频降功耗 |
| **3. 风扇控制** | LecooFan GUI / lecoofan.py | 150-250 ms 周期覆写 EC 寄存器 0x1809 | 按温度曲线控转速 |
| **4. CPU 静音限速** | quiet_cpu.bat / GUI | 关睿频 + EPP 省电 + 被动散热（点模式卡自带配套风扇） | 满载 84→66°C，风扇 ~3400→~2050 RPM |

**缺一层，效果就出不来。** 只控风扇不降压 = 高温 + 还是吵（因为 CPU 本身太热）。只降压不控风扇 = 温度降了但 EC 可能还是让风扇转太快。

**第 4 层是后来补上的**：前两层都在"降温"，风扇层只是"控制转速"，真正让笔记本安静下来的是把 CPU 发热量本身压下去——见下一节和各档实测数据。

---

## 📦 所有依赖 / All Dependencies

### 硬件 / Hardware

| 项目 | 值 | 备注 |
|------|------|------|
| **笔记本型号** | **Lenovo N175L** (Lecoo / 来酷) | 14 寸轻薄本，IT5570 EC |
| **CPU** | Intel Core Ultra 5 125H | Meteor Lake, 14核/18线程, 最高 4.5GHz |
| **EC 芯片** | ITE IT5570 | Super I/O, 8051 内核 |
| **EC 端口** | 0x4E / 0x4F | LPC 总线上的 Super I/O 配置空间 |
| **HRAM 基址** | 0x0400 | EC Host RAM 窗口 |
| **风扇 PWM 寄存器** | **0x1809** (绝对地址) | IT5570 PWM channel 7 |
| **CPU 温度寄存器** | HRAM + 0x70 = EC 0x0470 | 单位 °C |
| **风扇转速寄存器** | HRAM + 0x76/0x77 = EC 0x0476/0x0477 | 16位计数器 |

### 软件 / Software

#### [1] Python 3.12 — 运行风扇控制脚本
| 字段 | 值 |
|------|------|
| **路径** | `C:\Users\a8881\AppData\Local\Programs\Python\Python312\python.exe` |
| **版本** | 3.12.10 |
| **安装来源** | [python.org](https://www.python.org/downloads/) |
| **用途** | 运行 lecoofan.py 守护进程 |
| **额外包** | 无（只用标准库 ctypes） |
| **验证** | `python --version` |

#### [2] inpoutx64.dll — 内核级 IO 驱动
| 字段 | 值 |
|------|------|
| **路径** | `C:\Program Files\LecooControlCenter\inpoutx64.dll` |
| **来源** | 随 Lecoo Control Center 安装 |
| **用途** | 用户态程序通过此驱动访问 EC IO 端口 (Inp32/Out32) |
| **驱动服务** | `inpoutx64` (内核驱动) |
| **验证** | `python -c "import ctypes; d=ctypes.windll.LoadLibrary(r'C:\Program Files\LecooControlCenter\inpoutx64.dll'); print('Driver open:', bool(d.IsInpOutDriverOpen()))"` |
| **注意** | 驱动必须已加载。Lecoo 安装后自动加载。如果驱动未加载，脚本会报错。|

#### [3] ThrottleStop 9.7 — CPU 降压和频率管理
| 字段 | 值 |
|------|------|
| **路径** | `C:\FenglinApps\ThrottleStop_9.7\ThrottleStop.exe` |
| **配置** | `C:\FenglinApps\ThrottleStop_9.7\ThrottleStop.ini` |
| **来源** | [TechPowerUp](https://www.techpowerup.com/download/techpowerup-throttlestop/) |
| **用途** | CPU core/cache 降压、Speed Shift EPP、C-State 管理 |
| **版本** | 9.7 |
| **验证** | 运行 ThrottleStop.exe 查看主窗口，确认 FIVR 设置生效 |

#### [4] Lecoo Control Center — 提供 inpoutx64 驱动
| 字段 | 值 |
|------|------|
| **安装路径** | `C:\Program Files\LecooControlCenter\` |
| **可执行文件** | `C:\Program Files\LecooControlCenter\lecoo-center-qt5.exe` |
| **守护进程** | `C:\Program Files\LecooControlCenter\lecoo-ec-daemon.exe` |
| **驱动** | `C:\Program Files\LecooControlCenter\inpoutx64.dll` |
| **用途** | **仅用于提供 inpoutx64 驱动**。GUI 和 守护进程对 N175L 基本无用（风扇控制寄存器不对）。|

#### [5] Windows Task Scheduler — 自启动管理
| 字段 | 值 |
|------|-----|
| **命令行** | `schtasks.exe` |
| **任务名称** | `ThrottleStop` 和 `LecooFan` |
| **触发条件** | 用户登录时 (AtLogOn) |
| **权限** | 管理员权限 (Highest Available) |
| **LecooFan 任务内容** | `LecooFanGUI.exe --tray`（图形控制台托盘模式，2026-10-13 起；旧版是 python 守护进程） |
| **验证** | `schtasks /Query /TN ThrottleStop` → 显示状态 Ready |
| | `schtasks /Query /TN LecooFan` → 显示状态 Ready |

---

## 📥 安装与卸载 / Install & Uninstall

发到 GitHub Release 的包是 `LecooFan-Setup-1.0.0.zip`（约 120 KB），里面同时带编译好的 exe 和完整源码（AGPL 要求）。

### 安装 / Install

1. 解压 zip 到任意位置（**整个目录一起解压**，不要把 `install.bat` / `Install-LecooFan.ps1` 单独拷出来——它们旁边必须有 exe）。
2. 双击 `install.bat`。会弹一次 UAC（要写 Program Files 和注册表）。
3. 完了托盘里就有图标了。安装器做的事：

| 项 | 位置 |
|----|------|
| 程序本体 | `C:\Program Files\LecooFan\`（exe、脚本、README、LICENSE、`src\` 源码） |
| 计划任务 | `\LecooFan`：登录时以最高权限启动 `LecooFanGUI.exe --tray`（电池下也不停，真正干活的） |
| 启动项 | HKCU Run 键 → `C:\Program Files\LecooFan\LecooFanTray.exe`（任务管理器「启动应用」里可见、可禁用） |
| 卸载入口 | `HKLM\...\Uninstall\LecooFan`（设置 → 应用 → 「LecooFan 风扇控制台」里可卸载） |
| 开始菜单 | `所有用户\...\Programs\LecooFan\`：控制台快捷方式 + 卸载快捷方式 |

升级重装直接再跑一次 `install.bat`：同名任务/启动项/卸载入口会被覆盖刷新，`lecoofan_gui.cfg`（你的模式、转速上限等配置）会保留。

前提条件（缺了也能装，但风扇控制不可用）：`C:\Program Files\LecooControlCenter\inpoutx64.dll`（EC 驱动，见上面依赖 [2]/[4]）。

### 卸载 / Uninstall

三种入口任选其一：

- 开始菜单 → LecooFan → **卸载 LecooFan**
- 设置 → 应用 → 「LecooFan 风扇控制台」→ 卸载
- 直接双击 `C:\Program Files\LecooFan\uninstall.bat`

卸载器按顺序做：**先把风扇交还 EC**（跑一次 `LecooFanGUI.exe --fan-off`，绝不会把机器留在被压低的转速状态）→ 停止并删除计划任务 → 删 Run 启动项 → 删卸载入口和开始菜单快捷方式 → 删整个安装目录。全部清干净，无残留。

### 从源码自己打包 / Build the Package

```bash
# 1. 编译两个 exe（命令见「图形控制台」的重编译一节）
# 2. 打 zip：dist/LecooFan-Setup-1.0.0/（staging）+ LecooFan-Setup-1.0.0.zip
powershell -File installer\build-package.ps1 -Version 1.0.0
```

`build-package.ps1` 还有个 `-WithSfx` 开关想生成 7-Zip 自解压 exe，**本机不可用**：7-Zip 26.02 的 SFX 模块完全不读内嵌配置（`InstallPath`/`RunProgram` 都被忽略，退化成"往当前目录解压并弹覆盖询问"），表现为运行后卡住不动。默认只出 zip，装法一样（解压 → install.bat）。

---

## 🎛 ThrottleStop 配置 / ThrottleStop Setup

### 安装 / Installation

ThrottleStop 已经在 `C:\FenglinApps\ThrottleStop_9.7\`，绿色版不需要安装，双击直接运行。

如果重新安装：
1. 从 [TechPowerUp](https://www.techpowerup.com/download/techpowerup-throttlestop/) 下载
2. 解压到 `C:\FenglinApps\ThrottleStop_9.7\`
3. 双击 ThrottleStop.exe

### 配置项 / Settings that Matter

打开 ThrottleStop，以下是**全部需要关注的设置**：

#### FIVR 降压（最重要）

这是风扇能安静的核心前提。不降压，CPU 空闲就 80°C+，风扇不可能安静。

1. 点击 **FIVR** 按钮
2. 勾选 **Unlock Adjustable Voltage**
3. 设置以下偏移量（从 CPU 体质从低到高尝试）：

| 项目 | 建议值 | 安全范围 | 说明 |
|------|--------|---------|------|
| **Core Voltage Offset** | -60 mV | -30 ~ -100 mV | 核心电压偏移，大多数 Intel Ultra 5 125H 稳定在 -60mV |
| **Cache Voltage Offset** | -60 mV | -30 ~ -100 mV | 缓存电压偏移，通常和 Core 同步 |
| **System Agent (SA) Offset** | -40 mV | -20 ~ -60 mV | 系统代理（内存控制器等） |
| **Intel GPU Offset** | -40 mV | -20 ~ -80 mV | 核显电压偏移（不影响独显） |
| **iGPU Unslice Offset** | -40 mV | -20 ~ -60 mV | iGPU 非核心部分 |

**⚠ 降压测试流程：**
1. 先设置 -30mV → 应用（Apply）→ 运行 Cinebench 或 Prime95 10 分钟
2. 无蓝屏 → 再降 10mV → 再测试
3. 出现蓝屏或不稳定 → 回退到上一个稳定值
4. 每颗 CPU 体质不同，没有通用的安全值

**Battery (vs AC) 降压：** 如果想进一步省电，可以设置电池模式下的额外降压。

#### Speed Shift (MMIO)

Speed Shift 控制 CPU 对负载的响应速度。

| 设置 | 建议值 | 效果 |
|------|--------|------|
| **Speed Shift EPP (AC)** | 80 | 平衡性能和节能，让 CPU 在空闲时主动降频 |
| **Speed Shift EPP (Battery)** | 128 | 电池模式下更节能 |
| **Disable Turbo (short)** | 不勾选 | 保持睿频能力（安静模式可勾选） |

设置位置：主窗口 → 勾选 **Speed Shift** → 调节滑块到 EPP=80。

注意：EPP 值范围 0-255，0=最高性能，255=最节能。

#### C-States

CPU 空闲深度睡眠状态。启用后 CPU 在空闲时进入更深睡眠，大幅降功耗。

| 设置 | 建议值 | 效果 |
|------|--------|------|
| **C1E** | 启用 | 浅睡眠，快速唤醒 |
| **C3** | 启用 | 中等睡眠，时钟门控 |
| **C6/C7s** | 启用 | 深度睡眠，电压门控，省电效果明显 |
| **C8/C9/C10** | 启用 | 超深度睡眠，SoC 级断电 |

设置位置：主窗口 → **C States** 按钮 → 弹出窗口里全部启用。

但在 ThrottleStop 的 GState 窗口中设置。或者直接在 BIOS 里开启。

**注意：** Meteor Lake 的 C-State 控制有限，有些设置只能在 BIOS 改。

#### PROCHOT (热节流保护)

| 设置 | 建议值 | 说明 |
|------|--------|------|
| **PROCHOT Offset** | 0 (默认 95°C) | 不改。这是 CPU 热节流的触发温度，改了可能烧 CPU |
| **Disable PROCHOT** | **绝对不要勾选** | 禁用热保护。会烧 CPU。永远不要勾这个。|

#### 其他设置

| 设置 | 建议值 | 说明 |
|------|--------|------|
| **BD PROCHOT** | 保持启用 | 阻止显卡或其他设备拉高 CPU 功耗产生热量 |
| **SpeedStep** | 启用 | 旧版降频技术，Meteor Lake 上已不关键但保留 |
| **CLAMP** | 不勾选 | 限制 CPU 到最低倍频，非必要 |

### 验证降压生效

打开 ThrottleStop → 主窗口查看 **FIVR** 按钮旁边的电压值。
或者用 HWMonitor / HWiNFO 查看 Vcore 和温度。

**正常效果：** 降压后空闲温度从 80°C 降至 65-70°C，满载温度降 5-15°C。

---

## 🎚 CPU 静音限速 / Quiet CPU Limit

风扇曲线只能压转速，压不住发热源。这一层从 CPU 侧限速：**关睿频 + 省电偏好 + 被动散热**，让 CPU 少发热，风扇自然就慢。
想要图形界面就双击 `LecooFanGUI.exe`（见下一节），命令行则用 `quiet_cpu.bat` / `restore_cpu.bat`。

> **点模式卡 = CPU 设置 + 配套风扇一次配好**（高性能/平衡 → 风扇 EC 自动；安静 → 静音上限 2000 RPM；极静 → 1600 RPM）。
> 「静音上限」只锁转速，不再自动切换 CPU 档位——所以选「高性能」不会被偷换成安静档，性能满血、风扇该多吵就多吵。
> 安静档默认锁 2000 而不是 1800：实测两者听感无差别（1800 也只比 EC 地板 ~1700 高一档），2000 对散热略好一点。

### 本机实测：哪些键真的生效

| 设置 | 位置 | 本机结果 |
|------|------|----------|
| 最大处理器状态 `PROCTHROTTLEMAX` | 经典限频 | ❌ **无效**：设 50% 仍跑到 4.2GHz |
| 处理器最大频率 `PROCFREQMAX` | 绝对 MHz 上限 | ❌ **无效**：设 2400MHz 仍跑到 4273MHz |
| 处理器性能提升模式 `PERFBOOSTMODE` | 睿频开关 | ✅ **生效**（唯一真正压得住频率的键） |
| 能效偏好 `PERFEPP`（含 EPP1/EPP2） | HWP 提示 | ✅ 生效（往省电端迁移） |
| 系统散热方式 `SYSCOOLPOL` | 主动/被动 | ✅ 生效（被动 = 先降频再转风扇） |
| 处理器性能自主模式 `PERFAUTONOMOUS` | HWP | ⚠️ 生效，但实测效果相反，见下表 |

> 混合架构（P 核 + E 核）笔记本上 `PROCTHROTTLEMAX` 被固件/系统忽略是已知现象；
> 所以别只看设置项有没有写进去，要看**实测温度和跑分**。

### 四种模式与实测数据

点任一模式卡 = 改 CPU 电源设置 **+** 按下表配好风扇，之后手动改风扇算微调。

| 模式 | 关键设置 | 风扇配套 | 满载温度 | 风扇转速 | 单核跑分 | 全核跑分 |
|------|----------|----------|----------|----------|----------|----------|
| **高性能** | boost=2, EPP 0/0, 散热=主动 | EC 自动（放开） | 87 °C（120s 复核均 80°C） | ~4400 RPM（复核均 4600） | 0.31 s | 1.86 s |
| **平衡（原厂）** | boost=2, EPP 33/50, 散热=主动 | EC 自动 | 84 °C | ~3400 RPM | 0.35 s | 1.87 s |
| **安静（推荐）** | boost=0, EPP 60/70, 散热=被动 | 静音上限 2000 RPM | **≤69 °C**（120s 均 63.7°C） | **~2050 RPM**（均 2053） | 0.66 s | 2.22 s |
| **极静（实验）** | 安静 + 关 HWP 自主模式 | 静音上限 1600 RPM | ≤66 °C（120s 均 64.4°C） | ~1740 RPM（EC 地板） | 0.68 s | 2.27 s |

跑分 = 4×10⁶ 次整数乘加（14 进程满载 24 秒，单核取 3 次最好成绩，全核为墙钟时间）；CPU 参数没变，跑分数与原表一致。
温度/转速列 2026-10-13 用 120 秒长跑重测（配合套风扇后的真实表现：安静/极静不再风扇狂转，高性能风扇全放开）。
轻载时数字会好看很多（安静档空载 ≈ 45-48 °C / ~2090 RPM）。

**三点值得记：**
1. **"安静"档是甜点**：满载 84→66 °C，风扇从 ~3400 锁到 ~2050 RPM 且不再往上飙（72 °C 护栏兜底，全程未触发），代价是单核慢约 1.9 倍。
2. **多核代价很小**：全核跑分只慢约 15%（1.87→2.22 s）。14 核本来就撞功耗墙，限速主要伤单线程响应速度。
3. **"极静"不再反噬**：配上 1600 RPM 上限后它反而成为最安静的档（均 64.4 °C / ~1735 RPM，与安静档几乎同温——因为 1600 已低于 EC 自身地板 ~1700，实际坐底）。关 HWP 自主模式仍属实验，日常推荐"安静"。

**和 32GB 大内存的用法很搭**：内存密集型的长时间负载（虚拟机、容器、编译、一堆后台服务）耗的是内存带宽而不是瞬时算力，
切到"安静"档后整机稳定在 ~64-69 °C / ~2050 RPM 基本听不见的风扇，只有编译那类单线程环节会慢一些。

### 命令行用法

```bash
quiet_cpu.bat          # 安静档（推荐）
quiet_cpu.bat deep     # 实验极静档（会更热更吵）
restore_cpu.bat        # 还原出厂（睿频回来）
```

两个脚本会自己请求管理员权限，改的是**当前电源方案**，重启后依然有效。

---

## 🖥 图形控制台 / GUI Console

双击 `LecooFanGUI.exe`（会弹一次 UAC）。不需要安装任何东西——它是用 Windows 自带的 .NET Framework 编译器编译的 WinForms 程序。
高分辨率屏幕（缩放 125%~200%）已适配，布局按 DPI 等比放大；不想要窗口就用 `--tray` 只留托盘图标。

**界面四块：**

1. **CPU 模式** — 四个按钮一键切换（高性能 / 平衡(原厂) / 安静 / 极静(实验)），点一下 = CPU 电源设置 + 配套风扇一次配好；当前档位高亮并显示该档实测数据。
2. **实时监控** — EC 温度、风扇 RPM、PWM、最高核心频率、平均频率、负载；下面一行显示当前电源方案里真实的 `睿频 / EPP / 散热 / 自主模式 / 最大状态 / 最大频率`，用来确认点击真的生效了。
3. **微调** — EPP 滑块 (0-100) + 睿频开关 + 散热方式，自己试参数用。
4. **风扇控制** — EC 自动 / **静音上限（锁转速）** / 静音曲线（同 lecoofan.py 的曲线，250 ms 覆写 0x1809）/ 手动 PWM 滑块。**关窗口 = 收回托盘继续跑**（托盘里可随时再打开）；只有托盘菜单「退出」或温度 ≥92 °C 才会把风扇交还 EC。

右下角「压力测试 10 秒」会拉起 14 个线程，统计这段时间的**最高核心频率 / 最高温度 / 最高转速 / 平均负载**，
方便在两个档位之间直接对比（听感差别 + 数字差别）。

窗口内的日志同时写入同目录的 `lecoofan_gui.log`。

**命令行（免开窗口，可脚本化）：**

```bash
LecooFanGUI.exe --tray                # 托盘模式：不弹窗口，只在任务栏托盘待命（开机自启动用的就是这个）
LecooFanGUI.exe --install-autostart   # 设置开机自启动（计划任务，需管理员）
LecooFanGUI.exe --uninstall-autostart # 取消开机自启动
LecooFanGUI.exe --apply quiet     # 切换模式 (perf | bal | quiet | deep)
LecooFanGUI.exe --report          # 采样一次：温度/转速/PWM/频率/当前设置
LecooFanGUI.exe --fan-cap 2000 10 # 静音上限：10 秒内把转速锁在 2000 RPM
LecooFanGUI.exe --fan-curve 10    # 接管风扇 10 秒（走静音曲线）后交还
LecooFanGUI.exe --fan-off         # 立即交还风扇给 EC
LecooFanGUI.exe --diag            # EC 与性能计数器诊断
LecooFanGUI.exe --dumpui 1.25     # 布局自检：模拟 125% DPI 渲染一遍窗口，检查文字/控件是否溢出
```

### 把风扇锁在 2000 RPM（静音上限 / Silent Cap）

风扇控制里选 **「静音上限」**，转速下拉选 2000 RPM，点一下就是日常最安静的状态；
也可以直接点「安静」模式卡——CPU 和风扇会一起配好（模式卡自带风扇配套，「静音上限」本身只锁转速、不动 CPU 档位）。

它不是猜一个 PWM 值，而是**按实测转速闭环**：每 250 ms 读一次 EC 转速，比目标吵就把 PWM 收一级，
比目标安静就放一级（±120 RPM 死区，避免抖动/啸叫）。所以机器再热，转速也不会被 EC 自己拉上去。

**实测（本机空载 45-48 °C）：**

| 状态 | 写入 PWM | 实测转速 |
|------|----------|----------|
| 静音上限 2000 RPM | `0x18`~`0x22`（均值 0x20） | **1687-2202 RPM**（均 2090） |
| 静音上限 1800 RPM（旧默认） | `0x18`（EC 地板） | 1731-1963 RPM |
| 退出后 EC 自动 | `0x02` | 1653 RPM |

> EC 自身的下限约 **1700 RPM**，写比 `0x18` 更低的 PWM 会被 EC 抬回来——所以 1800 和 2000 都贴在地板上，
> **听感没有区别**（这也是安静档默认改 2000 的原因：一样安静，转速余量多 200 RPM，散热略好）。
> 比原厂空载常见的三千多转安静一大截；剩下那点转速波动是风扇惯性回落快慢（要几秒），不是控不住。

**120 秒满载实测（2026-10-13，模式卡整套预设，护栏全程未触发）：**

| 档位 + 风扇 | 均温 | 最高温 | 均转速 | 最高转速 |
|-------------|------|--------|--------|----------|
| 高性能 + EC 自动 | 79.7 °C | 88 °C | 4596 RPM | 5170 RPM |
| 安静 + 锁 2000 RPM | 63.7 °C | 69 °C | 2053 RPM | 2069 RPM |
| 极静 + 锁 1600 RPM | 64.4 °C | 66 °C | 1735 RPM | 2894 RPM（启动瞬态） |

> 高性能不再被压：风扇全放开（均值 4600 RPM），最高核心频率均值 3848 MHz，跑分回到原表水平；
> 安静/极静锁在地板附近，比原来"风扇自动"还凉（66 °C vs 原来 71 °C 且只有 ~1800 转）。
> 安静档 2000 RPM 的 120 秒里转速一直咬在 2050 上下（换向 0 次、PWM 只动过 2 次），温度全程没碰到 72 °C 护栏。
> 复现命令：`LecooFanGUI.exe --ui-modes 4`（自检 4 档整套预设）/ `--soak 120 perf auto` / `--soak 120 quiet silent`。

**两条安全边界（都是自动的，不用记）：**

1. **72 °C 护栏**：超过护栏温度后按每度 6 级 PWM **平滑**放开转速，宁可吵也不烧；温度降回来自动收回。
2. **≥92 °C**：立刻整个交还 EC 自动控制。

验证方式：把护栏临时压到 40 °C 复跑，PWM 立刻放开到 `0x72`、转速 4911 RPM；护栏正常时全程 `laidopen=False`、PWM 稳定在 `0x18`。

**注意**：锁转速是「本程序每 250 ms 覆写一次 0x1809」实现的，所以**程序不在跑就回到 EC 自动**（原厂曲线）。
想让它在后台长期保持：双击启动后**关窗口就是收回托盘**，进程继续跑；或者直接用开机自启动（见下面章节）。
另外**满载时锁不住 2000**——那不是限制失效，而是 2000 RPM 物理上散不掉那么多热，护栏会按温度把转速放上去。

`--report` / `--diag` 的结果会同时打印到控制台并写入 `gui_cli_report.txt`。

**自己重新编译：**

```bash
cd C:\Projects\lecoofan

# 图形控制台（带 requireAdministrator + dpiAware 清单）
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe -nologo -codepage:65001 ^
  -target:winexe -out:LecooFanGUI.exe -win32manifest:lecoofan_gui.manifest ^
  -r:System.dll -r:System.Windows.Forms.dll -r:System.Drawing.dll lecoofan_gui.cs lecoofan_ui.cs

# 开机启动桩（不能加清单：带了管理员清单每次登录都会弹 UAC）
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe -nologo -codepage:65001 ^
  -target:winexe -out:LecooFanTray.exe lecoofan_tray.cs
```

编译完重新跑一次 `LecooFanGUI.exe --install-autostart`（管理员）就会把新的 exe 挂到启动项上。

**注意**
- 需要管理员权限（改电源方案 + 加载 inpout 驱动都要）。
- 静音曲线不要和 `lecoofan.py` 守护进程同时接管 EC（GUI 启动时会检查 `lecoofan.pid` 并提醒）。
- 频率读数是性能计数器的 `Actual Frequency`；混合架构上它偏保守，判断快慢请以跑分为准。

### 高分辨率屏幕（DPI 适配）

程序带 `dpiAware` 清单且会按当前屏幕缩放**手动等比放大布局**。125% / 150% / 175%（本机）/ 200% 都用
`--dumpui <缩放>` 自检过：没有文字被截断、控件不重叠。100% 缩放的布局同样验证过（96 DPI 下控件的像素高度与设计值一致）。
字号按「点」给，交给 Windows 按 DPI 渲染——所以放大后不会出现"字小得看不清"或"字挤在一起"。

### 托盘模式 + 开机自启动

**"这个软件不启动就没法工作，是不是？"——对。** 锁转速/曲线都是进程活着才每 250 ms 覆写一次 EC，
进程没了（重启、手动退出、被杀）风扇就退回原厂曲线。所以推荐两种常驻方式：

1. **托盘模式**：`LecooFanGUI.exe --tray`，只出一个温度变色的风扇托盘图标（绿→黄→红），右键可"打开控制台 / 交还风扇 / 退出"。窗口关掉不是退出，是收回托盘。
2. **开机自启动**：`LecooFanGUI.exe --install-autostart`（管理员）。它装两处：
   - **计划任务 `\LecooFan`**（真正干活的）：登录时以最高权限启动托盘，不弹窗口；任务不会出现在"任务管理器 → 启动应用"里，但这是它无声无 UAC 的原因。
   - **启动项 `LecooFan`**（注册表 Run 键）：`任务管理器 → 启动应用` 里能看到、能一键禁用的就是它。它指向无管理员清单的启动桩 `LecooFanTray.exe`，登录时只去触发上面的计划任务，不提权、不弹 UAC。GUI 本体带 `requireAdministrator` 清单，直接挂 Run 键会在每次登录弹 UAC，所以中间加一层桩。

### 配置自动恢复

上次用的档位（CPU 模式 + 风扇模式 + 转速上限 + 微调值）存在同目录 `lecoofan_gui.cfg`，
下次启动（包括开机自启动的托盘模式）自动恢复并写入日志 `已恢复上次配置: cpu=... fan=...`。

---

## 🔧 风扇控制 / Fan Control

### 快速启动 / Quick Start

```bash
cd C:\Projects\lecoofan

# 查看当前状态
python lecoofan.py --status

# 10 秒测试
python lecoofan.py --test

# 前台运行（日志实时显示）
python lecoofan.py

# 后台静音运行
python lecoofan.py --quiet
```

### 双击操作 / Double-click

| 文件 | 操作 |
|------|------|
| `start_fan.bat` | 启动风扇控制（前台有日志窗口） |
| `stop_fan.bat` | 停止 + 恢复出厂设置（点击后确认） |
| `toggle_fan.bat` | 自动切换：运行中→恢复出厂，已停→启动 |

### 默认风扇曲线 / Default Fan Curve

```
CPU 温度      目标 PWM     实际 ~RPM     噪音感知
< 45°C        0x18 (24)    ~1700        近乎静音
  55°C        0x30 (48)    ~3100        轻微风声
  65°C        0x50 (80)    ~4000        可听见
  72°C        0x60 (96)    ~4400        明显
  78°C        0x70 (112)   ~4900        吵
> 85°C        0x78 (120)   ~5200        很吵（最高有效值）
```

中间温度线性插值。

### 更新日志频率

默认每 10 秒输出一行（`tick % 10 == 0`），PWM 变化时立即输出一行。

---

## 🚀 自启动 / Auto-Start

两个 Task Scheduler 任务已创建，登录后自动启动：

```bash
# 查看任务
schtasks /Query /TN ThrottleStop
schtasks /Query /TN LecooFan

# 手动运行（LecooFan 跑到托盘里）
schtasks /Run /TN ThrottleStop
schtasks /Run /TN LecooFan

# 禁用（不删除，只是暂停）
schtasks /Change /TN ThrottleStop /DISABLE
schtasks /Change /TN LecooFan /DISABLE

# 启用
schtasks /Change /TN ThrottleStop /ENABLE
schtasks /Change /TN LecooFan /ENABLE

# 完全删除
schtasks /Delete /TN ThrottleStop /F
schtasks /Delete /TN LecooFan /F

# 只重新安装 GUI 这条（推荐）
LecooFanGUI.exe --install-autostart
LecooFanGUI.exe --uninstall-autostart

# 两个任务都重新安装
powershell -ExecutionPolicy Bypass -File "C:\Projects\lecoofan\setup.ps1"
```

GUI 的开机自启动由**两处**配合（`--install-autostart` 一次装好）：

| 位置 | 做什么 | 在任务管理器里 |
|------|--------|----------------|
| 计划任务 `\LecooFan` | 真正干活：登录时以最高权限启动 GUI 托盘 | 不显示（计划任务都不显示） |
| Run 键 `HKCU\...\Run\LecooFan` | 指向启动桩 `LecooFanTray.exe`，登录时触发上面的计划任务 | **"启动应用"页可见、可禁用** |

> 为什么要两层：计划任务最可靠（最高权限、无声、无 UAC、拔电池也不停），但任务管理器"启动应用"页只列
> Run 键和启动文件夹，不列计划任务——用户看不到就没法管理。启动桩没有管理员清单，只负责触发任务，
> 所以两层加起来：既在列表里看得见，又全程不弹 UAC。GUI 本体不能直接挂 Run 键（清单要求管理员，每次登录都会弹 UAC）。
> 在"启动应用"里禁用 `LecooFan` 只会停掉桩（启动项消失），计划任务仍在跑；要全停：禁用页面里的项 + `schtasks /Change /TN LecooFan /DISABLE`。

### 任务详情 / Task Details

| 属性 | ThrottleStop | LecooFan |
|------|-------------|----------------|
| **可执行文件** | `ThrottleStop.exe` | `LecooFanGUI.exe` |
| **参数** | (无，直接启动) | `--tray`（托盘模式，不弹窗口） |
| **触发** | AtLogOn (登录时) | AtLogOn (登录时) |
| **延迟** | 无 | 无 |
| **权限** | Highest (管理员) | Highest (管理员) |
| **运行条件** | 电池/插电均可 | 电池/插电均可 |
| **超时** | 无限制 | 无限制 |
| **多实例** | 忽略新实例 | 忽略新实例（托盘里已有就不会开第二个） |

> **2026-10-13 变更**：`LecooFan` 任务原来启动的是 python 守护进程 `lecoofan.py --quiet`（15 秒延迟）。
> 现在改为启动图形控制台的托盘模式——功能是超集（静音曲线/锁转速/CPU 模式切换都在），而且两个会互相抢 EC，只能留一个。
> `setup.ps1` 已同步；旧 python 守护进程仍可手动跑 `python lecoofan.py --quiet`，但别和 GUI 同时开。
> 想回到纯 python 自启动：把任务动作改回 `-u "C:\Projects\lecoofan\lecoofan.py" --quiet` 即可（README 之外无其他依赖）。

---

## 🔄 一键恢复出厂 / One-Click Restore

**出任何问题，先跑这个：**

```
双击 stop_fan.bat
```

或

```bash
python restore_fan.py
```

### restore_fan.py 执行流程

```
[1/4] 停止 LecooFan 守护进程
  → kill lecoofan.py 进程
  → 删除 PID 文件

[2/4] 连接 EC
  → 加载 inpoutx64.dll
  → 确认驱动已加载

[3/4] 释放风扇控制
  → 读取当前 PWM（记录 before 状态）
  → 写 0x00 到 0x1809（释放手动控制）
  → 等待 3 秒让 EC 恢复
  → 读取当前 PWM（记录 after 状态）

[4/4] 验证
  → 确认 PWM 已从 0x00 变为 EC 自动值
  → 输出温度、PWM、风扇转速
  → 打印 "FAN CONTROL RESTORED TO FACTORY DEFAULTS"
```

### 恢复后的行为

- EC 固件会在 1-2 秒内重新接管风扇控制
- 风扇转速回到笔记本原厂曲线（通常偏保守/偏吵）
- 所有自定义控制完全停止
- 如果 10 秒后风扇还不转 → **立刻重启电脑**

---

## 🎯 风扇曲线调优 / Tuning the Fan Curve

编辑 `lecoofan.py` 顶部 `FAN_CURVE` 列表：

```python
FAN_CURVE = [
    (45, 0x18),   # (温度°C, PWM十六进制值)
    (55, 0x30),
    (65, 0x50),
    (72, 0x60),
    (78, 0x70),
    (85, 0x78),
]
```

### PWM 值与实际转速对照表

| 写 0x1809 | EC 实际值 | ~RPM | 噪音 |
|-----------|----------|------|------|
| `0x00` | ~0x18 (24) | ~1700 | ✅ 近乎静音，EC 强制下限 |
| `0x18` (24) | ~0x18 | ~1700 | ✅ 最低有效值 |
| `0x20` (32) | ~0x26 | ~2450 | ✅ 轻微 |
| `0x30` (48) | ~0x36 | ~3100 | ✅ 可接受 |
| `0x40` (64) | ~0x46 | ~3700 | ⚠ 可听见 |
| `0x50` (80) | ~0x4C | ~4000 | ⚠ 明显 |
| `0x60` (96) | ~0x5A | ~4400 | 🔇 吵 |
| `0x70` (112) | ~0x6A | ~4900 | 🔇 很吵 |
| `0x78` (120) | ~0x78 | ~5200 | 🔇 最大有效值 |
| `0x80`+ (128+) | ~0x78 | ~5200 | EC 强制上限 |

**注意：** EC 固件强制钳位。写 0x00 实际得到 ~0x18，写 0xFF 实际得到 ~0x78。

### 更安静曲线示例（风险更高）

```python
FAN_CURVE = [
    (55, 0x18),   # 55°C 以下：保持最低速 (1700 RPM)
    (70, 0x30),   # 70°C：~3100 RPM
    (80, 0x50),   # 80°C：~4000 RPM
    (90, 0x78),   # 90°C：最大 (5200 RPM)
]
```

这条曲线更安静，但 CPU 温度会更高。建议配合 ThrottleStop 降压后使用。

### 曲线安全原则

1. **最高温度点≥90°C 时 PWM 必须 ≥0x78** — 确保极限情况有足够散热
2. **最低温度点的 PWM 用 0x18** — EC 强制下限，写更低也没用
3. **修改后立即测试** — 运行 `python lecoofan.py --test` 观察 10 秒
4. **准备恢复手段** — 保持终端开着，随时 Ctrl+C + `python restore_fan.py`

---

## 🔍 故障排除 / Troubleshooting

### 风扇完全不转 / Fan Not Spinning

```
立刻：python restore_fan.py
等待 10 秒 → 如果还不转：重启电脑
```

### 驱动加载失败 / Driver Not Loading

```
Error: "inpoutx64 driver not loaded"
解决：确保 Lecoo Control Center 已安装
      C:\Program Files\LecooControlCenter\inpoutx64.dll 存在
      以管理员身份运行
```

### 开机后短暂显示「驱动未就绪」/ Cold-Start Retry at Logon

```
现象：开机自启动后状态栏短暂显示「inpout 驱动未就绪（自动重试中…）」，
      几秒内自动变成「已连接 (IT5570 @ 0x4E)」，无需任何操作。
原因：开机十几秒内 inpoutx64 内核驱动可能尚未加载，程序会先主动启动驱动服务，
      并每隔约 2 秒自动补连，直到成功（v1.0.1 起）。
若超过 30 秒仍不连接：
  1. 手动启动驱动服务，然后重启程序：sc start inpoutx64
  2. 检查计划任务是否以最高权限运行：schtasks /Query /TN LecooFan /V
```

### ThrottleStop 降压不生效 / Undervolt Not Working

```
现象：FIVR 里设置了偏移量，但 HWMonitor 显示电压没变
可能原因：
  - Meteor Lake (Intel Core Ultra) 的电压控制被 BIOS 锁定
  - 部分笔记本厂商锁了 undervolt，需要：
    a) 在 BIOS 中禁用 "Undervolt Protection"（如可用）
    b) 或使用 Grub 参数（仅 Linux）
  - 如果完全锁定，只能靠 Speed Shift EPP + 降低 PL1/PL2 来降热
```

### 风扇噪音没变化 / Fan Still Loud

```
1. 运行 lecoofan.py --status 查看温度
   → 如果温度 >80°C：风扇不可能安静，CPU 就是需要散热
   → 解决方向：ThrottleStop 降压 / 检查后台进程

2. 如果温度 <70°C 但风扇还是 >4000 RPM：
   → 曲线可能太激进，调低 FAN_CURVE 的 PWM 值

3. 检查是否有后台程序占 CPU：
   任务管理器 → 按 CPU 排序 → 杀掉非必要高占用进程
```

### Task Scheduler 任务不运行 / Task Not Starting

```bash
# 检查任务状态
schtasks /Query /TN LecooFan /V

# 手动运行测试
schtasks /Run /TN LecooFan

# 查看上次运行结果
schtasks /Query /TN LecooFan /V | find "Last Result"
```

### GBK 编码错误 / Encoding Errors

Python 在 Windows 上默认用 GBK 编码。如果脚本输出乱码或报 `UnicodeEncodeError`:

```bash
# 用 -u 参数运行（已内置在 bat 文件和 Task Scheduler 中）
python -u lecoofan.py

# 或设置环境变量
set PYTHONIOENCODING=utf-8
python lecoofan.py
```

---

## 📁 文件索引 / File Index

```
C:\Projects\lecoofan\          # ★ 本项目的根目录
│
├── lecoofan.py                       # 风扇控制守护进程（核心）
│   ├── class EC                     # EC 寄存器读写 (inpoutx64)
│   ├── temp_to_pwm()                # 温度→PWM 插值
│   ├── run_loop()                   # 主循环 (150ms 间隔)
│   ├── show_status()                # 状态输出
│   └── CLI: --status / --test / --quiet
│
├── restore_fan.py                   # 一键恢复出厂（安全网）
│   ├── ec_connect()                 # 连接 EC 驱动
│   ├── kill_fengfan()               # 杀守护进程
│   ├── 写入 0x00 到 0x1809         # 释放手动控制
│   └── 验证 EC 恢复自动控制         # 确认恢复
│
├── setup.ps1                        # 安装脚本 (Task Scheduler)
│   ├── 创建 ThrottleStop 任务       # 登录自启动
│   └── 创建 LecooFan 任务     # 登录自启动（GUI 托盘模式）
│
├── installer\                       # ★ 安装/卸载/打包（见「安装与卸载」一节）
│   ├── install.bat                  # 安装入口（自提权；等子进程跑完才退出）
│   ├── uninstall.bat                # 卸载入口（自提权；ARP 的卸载入口指向它）
│   ├── Install-LecooFan.ps1         # 安装逻辑：拷文件+任务+启动项+ARP+快捷方式+启动
│   ├── Uninstall-LecooFan.ps1       # 卸载逻辑：交还风扇→删任务/启动项/ARP/目录
│   └── build-package.ps1            # 打包：staging + zip（-WithSfx 可选，本机不可用）
│
├── start_fan.bat                    # 双击启动（前台窗口）
├── stop_fan.bat                     # 双击恢复出厂（安全入口）
├── toggle_fan.bat                   # 双击切换（智能切换）
│
├── quiet_cpu.bat                    # ★ CPU 静音限速（关睿频，等价 GUI 的"安静"档）
├── restore_cpu.bat                  # ★ 还原出厂 CPU 设置（睿频回来）
│
├── LecooFanGUI.exe                  # ★ 图形控制台（双击运行，需管理员；--tray 托盘模式）
├── lecoofan_gui.cs                  # GUI 源码（C# WinForms，无第三方依赖）
├── lecoofan_ui.cs                   # GUI 主题/控件源码（ModeCard/Sparkline 等）
├── lecoofan_gui.manifest            # requireAdministrator + dpiAware 清单
├── LecooFanTray.exe                 # ★ 开机启动桩（无清单，Run 键指向它；只触发计划任务）
├── lecoofan_tray.cs                 # 启动桩源码
├── lecoofan_tray.log                # 启动桩日志 (自动生成)
├── lecoofan_gui.cfg                 # GUI 配置：上次的 CPU/风扇档位 (自动生成)
├── lecoofan_gui.log                 # GUI 运行日志 (自动生成)
├── gui_cli_report.txt               # --report/--diag 输出 (自动生成)
├── lecoofan.pid                      # PID 文件 (自动生成)
└── README.md                        # ← 本文档

C:\FenglinApps\ThrottleStop_9.7\     # ThrottleStop 安装目录
├── ThrottleStop.exe                 # CPU 降压工具
└── ThrottleStop.ini                 # 配置文件

C:\Program Files\LecooControlCenter\ # Lecoo 控制中心
├── inpoutx64.dll                    # ★ 内核 IO 驱动（核心依赖）
├── lecoo-ec-daemon.exe              # 守护进程（对 N175L 无用）
└── lecoo-center-qt5.exe             # GUI（对 N175L 部分无用）

C:\Users\a8881\AppData\Local\Programs\Python\Python312\
└── python.exe                       # Python 3.12 解释器
```

---

## 🔬 技术原理 / How It Works

### EC 寄存器访问协议

ITE IT5570 EC 芯片通过 LPC 总线暴露 `0x4E/0x4F` 两个 IO 端口。

```
写 0x87, 0x01, 0x55, 0x55 → 0x4E    进入配置模式
写 0x2E → 0x11                        选择 LDN 0x11
写 0x2F → 地址高字节                   设置寄存器地址
写 0x2E → 0x10
写 0x2F → 地址低字节
写 0x2E → 0x12                        执行读写
读/写 0x2F → 数据
写 0x02 → 0x4E, 写 0x02 → 0x4F       退出配置模式
```

### 为什么需要 250ms 以内覆写

EC 内部有一个 8051 架构的固件，它约每 200ms 写入一次 `0x1809` 来执行自动风扇控制。我们的程序必须以 **更高频率** 写入才能覆盖它。实测 150ms 间隔能维持约 90% 的控制时间，GUI 用 250ms 闭环（读转速→调 PWM）同样锁得住——转速有惯性，250ms 的响应比风扇物理惯性快得多。

```
时间线:
EC:     ---W-------W-------W-------W---  (EC 每 ~200ms 写 0x1809)
我们:   -W--W--W--W--W--W--W--W--W--W-  (我们每 150ms 写 0x1809)
结果:   赢赢赢输赢赢赢赢赢赢赢输赢赢赢赢  (~90% 控制率)
```

当 EC 偶尔覆盖成功时（约 10% 时间），我们的下一次写入在 150ms 内就会把它改回来。对风扇转速的影响可忽略。

### 与 Lecoo 守护进程的冲突

`lecoo-ec-daemon.exe` 写 `EC 0x044B`（HRAM+0x4B）来控制风扇，但 **N175L 的风扇寄存器是 0x1809 而不是 0x044B**。这是 Lecoo Control Center 的 bug（它只适配了 N155A 等旧型号）。

| | Lecoo daemon | LecooFan |
|---|---|---|
| **写的地址** | 0x044B (HRAM+0x4B) | **0x1809** |
| **是否有效** | ❌ 对 N175L 无效 | ✅ 正确 |
| **写频率** | ~2s | 150ms |
| **温控曲线** | 不可配置 | 可配置 |

两个程序跑在一起不会互相干扰（写不同寄存器），但建议二选一，避免竞态。

### ThrottleStop 降压原理

CPU 核心电压和频率的关系：

```
功耗 ∝ 电容 × 电压² × 频率
```

降压 → 电压降低 → 功耗成平方关系下降 → 发热减少 → 风扇可以转更慢。

对 Intel Core Ultra 5 125H（Meteor Lake 架构），降压通过 MSR (Model-Specific Register) 实现。ThrottleStop 的 FIVR (Fully Integrated Voltage Regulator) 界面写入这些 MSR：

- `MSR 0x150`：核心电压偏移
- `MSR 0x151`：缓存电压偏移
- `MSR 0x152`：系统代理电压偏移

每个 CPU 体质不同，降压幅度需要逐个测试。

### 完整冷却链 / Complete Cooling Chain

```
操作                          →  结果
──────────────────────────────────────────────────────────────
ThrottleStop 降压 -60mV      →  CPU 满载功耗降 15-20%
Speed Shift EPP = 80         →  空闲时主动降频到 0.8-1.5GHz
启用 C-States                →  空闲时 SoC 进入深度睡眠
                              ↓
CPU 空闲温度从 85°C → 65°C   →  风扇可以跑 1700 RPM
CPU 满载温度从 95°C → 80°C   →  风扇跑 4000 RPM 就够
                              ↓
lecoofan.py 按曲线控 PWM      →  温度低 → 风扇低转速 → 安静
```

---

## 已知问题 / Known Issues

1. **Lecoo daemon 风扇地址错误** — 社区维护者没适配 N175L，风扇风扇控制寄存器用了 0x044B 而非 0x1809
2. **EC 固件钳位** — 写超出 0x18-0x78 范围的值被 EC 强制修正，无法突破
3. **EC 竞争覆盖** — 即使 150ms 轮询，仍有 ~10% 时间 EC 覆盖成功
4. **Meteor Lake 降压锁定** — 部分 BIOS 版本锁定 FIVR 降压，只能靠 Speed Shift
5. **inpoutx64 驱动依赖** — 驱动必须预装，无法自动部署

---

## License

GNU AGPL-3.0v3 — [LICENSE](LICENSE)

---

## 致谢 / Credits

- [LaVashikk/Lecoo-Control-Center](https://github.com/LaVashikk/Lecoo-Control-Center) — ITE EC 驱动和社区逆向工程
- UncleWebb / 8051.me 社区 — EC 寄存器映射资料
- TechPowerUp ThrottleStop — CPU 降压工具
- inpoutx64 — 用户态 IO 端口访问驱动

---

*Made with frustration and silence-seeking. 2025-2026*
