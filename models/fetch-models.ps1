#requires -Version 5.1
<#
  下载并校验中文 OCR 模型（RapidOcrNet 只内置 latin，中文必须自备）
  ------------------------------------------------------------------
  来源与 SHA256 均取自 RapidOCR 官方 default_models.yaml（v3.10.0 固定版本）：
    https://github.com/RapidAI/RapidOCR/blob/main/python/rapidocr/default_models.yaml

  用法：
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\models\fetch-models.ps1
    ... -V6         # 改用 PP-OCRv6 small（多语种，约 31MB）
#>
param(
  [switch]$V6,
  [switch]$Force,
  [string]$OutDir = (Join-Path $PSScriptRoot 'v5')
)

$ErrorActionPreference = 'Stop'
$base = 'https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/onnx'

if ($V6) {
  $OutDir = Join-Path $PSScriptRoot 'v6'
  $files = @(
    @{ Name = 'PP-OCRv6_det_small.onnx'; Url = "$base/PP-OCRv6/det/PP-OCRv6_det_small.onnx"; Sha = '090f04abcd9d9a7498bc4ebf677e4cb9bdce1fe4197ddb7e529f1ef44e1ff94f' },
    @{ Name = 'PP-OCRv6_rec_small.onnx'; Url = "$base/PP-OCRv6/rec/PP-OCRv6_rec_small.onnx"; Sha = '6f327246b50388f3c176ae304bd95767ea6dc0c9ae92153ef8cbe210b3c14884' },
    @{ Name = 'ppocrv6_small_dict.txt';  Url = 'https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/paddle/PP-OCRv6/rec/PP-OCRv6_rec_small/ppocrv6_dict.txt'; Sha = '' }
  )
} else {
  $files = @(
    @{ Name = 'ch_PP-OCRv5_mobile_det.onnx'; Url = "$base/PP-OCRv5/det/ch_PP-OCRv5_det_mobile.onnx"; Sha = '4d97c44a20d30a81aad087d6a396b08f786c4635742afc391f6621f5c6ae78ae' },
    @{ Name = 'ch_PP-OCRv5_rec_mobile_infer.onnx'; Url = "$base/PP-OCRv5/rec/ch_PP-OCRv5_rec_mobile.onnx"; Sha = '5825fc7ebf84ae7a412be049820b4d86d77620f204a041697b0494669b1742c5' },
    @{ Name = 'ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx'; Url = "$base/PP-OCRv5/cls/ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"; Sha = '54379ae5174d026780215fc748a7f31910dee36818e63d49e17dc598ecc82df7' },
    @{ Name = 'ppocrv5_dict.txt'; Url = 'https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/paddle/PP-OCRv5/rec/ch_PP-OCRv5_rec_mobile/ppocrv5_dict.txt'; Sha = '' }
  )
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$ok = 0; $fail = 0

foreach ($f in $files) {
  $dest = Join-Path $OutDir $f.Name

  if ((Test-Path $dest) -and -not $Force) {
    Write-Host ("已存在，跳过: {0}" -f $f.Name)
    $ok++
    continue
  }

  Write-Host ("下载 {0} …" -f $f.Name)
  try {
    Invoke-WebRequest -Uri $f.Url -OutFile $dest -UseBasicParsing -TimeoutSec 120
  } catch {
    Write-Host ("  失败: {0}" -f $_.Exception.Message) -ForegroundColor Red
    $fail++
    continue
  }

  $sizeMb = (Get-Item $dest).Length / 1MB
  if ($f.Sha) {
    $actual = (Get-FileHash $dest -Algorithm SHA256).Hash.ToLower()
    if ($actual -ne $f.Sha) {
      Write-Host ("  SHA256 不匹配！期望 {0} 实际 {1}" -f $f.Sha, $actual) -ForegroundColor Red
      Remove-Item $dest -Force
      $fail++
      continue
    }
    Write-Host ("  OK {0:N1} MB, SHA256 校验通过" -f $sizeMb) -ForegroundColor Green
  } else {
    Write-Host ("  OK {0:N1} MB（无校验和，按官方 URL 取）" -f $sizeMb) -ForegroundColor Yellow
  }
  $ok++
}

Write-Host ("`n完成：成功 {0} / 失败 {1} → {2}" -f $ok, $fail, $OutDir)
if ($fail -gt 0) { exit 1 }
