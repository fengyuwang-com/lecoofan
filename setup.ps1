#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Complete setup script for LecooFan + ThrottleStop on N175L.
    Run this as Administrator (right-click → Run with PowerShell).
#>

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# ─── Configuration ───────────────────────────────────────────────────────────
$ThrottleStopPath = "C:\FenglinApps\ThrottleStop_9.7\ThrottleStop.exe"
$ThrottleStopArgs = "-t"  # minimize to tray

$PythonExe = "C:\Users\a8881\AppData\Local\Programs\Python\Python312\python.exe"
$FanScript = Join-Path $ScriptDir "lecoofan.py"
$FanPythonArgs = "-u `"$FanScript`" --quiet"

function Write-Step {
    param([string]$Text)
    Write-Host "`n>>> $Text" -ForegroundColor Cyan
}

function Write-OK {
    Write-Host "  [OK]" -ForegroundColor Green
}

function Write-Warn {
    param([string]$Text)
    Write-Host "  [!] $Text" -ForegroundColor Yellow
}

# ─────────────────────────────────────────────────────────────────────────────
Write-Step "Verifying file paths..."

if (-not (Test-Path $ThrottleStopPath)) {
    Write-Warn "ThrottleStop not found at: $ThrottleStopPath"
    Write-Warn "Install ThrottleStop first, then re-run this script."
} else {
    Write-OK
}
if (-not (Test-Path $PythonExe)) {
    Write-Warn "Python not found at: $PythonExe"
    Write-Warn "Edit the path in this script and the .bat files."
} else {
    Write-OK
}
if (-not (Test-Path $FanScript)) {
    Write-Warn "lecoofan.py not found at: $FanScript"
} else {
    Write-OK
}

# ─── Task 1: ThrottleStop ───────────────────────────────────────────────────
Write-Step "Setting up ThrottleStop auto-start (Task Scheduler)..."

$tsAction = New-ScheduledTaskAction -Execute $ThrottleStopPath
$tsTrigger = New-ScheduledTaskTrigger -AtLogOn
$tsSettings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -ExecutionTimeLimit 0
$tsPrincipal = New-ScheduledTaskPrincipal `
    -GroupId "BUILTIN\Administrators" `
    -RunLevel Highest
$tsTask = New-ScheduledTask `
    -Action $tsAction `
    -Trigger $tsTrigger `
    -Settings $tsSettings `
    -Principal $tsPrincipal `
    -Description "ThrottleStop — CPU undervolt + Speed Shift on N175L"

Register-ScheduledTask -TaskName "ThrottleStop" -InputObject $tsTask -Force
Write-OK

# ─── Task 2: LecooFan（图形控制台，登录后直接进托盘，不弹窗口）─────────────────
Write-Step "Setting up LecooFan auto-start (Task Scheduler)..."

$FanGuiExe = Join-Path $ScriptDir "LecooFanGUI.exe"
if (-not (Test-Path $FanGuiExe)) {
    Write-Warn "LecooFanGUI.exe not found at: $FanGuiExe"
    Write-Warn "Build it first (see 'Rebuild' in README), then re-run this script."
} else {
    $fanAction = New-ScheduledTaskAction -Execute $FanGuiExe -Argument "--tray"
    $fanTrigger = New-ScheduledTaskTrigger -AtLogOn
    $fanSettings = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries `
        -StartWhenAvailable `
        -ExecutionTimeLimit 0 `
        -MultipleInstances IgnoreNew
    $fanPrincipal = New-ScheduledTaskPrincipal `
        -UserId "$env:USERDOMAIN\$env:USERNAME" `
        -LogonType Interactive `
        -RunLevel Highest
    $fanTask = New-ScheduledTask `
        -Action $fanAction `
        -Trigger $fanTrigger `
        -Settings $fanSettings `
        -Principal $fanPrincipal `
        -Description "LecooFan GUI: N175L EC fan control + CPU modes (tray autostart)"

    Register-ScheduledTask -TaskName "LecooFan" -InputObject $fanTask -Force
    Write-OK
}

# 启动项（注册表 Run 键）：任务管理器的"启动应用"列表只认 Run 键和启动文件夹，
# 计划任务不显示在里面。这里指向无清单的启动桩 LecooFanTray.exe——它登录时去触发
# 上面的计划任务，不提权、不弹 UAC，让启动项在任务管理器里可见且可一键禁用。
$FanTrayStub = Join-Path $ScriptDir "LecooFanTray.exe"
if (Test-Path $FanTrayStub) {
    Set-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" `
        -Name "LecooFan" -Value "`"$FanTrayStub`""
    Write-OK
} else {
    Write-Warn "LecooFanTray.exe not found (build it - see README 'Rebuild'); startup entry skipped."
}

# 说明：旧的 python 守护进程任务（lecoofan.py --quiet）已被上面同名任务取代。
# 它的功能（静音曲线/锁转速）现在由 GUI 托盘模式承担；两个同时跑会互相抢 EC。

# ─────────────────────────────────────────────────────────────────────────────
Write-Step "Summary"
Write-Host "  Task Scheduler entries created:"
Write-Host "    - ThrottleStop    (at logon, admin, minimized to tray)"
Write-Host "    - LecooFan  (at logon, admin, GUI in tray: fan control + CPU modes)"
Write-Host ""
Write-Host "  Startup entry (visible in Task Manager > Startup apps):"
Write-Host "    - Run key HKCU\...\Run\LecooFan -> LecooFanTray.exe (triggers the task, no UAC)"
Write-Host ""
Write-Host "  To start manually NOW:" -ForegroundColor Yellow
Write-Host "    Start-Process -WindowStyle Hidden $ThrottleStopPath"
Write-Host "    Start-Process -FilePath $FanGuiExe -ArgumentList '--tray'"
Write-Host ""
Write-Host "  To disable:"
Write-Host "    Disable-ScheduledTask -TaskName 'ThrottleStop'"
Write-Host "    Disable-ScheduledTask -TaskName 'LecooFan'"
Write-Host ""
Write-Host "  To remove:"
Write-Host "    Unregister-ScheduledTask -TaskName 'ThrottleStop' -Confirm:`$false"
Write-Host "    Unregister-ScheduledTask -TaskName 'LecooFan' -Confirm:`$false"
Write-Host ""
Write-Host "Done." -ForegroundColor Green
