#Requires -Version 5.1
<#
.SYNOPSIS
    卸载 WinBC：停止进程、删除开机自启、删除计划任务、可选删除数据。
.EXAMPLE
    .\uninstall.ps1
    .\uninstall.ps1 -RemoveData -InstallDir 'D:\Apps\WinBC'
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "$env:LOCALAPPDATA\WinBC\bin",
    [switch]$RemoveData
)

$ErrorActionPreference = 'Continue'

# 1. 停止进程
Get-Process -Name 'WinBC' -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "==> 结束进程 PID $($_.Id)" -ForegroundColor Yellow
    try { $_.Kill() } catch { }
}
Start-Sleep -Milliseconds 500

# 2. 开机自启
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (Get-ItemProperty -Path $runKey -Name 'WinBC' -ErrorAction SilentlyContinue) {
    Remove-ItemProperty -Path $runKey -Name 'WinBC' -Force
    Write-Host '==> 已移除开机自启' -ForegroundColor Green
}

# 3. 计划任务
$taskName = 'WinBC-ShutdownSnapshot'
$exists = schtasks /Query /TN $taskName 2>$null
if ($LASTEXITCODE -eq 0) {
    schtasks /Delete /TN $taskName /F | Out-Null
    Write-Host "==> 已删除计划任务 $taskName" -ForegroundColor Green
}

# 4. 程序文件
if (Test-Path (Join-Path $InstallDir 'WinBC.exe')) {
    Remove-Item (Join-Path $InstallDir 'WinBC.exe') -Force
    Write-Host "==> 已删除 $InstallDir\WinBC.exe" -ForegroundColor Green
    try { Remove-Item $InstallDir -Force -Recurse -ErrorAction Stop } catch { }
}

# 5. 数据
if ($RemoveData) {
    $data = Join-Path $env:LOCALAPPDATA 'WinBC'
    if (Test-Path $data) {
        Remove-Item $data -Recurse -Force
        Write-Host "==> 已删除数据目录 $data（所有快照）" -ForegroundColor Green
    }
}
else {
    Write-Host '   （快照数据仍保留在 %LOCALAPPDATA%\WinBC，加 -RemoveData 可一并删除）' -ForegroundColor DarkGray
}

Write-Host '==> 卸载完成' -ForegroundColor Green
