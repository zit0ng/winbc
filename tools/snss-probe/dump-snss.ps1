<#
.SYNOPSIS
  把 Chromium SNSS 会话文件的所有 command 逐条 dump 出来（id + size + payload hex），
  用于反推 kCommandSetTabWindow 等命令的二进制布局。
.PARAMETER Path
  SNSS 文件路径（Session_* / Tabs_* / Last Session）。
.PARAMETER MaxHex
  每条 command 打印的 payload hex 最大字节数，默认 48。
.PARAMETER CmdFilter
  只打印这些 command id（逗号分隔），空 = 全部。
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string]$Path,
  [int]$MaxHex = 48,
  [int[]]$CmdFilter = @()
)

$ErrorActionPreference = 'Stop'
$bytes = [System.IO.File]::ReadAllBytes((Resolve-Path $Path))
$len = $bytes.Length

function Dec([int64]$v) { $v }

$magic = [System.Text.Encoding]::ASCII.GetString($bytes, 0, 4)
$version = [System.BitConverter]::ToInt32($bytes, 4)
Write-Host "FILE   : $Path"
Write-Host "SIZE   : $len"
Write-Host "MAGIC  : $magic"
Write-Host "VERSION: $version"
if ($magic -ne 'SNSS') { throw "not an SNSS file (magic=$magic)" }

$pos = 8
$n = 0
$stats = @{}
$rows = New-Object System.Collections.Generic.List[object]

while ($pos + 2 -le $len) {
  $size = [int][System.BitConverter]::ToUInt16($bytes, $pos)
  $pos += 2
  if ($size -le 0) { Write-Host "!! zero/negative size at $pos"; break }
  if ($pos + $size -gt $len) { Write-Host "!! truncated payload at $pos size=$size remain=$($len-$pos)"; break }
  $cmd = $bytes[$pos]
  $chunkLen = [Math]::Min($MaxHex, $size)
  $hex = -join ($bytes[$pos..($pos + $chunkLen - 1)] | ForEach-Object { $_.ToString('x2') })
  $rows.Add([pscustomobject]@{ N = $n; Off = $pos; Id = $cmd; Size = $size; Hex = $hex })
  if (-not $stats.ContainsKey($cmd)) { $stats[$cmd] = 0 }
  $stats[$cmd]++
  $pos += $size
  $n++
}

Write-Host "COMMANDS: $n   tailPos=$pos (len=$len, leftover=$(($len-$pos)))"
Write-Host ""
Write-Host "== command id histogram =="
$stats.GetEnumerator() | Sort-Object -Property Value -Descending | ForEach-Object {
  Write-Host ("  id={0,-3} count={1}" -f $_.Key, $_.Value)
}
Write-Host ""
Write-Host "== command list =="
foreach ($r in $rows) {
  if ($CmdFilter.Count -gt 0 -and ($CmdFilter -notcontains $r.Id)) { continue }
  Write-Host ("#{0,-5} off={1,-7} id={2,-3} size={3,-5} {4}" -f $r.N, $r.Off, $r.Id, $r.Size, $r.Hex)
}
