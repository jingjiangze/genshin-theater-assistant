param(
  [Parameter(Mandatory=$true)][string]$Source,   # 一帧截图（samples\*.png）
  [Parameter(Mandatory=$true)][int]$X,
  [Parameter(Mandatory=$true)][int]$Y,
  [Parameter(Mandatory=$true)][int]$W,
  [Parameter(Mandatory=$true)][int]$H,
  [Parameter(Mandatory=$true)][string]$Out        # templates\xxx.png
)
# 从一帧截图里裁出一块作为模板（只用 System.Drawing，无外部依赖）
Add-Type -AssemblyName System.Drawing
$img = [System.Drawing.Bitmap]::FromFile((Resolve-Path $Source).Path)
try {
  $rect = [System.Drawing.Rectangle]::new($X, $Y, $W, $H)
  if ($rect.Right -gt $img.Width -or $rect.Bottom -gt $img.Height) { throw "裁剪区域超出图片 ($($img.Width)x$($img.Height))" }
  $crop = $img.Clone($rect, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $dir = Split-Path -Parent $Out
  if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
  $crop.Save((Join-Path (Resolve-Path (Split-Path -Parent $Out)).Path (Split-Path -Leaf $Out)), [System.Drawing.Imaging.ImageFormat]::Png)
  $crop.Dispose()
  Write-Output ("模板已保存: {0}  ({1}x{2} from {3} at {4},{5})" -f $Out, $W, $H, (Split-Path -Leaf $Source), $X, $Y)
} finally { $img.Dispose() }
