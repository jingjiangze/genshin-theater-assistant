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
    # 字典：上游 default_models.yaml 没有公布 SHA256；这里的值来自**本地实测**（74,947 B），
    # 只用于检测本地文件损坏/半截，不宣称是上游权威值。上游若更新字典需同步改这里。
    @{ Name = 'ppocrv6_small_dict.txt';  Url = 'https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/paddle/PP-OCRv6/rec/PP-OCRv6_rec_small/ppocrv6_dict.txt'; Sha = 'b5f2bfe2bdd9448429e3e82b51c789775d9b42f2403d082b00662eb77e401c5d' }
  )
} else {
  $files = @(
    @{ Name = 'ch_PP-OCRv5_mobile_det.onnx'; Url = "$base/PP-OCRv5/det/ch_PP-OCRv5_det_mobile.onnx"; Sha = '4d97c44a20d30a81aad087d6a396b08f786c4635742afc391f6621f5c6ae78ae' },
    @{ Name = 'ch_PP-OCRv5_rec_mobile_infer.onnx'; Url = "$base/PP-OCRv5/rec/ch_PP-OCRv5_rec_mobile.onnx"; Sha = '5825fc7ebf84ae7a412be049820b4d86d77620f204a041697b0494669b1742c5' },
    @{ Name = 'ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx'; Url = "$base/PP-OCRv5/cls/ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"; Sha = '54379ae5174d026780215fc748a7f31910dee36818e63d49e17dc598ecc82df7' },
    # 字典：上游无公布值，本地也无独立权威记录 → 保持空（脚本会显式告警「未校验」，不假装校验过）。
    @{ Name = 'ppocrv5_dict.txt'; Url = 'https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/paddle/PP-OCRv5/rec/ch_PP-OCRv5_rec_mobile/ppocrv5_dict.txt'; Sha = '' }
  )
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$ok = 0; $fail = 0

foreach ($f in $files) {
  $dest = Join-Path $OutDir $f.Name

  if ((Test-Path $dest) -and -not $Force) {
    if ($f.Sha) {
      # 已存在也要校验：旧版直接 $ok++ continue，半截文件/被替换的文件会被当成功（完整性可绕过）
      $existing = (Get-FileHash $dest -Algorithm SHA256).Hash.ToLower()
      if ($existing -eq $f.Sha) {
        Write-Host ("已存在且 SHA256 通过: {0}" -f $f.Name) -ForegroundColor Green
        $ok++
        continue
      }
      Write-Host ("已存在但 SHA256 不匹配（期望 {0} 实际 {1}），删除重下: {2}" -f $f.Sha, $existing, $f.Name) -ForegroundColor Yellow
      Remove-Item $dest -Force -ErrorAction SilentlyContinue
    } else {
      # 没有校验值的文件（如 v5 字典）无法判断完整性：仍跳过，但显式告警，不冒充「已校验」
      Write-Host ("已存在（无校验和，未校验）: {0}" -f $f.Name) -ForegroundColor Yellow
      $ok++
      continue
    }
  }

  Write-Host ("下载 {0} …" -f $f.Name)
  try {
    Invoke-WebRequest -Uri $f.Url -OutFile $dest -UseBasicParsing -TimeoutSec 120
  } catch {
    Write-Host ("  失败: {0}" -f $_.Exception.Message) -ForegroundColor Red
    # 中断会留下半截文件；不删掉的话下次会被当成「已存在」（有 SHA 时会被校验拦下，无 SHA 时静默通过）
    if (Test-Path $dest) { Remove-Item $dest -Force -ErrorAction SilentlyContinue }
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
# 成功也显式置 0：否则 $LASTEXITCODE 会保留上一条命令的值，调用方的 if ($LASTEXITCODE -ne 0) 会误判
exit 0
