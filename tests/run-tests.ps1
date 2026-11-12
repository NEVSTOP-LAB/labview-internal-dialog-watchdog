#Requires -Version 7
<#
  构建 + 单元测试 + 端到端夹具测试（不需要 LabVIEW）。
  只验证 Internal Warning 对话框：识别、n 秒延迟、自动点击；其他对话框不处理。
#>
param(
    [switch]$SkipBuild,
    [string]$TempDir
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $TempDir) {
    $TempDir = Join-Path $root 'artifacts\tests'
}
New-Item -ItemType Directory -Force -Path $TempDir | Out-Null

if (-not $SkipBuild) {
    & pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build.ps1') -Tests -Clean
    if ($LASTEXITCODE -ne 0) { throw 'build.ps1 失败' }
}

$exe = Join-Path $root 'dist\LabVIEWDialogWatchdog.exe'
$fixture = Join-Path $root 'dist\tests\LabVIEWDialogFixture.exe'
$unitTests = Join-Path $root 'dist\tests\LabVIEWDialogWatchdog.Tests.exe'

$script:failures = 0
$script:total = 0
function Assert-Verdict([string]$Name, [bool]$Condition, [string]$Detail) {
    $script:total++
    if ($Condition) {
        Write-Host "PASS $Name"
    } else {
        Write-Host "FAIL $Name  $Detail"
        $script:failures++
    }
}

function Quote-Arg([string]$Value) {
    if ($Value -match '\s') { return '"' + $Value + '"' }
    return $Value
}

function Start-Quoted([string]$FilePath, [string[]]$Arguments, [string]$RedirectOut, [string]$RedirectErr) {
    $quoted = @($Arguments | ForEach-Object { Quote-Arg $_ })
    return Start-Process -FilePath $FilePath -ArgumentList $quoted -PassThru `
        -RedirectStandardOutput $RedirectOut -RedirectStandardError $RedirectErr
}

function Wait-ProcessExit {
    param([Parameter(Mandatory = $true)]$Process, [int]$TimeoutMs = 60000)
    if (-not $Process.WaitForExit($TimeoutMs)) {
        Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
        return -1
    }
    return $Process.ExitCode
}

function Invoke-WatchdogCli {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$Tag
    )
    $stdout = Join-Path $TempDir "cli-$Tag.out"
    $stderr = Join-Path $TempDir "cli-$Tag.err"
    Remove-Item -LiteralPath $stdout, $stderr -Force -ErrorAction SilentlyContinue
    $process = Start-Quoted -FilePath $exe -Arguments $Arguments -RedirectOut $stdout -RedirectErr $stderr
    $exitCode = Wait-ProcessExit -Process $process -TimeoutMs 60000
    return [pscustomobject]@{
        ExitCode = $exitCode
        StdOut   = (Get-Content -LiteralPath $stdout -Raw -Encoding UTF8 -ErrorAction SilentlyContinue)
        StdErr   = (Get-Content -LiteralPath $stderr -Raw -Encoding UTF8 -ErrorAction SilentlyContinue)
    }
}

function New-TestConfig {
    param([string]$Path, [string]$LogDir, [string]$Target = 'LabVIEWDialogFixture', [int]$DelaySeconds = 1)
    New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
    @{
        targetProcessNames = @($Target)
        logDirectory       = $LogDir
        clickDelaySeconds  = $DelaySeconds
        pollIntervalMs     = 200
        showNotifications  = $false
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $Path -Encoding utf8
}

function Get-LogTimestamp {
    param([string]$LogPath, [string]$Pattern)
    if (-not (Test-Path -LiteralPath $LogPath)) { return $null }
    $line = Get-Content -LiteralPath $LogPath -Encoding UTF8 |
        Where-Object { $_ -match $Pattern } | Select-Object -First 1
    if (-not $line) { return $null }
    $m = [regex]::Match($line, '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})')
    if (-not $m.Success) { return $null }
    return [datetime]::ParseExact($m.Groups[1].Value, 'yyyy-MM-dd HH:mm:ss.fff', $null)
}

Write-Host '== 单元测试 =='
$unitOut = Join-Path $TempDir 'unit.out'
$unitErr = Join-Path $TempDir 'unit.err'
$unitRun = Start-Quoted -FilePath $unitTests -Arguments @($TempDir) -RedirectOut $unitOut -RedirectErr $unitErr
$unitExit = Wait-ProcessExit -Process $unitRun -TimeoutMs 30000
Assert-Verdict 'unit tests' ($unitExit -eq 0) "exit=$unitExit"

Write-Host '== 端到端：Internal Warning 在 n 秒后自动点击 =='
$config = Join-Path $TempDir 'config-internal.json'
$logDir = Join-Path $TempDir 'logs-internal'
$marker = Join-Path $TempDir 'closed-internal.txt'
Remove-Item -LiteralPath $logDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $marker -Force -ErrorAction SilentlyContinue
New-TestConfig -Path $config -LogDir $logDir -DelaySeconds 1

$fixtureProcess = Start-Quoted -FilePath $fixture -Arguments @('--internal-warning', $marker) `
    -RedirectOut (Join-Path $TempDir 'fixture-internal.out') -RedirectErr (Join-Path $TempDir 'fixture-internal.err')
try {
    Start-Sleep -Milliseconds 1200
    $scanOut = Join-Path $TempDir 'scan-internal.json'
    $scan = Invoke-WatchdogCli -Arguments @('--scan', '--json', '--config', $config, '--out', $scanOut) -Tag 'scan-internal'
    $scanText = Get-Content -LiteralPath $scanOut -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
    $items = @($scanText | ConvertFrom-Json)
    Assert-Verdict '识别 Internal Warning 对话框' ($items.Count -eq 1) $scanText
    if ($items.Count -eq 1) {
        Assert-Verdict '命中确认按钮' ($items[0].button -match '确定|OK') "button=$($items[0].button)"
        Assert-Verdict '文本包含 Internal Warning' ($items[0].text -match 'Internal Warning') "text=$($items[0].text)"
    }

    $watchOut = Join-Path $TempDir 'watch-internal.txt'
    $watch = Invoke-WatchdogCli -Arguments @(
        '--watch-seconds', '8', '--config', $config, '--require-dismissal', '--exit-after-dismiss', '--out', $watchOut
    ) -Tag 'watch-internal'
    Assert-Verdict '守护进程正常结束' ($watch.ExitCode -eq 0) "exit=$($watch.ExitCode)"
    Assert-Verdict '对话框被自动关闭' (Test-Path -LiteralPath $marker) '未看到关闭标记'

    $logPath = Join-Path $logDir 'LabVIEWDialogWatchdog.log'
    $detectedAt = Get-LogTimestamp -LogPath $logPath -Pattern '检测到 Internal Warning'
    $clickedAt = Get-LogTimestamp -LogPath $logPath -Pattern '第 1 次点击'
    Assert-Verdict '日志记录检测与点击' ($null -ne $detectedAt -and $null -ne $clickedAt) '日志缺少检测/点击'
    if ($null -ne $detectedAt -and $null -ne $clickedAt) {
        $gapMs = ($clickedAt - $detectedAt).TotalMilliseconds
        Assert-Verdict '延迟约 1 秒后点击' ($gapMs -ge 900 -and $gapMs -le 2500) "gap=$([int]$gapMs)ms"
    }
}
finally {
    if (-not $fixtureProcess.HasExited) {
        Stop-Process -Id $fixtureProcess.Id -Force -ErrorAction SilentlyContinue
    }
}

Write-Host '== 反向：其他对话框不处理 =='
$otherConfig = Join-Path $TempDir 'config-other.json'
$otherLog = Join-Path $TempDir 'logs-other'
$otherMarker = Join-Path $TempDir 'closed-other.txt'
Remove-Item -LiteralPath $otherLog -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $otherMarker -Force -ErrorAction SilentlyContinue
New-TestConfig -Path $otherConfig -LogDir $otherLog -DelaySeconds 0
$otherProcess = Start-Quoted -FilePath $fixture -Arguments @('--other', $otherMarker) `
    -RedirectOut (Join-Path $TempDir 'fixture-other.out') -RedirectErr (Join-Path $TempDir 'fixture-other.err')
try {
    Start-Sleep -Milliseconds 1200
    $otherScan = Join-Path $TempDir 'scan-other.json'
    $null = Invoke-WatchdogCli -Arguments @('--scan', '--json', '--config', $otherConfig, '--out', $otherScan) -Tag 'scan-other'
    $otherText = Get-Content -LiteralPath $otherScan -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
    $otherItems = @($otherText | ConvertFrom-Json)
    Assert-Verdict '普通对话框不被识别' ($otherItems.Count -eq 0) $otherText

    $null = Invoke-WatchdogCli -Arguments @('--watch-seconds', '2', '--config', $otherConfig) -Tag 'watch-other'
    Assert-Verdict '普通对话框不被点击' (-not (Test-Path -LiteralPath $otherMarker)) '普通对话框被点击了'
}
finally {
    if (-not $otherProcess.HasExited) {
        Stop-Process -Id $otherProcess.Id -Force -ErrorAction SilentlyContinue
    }
}

Write-Host '== 安全：--dry-run 不点击 =='
$dryConfig = Join-Path $TempDir 'config-dry.json'
$dryLog = Join-Path $TempDir 'logs-dry'
$dryMarker = Join-Path $TempDir 'closed-dry.txt'
Remove-Item -LiteralPath $dryLog -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $dryMarker -Force -ErrorAction SilentlyContinue
New-TestConfig -Path $dryConfig -LogDir $dryLog -DelaySeconds 0
$dryProcess = Start-Quoted -FilePath $fixture -Arguments @('--internal-warning', $dryMarker) `
    -RedirectOut (Join-Path $TempDir 'fixture-dry.out') -RedirectErr (Join-Path $TempDir 'fixture-dry.err')
try {
    Start-Sleep -Milliseconds 1200
    $null = Invoke-WatchdogCli -Arguments @('--watch-seconds', '2', '--dry-run', '--config', $dryConfig) -Tag 'watch-dry'
    Assert-Verdict 'dry-run 不点击' (-not (Test-Path -LiteralPath $dryMarker)) 'dry-run 点击了对话框'
    Assert-Verdict 'dry-run 有日志' (((Get-Content -LiteralPath (Join-Path $dryLog 'LabVIEWDialogWatchdog.log') -Raw -Encoding UTF8) -match 'dry-run')) '日志缺少 dry-run'
}
finally {
    if (-not $dryProcess.HasExited) {
        Stop-Process -Id $dryProcess.Id -Force -ErrorAction SilentlyContinue
    }
}

Write-Host '== 反向：非目标进程不处理 =='
$nonTargetConfig = Join-Path $TempDir 'config-nontarget.json'
$nonTargetLog = Join-Path $TempDir 'logs-nontarget'
$nonTargetMarker = Join-Path $TempDir 'closed-nontarget.txt'
Remove-Item -LiteralPath $nonTargetLog -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $nonTargetMarker -Force -ErrorAction SilentlyContinue
New-TestConfig -Path $nonTargetConfig -LogDir $nonTargetLog -Target 'NotTheFixture' -DelaySeconds 0
$nonTargetProcess = Start-Quoted -FilePath $fixture -Arguments @('--internal-warning', $nonTargetMarker) `
    -RedirectOut (Join-Path $TempDir 'fixture-nontarget.out') -RedirectErr (Join-Path $TempDir 'fixture-nontarget.err')
try {
    Start-Sleep -Milliseconds 1200
    $nonTargetScan = Join-Path $TempDir 'scan-nontarget.json'
    $null = Invoke-WatchdogCli -Arguments @('--scan', '--json', '--config', $nonTargetConfig, '--out', $nonTargetScan) -Tag 'scan-nontarget'
    $nonTargetText = Get-Content -LiteralPath $nonTargetScan -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
    $nonTargetItems = @($nonTargetText | ConvertFrom-Json)
    Assert-Verdict '非目标进程不被识别' ($nonTargetItems.Count -eq 0) $nonTargetText
}
finally {
    if (-not $nonTargetProcess.HasExited) {
        Stop-Process -Id $nonTargetProcess.Id -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ''
if ($script:failures -eq 0) {
    Write-Host "ALL PASS ($script:total checks)"
    exit 0
}
Write-Host "FAILED: $script:failures / $script:total checks"
exit 1
