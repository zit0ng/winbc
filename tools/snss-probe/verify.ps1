<#
.SYNOPSIS
  BrowserSessionReader 实测 / 回归脚本。
.DESCRIPTION
  三段验证：
   A. 真实环境：dump 当前机器的解析结果（给人看的主产物 + JSON）。
   B. 合成 Opera 多窗口 SNSS：验证 窗口分组 / 同一索引后写覆盖（丢掉已关闭标签）/ 选中态 /
      URL 4 字节补齐 / 标题按"字符数"解码 / 空 URL 过滤。
   C. 合成 Firefox mozLz4：验证 mozLz4 头 + LZ4 块解压（含回引）+ JSON 解析 + 窗口/选中映射。
  为让 B/C 通过"只在运行中的浏览器才记录"的过滤，会临时起一个改名后的 ping.exe 当假进程，
  结束后全部清理（删合成文件、按 PID 杀假进程、删临时目录）。
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\verify.ps1
#>
[CmdletBinding()]
param(
  [string]$OutDir = '',
  [switch]$SkipFixtures
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrEmpty($OutDir)) {
  $OutDir = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
}
$probeProj = Join-Path $OutDir 'WinBC.SnssProbe.csproj'
$results = New-Object System.Collections.Generic.List[object]

function Add-Result([string]$name, [bool]$ok, [string]$detail) {
  $results.Add([pscustomobject]@{ Name = $name; OK = $ok; Detail = $detail })
  Write-Host ("[{0}] {1}  {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)
}

function Run-Probe([string]$outFile) {
  Push-Location $OutDir
  try {
    $out = & dotnet run -c Release --no-build -- $outFile 2>&1
    return @{ ExitCode = $LASTEXITCODE; Output = ($out -join "`n") }
  } finally { Pop-Location }
}

# ───────────────────────── SNSS 合成工具 ─────────────────────────
function New-SnssWriter {
  $ms = New-Object System.IO.MemoryStream
  $bw = New-Object System.IO.BinaryWriter($ms)
  return @{ Stream = $ms; Writer = $bw }
}
function W-I32($w, [int]$v) { $w.Writer.Write([int]$v) }
function W-U16($w, [int]$v) { $w.Writer.Write([uint16]$v) }

function Add-SnssHeader($w) {
  $w.Writer.Write([System.Text.Encoding]::ASCII.GetBytes('SNSS'))
  W-I32 $w 3
}

function Add-Cmd-Simple($w, [int]$id, [int]$a, [int]$b) {
  W-U16 $w 9
  $w.Writer.Write([byte]$id)
  W-I32 $w $a
  W-I32 $w $b
}

function Add-Cmd-Nav($w, [int]$id, [int]$tabId, [int]$navIdx, [string]$url, [string]$title) {
  $urlBytes = [System.Text.Encoding]::UTF8.GetBytes($url)
  $titleBytes = [System.Text.Encoding]::Unicode.GetBytes($title)
  $urlPad = (4 - ($urlBytes.Length % 4)) % 4
  $titlePad = (4 - ($titleBytes.Length % 4)) % 4

  $fieldsLen = 12 + $urlBytes.Length + $urlPad + 4 + $titleBytes.Length + $titlePad
  W-U16 $w (5 + $fieldsLen)
  $w.Writer.Write([byte]$id)
  W-I32 $w $fieldsLen                     # [1..4] 长度前缀（实测 Chromium 写法）
  W-I32 $w $tabId
  W-I32 $w $navIdx
  W-I32 $w $urlBytes.Length
  $w.Writer.Write($urlBytes)
  if ($urlPad -gt 0) { $w.Writer.Write((New-Object byte[] $urlPad)) }
  W-I32 $w ([int]($titleBytes.Length / 2)) # 标题长度 = 字符数，不是字节数
  $w.Writer.Write($titleBytes)
  if ($titlePad -gt 0) { $w.Writer.Write((New-Object byte[] $titlePad)) }
}

function Save-Snss($w, [string]$path) {
  $w.Writer.Flush()
  [System.IO.File]::WriteAllBytes($path, $w.Stream.ToArray())
  $w.Writer.Dispose(); $w.Stream.Dispose()
}

# ───────────────────────── LZ4 block -> mozLz4 ─────────────────────────
function Add-LitRun($bw, [byte[]]$lit) {
  $n = $lit.Length
  $code = [Math]::Min(15, $n)
  $bw.Write([byte]($code -shl 4))
  if ($code -eq 15) {
    $v = $n - 15
    while ($v -ge 255) { $bw.Write([byte]255); $v -= 255 }
    $bw.Write([byte]$v)
  }
  if ($n -gt 0) { $bw.Write($lit) }
}

<#
  把 JSON 压成 mozLz4：magic + int32 未压缩长度 + LZ4 block。
  为了让解压逻辑真的走到"回引"分支，在 JSON 里预留一段 N 个相同字符的连写，
  压缩时把前 1 个字符当字面量，剩下的 N-1 个用 offset=1 的 match 表达。
#>
function New-MozLz4([byte[]]$json, [int]$runStart, [int]$runLen) {
  $ms = New-Object System.IO.MemoryStream
  $bw = New-Object System.IO.BinaryWriter($ms)
  $bw.Write([byte[]](0x6d, 0x6f, 0x7a, 0x4c, 0x7a, 0x34, 0x30, 0x00))   # "mozLz40\0"
  $bw.Write([int]$json.Length)

  $head = New-Object byte[] ($runStart + 1)
  [Array]::Copy($json, 0, $head, 0, $runStart + 1)
  $litLen = $head.Length
  $matchTotal = $runLen - 1                       # 还需复制多少字节
  $matchCodeVal = $matchTotal - 4                 # 编码里 matchLen 已含 +4
  $tokenCode = [Math]::Min(15, $litLen)
  $mCode = [Math]::Min(15, $matchCodeVal)
  $bw.Write([byte](($tokenCode -shl 4) -bor $mCode))
  if ($tokenCode -eq 15) {
    $v = $litLen - 15
    while ($v -ge 255) { $bw.Write([byte]255); $v -= 255 }
    $bw.Write([byte]$v)
  }
  $bw.Write($head)
  $bw.Write([byte]0x01); $bw.Write([byte]0x00)    # offset = 1 -> 连续复制同一字符
  if ($mCode -eq 15) {
    $v = $matchCodeVal - 15
    while ($v -ge 255) { $bw.Write([byte]255); $v -= 255 }
    $bw.Write([byte]$v)
  }

  $restStart = $runStart + $runLen
  $restLen = $json.Length - $restStart
  $rest = New-Object byte[] $restLen
  [Array]::Copy($json, $restStart, $rest, 0, $restLen)
  Add-LitRun $bw $rest
  $bw.Flush()
  $out = $ms.ToArray()
  $bw.Dispose(); $ms.Dispose()
  return $out
}

function Find-ByteRun([byte[]]$data, [byte]$value, [int]$count) {
  for ($i = 0; $i -le $data.Length - $count; $i++) {
    if ($data[$i] -ne $value) { continue }
    $ok = $true
    for ($k = 1; $k -lt $count; $k++) {
      if ($data[$i + $k] -ne $value) { $ok = $false; break }
    }
    if ($ok) { return $i }
  }
  return -1
}

# ───────────────────────── A. 真实环境 ─────────────────────────
Write-Host ''
Write-Host '=== A. 真实环境解析 ==='
dotnet build $probeProj -c Release -v q --nologo | Out-Null
$realOut = Join-Path $OutDir 'out-verify.txt'
$r = Run-Probe $realOut
Write-Host $r.Output
$realJson = [System.IO.Path]::ChangeExtension($realOut, '.json')
$okReal = (Test-Path $realOut) -and (Test-Path $realJson)
Add-Result 'A1 dump + json 生成' $okReal "$realOut"

if ($okReal) {
  $data = @(Get-Content $realJson -Raw -Encoding UTF8 | ConvertFrom-Json | ForEach-Object { $_ })
  $tabs = @($data | ForEach-Object { $_.Tabs } | ForEach-Object { $_ })
  Add-Result 'A2 结构 = BrowserWindowEntry 列表' ($data.Count -ge 0) ("窗口 " + $data.Count + " / 标签 " + $tabs.Count)
  Add-Result 'A3 无空 URL' (@($tabs | Where-Object { [string]::IsNullOrEmpty($_.Url) }).Count -eq 0) ''
  Add-Result 'A4 标签总数 <= 100' ($tabs.Count -le 100) ("实际 " + $tabs.Count)
  $nonRunning = @($data | Where-Object { $_.BrowserId -in @('edge', 'opera', 'firefox') })
  Add-Result 'A5 未运行的浏览器未出现' ($nonRunning.Count -eq 0) (($data | ForEach-Object { $_.BrowserId }) -join ',')
}

# ───────────────────────── B/C. 合成 fixture ─────────────────────────
$operaSessionDir = Join-Path $env:APPDATA 'Opera Software\Opera Stable\Default\Sessions'
$operaFixture = Join-Path $operaSessionDir 'Session_99999999999999999'
$ffRoot = Join-Path $env:APPDATA 'Mozilla\Firefox'
$ffBackup = Join-Path $ffRoot 'Profiles\verify.fixture\sessionstore-backups'
$dummyIds = New-Object System.Collections.Generic.List[int]
$dummyExe = New-Object System.Collections.Generic.List[string]

if (-not $SkipFixtures) {
  try {
    # 假进程：ping.exe 改名成 opera.exe / firefox.exe，让"只在运行才记录"的过滤放行
    $seed = Join-Path $env:SystemRoot 'System32\PING.EXE'
    foreach ($n in @('opera', 'firefox')) {
      # 文件名必须正好是 <浏览器进程名>.exe，否则 ProcessName 不是 opera/firefox
      $dir = Join-Path $env:TEMP ("winbc-fake-" + $n)
      New-Item -ItemType Directory -Force -Path $dir | Out-Null
      $fake = Join-Path $dir ($n + '.exe')
      Copy-Item $seed $fake -Force
      $p = Start-Process -FilePath $fake -ArgumentList '-n', '90', '127.0.0.1' -PassThru -WindowStyle Hidden
      $dummyIds.Add($p.Id)
      $dummyExe.Add($fake)
    }
    Start-Sleep -Milliseconds 500
    Add-Result 'B0 假进程就绪' ((Get-Process -Id $dummyIds -ErrorAction SilentlyContinue).Count -eq 2) (($dummyIds | ForEach-Object { $_.ToString() }) -join ',')

    # ── B. Opera 多窗口 SNSS ──
    Write-Host ''
    Write-Host '=== B. 合成 Opera 多窗口会话 ==='
    $w = New-SnssWriter
    Add-SnssHeader $w
    # 窗口 1（选中第 1 个，0 基）
    Add-Cmd-Simple $w 8 0x0A0B0001 1
    Add-Cmd-Simple $w 0 0x0A0B0001 1001
    Add-Cmd-Simple $w 2 1001 0
    Add-Cmd-Nav    $w 6 1001 0 'https://example.com/a'  'A 页'          # urlLen 19 -> 1 字节补齐
    Add-Cmd-Nav    $w 6 1001 1 'https://example.com/a2' 'A2 页·标题'    # 同 tab 多条 -> 取 selected nav
    Add-Cmd-Simple $w 7 1001 1
    Add-Cmd-Simple $w 0 0x0A0B0001 1002
    Add-Cmd-Simple $w 2 1002 1
    Add-Cmd-Nav    $w 6 1002 0 'https://example.org/b'  'B 页'
    Add-Cmd-Nav    $w 6 1002 0 'https://example.org/b'  'B 页覆盖'      # 同 (tab,idx) 后写覆盖
    Add-Cmd-Simple $w 0 0x0A0B0001 1003
    Add-Cmd-Simple $w 2 1003 2                     # 占位索引 2
    Add-Cmd-Nav    $w 6 1003 0 'https://closed.example/x' '已关闭的旧标签'
    Add-Cmd-Simple $w 0 0x0A0B0001 1004
    Add-Cmd-Simple $w 2 1004 2                     # 同索引后写 -> 1003 必须消失
    Add-Cmd-Nav    $w 6 1004 0 'https://example.net/c'  'C 页'          # urlLen 19
    Add-Cmd-Simple $w 0 0x0A0B0001 1005
    Add-Cmd-Simple $w 2 1005 3                     # 空 URL -> 必须被过滤
    # 窗口 2（选中第 0 个）
    Add-Cmd-Simple $w 8 0x0A0B0002 0
    Add-Cmd-Simple $w 0 0x0A0B0002 2001
    Add-Cmd-Simple $w 2 2001 0
    Add-Cmd-Nav    $w 6 2001 0 'https://example.io/d'  'D 页'           # urlLen 17
    Add-Cmd-Simple $w 0 0x0A0B0002 2002
    Add-Cmd-Simple $w 2 2002 1
    Add-Cmd-Nav    $w 6 2002 0 'https://example.io/e'  'E 页'
    Save-Snss $w $operaFixture

    $fxOut = Join-Path $OutDir 'out-fixture-opera.txt'
    $r2 = Run-Probe $fxOut
    Write-Host $r2.Output
    $fx = @(Get-Content ([System.IO.Path]::ChangeExtension($fxOut, '.json')) -Raw -Encoding UTF8 | ConvertFrom-Json | ForEach-Object { $_ })
    $opera = @($fx | Where-Object { $_.BrowserId -eq 'opera' })

    Add-Result 'B1 两个窗口分别成组' ($opera.Count -eq 2) ("opera 窗口数 " + $opera.Count)
    if ($opera.Count -eq 2) {
      $w0 = $opera | Where-Object { $_.WindowIndex -eq 0 }
      $w1 = $opera | Where-Object { $_.WindowIndex -eq 1 }
      Add-Result 'B2 窗口0 标签数=3（空 URL 被滤）' (@($w0.Tabs).Count -eq 3) ("实际 " + @($w0.Tabs).Count)
      Add-Result 'B3 窗口0 顺序与覆盖正确' (($w0.Tabs[0].Url -eq 'https://example.com/a2') -and ($w0.Tabs[1].Url -eq 'https://example.org/b') -and ($w0.Tabs[2].Url -eq 'https://example.net/c')) (($w0.Tabs | ForEach-Object { $_.Url }) -join ' | ')
      Add-Result 'B4 已关闭标签被同索引覆盖剔除' (@($w0.Tabs | Where-Object { $_.Url -like '*closed.example*' }).Count -eq 0) ''
      Add-Result 'B5 选中导航项生效（1001 -> /a2）' ($w0.Tabs[0].Url -eq 'https://example.com/a2') ''
      Add-Result 'B6 标题按字符数解码（含中文）' (($w0.Tabs[0].Title -eq 'A2 页·标题') -and ($w0.Tabs[1].Title -eq 'B 页覆盖')) ("'" + $w0.Tabs[0].Title + "' / '" + $w0.Tabs[1].Title + "'")
      Add-Result 'B7 窗口0 选中态 = 索引 1' ((@($w0.Tabs | Where-Object { $_.Selected }).Count -eq 1) -and $w0.Tabs[1].Selected) ''
      Add-Result 'B8 窗口1 两个标签且选中第 0 个' ((@($w1.Tabs).Count -eq 2) -and $w1.Tabs[0].Selected -and (-not $w1.Tabs[1].Selected)) ''
      Add-Result 'B9 ExePath/UserDataDir/ProfileName 有值' ((-not [string]::IsNullOrEmpty($w0.ProfileDir)) -and (-not [string]::IsNullOrEmpty($w0.ProfileName))) ("profile=" + $w0.ProfileName)
    }
  } catch {
    Add-Result 'B* fixture 执行异常' $false $_.Exception.Message
  } finally {
    if (Test-Path $operaFixture) { Remove-Item $operaFixture -Force }
  }

  # ── C. Firefox mozLz4 ──
  try {
    Write-Host ''
    Write-Host '=== C. 合成 Firefox mozLz4 会话 ==='
    New-Item -ItemType Directory -Force -Path $ffBackup | Out-Null
    $run = 'a' * 40        # 故意留一段 40 个相同字符，逼解压走到回引分支
    $session = @{
      windows = @(
        @{
          selected = 2
          tabs = @(
            @{ index = 1; pinned = $true;  entries = @(@{ url = 'https://ff.example/one'; title = 'FF 一' }) },
            @{ index = 1; pinned = $false; entries = @(@{ url = 'https://ff.example/two'; title = $run }) }
          )
        },
        @{
          selected = 1
          tabs = @(
            @{ index = 2; pinned = $false; entries = @(@{ url = 'about:blank'; title = '空白' }, @{ url = 'https://ff.example/three'; title = 'FF 三' }) }
          )
        }
      )
    }
    $jsonBytes = [System.Text.Encoding]::UTF8.GetBytes(($session | ConvertTo-Json -Depth 10 -Compress))
    $runStart = Find-ByteRun $jsonBytes 0x61 40
    Add-Result 'C0 fixture 里找到 40 字符连写' ($runStart -ge 0) ("offset " + $runStart)

    $lz4 = New-MozLz4 $jsonBytes $runStart 40
    [System.IO.File]::WriteAllBytes((Join-Path $ffBackup 'recovery.jsonlz4'), $lz4)

    $fxOut2 = Join-Path $OutDir 'out-fixture-firefox.txt'
    $r3 = Run-Probe $fxOut2
    Write-Host $r3.Output
    $fx2 = @(Get-Content ([System.IO.Path]::ChangeExtension($fxOut2, '.json')) -Raw -Encoding UTF8 | ConvertFrom-Json | ForEach-Object { $_ })
    $ff = @($fx2 | Where-Object { $_.BrowserId -eq 'firefox' })

    Add-Result 'C1 mozLz4 解压 + 两个窗口' ($ff.Count -eq 2) ("firefox 窗口数 " + $ff.Count)
    if ($ff.Count -eq 2) {
      $f0 = $ff | Where-Object { $_.WindowIndex -eq 0 }
      $f1 = $ff | Where-Object { $_.WindowIndex -eq 1 }
      Add-Result 'C2 窗口0 两个标签、pinned 生效' ((@($f0.Tabs).Count -eq 2) -and $f0.Tabs[0].Pinned -and ($f0.Tabs[0].Url -eq 'https://ff.example/one')) ''
      Add-Result 'C3 回引解压出的 40 字符标题完整' ($f0.Tabs[1].Title.Length -eq 40) ("长度 " + $f0.Tabs[1].Title.Length)
      Add-Result 'C4 窗口0 选中第 2 个（Firefox 1 基）' ($f0.Tabs[1].Selected -and (-not $f0.Tabs[0].Selected)) ''
      Add-Result 'C5 窗口1 按 1 基 index=2 取到最后一条 entry' ((@($f1.Tabs).Count -eq 1) -and ($f1.Tabs[0].Url -eq 'https://ff.example/three')) ("实际 " + $f1.Tabs[0].Url)
    }
  } catch {
    Add-Result 'C* fixture 执行异常' $false $_.Exception.Message
  } finally {
    foreach ($id in $dummyIds) {
      try { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue } catch { }
    }
    foreach ($p in $dummyExe) {
      if (Test-Path $p) { Remove-Item $p -Force -ErrorAction SilentlyContinue }
      $d = Split-Path $p -Parent
      if ((Test-Path $d) -and ([System.IO.Path]::GetFileName($d) -like 'winbc-fake-*')) { Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue }
    }
    if (Test-Path $ffRoot) { Remove-Item $ffRoot -Recurse -Force -ErrorAction SilentlyContinue }
    $mozRoot = Split-Path $ffRoot -Parent
    if ((Test-Path $mozRoot) -and (@(Get-ChildItem $mozRoot -Recurse -Force -File -ErrorAction SilentlyContinue)).Count -eq 0) { Remove-Item $mozRoot -Recurse -Force -ErrorAction SilentlyContinue }
  }
}

# ───────────────────────── 汇总 ─────────────────────────
Write-Host ''
Write-Host '================ 汇总 ================'
$fail = @($results | Where-Object { -not $_.OK })
$results | ForEach-Object { Write-Host ("{0,-4} {1}  {2}" -f $(if ($_.OK) { 'PASS' } else { 'FAIL' }), $_.Name, $_.Detail) }
Write-Host ''
if ($fail.Count -eq 0) { Write-Host "全部通过（$($results.Count) 项）" } else { Write-Host "失败 $($fail.Count) / $($results.Count)" }
$results | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $OutDir 'verify-results.json') -Encoding UTF8
if ($fail.Count -gt 0) { exit 1 }
