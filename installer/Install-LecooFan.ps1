#Requires -Version 3.0
<#
.SYNOPSIS
    LecooFan 安装器：把 GUI + 启动桩装到 Program Files，并注册全部"服务"：
      1. 计划任务 \LecooFan        登录时以最高权限启动托盘（真正干活的）
      2. Run 键启动项 LecooFan     指向 LecooFanTray.exe（任务管理器"启动应用"可见）
      3. 卸载入口                  注册表 Uninstall 项（应用和功能里可卸载）
      4. 开始菜单快捷方式          控制台 + 卸载
    然后立刻启动一次。

    幂等：可重复运行（覆盖升级，保留 lecoofan_gui.cfg 用户配置）。
    从 install.bat 双击运行（会自动请求管理员权限）。
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "C:\Program Files\LecooFan",
    [switch]$NoStart
)

$ErrorActionPreference = "Stop"
$Version = "1.0.1"
$AppName  = "LecooFan"
$TaskName = "LecooFan"
$RunKeyPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$UninstallKey = "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\LecooFan"
$PkgDir = Split-Path -Parent $MyInvocation.MyCommand.Path

function Write-Step { param([string]$Text) Write-Host "`n>>> $Text" -ForegroundColor Cyan }
function Write-OK   { Write-Host "  [OK] " -ForegroundColor Green }
function Write-Warn { param([string]$Text) Write-Host "  [!] $Text" -ForegroundColor Yellow }

# ─── 管理员检查 ──────────────────────────────────────────────────────────────
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($id)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Warn "请从 install.bat 运行（它会自动申请管理员权限），或右键本脚本 → 使用 PowerShell 运行。"
    exit 1
}

# ─── 0. 停掉旧实例（先把风扇交还 EC，别把机器留在低转速状态）──────────────────
Write-Step "停止可能正在运行的旧实例 ..."
$running = Get-Process -Name "LecooFanGUI" -ErrorAction SilentlyContinue
if ($running) {
    # 先让旧实例把风扇交还 EC（用它自己的 --fan-off，走 EC 驱动，需要管理员）
    $old = $running | Select-Object -First 1
    $oldPath = $old.Path
    if ($oldPath -and (Test-Path $oldPath)) {
        Write-Host "  交还风扇给 EC ..."
        try { Start-Process -FilePath $oldPath -ArgumentList "--fan-off" -Wait -WindowStyle Hidden } catch {}
    }
    Stop-Process -Name "LecooFanGUI" -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
    Write-OK
} else { Write-Host "  (没有在跑的实例)" }

# ─── 1. 依赖检查（inpoutx64 驱动 DLL，来自 Lecoo Control Center）────────────────
Write-Step "检查依赖 ..."
$driver = "C:\Program Files\LecooControlCenter\inpoutx64.dll"
if (Test-Path $driver) { Write-OK "inpoutx64.dll 已安装" }
else {
    Write-Warn "未找到 $driver"
    Write-Warn "它是访问 EC 的内核驱动（随 Lecoo Control Center 安装）。没有它风扇控制不可用，但仍可先装。"
}

# ─── 2. 拷贝文件（保留旧配置） ────────────────────────────────────────────────
Write-Step "安装文件到 $InstallDir ..."
# 前置硬校验：两个 exe 必须和本脚本同目录（zip/自解压包解压后就是这样）。
# 缺了还继续装 = 装个空壳：任务启动了但 GUI 不存在。宁可不装。
foreach ($must in @("LecooFanGUI.exe", "LecooFanTray.exe")) {
    if (-not (Test-Path (Join-Path $PkgDir $must))) {
        Write-Warn "安装包不完整：本脚本旁边找不到 $must。"
        Write-Warn "请确保整个 zip 解压后再运行 install.bat（不要单独把 ps1/bat 拷出来）。"
        exit 1
    }
}
if (-not (Test-Path $InstallDir)) { New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null }
$cfgBackup = $null
$cfgPath = Join-Path $InstallDir "lecoofan_gui.cfg"
if (Test-Path $cfgPath) { $cfgBackup = Get-Content $cfgPath -Raw }   # 升级时保留用户配置

foreach ($f in @("LecooFanGUI.exe", "LecooFanTray.exe", "Install-LecooFan.ps1",
                 "Uninstall-LecooFan.ps1", "install.bat", "uninstall.bat", "README.md", "LICENSE")) {
    $src = Join-Path $PkgDir $f
    if (Test-Path $src) { Copy-Item $src -Destination $InstallDir -Force }
}
# 源码一起装（AGPL 要求随二进制提供源代码）
$srcDir = Join-Path $PkgDir "src"
if (Test-Path $srcDir) { Copy-Item $srcDir -Destination $InstallDir -Recurse -Force }
if ($cfgBackup -ne $null) { Set-Content -Path $cfgPath -Value $cfgBackup -NoNewline }
Write-OK

# ─── 3. 计划任务（登录时 / 最高权限 / 直接进托盘 / 电池友好） ──────────────────
Write-Step "注册计划任务 $TaskName ..."
$gui = Join-Path $InstallDir "LecooFanGUI.exe"
$action  = New-ScheduledTaskAction -Execute $gui -Argument "--tray"
$trigger = New-ScheduledTaskTrigger -AtLogOn
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -StartWhenAvailable -ExecutionTimeLimit 0 -MultipleInstances IgnoreNew
$taskPrincipal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" `
    -LogonType Interactive -RunLevel Highest
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings `
    -Principal $taskPrincipal -Description "LecooFan GUI: N175L EC fan control + CPU modes (tray autostart)" -Force | Out-Null
Write-OK

# ─── 4. Run 键启动项（任务管理器"启动应用"可见，指向启动桩，不弹 UAC）──────────
Write-Step "添加启动项（任务管理器 → 启动应用 可见） ..."
$stub = Join-Path $InstallDir "LecooFanTray.exe"
Set-ItemProperty -Path $RunKeyPath -Name $AppName -Value "`"$stub`""
Write-OK

# ─── 5. 卸载入口（应用和功能） + 开始菜单快捷方式 ─────────────────────────────
Write-Step "注册卸载入口和开始菜单快捷方式 ..."
$uninstBat = Join-Path $InstallDir "uninstall.bat"
# Set-ItemProperty 不能建键：键不存在时先建，否则整个向导在这步炸掉
if (-not (Test-Path $UninstallKey)) { New-Item -Path $UninstallKey -Force | Out-Null }
Set-ItemProperty -Path $UninstallKey -Name "DisplayName"     -Value "LecooFan 风扇控制台"
Set-ItemProperty -Path $UninstallKey -Name "DisplayVersion"  -Value $Version
Set-ItemProperty -Path $UninstallKey -Name "Publisher"       -Value "Fengyu Wang"
Set-ItemProperty -Path $UninstallKey -Name "DisplayIcon"     -Value $gui
Set-ItemProperty -Path $UninstallKey -Name "InstallLocation" -Value $InstallDir
Set-ItemProperty -Path $UninstallKey -Name "UninstallString" -Value "`"$uninstBat`""
Set-ItemProperty -Path $UninstallKey -Name "QuietUninstallString" -Value "`"$uninstBat`""
Set-ItemProperty -Path $UninstallKey -Name "NoModify"        -Value 1
Set-ItemProperty -Path $UninstallKey -Name "NoRepair"        -Value 1
Set-ItemProperty -Path $UninstallKey -Name "URLInfoAbout"    -Value "https://github.com/fengyuwang-com/lecoofan"

$ws = New-Object -ComObject WScript.Shell
$smDir = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\LecooFan"
if (-not (Test-Path $smDir)) { New-Item -ItemType Directory -Path $smDir -Force | Out-Null }
$lnk = $ws.CreateShortcut((Join-Path $smDir "LecooFan 控制台.lnk"))
$lnk.TargetPath = $gui; $lnk.WorkingDirectory = $InstallDir; $lnk.Description = "LecooFan 风扇控制台（托盘）"; $lnk.Save()
$lnk2 = $ws.CreateShortcut((Join-Path $smDir "卸载 LecooFan.lnk"))
$lnk2.TargetPath = $uninstBat; $lnk2.WorkingDirectory = $InstallDir; $lnk2.Description = "卸载 LecooFan"; $lnk2.Save()
Write-OK

# ─── 6. 立刻启动一次 ─────────────────────────────────────────────────────────
if ($NoStart) { Write-Host "  (按参数要求不自动启动)" }
else {
    Write-Step "启动 ..."
    Start-ScheduledTask -TaskName $TaskName
    Start-Sleep -Seconds 2
    if (Get-Process -Name "LecooFanGUI" -ErrorAction SilentlyContinue) { Write-OK "托盘已在通知区运行" }
    else { Write-Warn "启动失败，可手动运行: $gui --tray" }
}

Write-Host ""
Write-Host "安装完成。版本 $Version，位置 $InstallDir" -ForegroundColor Green
Write-Host "  - 开机自启动已就绪（任务管理器 → 启动应用 里的 LecooFan）"
Write-Host "  - 卸载：开始菜单 → LecooFan → 卸载，或 应用和功能 → LecooFan 风扇控制台 → 卸载"
Write-Host ""
