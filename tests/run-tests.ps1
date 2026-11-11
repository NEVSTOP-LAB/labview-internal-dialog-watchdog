#Requires -Version 7
<#
  构建 + 单元测试 + 端到端夹具测试（不需要 LabVIEW）。
  端到端用 LabVIEWDialogFixture.exe 模拟模态对话框，验证扫描、点击与恢复。
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

function Wait-ProcessExit {
    param(
        [Parameter(Mandatory = $true)]$Process,
        [int]$TimeoutMs = 60000
    )
    if (-not $Process.WaitForExit($TimeoutMs)) {
        Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
        return -1
    }
    return $Process.ExitCode
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

Write-Host '== 单元测试 =='
$unitOut = Join-Path $TempDir 'unit.out'
$unitErr = Join-Path $TempDir 'unit.err'
$unitRun = Start-Quoted -FilePath $unitTests -Arguments @($TempDir) `
    -RedirectOut $unitOut -RedirectErr $unitErr
$unitExit = Wait-ProcessExit -Process $unitRun -TimeoutMs 30000
Assert-Verdict 'unit tests' ($unitExit -eq 0) "exit=$unitExit"

function Invoke-DialogScenario {
    param(
        [Parameter(Mandatory = $true)][string]$Mode,
        [Parameter(Mandatory = $true)][bool]$ExpectInternal,
        [string]$Target = 'LabVIEWDialogFixture'
    )

    $slug = $Mode.TrimStart('-')
    $config = Join-Path $TempDir "config-$slug.json"
    $logDir = Join-Path $TempDir "logs-$slug"
    $marker = Join-Path $TempDir "closed-$slug.txt"
    Remove-Item -LiteralPath $logDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $marker -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null

    @{
        targetProcessNames = @($Target)
        logDirectory       = $logDir
        pollIntervalMs     = 200
        minSeenCount       = 1
        enableKeyboardFallback = $true
        enableWindowCloseFallback = $false
        maxClickAttempts   = 3
        clickRetryDelayMs  = 500
        keyboardFallbackDelayMs = 300
        showNotifications  = $false
        confirmButtonTexts = @('OK', '确定', 'Continue', '继续', 'Close', '关闭')
        excludeButtonTexts = @('Cancel', '取消', 'Stop', '停止', 'Abort', '放弃', 'No', '否')
        keyboardFallbackWindowClasses = @('LVDChild', 'LVDChildWatchdogFixture')
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $config -Encoding utf8

    $fixtureOut = Join-Path $TempDir "fixture-$slug.out"
    $fixtureErr = Join-Path $TempDir "fixture-$slug.err"
    $dialogProcess = Start-Quoted -FilePath $fixture -Arguments @($Mode, $marker) `
        -RedirectOut $fixtureOut -RedirectErr $fixtureErr
    try {
        $scanOut = Join-Path $TempDir "scan-$slug.json"
        $items = @()
        for ($attempt = 0; $attempt -lt 10; $attempt++) {
            Start-Sleep -Milliseconds 400
            $null = Invoke-WatchdogCli -Arguments @('--scan', '--json', '--config', $config, '--out', $scanOut) -Tag "scan-$slug"
            if (Test-Path -LiteralPath $scanOut) {
                $scanText = Get-Content -LiteralPath $scanOut -Raw -Encoding UTF8
                $items = @($scanText | ConvertFrom-Json)
                if ($items.Count -ge 1) { break }
            }
        }
        Assert-Verdict "${slug}: 扫描到对话框" ($items.Count -ge 1) 'scan returned no dialog'
        if ($items.Count -ge 1) {
            Assert-Verdict "${slug}: 命中确认按钮" ($items[0].button -match '确定|OK|Enter') "button=$($items[0].button)"
            Assert-Verdict "${slug}: internalError=$ExpectInternal" ($items[0].internalError -eq $ExpectInternal) "got=$($items[0].internalError)"
        }

        $watchOut = Join-Path $TempDir "watch-$slug.txt"
        $watch = Invoke-WatchdogCli -Arguments @(
            '--watch-seconds', '8', '--config', $config, '--require-dismissal', '--exit-after-dismiss', '--out', $watchOut
        ) -Tag "watch-$slug"
        $watchText = Get-Content -LiteralPath $watchOut -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
        Assert-Verdict "${slug}: 守护进程正常结束" ($watch.ExitCode -eq 0) "exit=$($watch.ExitCode) $watchText"
        Assert-Verdict "${slug}: 对话框被关闭" (Test-Path -LiteralPath $marker) '未看到关闭标记'
        Assert-Verdict "${slug}: 日志记录点击" (((Get-Content -LiteralPath (Join-Path $logDir 'LabVIEWDialogWatchdog.log') -Raw -Encoding UTF8) -match '点击')) '日志缺少点击记录'
    }
    finally {
        if (-not $dialogProcess.HasExited) {
            Stop-Process -Id $dialogProcess.Id -Force -ErrorAction SilentlyContinue
        }
    }
}

Write-Host '== 端到端：Win32 MessageBox =='
Invoke-DialogScenario -Mode '--messagebox' -ExpectInternal $false
Write-Host '== 端到端：internal error 文案 =='
Invoke-DialogScenario -Mode '--internal-error' -ExpectInternal $true
Write-Host '== 端到端：非 #32770 模态框（UIA 兜底）=='
Invoke-DialogScenario -Mode '--custom' -ExpectInternal $true
Write-Host '== 端到端：LabVIEW 自绘对话框（无按钮，Enter 兜底）=='
Invoke-DialogScenario -Mode '--labview-custom' -ExpectInternal $false

Write-Host '== 安全用例：危险标题不处理 =='
$dangerConfig = Join-Path $TempDir 'config-danger.json'
$dangerLog = Join-Path $TempDir 'logs-danger'
$dangerMarker = Join-Path $TempDir 'closed-danger.txt'
New-Item -ItemType Directory -Force -Path $dangerLog | Out-Null
Remove-Item -LiteralPath $dangerMarker -Force -ErrorAction SilentlyContinue
@{
    targetProcessNames = @('LabVIEWDialogFixture')
    logDirectory       = $dangerLog
    pollIntervalMs     = 200
    showNotifications  = $false
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $dangerConfig -Encoding utf8
$dangerProcess = Start-Quoted -FilePath $fixture -Arguments @('--danger', $dangerMarker) `
    -RedirectOut (Join-Path $TempDir 'fixture-danger.out') -RedirectErr (Join-Path $TempDir 'fixture-danger.err')
try {
    Start-Sleep -Milliseconds 1200
    $dangerOut = Join-Path $TempDir 'scan-danger.json'
    $null = Invoke-WatchdogCli -Arguments @('--scan', '--json', '--config', $dangerConfig, '--out', $dangerOut) -Tag 'scan-danger'
    $dangerText = Get-Content -LiteralPath $dangerOut -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
    $dangerItems = @($dangerText | ConvertFrom-Json)
    Assert-Verdict '危险标题（保存更改）不被识别' ($dangerItems.Count -eq 0) $dangerText
    Assert-Verdict '危险标题对话框保持未关闭' (-not (Test-Path -LiteralPath $dangerMarker)) '不应点击保存类提示'
} finally {
    if (-not $dangerProcess.HasExited) {
        Stop-Process -Id $dangerProcess.Id -Force -ErrorAction SilentlyContinue
    }
}

Write-Host '== 安全用例：默认配置不处理无按钮自绘框 =='
$defaultSafeConfig = Join-Path $TempDir 'config-default-safe.json'
$defaultSafeLog = Join-Path $TempDir 'logs-default-safe'
$defaultSafeMarker = Join-Path $TempDir 'closed-default-safe.txt'
New-Item -ItemType Directory -Force -Path $defaultSafeLog | Out-Null
Remove-Item -LiteralPath $defaultSafeMarker -Force -ErrorAction SilentlyContinue
@{
    targetProcessNames = @('LabVIEWDialogFixture')
    logDirectory       = $defaultSafeLog
    pollIntervalMs     = 200
    minSeenCount       = 1
    showNotifications  = $false
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $defaultSafeConfig -Encoding utf8
$defaultSafeProcess = Start-Quoted -FilePath $fixture -Arguments @('--labview-custom', $defaultSafeMarker) `
    -RedirectOut (Join-Path $TempDir 'fixture-default-safe.out') -RedirectErr (Join-Path $TempDir 'fixture-default-safe.err')
try {
    Start-Sleep -Milliseconds 1200
    $defaultSafeOut = Join-Path $TempDir 'scan-default-safe.json'
    $null = Invoke-WatchdogCli -Arguments @('--scan', '--json', '--config', $defaultSafeConfig, '--out', $defaultSafeOut) -Tag 'scan-default-safe'
    $defaultSafeText = Get-Content -LiteralPath $defaultSafeOut -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
    $defaultSafeItems = @($defaultSafeText | ConvertFrom-Json)
    Assert-Verdict '默认配置：无按钮自绘框不被识别' ($defaultSafeItems.Count -eq 0) $defaultSafeText
} finally {
    if (-not $defaultSafeProcess.HasExited) {
        Stop-Process -Id $defaultSafeProcess.Id -Force -ErrorAction SilentlyContinue
    }
}

Write-Host '== 安全用例：--dry-run 不点击 =='
$dryConfig = Join-Path $TempDir 'config-dryrun.json'
$dryLog = Join-Path $TempDir 'logs-dryrun'
$dryMarker = Join-Path $TempDir 'closed-dryrun.txt'
New-Item -ItemType Directory -Force -Path $dryLog | Out-Null
Remove-Item -LiteralPath $dryMarker -Force -ErrorAction SilentlyContinue
@{
    targetProcessNames = @('LabVIEWDialogFixture')
    logDirectory       = $dryLog
    pollIntervalMs     = 200
    showNotifications  = $false
    dryRun             = $true
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $dryConfig -Encoding utf8
$dryProcess = Start-Process -FilePath $fixture -ArgumentList @('--messagebox', $dryMarker) -PassThru `
    -RedirectStandardOutput (Join-Path $TempDir 'fixture-dryrun.out') -RedirectStandardError (Join-Path $TempDir 'fixture-dryrun.err')
try {
    Start-Sleep -Milliseconds 1200
    $null = Invoke-WatchdogCli -Arguments @('--watch-seconds', '2', '--config', $dryConfig) -Tag 'dryrun'
    Assert-Verdict 'dry-run: 对话框保持未关闭' (-not (Test-Path -LiteralPath $dryMarker)) 'dry-run 不应点击'
    Assert-Verdict 'dry-run: 日志说明将点击' (((Get-Content -LiteralPath (Join-Path $dryLog 'LabVIEWDialogWatchdog.log') -Raw -Encoding UTF8) -match 'dry-run')) '日志缺少 dry-run 记录'
} finally {
    if (-not $dryProcess.HasExited) {
        Stop-Process -Id $dryProcess.Id -Force -ErrorAction SilentlyContinue
    }
}

Write-Host '== 反向用例：非目标进程不处理 =='
$negativeConfig = Join-Path $TempDir 'config-negative.json'
$negativeLog = Join-Path $TempDir 'logs-negative'
New-Item -ItemType Directory -Force -Path $negativeLog | Out-Null
@{
    targetProcessNames = @('NotTheFixture')
    logDirectory       = $negativeLog
    pollIntervalMs     = 200
    showNotifications  = $false
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $negativeConfig -Encoding utf8
$negativeMarker = Join-Path $TempDir 'closed-negative.txt'
Remove-Item -LiteralPath $negativeMarker -Force -ErrorAction SilentlyContinue
$negativeProcess = Start-Process -FilePath $fixture -ArgumentList @('--messagebox', $negativeMarker) -PassThru `
    -RedirectStandardOutput (Join-Path $TempDir 'fixture-negative.out') -RedirectStandardError (Join-Path $TempDir 'fixture-negative.err')
try {
    Start-Sleep -Milliseconds 1200
    $negativeOut = Join-Path $TempDir 'scan-negative.json'
    $null = Invoke-WatchdogCli -Arguments @('--scan', '--json', '--config', $negativeConfig, '--out', $negativeOut) -Tag 'scan-negative'
    $negativeText = Get-Content -LiteralPath $negativeOut -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
    $negativeItems = @($negativeText | ConvertFrom-Json)
    Assert-Verdict '反向：非目标进程对话框不被识别' ($negativeItems.Count -eq 0) $negativeText
} finally {
    if (-not $negativeProcess.HasExited) {
        Stop-Process -Id $negativeProcess.Id -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ''
if ($script:failures -eq 0) {
    Write-Host "ALL PASS ($script:total checks)"
    exit 0
}
Write-Host "FAILED: $script:failures / $script:total checks"
exit 1
