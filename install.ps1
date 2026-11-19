#Requires -Version 5.1
<#
.SYNOPSIS
    安装 WinBC：复制程序、设置开机自启、注册"关机瞬间采集"计划任务。
.DESCRIPTION
    全部操作都在当前用户范围内（HKCU Run + 当前用户计划任务），不需要管理员权限。
.PARAMETER InstallDir
    安装目录，默认 %LOCALAPPDATA%\WinBC\bin
.PARAMETER NoAutostart
    不写开机自启（HKCU Run）
.PARAMETER NoTask
    不注册关机采集计划任务
.EXAMPLE
    .\install.ps1
    .\install.ps1 -InstallDir 'D:\Apps\WinBC'
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "$env:LOCALAPPDATA\WinBC\bin",
    [switch]$NoAutostart,
    [switch]$NoTask
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root 'publish\WinBC.exe'

if (-not (Test-Path $src)) {
    throw "找不到 $src。请先运行 .\build.ps1"
}

# ---------- 1. 复制程序 ----------
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
$exe = Join-Path $InstallDir 'WinBC.exe'

$running = Get-Process -Name 'WinBC' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host '==> 关闭正在运行的 WinBC 实例' -ForegroundColor Yellow
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 600
}

Copy-Item $src $exe -Force
Write-Host "==> 已安装：$exe" -ForegroundColor Green

# ---------- 2. 开机自启 ----------
if (-not $NoAutostart) {
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    if (-not (Test-Path $runKey)) { New-Item -Path $runKey -Force | Out-Null }
    Set-ItemProperty -Path $runKey -Name 'WinBC' -Value ('"' + $exe + '" --tray') -Force
    Write-Host '==> 已设置开机自启：HKCU\...\Run\WinBC = "WinBC.exe" --tray' -ForegroundColor Green
}

# ---------- 3. 关机采集计划任务（第二道保险） ----------
if (-not $NoTask) {
    $taskName = 'WinBC-ShutdownSnapshot'
    $user = "$env:USERDOMAIN\$env:USERNAME"

    $xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>WinBC：系统即将关机/重启时，立即记录一次桌面状态快照。</Description>
    <URI>\$taskName</URI>
  </RegistrationInfo>
  <Triggers>
    <EventTrigger>
      <Enabled>true</Enabled>
      <Subscription>&lt;QueryList&gt;&lt;Query Id="0" Path="System"&gt;&lt;Select Path="System"&gt;*[System[Provider[@Name='User32'] and (EventID=1074)]]&lt;/Select&gt;&lt;/Query&gt;&lt;/QueryList&gt;</Subscription>
      <Delay>PT0S</Delay>
    </EventTrigger>
    <EventTrigger>
      <Enabled>true</Enabled>
      <Subscription>&lt;QueryList&gt;&lt;Query Id="0" Path="System"&gt;&lt;Select Path="System"&gt;*[System[Provider[@Name='EventLog'] and (EventID=6006)]]&lt;/Select&gt;&lt;/Query&gt;&lt;/QueryList&gt;</Subscription>
      <Delay>PT0S</Delay>
    </EventTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>$user</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT2M</ExecutionTimeLimit>
    <Priority>4</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>$exe</Command>
      <Arguments>--snapshot shutdown</Arguments>
      <WorkingDirectory>$InstallDir</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
"@

    $tmp = Join-Path $env:TEMP ("winbc_task_" + [guid]::NewGuid().ToString('N') + ".xml")
    # schtasks 需要 UTF-16 编码的 XML
    [System.IO.File]::WriteAllText($tmp, $xml, [System.Text.UnicodeEncoding]::new($false, $true))

    try {
        $out = schtasks /Create /TN $taskName /XML $tmp /F 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Host '==> 计划任务注册失败（不影响主功能——常驻程序和滚动快照仍在工作）' -ForegroundColor Yellow
            Write-Host ($out -join "`n") -ForegroundColor DarkGray
        }
        else {
            Write-Host "==> 已注册计划任务：$taskName（事件 1074/6006）" -ForegroundColor Green
        }
    }
    finally {
        Remove-Item $tmp -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ''
Write-Host '==> 安装完成' -ForegroundColor Green
Write-Host '    正在启动 WinBC 托盘程序…'
Start-Process -FilePath $exe -ArgumentList '--tray' -WorkingDirectory $InstallDir
Start-Sleep -Seconds 2

$p = Get-Process -Name 'WinBC' -ErrorAction SilentlyContinue
if ($p) {
    Write-Host "    WinBC 正在运行（PID $($p[0].Id)），托盘区可见。" -ForegroundColor Green
}
else {
    Write-Host '    WinBC 未在运行，请看 %LOCALAPPDATA%\WinBC\logs 下的日志。' -ForegroundColor Yellow
}
Write-Host ''
Write-Host '  快照目录：%LOCALAPPDATA%\WinBC\snapshots'
Write-Host '  日志目录：%LOCALAPPDATA%\WinBC\logs'
Write-Host '  卸载：    .\uninstall.ps1'
