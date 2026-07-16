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

# ─── Task 2: LecooFan ─────────────────────────────────────────────────
Write-Step "Setting up LecooFan auto-start (Task Scheduler)..."

$fanAction = New-ScheduledTaskAction -Execute $PythonExe -Argument $FanPythonArgs
$fanTrigger = New-ScheduledTaskTrigger -AtLogOn
$fanSettings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -ExecutionTimeLimit 0
$fanPrincipal = New-ScheduledTaskPrincipal `
    -GroupId "BUILTIN\Administrators" `
    -RunLevel Highest
$fanTask = New-ScheduledTask `
    -Action $fanAction `
    -Trigger $fanTrigger `
    -Settings $fanSettings `
    -Principal $fanPrincipal `
    -Description "LecooFan — N175L IT5570 EC quiet fan curve (lecoofan)"

Register-ScheduledTask -TaskName "LecooFan" -InputObject $fanTask -Force
Write-OK

# ─────────────────────────────────────────────────────────────────────────────
Write-Step "Summary"
Write-Host "  Task Scheduler entries created:"
Write-Host "    - ThrottleStop    (at logon, admin, minimized to tray)"
Write-Host "    - LecooFan  (at logon, admin, quiet background)"
Write-Host ""
Write-Host "  To start manually NOW:" -ForegroundColor Yellow
Write-Host "    Start-Process -WindowStyle Hidden $ThrottleStopPath"
Write-Host "    Start-Process -WindowStyle Hidden -FilePath $PythonExe -ArgumentList '$FanPythonArgs'"
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
