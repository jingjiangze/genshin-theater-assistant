#requires -Version 5.1
<#
  从一帧游戏截图批量裁模板，并立即用识别引擎自检匹配分。
  ---------------------------------------------------------------
  用途：等 samples/auto/ 里有帧之后，一条命令产出 templates/<宽>x<高>/ 下的模板。

  用法：
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\make-templates.ps1 `
      -Frame .\samples\auto\act3-1920x1080.png `
      -Specs .\tools\specs.example.json

  Specs 格式（坐标为帧内像素）：
    [ { "name": "choice", "x": 300, "y": 400, "w": 120, "h": 66 } ]
#>
param(
  [Parameter(Mandatory=$true)][string]$Frame,
  [Parameter(Mandatory=$true)][string]$Specs,
  [double]$MinScore = 0.95
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

Add-Type -AssemblyName System.Drawing

# 复用主程序的匹配器实现，避免两套代码漂移
$app = Join-Path $root 'theater-overlay.ps1'
if (-not (Test-Path $app)) { throw "找不到 $app" }
$src = Get-Content $app -Raw -Encoding UTF8
$m = [regex]::Match($src, "(?s)\`$csharp\s*=\s*@'\r?\n(.*?)\r?\n'@")
if (-not $m.Success) { throw '无法从 theater-overlay.ps1 提取识别代码' }
Add-Type -TypeDefinition $m.Groups[1].Value -ReferencedAssemblies System.Windows.Forms, System.Drawing

$srcPath = (Resolve-Path $Frame).Path
$img = [System.Drawing.Bitmap]::FromFile($srcPath)
$bucket = "{0}x{1}" -f $img.Width, $img.Height
$outDir = Join-Path $root ("templates\" + $bucket)
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$list = Get-Content (Resolve-Path $Specs).Path -Raw -Encoding UTF8 | ConvertFrom-Json
$rows = @()

foreach ($s in $list) {
  $rect = [System.Drawing.Rectangle]::new([int]$s.x, [int]$s.y, [int]$s.w, [int]$s.h)
  if ($rect.Right -gt $img.Width -or $rect.Bottom -gt $img.Height) {
    Write-Warning ("跳过 {0}：裁剪区域越界" -f $s.name)
    continue
  }

  $crop = $img.Clone($rect, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $out = Join-Path $outDir ($s.name + '.png')
  try {
    $crop.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $r = [Matcher]::Find($img, $crop, 2)
    $rows += [pscustomobject]@{
      Name = $s.name
      Size = ("{0}x{1}" -f $s.w, $s.h)
      Score = [Math]::Round($r.Score, 4)
      HitX = $r.X
      HitY = $r.Y
      Expect = ("{0},{1}" -f $s.x, $s.y)
      Pass = ($r.Score -ge $MinScore -and [Math]::Abs($r.X - $s.x) -le 2 -and [Math]::Abs($r.Y - $s.y) -le 2)
      File = $out
    }
  } finally {
    $crop.Dispose()
  }
}

$img.Dispose()

$rows | Format-Table -AutoSize | Out-String -Width 160 | Write-Host

$bad = @($rows | Where-Object { -not $_.Pass }).Count
Write-Host ("模板目录: {0}" -f $outDir)
Write-Host ("通过 {0} / 失败 {1}（阈值 {2}）" -f ($rows.Count - $bad), $bad, $MinScore)
if ($bad -gt 0) { exit 1 }
