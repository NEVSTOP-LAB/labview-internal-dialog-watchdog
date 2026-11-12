#Requires -Version 7
<#
  安装 LabVIEW Dialog Watchdog：
  - 把 exe 复制到程序目录（默认 %LOCALAPPDATA%\Programs\LabVIEWDialogWatchdog）
  - 首次安装生成默认配置与日志目录
  - -AutoStart 注册开机自启；-RemoveAutoStart 取消；两个都不传时保留现状
  - -Start 安装后立即启动（已在运行则跳过）
#>
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\LabVIEWDialogWatchdog'),
    [switch]$AutoStart,
    [switch]$RemoveAutoStart,
    [switch]$Start
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$distExe = Join-Path $root 'dist\LabVIEWDialogWatchdog.exe'
if (-not (Test-Path -LiteralPath $distExe)) {
    throw "找不到 $distExe；先运行 build.ps1 -Tests"
}

New-Item -ItemType Directory -Force -Path $InstallDir, (Join-Path $InstallDir 'logs') | Out-Null
$exePath = Join-Path $InstallDir 'LabVIEWDialogWatchdog.exe'
try {
    Copy-Item -LiteralPath $distExe -Destination $exePath -Force -ErrorAction Stop
} catch {
    throw "无法写入 $exePath（可能正在运行）。先在托盘菜单退出守护，或结束对应进程后重试。原始错误：$($_.Exception.Message)"
}

$config = Join-Path $InstallDir 'LabVIEWDialogWatchdog.json'
if (-not (Test-Path -LiteralPath $config)) {
    @{
        targetProcessNames = @('LabVIEW')
        logDirectory       = (Join-Path $InstallDir 'logs')
        pollIntervalMs     = 300
        clickDelaySeconds  = 5
        showNotifications  = $true
        dryRun             = $false
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $config -Encoding utf8
}

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$valueName = 'LabVIEWDialogWatchdog'
if ($AutoStart) {
    New-Item -Path $runKey -Force | Out-Null
    Set-ItemProperty -Path $runKey -Name $valueName -Value ('"' + $exePath + '"')
} elseif ($RemoveAutoStart) {
    Remove-ItemProperty -Path $runKey -Name $valueName -ErrorAction SilentlyContinue
}

if ($Start) {
    $running = Get-Process LabVIEWDialogWatchdog -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $exePath }
    if (-not $running) {
        Start-Process -FilePath $exePath -ArgumentList ('--config "{0}"' -f $config)
        Start-Sleep -Seconds 1
    }
}

Write-Host "已安装：$exePath"
Write-Host "配置：  $config"
Write-Host "日志：  $(Join-Path $InstallDir 'logs')"
Write-Host "自启：  $(if ($AutoStart) { '已启用' } elseif ($RemoveAutoStart) { '已取消' } else { '保持现状' })"
