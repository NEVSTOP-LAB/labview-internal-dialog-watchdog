#Requires -Version 7
<#
  用系统自带的 .NET Framework 4.8 csc 编译，不依赖 dotnet SDK。
  产物：dist\LabVIEWInternalDialogWatchdog.exe；-Tests 时额外编出测试夹具与测试运行器。
#>
param(
    [switch]$Tests,
    [switch]$Clean,
    [string]$OutDir = 'dist'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) {
    throw "找不到 .NET Framework csc.exe：$csc"
}

$dist = Join-Path $root $OutDir
if ($Clean -and (Test-Path -LiteralPath $dist)) {
    Remove-Item -LiteralPath $dist -Recurse -Force
}
$testDir = Join-Path $dist 'tests'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
if ($Tests) {
    New-Item -ItemType Directory -Force -Path $testDir | Out-Null
}

$uiaDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\WPF'
$references = @(
    '/r:System.dll',
    '/r:System.Core.dll',
    '/r:System.Drawing.dll',
    '/r:System.Windows.Forms.dll',
    '/r:System.Web.Extensions.dll',
    ('/r:' + (Join-Path $uiaDir 'UIAutomationClient.dll')),
    ('/r:' + (Join-Path $uiaDir 'UIAutomationTypes.dll'))
)
$sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
$manifest = Join-Path $root 'src\app.manifest'

Write-Host '编译 LabVIEWInternalDialogWatchdog.exe ...'
& $csc /nologo /target:winexe /platform:anycpu /optimize+ /warn:4 `
    ('/win32manifest:' + $manifest) `
    ('/out:' + (Join-Path $dist 'LabVIEWInternalDialogWatchdog.exe')) `
    @references @sources
if ($LASTEXITCODE -ne 0) { throw "主程序编译失败（csc 退出码 $LASTEXITCODE）" }

if ($Tests) {
    Write-Host '编译测试夹具 LabVIEWDialogFixture.exe ...'
    & $csc /nologo /target:winexe /platform:anycpu /optimize+ `
        ('/out:' + (Join-Path $testDir 'LabVIEWDialogFixture.exe')) `
        @references (Join-Path $root 'tests\DialogFixture.cs')
    if ($LASTEXITCODE -ne 0) { throw "测试夹具编译失败（csc 退出码 $LASTEXITCODE）" }

    Write-Host '编译测试运行器 ...'
    $testSources = @($sources) + @(Join-Path $root 'tests\TestRunner.cs')
    & $csc /nologo /target:exe /platform:anycpu /optimize+ /main:LabVIEWInternalDialogWatchdog.Tests.TestRunner `
        ('/out:' + (Join-Path $testDir 'LabVIEWInternalDialogWatchdog.Tests.exe')) `
        @references @testSources
    if ($LASTEXITCODE -ne 0) { throw "测试运行器编译失败（csc 退出码 $LASTEXITCODE）" }
}

Write-Host "完成：$dist"
