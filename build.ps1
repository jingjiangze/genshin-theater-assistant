#requires -Version 5.1
<#
  构建流水线（对齐 BetterGI 的「Build / Test / Publish」三段式，但补上测试门槛）
  ---------------------------------------------------------------------------
  步骤：
    1) 版本号（version.txt，缺省用日期）
    2) 语法检查：所有 .ps1 用 Parser 解析
    3) 测试：调用 tests\run-tests.ps1，失败即中止
    4) 打包：dist\theater-overlay-<version>.zip
    5) 产物清单：SHA256SUMS.txt
  用法：
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
    powershell.exe ... -SkipTests
#>
param(
  [switch]$SkipTests,
  [switch]$RunBench
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root
$sw = [Diagnostics.Stopwatch]::StartNew()

function Step([string]$t) { Write-Host "`n== $t ==" -ForegroundColor Cyan }
function Fail([string]$msg) { Write-Host "FAILED: $msg" -ForegroundColor Red; exit 1 }

# ---------------------------------------------------------------- 1. 版本
Step '版本'
$versionFile = Join-Path $root 'version.txt'
$version = if (Test-Path $versionFile) { (Get-Content $versionFile -Raw).Trim() } else { Get-Date -Format 'yyyy.MM.dd' }
Write-Host "version = $version"

# ---------------------------------------------------------------- 2. 语法
Step '语法检查'
$psFiles = Get-ChildItem $root -Recurse -Include *.ps1 -File | Where-Object { $_.FullName -notmatch '\\dist\\' }
$bad = 0
foreach ($f in $psFiles) {
  $tokens = $null; $errors = $null
  [void][System.Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$tokens, [ref]$errors)
  if ($errors -and $errors.Count -gt 0) {
    $bad++
    Write-Host ("  ERR {0}: {1}" -f $f.Name, ($errors[0].Message)) -ForegroundColor Red
  }
}
if ($bad -gt 0) { Fail "$bad 个脚本语法错误" }
Write-Host ("  {0} 个脚本通过" -f $psFiles.Count)

# ---------------------------------------------------------------- 3. 测试
if (-not $SkipTests) {
  Step '测试'
  & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tests\run-tests.ps1')
  if ($LASTEXITCODE -ne 0) { Fail '测试未通过' }
} else {
  Step '测试（已跳过）'
}

if ($RunBench) {
  Step '精度基准'
  & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tests\bench-accuracy.ps1')
  if ($LASTEXITCODE -ne 0) { Fail '精度基准执行失败' }
}

# ---------------------------------------------------------------- 4. 打包
Step '打包'
$dist = Join-Path $root 'dist'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force -Path $dist | Out-Null

$stage = Join-Path $env:TEMP ("theater-overlay-stage-" + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Force -Path $stage | Out-Null

$include = @(
  'theater-overlay.ps1', 'crop-template.ps1', 'build.ps1', 'version.txt',
  'README.md', 'AUDIT-2026-11-05.md',
  'data\season29.json', 'data\characters.json',
  'templates\templates.example.json',
  'tests\run-tests.ps1', 'tests\bench-accuracy.ps1',
  'samples\roster\small.json', 'samples\roster\big.json'
)
foreach ($rel in $include) {
  $srcPath = Join-Path $root $rel
  if (-not (Test-Path $srcPath)) { Write-Host ("  跳过（不存在）: {0}" -f $rel) -ForegroundColor Yellow; continue }
  $destPath = Join-Path $stage $rel
  $destDir = Split-Path -Parent $destPath
  if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Force -Path $destDir | Out-Null }
  Copy-Item $srcPath $destPath -Force
}

$zip = Join-Path $dist ("theater-overlay-{0}.zip" -f $version)
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
Remove-Item $stage -Recurse -Force
Write-Host ("  {0}  ({1:N0} KB)" -f (Split-Path -Leaf $zip), ((Get-Item $zip).Length / 1KB))

# ---------------------------------------------------------------- 5. 清单
Step '产物清单'
$sums = Get-ChildItem $dist -File | ForEach-Object {
  $h = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower()
  "{0}  {1}" -f $h, $_.Name
}
$sumFile = Join-Path $dist 'SHA256SUMS.txt'
$sums | Set-Content $sumFile -Encoding ascii
$sums | ForEach-Object { Write-Host ("  " + $_) }

$sw.Stop()
Write-Host ("`n构建完成，用时 {0:N1}s，版本 {1}" -f ($sw.Elapsed.TotalSeconds), $version) -ForegroundColor Green
