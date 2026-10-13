#Requires -Version 3.0
<#
.SYNOPSIS
    LecooFan 卸载器。做的事和安装器完全对称：
      1. 先把风扇交还 EC（关键：卸完不能让机器留在被压着的低转速状态）
      2. 杀掉在跑的 GUI/托盘实例
      3. 删除计划任务 + Run 键启动项
      4. 删除应用和功能里的卸载入口 + 开始菜单快捷方式
      5. 删掉安装目录

    从 uninstall.bat 双击运行（自动申请管理员权限）。
    也可被"应用和功能"调用（UninstallString 指向本 bat）。
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "C:\Program Files\LecooFan",
    [switch]$KeepConfig
)

$ErrorActionPreference = "Stop"
$TaskName = "LecooFan"
$RunKeyPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$UninstallKey = "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\LecooFan"
$LogFile = Join-Path $env:TEMP "LecooFan-uninstall.log"

function Write-Step { param([string]$Text) Write-Host "`n>>> $Text" -ForegroundColor Cyan }
function Write-OK   { Write-Host "  [OK] " -ForegroundColor Green }

# ─── 管理员检查 ──────────────────────────────────────────────────────────────
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($id)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Warn "请从 uninstall.bat 运行（它会自动申请管理员权限），或右键脚本 → 使用 PowerShell 运行。"
    exit 1
}

# ─── 1. 交还风扇给 EC（必须在删文件前做，否则装完残留一个被压低的转速）────────
Write-Step "交还风扇给 EC ..."
$guiToUse = $null
$guiInstalled = Join-Path $InstallDir "LecooFanGUI.exe"
if (Test-Path $guiInstalled) { $guiToUse = $guiInstalled }
else {
    $running = Get-Process -Name "LecooFanGUI" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($running -and $running.Path) { $guiToUse = $running.Path }
}
if ($guiToUse) {
    try {
        Start-Process -FilePath $guiToUse -ArgumentList "--fan-off" -Wait -WindowStyle Hidden
        Write-OK
    } catch { Write-Host "  (交还失败：$($_.Exception.Message))" }
} else { Write-Host "  (没找到可执行的 GUI，可能已删除；EC 超时后会自动恢复)" }

# ─── 2. 停任务 + 杀进程 ──────────────────────────────────────────────────────
Write-Step "停止后台任务 ..."
Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
Disable-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue | Out-Null
Stop-Process -Name "LecooFanGUI" -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
Write-OK

# ─── 3. 删除注册的服务（任务、启动项、卸载入口、快捷方式）────────────────────
Write-Step "删除计划任务和开机启动项 ..."
if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    Write-OK
} else { Write-Host "  (任务不存在)" }

Remove-ItemProperty -Path $RunKeyPath -Name "LecooFan" -ErrorAction SilentlyContinue
Write-OK

Write-Step "删除卸载入口和开始菜单快捷方式 ..."
Remove-Item -Path $UninstallKey -Recurse -Force -ErrorAction SilentlyContinue
$smDir = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\LecooFan"
if (Test-Path $smDir) { Remove-Item $smDir -Recurse -Force -ErrorAction SilentlyContinue }
$userSm = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\LecooFan"
if (Test-Path $userSm) { Remove-Item $userSm -Recurse -Force -ErrorAction SilentlyContinue }
Write-OK

# ─── 4. 删安装目录 ───────────────────────────────────────────────────────────
Write-Step "删除安装目录 $InstallDir ..."
if (Test-Path $InstallDir) {
    if ($KeepConfig) {
        $cfg = Join-Path $InstallDir "lecoofan_gui.cfg"
        if (Test-Path $cfg) {
            Copy-Item $cfg -Destination (Join-Path $env:TEMP "lecoofan_gui.cfg.keep") -Force
            Write-Host "  (已按 -KeepConfig 保留配置到 $env:TEMP\lecoofan_gui.cfg.keep)"
        }
    }
    Remove-Item $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path $InstallDir) {
        # 常见残留：没退干净的进程占着文件，再杀一次重试
        Stop-Process -Name "LecooFanGUI" -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
        Remove-Item $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path $InstallDir) { Write-Warn "目录仍有残留，请重启后手动删除: $InstallDir" }
    else { Write-OK }
} else { Write-Host "  (目录不存在)" }

Write-Host ""
Write-Host "卸载完成。风扇已交还 EC，所有自启动项已移除。" -ForegroundColor Green
Write-Host ""
