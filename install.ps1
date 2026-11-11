#Requires -Version 7
<#
  安装 LabVIEW Dialog Watchdog：
  - 把 exe 复制到程序目录（默认 %LOCALAPPDATA%\Programs\LabVIEWDialogWatchdog）
  - 首次安装生成默认配置与日志目录
  - 可选：注册 HKCU 开机自启、安装后立即启动
#>
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\LabVIEWDialogWatchdog'),
    [switch]$AutoStart,
    [switch]$Start
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$distExe = Join-Path $root 'dist\LabVIEWDialogWatchdog.exe'
if (-not (Test-Path -LiteralPath $distExe)) {
    throw "找不到 $distExe；先运行 build.ps1 -Tests"
}

New-Item -ItemType Directory -Force -Path $InstallDir, (Join-Path $InstallDir 'logs') | Out-Null
Copy-Item -LiteralPath $distExe -Destination (Join-Path $InstallDir 'LabVIEWDialogWatchdog.exe') -Force

$config = Join-Path $InstallDir 'LabVIEWDialogWatchdog.json'
if (-not (Test-Path -LiteralPath $config)) {
    @{
        targetProcessNames = @('LabVIEW')
        logDirectory       = (Join-Path $InstallDir 'logs')
        pollIntervalMs     = 300
        showNotifications  = $true
        dryRun             = $false
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $config -Encoding utf8
}

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$valueName = 'LabVIEWDialogWatchdog'
$exePath = Join-Path $InstallDir 'LabVIEWDialogWatchdog.exe'
if ($AutoStart) {
    New-Item -Path $runKey -Force | Out-Null
    Set-ItemProperty -Path $runKey -Name $valueName -Value ('"' + $exePath + '"')
} else {
    Remove-ItemProperty -Path $runKey -Name $valueName -ErrorAction SilentlyContinue
}

if ($Start) {
    $existing = Get-Process LabVIEWDialogWatchdog -ErrorAction SilentlyContinue
    if (-not $existing) {
        Start-Process -FilePath $exePath -ArgumentList @('--config', $config)
        Start-Sleep -Seconds 1
    }
}

Write-Host "已安装：$exePath"
Write-Host "配置：  $config"
Write-Host "日志：  $(Join-Path $InstallDir 'logs')"
Write-Host "自启：  $([bool]$AutoStart)"
