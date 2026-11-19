<#
.SYNOPSIS
  SNSS 会话解码验证脚本：按实测出的 command 布局还原 窗口/标签/URL/标题/选中态。
.DESCRIPTION
  实测布局（Chrome 138 条 command 精确走到文件尾，见 dump-snss.ps1）：
   header: "SNSS" + int32 version(=3)
   then:   [uint16 size][size bytes payload]
   payload[0] = command id
     id=0  SetTabWindow            : [int32 windowId][int32 tabId]
     id=2  SetTabIndexInWindow     : [int32 tabId][int32 index]
     id=6  UpdateTabNavigation     : [int32 navEntryId][int32 tabId][int32 navIndex]
                                     [int32 urlLen][url utf8][int32 titleLen][title utf16le]
     id=7  SetSelectedNavigationIdx: [int32 tabId][int32 navIndex]
     id=8  SetSelectedTabInIndex   : [int32 windowId][int32 tabIndex]
     id=14 SetWindowBounds2        : [int32 windowId][int32 x][int32 y][int32 w][int32 h][int32 showState]
   降级（Tabs_ 文件）布局：
     id=1  UpdateTabNavigation     : 同 id=6
     id=4  TabState                : [int32 tabId][int32 navIndex][int64 time]
.PARAMETER Path
  Session_/Last Session/Tabs_ 文件。
.PARAMETER OutFile
  解析结果 dump 文件。
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string]$Path,
  [string]$OutFile = ""
)

$ErrorActionPreference = 'Stop'
$bytes = [System.IO.File]::ReadAllBytes((Resolve-Path $Path))
$len = $bytes.Length
$magic = [System.Text.Encoding]::ASCII.GetString($bytes, 0, 4)
if ($magic -ne 'SNSS') { throw "not SNSS: $magic" }
$version = [System.BitConverter]::ToInt32($bytes, 4)

function R32([int]$o) { [System.BitConverter]::ToInt32($bytes, $o) }
function U32([int]$o) { [System.BitConverter]::ToUInt32($bytes, $o) }

# 累积状态
$tabWindow = @{}          # tabId -> windowId  (id=0)
$winIndexTab = @{}        # "win|idx" -> tabId (id=2, last wins)
$tabIndex = @{}           # tabId -> idx       (id=2, last wins)
$tabNavSel = @{}          # tabId -> navIndex  (id=7)
$winSelIndex = @{}        # windowId -> tabIdx (id=8)
$winBounds = @{}          # windowId -> @(x,y,w,h,state)
$nav = @{}                # "tabId|navIndex" -> @{Url;Title}  (last wins)
$tabOrder = New-Object System.Collections.Generic.List[int]   # 首次出现顺序（稳定排序用）
$seenTab = @{}

$pos = 8
$cmdCount = 0
while ($pos + 2 -le $len) {
  $size = [int][System.BitConverter]::ToUInt16($bytes, $pos)
  $pos += 2
  if ($size -le 0) { break }
  if ($pos + $size -gt $len) { break }
  $id = $bytes[$pos]
  $cmdCount++

  try {
    switch ($id) {
      0 {
        if ($size -ge 9) {
          $w = R32 ($pos + 1); $t = R32 ($pos + 5)
          $tabWindow[$t] = $w
          if (-not $seenTab.ContainsKey($t)) { $seenTab[$t] = 1; $tabOrder.Add($t) }
        }
      }
      2 {
        if ($size -ge 9) {
          $t = R32 ($pos + 1); $i = R32 ($pos + 5)
          $tabIndex[$t] = $i
          if (-not $seenTab.ContainsKey($t)) { $seenTab[$t] = 1; $tabOrder.Add($t) }
        }
      }
      6 {
        if ($size -ge 17) {
          $t = R32 ($pos + 5)
          $ni = R32 ($pos + 9)
          $ul = R32 ($pos + 13)
          if ($ul -ge 0 -and ($pos + 17 + $ul + 4) -le $len) {
            $url = [System.Text.Encoding]::UTF8.GetString($bytes, $pos + 17, $ul)
            $tl = R32 ($pos + 17 + $ul)
            $title = ""
            $toff = $pos + 21 + $ul
            if ($tl -ge 0 -and ($toff + $tl) -le $len) {
              $title = [System.Text.Encoding]::Unicode.GetString($bytes, $toff, $tl)
            }
            $nav["$t|$ni"] = @{ Url = $url; Title = $title }
            if (-not $seenTab.ContainsKey($t)) { $seenTab[$t] = 1; $tabOrder.Add($t) }
          }
        }
      }
      7 {
        if ($size -ge 9) { $tabNavSel[(R32 ($pos + 1))] = (R32 ($pos + 5)) }
      }
      8 {
        if ($size -ge 9) { $winSelIndex[(R32 ($pos + 1))] = (R32 ($pos + 5)) }
      }
      14 {
        if ($size -ge 25) {
          $w = R32 ($pos + 1)
          $winBounds[$w] = @((R32 ($pos + 5)), (R32 ($pos + 9)), (R32 ($pos + 13)), (R32 ($pos + 17)), (R32 ($pos + 21)))
        }
      }
      1 {
        # Tabs_ 文件里的导航记录，布局同 id=6
        if ($size -ge 17) {
          $t = R32 ($pos + 5); $ni = R32 ($pos + 9); $ul = R32 ($pos + 13)
          if ($ul -ge 0 -and ($pos + 17 + $ul + 4) -le $len) {
            $url = [System.Text.Encoding]::UTF8.GetString($bytes, $pos + 17, $ul)
            $tl = R32 ($pos + 17 + $ul)
            $title = ""
            $toff = $pos + 21 + $ul
            if ($tl -ge 0 -and ($toff + $tl) -le $len) { $title = [System.Text.Encoding]::Unicode.GetString($bytes, $toff, $tl) }
            $nav["$t|$ni"] = @{ Url = $url; Title = $title }
            if (-not $seenTab.ContainsKey($t)) { $seenTab[$t] = 1; $tabOrder.Add($t) }
          }
        }
      }
    }
  } catch {
    Write-Warning ("cmd #{0} id={1} at {2} threw: {3}" -f $cmdCount, $id, $pos, $_.Exception.Message)
  }
  $pos += $size
}

# 窗口分组：优先用 id=2 的 (window,index) 唯一性，index 冲突时后写覆盖（等于丢弃已关闭的旧 tab）
$groups = @{}
foreach ($t in $tabOrder) {
  if (-not $tabIndex.ContainsKey($t)) { continue }
  $w = if ($tabWindow.ContainsKey($t)) { $tabWindow[$t] } else { 0 }
  $i = $tabIndex[$t]
  $k = "$w|$i"
  if (-not $groups.ContainsKey($k) -or $groups[$k].Seq -lt $tabOrder.IndexOf($t)) {
    $groups[$k] = @{ Window = $w; Index = $i; Tab = $t; Seq = $tabOrder.IndexOf($t) }
  }
}

# 兜底：没有 id=2 的 tab 也放进来（按 tab id 顺序，index 顺延）
$orphans = @($tabOrder | Where-Object { -not $tabIndex.ContainsKey($_) })

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("FILE    : $Path")
[void]$sb.AppendLine("SIZE    : $len  VERSION: $version  COMMANDS: $cmdCount")
[void]$sb.AppendLine("TABS(tabId seen): $($tabOrder.Count)   WINDOWS(by id=0): $(($tabWindow.Values | Sort-Object -Unique).Count)")
[void]$sb.AppendLine("")

$windowIds = @($groups.Values | ForEach-Object { $_.Window } | Sort-Object -Unique)
if ($windowIds.Count -eq 0) { $windowIds = @(0) }

foreach ($w in $windowIds) {
  $selIdx = if ($winSelIndex.ContainsKey($w)) { $winSelIndex[$w] } else { -1 }
  $bx = if ($winBounds.ContainsKey($w)) { $winBounds[$w] } else { $null }
  [void]$sb.AppendLine("=== WINDOW id=$w selectedTabIndex=$selIdx bounds=$(if ($bx) { "x=$($bx[0]) y=$($bx[1]) w=$($bx[2]) h=$($bx[3]) state=$($bx[4])" } else { '?' }) ===")
  $tabs = @($groups.Values | Where-Object { $_.Window -eq $w } | Sort-Object Index)
  foreach ($g in $tabs) {
    $t = $g.Tab
    $selNav = if ($tabNavSel.ContainsKey($t)) { $tabNavSel[$t] } else { -1 }
    # 当前显示 URL：selected nav index 优先，否则取最大 index
    $key = "$t|$selNav"
    if (-not $nav.ContainsKey($key)) {
      $cands = @($nav.Keys | Where-Object { $_ -like "$t|*" } | ForEach-Object { [int]($_ -split '\|')[1] })
      if ($cands.Count -gt 0) { $selNav = ($cands | Sort-Object -Descending)[0]; $key = "$t|$selNav" }
    }
    $u = if ($nav.ContainsKey($key)) { $nav[$key].Url } else { "<none>" }
    $ti = if ($nav.ContainsKey($key)) { $nav[$key].Title } else { "" }
    $navCount = @($nav.Keys | Where-Object { $_ -like "$t|*" }).Count
    $mark = if ($g.Index -eq $selIdx) { "[*]" } else { "[ ]" }
    [void]$sb.AppendLine(("{0} idx={1,-2} tab={2,-10} nav={3,-4}/{4} selNav={5} url={6}" -f $mark, $g.Index, $t, $navCount, ($navCount - 1), $selNav, $u))
    [void]$sb.AppendLine(("        title={0}" -f $ti))
  }
  [void]$sb.AppendLine("")
}

if ($orphans.Count -gt 0) {
  [void]$sb.AppendLine("=== TABS WITHOUT INDEX (unassigned) : $($orphans.Count) ===")
  foreach ($t in $orphans) {
    $cands = @($nav.Keys | Where-Object { $_ -like "$t|*" } | ForEach-Object { [int]($_ -split '\|')[1] })
    if ($cands.Count -eq 0) { continue }
    $m = ($cands | Sort-Object -Descending)[0]
    [void]$sb.AppendLine(("  tab={0,-10} url={1}" -f $t, $nav["$t|$m"].Url))
  }
}

$text = $sb.ToString()
if ($OutFile) { $text | Set-Content -Path $OutFile -Encoding UTF8; Write-Host "written: $OutFile" }
else { Write-Host $text }
