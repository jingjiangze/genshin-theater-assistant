#requires -Version 5.1
<#
  识别精度基准（回答"精准度够不够"）
  ---------------------------------------------------------------
  做法：合成一张 1280x720 画面，在已知坐标植入 3 个不同纹理的标记（act/choice/result），
        裁成模板后，在 6 种扰动下测量「全局最优匹配是否落在真实坐标」以及匹配分。
        再测「标记不存在」时的最高分，据此给出阈值建议与误报余量。
  运行：powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\bench-accuracy.ps1
#>
param([int]$Step = 2)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$app  = Join-Path $root 'theater-overlay.ps1'
$work = Join-Path $root 'tests\_bench'
New-Item -ItemType Directory -Force -Path $work | Out-Null

# ---- 复用主程序里的识别实现，避免两套代码漂移 ----
$src = Get-Content $app -Raw -Encoding UTF8
$m = [regex]::Match($src, "(?s)\`$csharp\s*=\s*@'\r?\n(.*?)\r?\n'@")
if (-not $m.Success) { throw '无法从 theater-overlay.ps1 提取 C# 识别代码块' }
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition $m.Groups[1].Value -ReferencedAssemblies System.Windows.Forms, System.Drawing
Write-Host ("已加载识别实现: {0} 行 C#" -f ($m.Groups[1].Value -split "`n").Count)

function New-Frame([string]$path, [bool]$withMarkers = $true) {
  $w = 1280; $h = 720
  $bmp = New-Object System.Drawing.Bitmap -ArgumentList $w, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.Clear([System.Drawing.Color]::FromArgb(255, 28, 32, 44))
  $rand = New-Object System.Random 7112
  $brushes = @([System.Drawing.Brushes]::DimGray, [System.Drawing.Brushes]::SlateGray, [System.Drawing.Brushes]::Black, [System.Drawing.Brushes]::LightSlateGray)
  for ($i = 0; $i -lt 1500; $i++) {
    $g.FillRectangle($brushes[$rand.Next(0, 4)], $rand.Next(0, $w - 6), $rand.Next(0, $h - 6), $rand.Next(2, 12), $rand.Next(2, 12))
  }
  if ($withMarkers) {
    # 三个结构不同的标记（模拟 act 数字 / 三选一面板 / 结算条）
    $g.FillRectangle([System.Drawing.Brushes]::Gold,    420, 96, 44, 44)
    $g.FillRectangle([System.Drawing.Brushes]::Black,   430, 106, 24, 24)
    $g.FillRectangle([System.Drawing.Brushes]::Gold,    436, 112, 12, 12)

    $g.FillRectangle([System.Drawing.Brushes]::White,   300, 400, 120, 8)
    $g.FillRectangle([System.Drawing.Brushes]::Cyan,    300, 420, 90, 30)
    $g.FillRectangle([System.Drawing.Brushes]::White,   300, 460, 60, 6)

    $g.FillRectangle([System.Drawing.Brushes]::Orange,  900, 600, 200, 10)
    $g.FillRectangle([System.Drawing.Brushes]::Orange,  900, 620, 140, 10)
    $g.FillRectangle([System.Drawing.Brushes]::Orange,  900, 640, 80, 10)
  }
  $g.Dispose()
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
}

function Save-Jpeg([System.Drawing.Bitmap]$src, [string]$path, [int]$quality) {
  $codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
  $params = New-Object System.Drawing.Imaging.EncoderParameters 1
  $params.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality), ([int]$quality)
  $src.Save($path, $codec, $params)
}

function Adjust-Brightness([System.Drawing.Bitmap]$src, [double]$factor) {
  $out = New-Object System.Drawing.Bitmap -ArgumentList $src.Width, $src.Height
  $g = [System.Drawing.Graphics]::FromImage($out)
  $cm = New-Object System.Drawing.Imaging.ColorMatrix
  $cm.Matrix00 = [single]$factor; $cm.Matrix11 = [single]$factor; $cm.Matrix22 = [single]$factor; $cm.Matrix33 = 1; $cm.Matrix44 = 1
  $ia = New-Object System.Drawing.Imaging.ImageAttributes
  $ia.SetColorMatrix($cm)
  $rect = New-Object System.Drawing.Rectangle 0, 0, $src.Width, $src.Height
  $g.DrawImage($src, $rect, 0, 0, $src.Width, $src.Height, [System.Drawing.GraphicsUnit]::Pixel, $ia)
  $g.Dispose()
  return $out
}

function Add-Noise([System.Drawing.Bitmap]$src, [int]$sigma, [int]$seed = 99) {
  $out = New-Object System.Drawing.Bitmap -ArgumentList $src.Width, $src.Height
  $g = [System.Drawing.Graphics]::FromImage($out)
  $g.DrawImage($src, 0, 0, $src.Width, $src.Height)
  $rand = New-Object System.Random $seed
  # 稀疏噪点（近似压缩/抗锯齿噪声，避免逐像素 90 万次 Invoke）
  for ($i = 0; $i -lt ($src.Width * $src.Height / 40); $i++) {
    $v = $rand.Next(-$sigma * 4, $sigma * 4)
    $col = if ($v -ge 0) { [System.Drawing.Color]::FromArgb(40, 255, 255, 255) } else { [System.Drawing.Color]::FromArgb(40, 0, 0, 0) }
    $brush = New-Object System.Drawing.SolidBrush $col
    $g.FillRectangle($brush, $rand.Next(0, $src.Width), $rand.Next(0, $src.Height), 1, 1)
    $brush.Dispose()
  }
  $g.Dispose()
  return $out
}

# ---- 生成基础画面与模板 ----
$base = Join-Path $work 'base.png'
New-Frame $base $true
$baseBmp = [System.Drawing.Bitmap]::FromFile($base)

$specs = @(
  @{ Name = 'act3';    X = 420; Y = 96;  W = 44;  H = 44 },
  @{ Name = 'choice';  X = 300; Y = 400; W = 120; H = 66 },
  @{ Name = 'result';  X = 900; Y = 600; W = 200; H = 50 }
)
$templates = @{}
foreach ($s in $specs) {
  $rect = [System.Drawing.Rectangle]::new($s.X, $s.Y, $s.W, $s.H)
  $crop = $baseBmp.Clone($rect, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $p = Join-Path $work ($s.Name + '.png')
  $crop.Save($p, [System.Drawing.Imaging.ImageFormat]::Png)
  $crop.Dispose()
  $templates[$s.Name] = [System.Drawing.Bitmap]::FromFile($p)
}

# ---- 构造扰动用例 ----
$cases = [ordered]@{}
$cases['原图'] = { param($b) $b.Clone() }
$cases['位移(+1,+2)'] = {
  param($b)
  $o = New-Object System.Drawing.Bitmap -ArgumentList ($b.Width + 3), ($b.Height + 3)
  $g = [System.Drawing.Graphics]::FromImage($o); $g.Clear([System.Drawing.Color]::FromArgb(255, 28, 32, 44))
  $g.DrawImage($b, 1, 2); $g.Dispose(); $o
}
$cases['亮度×0.90'] = { param($b) Adjust-Brightness $b 0.90 }
$cases['亮度×1.08'] = { param($b) Adjust-Brightness $b 1.08 }
$cases['JPEG q=60'] = {
  param($b)
  $p = Join-Path $work 'tmp.jpg'; Save-Jpeg $b $p 60
  $img = [System.Drawing.Bitmap]::FromFile($p); $img
}
$cases['噪点 σ=6'] = { param($b) Add-Noise $b 6 }
$cases['缩放×1.10（预期失败）'] = {
  param($b)
  $o = New-Object System.Drawing.Bitmap -ArgumentList ([int]($b.Width * 1.10)), ([int]($b.Height * 1.10))
  $g = [System.Drawing.Graphics]::FromImage($o)
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.DrawImage($b, 0, 0, $o.Width, $o.Height); $g.Dispose(); $o
}

$rows = @()
foreach ($caseName in $cases.Keys) {
  $bmp = & $cases[$caseName] $baseBmp
  try {
    foreach ($s in $specs) {
      $tpl = $templates[$s.Name]
      $sw = [Diagnostics.Stopwatch]::StartNew()
      $r = [Matcher]::Find($bmp, $tpl, $Step)
      $sw.Stop()
      $tolerance = if ($caseName -like '位移*') { 3 } else { 2 }
      $correct = ([Math]::Abs($r.X - $s.X) -le $tolerance) -and ([Math]::Abs($r.Y - $s.Y) -le $tolerance)
      $k = if ($caseName -like '缩放*') { 1.10 } else { 1 }
      $rows += [pscustomobject]@{
        扰动 = $caseName; 模板 = $s.Name; 分数 = [Math]::Round($r.Score, 4)
        命中 = if ($correct) { 'Y' } else { 'N' }
        X = [int]($r.X / $k); Y = [int]($r.Y / $k); 真实X = $s.X; 真实Y = $s.Y
        耗时ms = $sw.ElapsedMilliseconds
      }
    }
  } finally { $bmp.Dispose() }
}

# ---- 缺失标记（误报测试）----
$blankBase = Join-Path $work 'blank.png'
New-Frame $blankBase $false
$blank = [System.Drawing.Bitmap]::FromFile($blankBase)
$falseRow = @()
foreach ($s in $specs) {
  $r = [Matcher]::Find($blank, $templates[$s.Name], $Step)
  $falseRow += [pscustomobject]@{ 模板 = $s.Name; 最高分 = [Math]::Round($r.Score, 4) }
}
$blank.Dispose()

# ---- 输出 ----
Write-Host "`n=== 命中率（每格 = 一个模板在一种扰动下的全局最优匹配）==="
$rows | Format-Table -AutoSize | Out-String -Width 200 | Write-Host

$total = $rows.Count
$hit = @($rows | Where-Object 命中 -eq 'Y').Count
Write-Host ("命中 {0}/{1} = {2:P1}" -f $hit, $total, ($hit / $total))

Write-Host "`n=== 标记不存在时的最高分（误报风险）==="
$falseRow | Format-Table -AutoSize | Out-String -Width 120 | Write-Host

$minTrue = ($rows | Where-Object 命中 -eq 'Y' | Measure-Object 分数 -Minimum).Minimum
$maxFalse = ($falseRow | Measure-Object 最高分 -Maximum).Maximum
Write-Host ("真实命中最低分 = {0}" -f $minTrue)
Write-Host ("缺失时最高分   = {0}" -f $maxFalse)
if ($maxFalse -lt $minTrue) {
  Write-Host ("建议阈值区间：({0}, {1}]，取中点 {2}" -f $maxFalse, $minTrue, [Math]::Round(($maxFalse + $minTrue) / 2, 3))
} else {
  Write-Host "警告：误报分不低于真实分，需缩小 ROI 或换更有区分度的锚点" -ForegroundColor Red
}

# 资源释放
$baseBmp.Dispose()
foreach ($t in $templates.Values) { $t.Dispose() }
