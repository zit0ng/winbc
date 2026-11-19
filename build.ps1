#Requires -Version 5.1
<#
.SYNOPSIS
    构建 WinBC 单文件 exe。
.DESCRIPTION
    默认生成依赖框架的单文件（约 1~3MB，需要本机安装 .NET 10 Desktop Runtime）。
    加 -SelfContained 生成自带运行时的独立 exe（约 150MB，任何 Win10/11 x64 可直接跑）。
.EXAMPLE
    .\build.ps1
    .\build.ps1 -SelfContained
    .\build.ps1 -Run
#>
[CmdletBinding()]
param(
    [switch]$SelfContained,
    [switch]$Run,
    [string]$OutDir = 'publish'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root 'src\WinBC\WinBC.csproj'
$out = Join-Path $root $OutDir

if (-not (Test-Path $proj)) { throw "找不到项目文件：$proj" }

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$args = @(
    'publish', $proj,
    '-c', 'Release',
    '-r', 'win-x64',
    "-p:PublishSingleFile=true",
    "-p:SelfContained=$($SelfContained.IsPresent.ToString().ToLower())",
    '-p:DebugType=none',
    '-p:GenerateDocumentationFile=false',
    '-o', $out,
    '--nologo'
)

Write-Host "==> dotnet $($args -join ' ')" -ForegroundColor Cyan
& dotnet @args
if ($LASTEXITCODE -ne 0) { throw "构建失败（exit $LASTEXITCODE）" }

$exe = Join-Path $out 'WinBC.exe'
if (-not (Test-Path $exe)) { throw "构建完成但没有生成 $exe" }

$size = [math]::Round((Get-Item $exe).Length / 1MB, 2)
Write-Host "==> 完成：$exe（$size MB）" -ForegroundColor Green
Write-Host "    下一步：.\install.ps1    # 安装并注册开机自启 / 关机采集任务"

if ($Run) {
    Write-Host '==> 启动 WinBC' -ForegroundColor Cyan
    Start-Process -FilePath $exe
}
