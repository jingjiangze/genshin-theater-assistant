#requires -Version 5.1
<#
  幻想真境剧诗悬浮窗 · 测试套件（参考 BetterGI 的 Test/ 项目分层：数据 / 纯逻辑 / 识别）
  运行：powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\run-tests.ps1
  退出码：0 = 全通过，1 = 有失败
#>
param([switch]$Verbose)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$script:Pass = 0
$script:Fail = 0
$script:Failures = @()

function Write-Result([bool]$ok, [string]$name, [string]$detail) {
  if ($ok) {
    $script:Pass++
    if ($Verbose) { Write-Host ("  PASS  {0}" -f $name) -ForegroundColor Green }
  } else {
    $script:Fail++
    $script:Failures += ("{0} :: {1}" -f $name, $detail)
    Write-Host ("  FAIL  {0}`n        {1}" -f $name, $detail) -ForegroundColor Red
  }
}

function Assert-True([bool]$cond, [string]$name, [string]$detail = '') { Write-Result $cond $name $detail }
function Assert-Equal($expected, $actual, [string]$name) {
  Write-Result ($expected -eq $actual) $name ("期望 '{0}' 实际 '{1}'" -f $expected, $actual)
}
function Assert-Match([string]$pattern, [string]$text, [string]$name) {
  Write-Result ($text -match $pattern) $name ("文本未匹配 /{0}/ : {1}" -f $pattern, ($text -replace '\s+', ' '))
}
function Assert-Range([double]$value, [double]$min, [double]$max, [string]$name) {
  Write-Result (($value -ge $min) -and ($value -le $max)) $name ("值 {0} 不在 [{1},{2}]" -f $value, $min, $max)
}

$ps = 'powershell.exe'
$app = Join-Path $root 'theater-overlay.ps1'
function Invoke-App { param([string[]]$Arguments) & $ps -NoProfile -ExecutionPolicy Bypass -File $app @Arguments 2>&1 }

# ---------------------------------------------------------------- 1. 语法
Write-Host "`n[1/5] 语法检查" -ForegroundColor Cyan
$scripts = Get-ChildItem $root -Filter *.ps1 -File
foreach ($s in $scripts) {
  $tokens = $null; $errors = $null
  [void][System.Management.Automation.Language.Parser]::ParseFile($s.FullName, [ref]$tokens, [ref]$errors)
  Assert-True (($null -eq $errors) -or ($errors.Count -eq 0)) ("语法: " + $s.Name) (($errors | Select-Object -First 1).Message)
}

# ---------------------------------------------------------------- 2. 数据
Write-Host "[2/5] 数据完整性" -ForegroundColor Cyan
$season = Get-Content (Join-Path $root 'data\season29.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$chars  = Get-Content (Join-Path $root 'data\characters.json') -Raw -Encoding UTF8 | ConvertFrom-Json

Assert-Equal 3 @($season.elements).Count '当期元素数量 = 3'
Assert-Equal 6 @($season.opening).Count '开幕角色 = 6'
Assert-Equal 4 @($season.guests).Count '特邀角色 = 4'
Assert-Equal 10 @($season.acts.Visionary.PSObject.Properties).Count '卓越 10 幕'
Assert-Equal 12 @($season.acts.Lunar.PSObject.Properties).Count '月谕 12 幕位'

$total = 0; $bossOk = $true; $missingReq = 0
foreach ($d in @('Visionary','Lunar')) {
  foreach ($p in $season.acts.$d.PSObject.Properties) {
    $act = $p.Value
    $total += @($act.encounters).Count
    if ([int]$p.Name -in @(3,6,8,10) -and @($act.encounters).Count -eq 0) { $bossOk = $false }
    foreach ($e in $act.encounters) { if (-not $e.req -and -not $e.mech) { $missingReq++ } }
  }
}
Assert-Equal 46 $total '卓越+月谕遭遇总数 = 46'
Assert-True $bossOk '首领幕(3/6/8/10)均有遭遇'
Assert-Equal 0 $missingReq '每条遭遇至少有要求或机制'

$allEnc = 0
foreach ($d in @('Easy','Normal','Hard','Visionary','Lunar')) {
  foreach ($p in $season.acts.$d.PSObject.Properties) { $allEnc += @($p.Value.encounters).Count }
}
Assert-Equal 85 $allEnc '五档难度遭遇总数 = 85'

Assert-Equal 129 @($chars.characters.PSObject.Properties).Count '角色元素表 = 129'
Assert-Equal 'Hydro' $chars.characters.Yelan 'Yelan = Hydro'
Assert-Equal 'Geo'   $chars.characters.Zhongli 'Zhongli = Geo'
$guestsOutside = $true
foreach ($g in $season.guests) {
  $el = $chars.characters.$g
  if (-not $el) { continue }
  if ($season.elements -contains $el) { $guestsOutside = $false }
}
Assert-True $guestsOutside '特邀角色均不在当期 3 元素内（交叉校验）'

# ---------------------------------------------------------------- 3. 资格判定
Write-Host "[3/5] 资格判定" -ForegroundColor Cyan
$small = (Invoke-App @('-Roster', (Join-Path $root 'samples\roster\small.json'), '-RosterReport')) -join "`n"
$big   = (Invoke-App @('-Roster', (Join-Path $root 'samples\roster\big.json'),   '-RosterReport')) -join "`n"
Assert-Match '池子 11 人' $small '小池 11 人'
Assert-Match '最高可进【轻简】' $small '小池最高 轻简'
Assert-Match '再补 1 人可进【普通】' $small '小池差 1 人进普通'
Assert-Match '元素9' $small '小池元素命中 9'
Assert-Match '特邀1' $small '小池特邀计入 1'
Assert-Match '池子 28 人' $big '大池 28 人'
Assert-Match '最高可进【月谕】' $big '大池最高 月谕'
Assert-Match '开幕试用1' $big '大池开幕试用计入 1'

# ---------------------------------------------------------------- 4. 状态机
Write-Host "[4/5] 状态机" -ForegroundColor Cyan
$cases = @(
  @{ Text='第 3 幕';        Kind='kind=act';    Act='act=3' },
  @{ Text='第十一幕';        Kind='kind=act';    Act='act=11' },
  @{ Text='伙伴·随机';       Kind='kind=choice'; Act='act=' },
  @{ Text='辉彩祝福';        Kind='kind=choice'; Act='act=' },
  @{ Text='巡演总结';        Kind='kind=result'; Act='act=' },
  @{ Text='幻想真境剧诗';    Kind='kind=theater'; Act='act=' }
)
foreach ($c in $cases) {
  $out = (Invoke-App @('-Classify', $c.Text)) -join "`n"
  Assert-Match $c.Kind $out ("状态机 kind: " + $c.Text)
  Assert-Match $c.Act  $out ("状态机 act: " + $c.Text)
}

# ---------------------------------------------------------------- 5. 识别（模板匹配）
Write-Host "[5/5] 模板匹配" -ForegroundColor Cyan
$benchDir = Join-Path $root 'tests\_tmp'
New-Item -ItemType Directory -Force -Path $benchDir | Out-Null
$synthPng = Join-Path $benchDir 'synth.png'
$synthTpl = Join-Path $benchDir 'synth-tpl.png'

$mk = @'
param([string]$Screen,[string]$Tpl)
Add-Type -AssemblyName System.Drawing
$w=800; $h=600
$bmp = New-Object System.Drawing.Bitmap -ArgumentList $w,$h
$g = [System.Drawing.Graphics]::FromImage($bmp); $g.Clear([System.Drawing.Color]::FromArgb(255,240,240,240))
$rand = New-Object System.Random 20261105
$brushes = @([System.Drawing.Brushes]::Black,[System.Drawing.Brushes]::DarkSlateGray,[System.Drawing.Brushes]::Gray,[System.Drawing.Brushes]::LightGray)
for($i=0;$i -lt 1200;$i++){ $g.FillRectangle($brushes[$rand.Next(0,4)], $rand.Next(0,$w-4), $rand.Next(0,$h-4), $rand.Next(2,10), $rand.Next(2,10)) }
$mx=123; $my=77
$g.FillRectangle([System.Drawing.Brushes]::Red, $mx, $my, 30, 8)
$g.FillRectangle([System.Drawing.Brushes]::Red, $mx, $my, 8, 30)
$g.FillRectangle([System.Drawing.Brushes]::Blue, $mx+14, $my+14, 8, 8)
$g.Dispose(); $bmp.Save($Screen,[System.Drawing.Imaging.ImageFormat]::Png)
$rect=[System.Drawing.Rectangle]::new($mx,$my,30,30)
$crop=$bmp.Clone($rect,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$crop.Save($Tpl,[System.Drawing.Imaging.ImageFormat]::Png); $crop.Dispose(); $bmp.Dispose()
'@
$mkPath = Join-Path $benchDir 'mk.ps1'
Set-Content -Path $mkPath -Value $mk -Encoding utf8
& $ps -NoProfile -ExecutionPolicy Bypass -File $mkPath -Screen $synthPng -Tpl $synthTpl | Out-Null

$learn = (Invoke-App @('-Learn', $synthPng, '-LearnTemplate', $synthTpl)) -join "`n"
Assert-Match 'score=1\.0000' $learn '合成图满分命中'
Assert-Match 'at \(123,77\)' $learn '命中坐标精确 (123,77)'
if ($learn -match '用时 (\d+) ms') {
  Assert-Range ([double]$Matches[1]) 1 3000 '单次匹配耗时 < 3s'
}

# ---------------------------------------------------------------- 汇总
Write-Host ("`n通过 {0} / 失败 {1}" -f $script:Pass, $script:Fail) -ForegroundColor $(if ($script:Fail -eq 0) { 'Green' } else { 'Red' })
if ($script:Fail -gt 0) {
  Write-Host "`n失败明细：" -ForegroundColor Red
  $script:Failures | ForEach-Object { Write-Host ("  - " + $_) -ForegroundColor Red }
  exit 1
}
exit 0
