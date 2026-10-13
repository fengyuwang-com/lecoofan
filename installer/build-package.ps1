#Requires -Version 3.0
<#
.SYNOPSIS>
    打包 LecooFan 安装包：
      1. staging:  dist/LecooFan-Setup-<ver>/   （exe + 安装脚本 + README + LICENSE + 源码）
      2. zip:      dist/LecooFan-Setup-<ver>.zip
      3. -WithSfx:  dist/LecooFan-Setup-<ver>.exe （7-Zip 自解压，解压完自动跑 install.bat）
                  默认关闭：本机 7-Zip 26.02 的 SFX 模块读不到内嵌配置
                  （InstallPath/RunProgram 全部被忽略，退化成"往当前目录解压"），
                  修好之前自解压 exe 不是一个可用的安装包，只有 zip 是。
    用法: powershell -File installer\build-package.ps1 [-Version 1.0.0] [-WithSfx]
#>
[CmdletBinding()]
param(
    [string]$Version = "1.0.0",
    [switch]$WithSfx
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "dist"
$stage = Join-Path $dist "LecooFan-Setup-$Version"

function Write-Step { param([string]$Text) Write-Host "`n>>> $Text" -ForegroundColor Cyan }
function Write-OK   { Write-Host "  [OK] " -ForegroundColor Green }
function Write-Warn { param([string]$Text) Write-Host "  [!] $Text" -ForegroundColor Yellow }

# ─── 0. 确认 exe 存在（构建产物不入库，这里必须是现编的）──────────────────────
$gui = Join-Path $root "LecooFanGUI.exe"
$stub = Join-Path $root "LecooFanTray.exe"
foreach ($e in @($gui, $stub)) {
    if (-not (Test-Path $e)) { throw "缺少构建产物: $e（先按 README 的命令编译）" }
}

# ─── 1. staging ─────────────────────────────────────────────────────────────
Write-Step "staging → $stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null
New-Item -ItemType Directory -Path "$stage\src" -Force | Out-Null

# 安装器本体
Copy-Item (Join-Path $PSScriptRoot "install.bat")            $stage -Force
Copy-Item (Join-Path $PSScriptRoot "uninstall.bat")          $stage -Force
Copy-Item (Join-Path $PSScriptRoot "Install-LecooFan.ps1")   $stage -Force
Copy-Item (Join-Path $PSScriptRoot "Uninstall-LecooFan.ps1") $stage -Force
# 程序
Copy-Item $gui  $stage -Force
Copy-Item $stub $stage -Force
# 文档 + 许可证
Copy-Item (Join-Path $root "README.md") $stage -Force
Copy-Item (Join-Path $root "LICENSE")   $stage -Force
# 源码（AGPL：随二进制发布）
foreach ($f in @("lecoofan_gui.cs", "lecoofan_ui.cs", "lecoofan_tray.cs",
                 "lecoofan_gui.manifest", "setup.ps1",
                 "quiet_cpu.bat", "restore_cpu.bat",
                 "start_fan.bat", "stop_fan.bat", "toggle_fan.bat")) {
    $src = Join-Path $root $f
    if (Test-Path $src) { Copy-Item $src "$stage\src\" -Force }
}
$exeCount = (Get-ChildItem $stage -Recurse -File).Count
Write-Host "  共 $exeCount 个文件"

# ─── 2. zip ─────────────────────────────────────────────────────────────────
Write-Step "打 zip"
$zip = Join-Path $dist "LecooFan-Setup-$Version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$stage\*" -DestinationPath $zip -CompressionLevel Optimal
$zipKb = [math]::Round((Get-Item $zip).Length / 1KB)
Write-Host ("  {0}  ({1} KB)" -f (Split-Path $zip -Leaf), $zipKb)

# ─── 3. 7-Zip SFX 自解压安装 exe（可选，见文件头说明）─────────────────────────
if (-not $WithSfx) {
    Write-Host "`n>>> SFX skipped (-WithSfx to enable; 7-Zip 26.02 config not honored on this machine)"
} else {
Write-Step "7-Zip SFX 自解压安装 exe"
$sfx = Join-Path $dist "LecooFan-Setup-$Version.exe"
if (Test-Path $sfx) { Remove-Item $sfx -Force }

$sevenZip = @("C:\Program Files\7-Zip\7z.exe", "C:\Program Files (x86)\7-Zip\7z.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
$sfxModule = @("C:\Program Files\7-Zip\7z.sfx", "C:\Program Files (x86)\7-Zip\7z.sfx") |
    Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $sevenZip -or -not $sfxModule) {
    Write-Warn "7-Zip not found, SFX exe skipped; zip only."
} else {
    $archive7z = Join-Path $env:TEMP "lecoofan-$Version.7z"
    if (Test-Path $archive7z) { Remove-Item $archive7z -Force }
    & $sevenZip a -t7z -mx=9 $archive7z "$stage\*" | Out-Null
    if (-not (Test-Path $archive7z)) { throw "7z archive failed" }

    # SFX 配置头。两个教训：
    #  1. here-string 里 `n 是 PowerShell 转义，会把配置行拆断导致模块解析失败
    #     （rc=255）——所有值保持单行，不用换行转义。
    #  2. 不能用 -Encoding UTF8：PS5.1 会写 BOM，SFX 模块只认紧贴自己的配置头；
    #     配置保持纯 ASCII，用 ASCII 写死。
    $cfg = Join-Path $env:TEMP "lecoofan-$Version-sfx.txt"
    $cfgText = @"
;!@Install@!UTF-8!
Title="LecooFan Setup $Version"
BeginPrompt="LecooFan $Version - extract and install?"
GUIMode="2"
InstallPath="%TEMP%\LecooFan-Setup"
OverwriteMode="0"
RunProgram="install.bat"
;!@InstallEnd@!
"@
    Set-Content -Path $cfg -Value $cfgText -Encoding ASCII

    # 拼接：SFX 模块 + 配置 + 7z 包
    $fs = [IO.File]::OpenWrite($sfx)
    try {
        $b1 = [IO.File]::ReadAllBytes($sfxModule); $fs.Write($b1, 0, $b1.Length)
        $b2 = [IO.File]::ReadAllBytes($cfg);         $fs.Write($b2, 0, $b2.Length)
        $b3 = [IO.File]::ReadAllBytes($archive7z);   $fs.Write($b3, 0, $b3.Length)
    } finally { $fs.Close() }
    Remove-Item $archive7z -Force -ErrorAction SilentlyContinue
    if ((Test-Path $sfx) -and (Get-Item $sfx).Length -gt 100KB) {
        $sfxKb = [math]::Round((Get-Item $sfx).Length / 1KB)
        Write-Host ("  {0}  ({1} KB)" -f (Split-Path $sfx -Leaf), $sfxKb)
    } else {
        Write-Warn "SFX concat produced a suspicious file; skipped."
        if (Test-Path $sfx) { Remove-Item $sfx -Force }
    }
}
}

# ─── 汇总 ───────────────────────────────────────────────────────────────────
Write-Step "产物"
Get-ChildItem $dist -File | ForEach-Object {
    Write-Host ("  {0,-32} {1,8:N0} KB" -f $_.Name, ($_.Length / 1KB))
}
Write-Host "`n完成。"
