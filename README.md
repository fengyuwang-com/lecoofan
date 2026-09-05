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
4. [ThrottleStop 配置 / ThrottleStop Setup](#-throttlestop-配置--throttlestop-setup)
5. [风扇控制 / Fan Control](#-风扇控制--fan-control)
6. [自启动 / Auto-Start](#-自启动--auto-start)
7. [一键恢复出厂 / One-Click Restore](#-一键恢复出厂--one-click-restore)
8. [风扇曲线调优 / Tuning the Curve](#-风扇曲线调优--tuning-the-fan-curve)
9. [故障排除 / Troubleshooting](#-故障排除--troubleshooting)
10. [文件索引 / File Index](#-文件索引--file-index)
11. [技术原理 / How It Works](#-技术原理--how-it-works)

---

## ⚠ 安全警告 / SAFETY FIRST

**任何时候觉得风扇异常 / If you suspect ANY issue:**

```bash
cd C:\FengProj\LecooFan
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
│  │   ThrottleStop      │  │  LecooFan  │  │
│  │   (CPU undervolt    │  │  (PWM daemon)    │  │
│  │    + Speed Shift)   │  │  lecoofan.py      │  │
│  │   ThrottleStop.exe  │  │  150ms poll      │  │
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
| **3. 风扇控制** | LecooFan | 150ms 周期覆写 EC 寄存器 0x1809 | 按温度曲线控转速 |

**缺一层，效果就出不来。** 只控风扇不降压 = 高温 + 还是吵（因为 CPU 本身太热）。只降压不控风扇 = 温度降了但 EC 可能还是让风扇转太快。

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
|------|------|
| **命令行** | `schtasks.exe` |
| **任务名称** | `ThrottleStop` 和 `LecooFan` |
| **触发条件** | 用户登录时 (AtLogOn) |
| **权限** | 管理员权限 (Highest Available) |
| **验证** | `schtasks /Query /TN ThrottleStop` → 显示状态 Ready |
| | `schtasks /Query /TN LecooFan` → 显示状态 Ready |

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

## 🔧 风扇控制 / Fan Control

### 快速启动 / Quick Start

```bash
cd C:\FengProj\LecooFan

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

# 手动运行
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

# 重新安装
powershell -ExecutionPolicy Bypass -File "C:\FengProj\LecooFan\setup.ps1"
```

### 任务详情 / Task Details

| 属性 | ThrottleStop | LecooFan |
|------|-------------|----------------|
| **可执行文件** | `ThrottleStop.exe` | `python.exe` |
| **参数** | (无，直接启动) | `-u "C:\FengProj\LecooFan\lecoofan.py" --quiet` |
| **触发** | AtLogOn (登录时) | AtLogOn (登录时) |
| **延迟** | 无 | 15 秒 |
| **权限** | Highest (管理员) | Highest (管理员) |
| **运行条件** | 电池/插电均可 | 电池/插电均可 |
| **超时** | 无限制 | 无限制 |

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
C:\FengProj\LecooFan\          # ★ 本项目的根目录
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
│   └── 创建 LecooFan 任务     # 登录自启动
│
├── start_fan.bat                    # 双击启动（前台窗口）
├── stop_fan.bat                     # 双击恢复出厂（安全入口）
├── toggle_fan.bat                   # 双击切换（智能切换）
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

### 为什么需要 150ms 覆写

EC 内部有一个 8051 架构的固件，它约每 200ms 写入一次 `0x1809` 来执行自动风扇控制。我们的脚本必须以 **更高频率** 写入才能覆盖它。实测 150ms 间隔能维持约 90% 的控制时间。

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
