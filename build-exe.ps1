#requires -Version 5.1
<#
  构建单文件 exe（自包含 .NET 8 + RapidOcrNet + 内置数据/模型）
  ------------------------------------------------------------------
  前提：本机需 .NET SDK 8.0 或更高（当前机器只有运行时，没有 SDK）。
        安装（用户目录，不动系统，约 220MB）：
          Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile $env:TEMP\dotnet-install.ps1
          & $env:TEMP\dotnet-install.ps1 -Channel 8.0 -InstallDir "$env:LOCALAPPDATA\Microsoft\dotnet"
        安装后把 "$env:LOCALAPPDATA\Microsoft\dotnet" 加入 PATH，再运行本脚本。

  产物：dist\TheaterAssistant.exe（单文件；内含运行时、data、templates、models）
#>
param(
  [string]$Dotnet = 'dotnet',
  [string]$Configuration = 'Release',
  [switch]$Zip
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$app  = Join-Path $root 'app'

Write-Host "== 检查 SDK ==" -ForegroundColor Cyan
$sdks = & $Dotnet --list-sdks 2>$null
if (-not $sdks) {
  Write-Host "找不到 .NET SDK（本机只有运行时，装不了单文件 exe）。" -ForegroundColor Red
  Write-Host @'
请先安装 SDK（用户目录级，不需要管理员）：
  Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile "$env:TEMP\dotnet-install.ps1"
  & "$env:TEMP\dotnet-install.ps1" -Channel 8.0 -InstallDir "$env:LOCALAPPDATA\Microsoft\dotnet"
  $env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"
然后重跑：.\build-exe.ps1
'@ -ForegroundColor Yellow
  exit 2
}
Write-Host ($sdks -join "`n")

Write-Host "`n== 同步数据与模板 ==" -ForegroundColor Cyan
$copyPairs = @(
  @{ From = (Join-Path $root 'data');      To = (Join-Path $app 'data') },
  @{ From = (Join-Path $root 'templates'); To = (Join-Path $app 'templates') },
  @{ From = (Join-Path $root 'models');    To = (Join-Path $app 'models') }
)
foreach ($p in $copyPairs) {
  $dest = $p.To
  if (-not (Test-Path $dest)) { New-Item -ItemType Directory -Force -Path $dest | Out-Null }
  if (Test-Path $p.From) {
    Copy-Item (Join-Path $p.From '*') $dest -Recurse -Force
    Write-Host ("  {0} -> {1}" -f (Split-Path $p.From -Leaf), $dest)
  } else {
    Write-Host ("  跳过（源不存在）: {0}" -f $p.From) -ForegroundColor Yellow
  }
}

Write-Host "`n== 还原依赖 ==" -ForegroundColor Cyan
& $Dotnet restore (Join-Path $app 'TheaterAssistant.csproj')
if ($LASTEXITCODE -ne 0) { throw 'restore 失败' }

Write-Host "`n== 发布单文件 ==" -ForegroundColor Cyan
& $Dotnet publish (Join-Path $app 'TheaterAssistant.csproj') -c $Configuration `
  -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true `
  -o (Join-Path $root 'dist')
if ($LASTEXITCODE -ne 0) { throw 'publish 失败' }

$exe = Join-Path $root 'dist\TheaterAssistant.exe'
if (-not (Test-Path $exe)) { throw '没生成 exe' }
$sizeMb = (Get-Item $exe).Length / 1MB
Write-Host ("`n产物: {0}  ({1:N1} MB)" -f $exe, $sizeMb) -ForegroundColor Green

$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLower()
"$hash  TheaterAssistant.exe" | Set-Content (Join-Path $root 'dist\SHA256SUMS.txt') -Encoding ascii
Write-Host ("SHA256: {0}" -f $hash)

if ($Zip) {
  $zip = Join-Path $root ("dist\TheaterAssistant-{0}.zip" -f (Get-Date -Format 'yyyyMMdd'))
  Compress-Archive -Path $exe, (Join-Path $root 'dist\SHA256SUMS.txt') -DestinationPath $zip -Force
  Write-Host ("压缩包: {0}" -f $zip) -ForegroundColor Green
}
