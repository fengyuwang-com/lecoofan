# FengFanControl

**Lenovo N175L (Lecoo) / ITE IT5570 EC 风扇控制**

> 让这台该死的笔记本风扇安静下来。  
> Make this damn laptop fan shut up.

---

## ⚠️ 安全警告 / SAFETY FIRST

**任何时候觉得风扇异常，先跑这个：**  
**If you suspect ANY issue, run this FIRST:**

```bash
python restore_fan.py
```

或双击 `stop_fan.bat`。

这个脚本一键恢复出厂设置：停止自定义控制，让 EC 固件重新接管风扇。  
One-click restore to factory: kills the daemon, releases the EC register, lets firmware resume auto-control.

**风扇控制不是闹着玩的 — CPU 过热会损坏硬件。**  
**Fan control is NOT a toy — CPU overheating can permanently damage hardware.**

底线：如果你不确定风扇曲线是否安全，就不要改。出厂曲线是硬件厂商验证过的。  
Bottom line: if you're unsure about the fan curve, don't change it. The factory curve was validated by the hardware vendor.

---

## 这是什么 / What Is This

FengFanControl 是一个 Python 守护进程，直接通过内核驱动读写笔记本 EC（Embedded Controller）寄存器，覆盖原厂风扇控制逻辑，实现更低转速、更安静的风扇曲线。

This is a Python daemon that directly controls the laptop's EC (Embedded Controller) fan register via a kernel driver, overriding the factory fan curve for quieter operation.

### 它做了什么 / What It Does

1. 每 1 秒读取 CPU 温度（EC HRAM+0x70）
2. 按温度→PWM 曲线计算目标转速
3. 写入 EC 寄存器 0x1809 覆盖风扇 PWM
4. EC 固件会试图恢复自动控制 → 脚本持续覆盖

### 它不做什么 / What It Does NOT Do

- ❌ 不降压 / Does not undervolt（需要 ThrottleStop）
- ❌ 不降频 / Does not throttle CPU
- ❌ 不改 BIOS / Does not modify BIOS
- ❌ 不刷 EC 固件 / Does not flash EC firmware

---

## 支持的硬件 / Supported Hardware

| Component | Value |
|-----------|-------|
| **Laptop** | Lenovo N175L (Lecoo) |
| **EC Chip** | ITE IT5570 |
| **Super I/O Port** | 0x4E |
| **HRAM Base** | 0x0400 |
| **Fan PWM Register** | **0x1809** (absolute EC address, PWM channel 7) |
| **CPU Temp Register** | HRAM + 0x70 (= EC 0x0470) |
| **Fan RPM Register** | HRAM + 0x76/0x77 (= EC 0x0476/0x0477, 16-bit counter) |
| **Kernel Driver** | `inpoutx64.dll` (from Lecoo Control Center) |

### 关键发现 / Key Findings

| Register | Purpose | Notes |
|----------|---------|-------|
| `0x1809` | CPU fan PWM (0x00-0xFF) | **这是我们要写的寄存器** |
| `HRAM+0x4B` (= 0x044B) | Lecoo 守护进程用的地址 | **对 N175L 完全无效**，这是一个 bug |
| `0x1803` | 键盘背光 (likely) | N161A 上验证过，不是 0x0F05 |
| `0x1802-0x1809` | PWM channels 0-7 | IT5570 有 8 个硬件 PWM 通道 |

#### EC 固件限制 / Firmware Constraints

EC 固件会对 PWM 值做钳位（clamping），你写什么值不一定得到什么值：

| Write | Actual | RPM | Note |
|-------|--------|-----|------|
| `0x00` | ~0x18 | ~1700 | EC 强制下限（最低转速安全保护） |
| `0x18` | ~0x18 | ~1700 | 最低有效值 |
| `0x40` | ~0x46 | ~3700 | |
| `0x78` | ~0x78 | ~5200 | 最高有效值 |
| `0x80+` | ~0x78 | ~5200 | EC 强制上限（最高转速保护） |

#### 默认风扇曲线 / Default Fan Curve

```
Temp < 45°C  → PWM 0x18 (~1700 RPM, near-silent)
Temp  55°C   → PWM 0x30 (~3100 RPM)
Temp  65°C   → PWM 0x50 (~4000 RPM)
Temp  72°C   → PWM 0x60 (~4400 RPM)
Temp  78°C   → PWM 0x70 (~4900 RPM)
Temp > 85°C  → PWM 0x78 (~5200 RPM max)
```

中间温度用线性插值。

---

## 文件说明 / File Map

```
FengFanControl/
├── fengfan.py          # 主守护进程 / Main daemon
├── restore_fan.py      # ⭐ 一键恢复出厂 / One-click factory restore
├── setup.py            # 安装自启动 / Install auto-start (Task Scheduler)
├── toggle_fan.bat      # 双击切换 / Double-click toggle
├── start_fan.bat       # 双击启动 / Double-click start
├── stop_fan.bat        # 双击停止+恢复 / Double-click stop + restore
├── fengfan.pid         # PID 文件（自动生成）/ PID file (auto-generated)
└── README.md           # 本文档 / This file
```

---

## 快速开始 / Quick Start

### 需求 / Requirements

- Windows 10/11
- Python 3.10+
- `inpoutx64.dll`（Lecoo Control Center 已安装时自动可用）

Lecoo Control Center 安装后 `inpoutx64.dll` 在：
`C:\Program Files\LecooControlCenter\inpoutx64.dll`

**Python 路径假设：** 脚本默认使用 `C:\Users\<用户名>\AppData\Local\Programs\Python\Python312\python.exe`。如果不同，请修改 `.bat` 文件中的 `PYTHON` 变量。

### 使用 / Usage

```bash
# 查看当前状态
python fengfan.py --status

# 前台运行（带日志）
python fengfan.py

# 后台静音运行
python fengfan.py --quiet

# 10 秒测试
python fengfan.py --test

# ⭐ 一键恢复出厂设置（出任何问题先跑这个）
python restore_fan.py
```

### 双击操作 / Double-Click Operations

| File | Action |
|------|--------|
| `start_fan.bat` | 启动风扇控制（前台窗口，可看到日志） |
| `stop_fan.bat` | 停止风扇控制 + 恢复出厂设置 |
| `toggle_fan.bat` | 切换：运行中→停止，已停→启动 |

**推荐工作流：** 双击 `stop_fan.bat` → 确认恢复正常 → 再双击 `start_fan.bat` 启动自定义曲线。

---

## 自启动安装 / Auto-Start Installation

```bash
# 安装（需要管理员权限）
python setup.py

# 卸载
python setup.py uninstall
```

安装后，每次登录自动启动 `fengfan.py --quiet`（延迟 15 秒）。

### Task Scheduler 手动控制

```bash
# 立即运行
schtasks /Run /TN FengFanControl

# 禁用自启动
schtasks /Change /TN FengFanControl /DISABLE

# 启用自启动
schtasks /Change /TN FengFanControl /ENABLE

# 删除任务
schtasks /Delete /TN FengFanControl /F
```

---

## 调整风扇曲线 / Tuning the Fan Curve

编辑 `fengfan.py` 顶部 `FAN_CURVE` 列表：

```python
FAN_CURVE = [
    (45, 0x18),   # (温度°C, PWM十六进制)
    (55, 0x30),
    (65, 0x50),
    (72, 0x60),
    (78, 0x70),
    (85, 0x78),
]
```

**规则：**
- PWM 范围：`0x00`-`0xFF`（0-255），但 EC 实际有效范围 ~`0x18`-`0x78`
- 温度从小到大排列
- 中间值线性插值自动计算
- 低于最低温度 → 用最低值，高于最高温度 → 用最高值

**更安静（风险更高的曲线示例）：**
```python
FAN_CURVE = [
    (50, 0x18),   # 50°C 以下保持最低转速
    (65, 0x30),   # 65°C 才稍微加速
    (80, 0x50),   # 80°C 才到~4000 RPM
    (90, 0x78),   # 90°C 才全速
]
```

**注意：** 曲线越保守（转速越低），CPU 温度越高。建议用 ThrottleStop 降压降低基础温度，否则安静风扇和高温 CPU 不可兼得。

---

## 配合 ThrottleStop 使用 / With ThrottleStop

这个脚本只控制风扇转速。要让风扇真正安静，需要同时降压 CPU。

推荐配置：
1. **ThrottleStop**: CPU Core/Cache 降压 -50mV 到 -100mV（具体取决于你的 CPU 体质）
2. **Speed Shift EPP**: 设置为 80-128（偏向节能）
3. **SpeedStep**: 启用
4. **C States**: 启用

降压后 CPU 温度通常下降 5-15°C，风扇可以跑到更低转速。

---

## 工作原理详解 / How It Works

### EC Register Access Protocol

IT5570 EC 芯片通过 LPC（Low Pin Count）总线连接 CPU，在 Super I/O 端口 0x4E/0x4F 上暴露配置空间。

访问 EC 寄存器的流程：

```
1. Enter configuration mode: 写 0x87, 0x01, 0x55, 0x55 到 0x4E
2. Select LDN (Logical Device Number) 0x11: 写 0x2E→0x11, 0x2F→LDN
3. Set address MSB:  写 0x2E→0x10, 0x2F→(addr>>8)
4. Set address LSB:  写 0x2E→0x11, 0x2F→(addr & 0xFF)
5. Read/Write data:  写 0x2E→0x12, 0x2F→data (读或写)
6. Exit config mode: 写 0x02 到 0x4E, 写 0x02 到 0x4F
```

```
+----------+     +----------+     +------------+
| fengfan  | --> | inpoutx64| --> | IT5570 EC  |
| (Python) |     | (driver) |     | (0x4E/0x4F)|
+----------+     +----------+     +-----+------+
                                         |
                                   +-----v------+
                                   | EC Firmware| ← 自动控制风扇
                                   | (8051 CPU) | ← 每 1s 写入 0x1809
                                   +------------+
```

### 为什么需要持续覆盖

EC 内部有一个 8051 架构的微控制器（固件），它会根据温度传感器自动调节风扇转速。它以约 1 秒为周期写入 `0x1809`。我们的脚本也是 1 秒周期写入同一个寄存器。谁最后写谁赢。

这就是为什么脚本间隔不能太长（>2s 会被 EC 覆盖回去），也不能太短（增加 CPU 负载无意义）。

### Lecoo Control Center 守护进程的问题

`lecoo-ec-daemon.exe` 也尝试控制风扇，但它使用 `HRAM+0x4B`（EC 寄存器 `0x044B`），这个地址对 N175L 不生效。同时，它的 HRAM 检测逻辑中温度阈值（80°C）对于 N175L（空闲~86°C）来说太严格，导致初始版本直接启动失败。

补丁方案：修改二进制 `0x15DA3` 处的比较字节从 `0x3F`（80°C）到 `0x4F`（96°C）。或者更好的方案：让社区维护者在 `EcOffsets` 结构中添加 `reg_fan_pwm` 字段，允许每个笔记本型号指定不同的风扇控制寄存器。

---

## 常见问题 / FAQ

### Q: 风扇完全不转了？

立刻双击 `stop_fan.bat` 或运行 `python restore_fan.py`。如果 10 秒后风扇还没恢复，重启电脑。

### Q: 风扇还是太吵？

1. 先运行 `python fengfan.py --status` 查看温度
2. 如果温度高（85°C+），风扇必然吵 → 需要 ThrottleStop 降压
3. 如果温度低（60°C以下）但风扇吵 → 调低 `FAN_CURVE` 的 PWM 值
4. 检查是否有后台进程占 CPU（任务管理器）

### Q: 开机自启动怎么关？

```bash
schtasks /Change /TN FengFanControl /DISABLE
```

### Q: 和 Lecoo Control Center 冲突吗？

`lecoo-ec-daemon.exe` 写的是 `0x044B`（HRAM+0x4B），不影响 `0x1809`，理论上不冲突。但两者都通过 `inpoutx64.dll` 访问 EC，如果同时进入 EC 配置模式可能产生微小的竞态条件。建议二选一。

### Q: 为什么不在 BIOS 里调？

N175L 的 BIOS 没有风扇控制选项。这不是联想的 ThinkPad 系列（有 Lenovo Fan Control），这是 Lecoo 廉价产品线，BIOS 功能极其有限。

### Q: 会 void warranty 吗？

读写 EC 寄存器不修改固件、不改 BIOS、不刷芯片。但实话实说：**任何内核级操作理论上都可能被厂商用来拒保**。风险自担。

---

## License

GNU General Public License v3.0

---

## 致谢 / Credits

- [LaVashikk/Lecoo-Control-Center](https://github.com/LaVashikk/Lecoo-Control-Center) — 社区逆向工程和 ITE EC 驱动
- 8051.me 社区 — EC 寄存器映射资料
- inpoutx64 驱动 — 用户态 IO 端口访问

---

*Made with frustration, silence-seeking. 2025-2026*
