#requires -Version 5.1
<#
  幻想真境剧诗 · 状态识别悬浮窗 v0.2（BetterGI 分层参照版）
  ==============================================================================
  分层（对齐 BetterGI 的 Capture / Recognize / Decide / Render）：
    [Capture]   只截取「原神」窗口像素：GDI CopyFromScreen（BitBlt 等价）
                —— 对应 BetterGI 的 Fischless.GameCapture/BitBlt
    [Recognize] ① 模板匹配（纯托管 C#，灰度 SSD，无原生依赖）
                ② Windows.Media.Ocr 文本兜底
                —— 对应 BetterGI 的 BgiVision(BvLocator) + ONNX/PaddleOCR 的最小替代
    [Decide]    模板命中 → 幕次/界面类型；否则 OCR 关键词兜底
    [Render]    置顶、不抢焦点、点击穿透；自身 WDA_EXCLUDEFROMCAPTURE

  明确不引入（BetterGI 里对应 Core/Input/** 整层）：
    SendInput / PostMessage / keybd_event / mouse_event / 手柄模拟
  也绝不引入：OpenProcess / ReadProcessMemory / WriteProcessMemory / CreateRemoteThread / 注入 / 网络

  BetterGI 为 GPL-3.0：本项目仅参考分层与选型，不复制其代码。
  ==============================================================================
  用法：
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\theater-overlay.ps1
    ... -ListWindows                 列出可见窗口
    ... -SelfTest                    测「截屏 + OCR + 模板引擎」链路与耗时
    ... -Collect -CollectSeconds 90  连续采样到 samples\（用来做模板）
    ... -Learn samples\frame-x.png templates\act3.png    测某模板在某帧的匹配分
    ... -Templates templates\templates.json              启用模板识别
#>
param(
  [string]$WindowTitle = '原神',
  [string]$Language    = 'zh-Hans',
  [string]$Roi         = '0,0,100,100',
  [int]$IntervalMs     = 1000,
  [string]$Templates   = '',
  [double]$TemplateThreshold = 0.86,
  [int]$MatchStep      = 2,
  [switch]$ListWindows,
  [switch]$SelfTest,
  [switch]$Collect,
  [int]$CollectSeconds = 60,
  [int]$CollectEveryMs = 2500,
  [string]$Roster = '',
  [switch]$RosterReport,
  [string]$Classify = '',
  [string]$Learn,
  [string]$LearnTemplate,
  [switch]$Verbose
)

$ErrorActionPreference = 'Stop'
$root   = Split-Path -Parent $MyInvocation.MyCommand.Path
$logDir = Join-Path $root 'logs'
$samDir = Join-Path $root 'samples'
$framePng = Join-Path $env:TEMP 'theater-overlay-frame.png'
New-Item -ItemType Directory -Force -Path $logDir, $samDir | Out-Null
$script:LogFile = Join-Path $logDir ("ocr-{0}.log" -f (Get-Date -Format 'yyyyMMdd'))

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Runtime.WindowsRuntime

$csharp = @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

public static class TW32 {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x20;
    public const int WS_EX_NOACTIVATE  = 0x08000000;

    public static void SetExcludedFromCapture(IntPtr hWnd) { try { SetWindowDisplayAffinity(hWnd, 0x11); } catch { } }
    public static void SetClickThrough(IntPtr hWnd, bool on) {
        int ex = GetWindowLong(hWnd, GWL_EXSTYLE);
        ex = on ? (ex | WS_EX_TRANSPARENT) : (ex & ~WS_EX_TRANSPARENT);
        SetWindowLong(hWnd, GWL_EXSTYLE, ex);
    }
}

public static class Matcher {
    static byte[] Gray(Bitmap src) {
        int w = src.Width, h = src.Height;
        var bytes = new byte[w * h];
        using (var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb))
        using (var g = Graphics.FromImage(bmp)) {
            g.DrawImage(src, 0, 0, w, h);
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try {
                int stride = data.Stride;
                var buf = new byte[stride * h];
                Marshal.Copy(data.Scan0, buf, 0, buf.Length);
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++) {
                        int i = y * stride + x * 3;
                        bytes[y * w + x] = (byte)((buf[i] * 29 + buf[i + 1] * 150 + buf[i + 2] * 77) >> 8);
                    }
            } finally { bmp.UnlockBits(data); }
        }
        return bytes;
    }

    public sealed class MatchResult { public double Score; public int X, Y; }

    public static MatchResult Find(Bitmap frame, Bitmap tpl, int step) {
        if (frame == null || tpl == null) throw new ArgumentNullException();
        if (tpl.Width > frame.Width || tpl.Height > frame.Height) throw new ArgumentException("模板比画面大");
        var f = Gray(frame); var t = Gray(tpl);
        int fw = frame.Width, fh = frame.Height, tw = tpl.Width, th = tpl.Height;
        double best = double.MaxValue; int bx = 0, by = 0;

        for (int y = 0; y + th <= fh; y += step)
            for (int x = 0; x + tw <= fw; x += step) {
                double sum = 0;
                for (int j = 0; j < th; j += 2) {
                    int fo = (y + j) * fw + x, to = j * tw;
                    for (int i = 0; i < tw; i += 2) {
                        double d = f[fo + i] - t[to + i];
                        sum += d * d;
                    }
                }
                if (sum < best) { best = sum; bx = x; by = y; }
            }

        // 细化：在粗扫最优点周围用「全分辨率」重算，避免与粗扫抽样口径混比
        int rx = bx, ry = by;
        double bestFine = double.MaxValue;
        for (int y = Math.Max(0, by - step); y <= Math.Min(fh - th, by + step); y++)
            for (int x = Math.Max(0, bx - step); x <= Math.Min(fw - tw, bx + step); x++) {
                double sum = 0;
                for (int j = 0; j < th; j++) {
                    int fo = (y + j) * fw + x, to = j * tw;
                    for (int i = 0; i < tw; i++) {
                        double d = f[fo + i] - t[to + i];
                        sum += d * d;
                    }
                }
                if (sum < bestFine) { bestFine = sum; rx = x; ry = y; }
            }
        double rmse = Math.Sqrt(bestFine / Math.Max(1.0, tw * th));
        return new MatchResult { Score = 1.0 - rmse / 255.0, X = rx, Y = ry };
    }
}

public class TheaterOverlayForm : Form {
    public Label Lbl;
    public TheaterOverlayForm() {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(16, 18, 26);
        Opacity = 0.94;
        Size = new Size(580, 220);
        Lbl = new Label();
        Lbl.Dock = DockStyle.Fill;
        Lbl.AutoSize = false;
        Lbl.ForeColor = Color.FromArgb(236, 240, 252);
        Lbl.BackColor = Color.Transparent;
        Lbl.Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Regular);
        Lbl.Padding = new Padding(12);
        Controls.Add(Lbl);
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams {
        get {
            var cp = base.CreateParams;
            cp.ExStyle |= TW32.WS_EX_NOACTIVATE | TW32.WS_EX_TRANSPARENT;
            return cp;
        }
    }
}
'@
Add-Type -TypeDefinition $csharp -ReferencedAssemblies System.Windows.Forms, System.Drawing

function Get-VisibleWindows {
  $result = New-Object System.Collections.ArrayList
  $cb = [TW32+EnumProc]{
    param($h, $l)
    if ([TW32]::IsWindowVisible($h)) {
      $sb = New-Object System.Text.StringBuilder 512
      [void][TW32]::GetWindowText($h, $sb, 512)
      if ($sb.ToString().Trim().Length -gt 0) {
        $cb2 = New-Object System.Text.StringBuilder 256
        [void][TW32]::GetClassName($h, $cb2, 256)
        [void]$result.Add([pscustomobject]@{ Handle = $h; Title = $sb.ToString(); Class = $cb2.ToString() })
      }
    }
    return $true
  }
  [void][TW32]::EnumWindows($cb, [IntPtr]::Zero)
  return $result
}

function Find-GameWindow([string]$needle) {
  Get-VisibleWindows |
    Where-Object { $_.Title -like "*$needle*" -or $_.Class -eq 'UnityWndClass' } |
    Sort-Object { if ($_.Class -eq 'UnityWndClass') { 0 } else { 1 } } |
    Select-Object -First 1
}

function Capture-Window([IntPtr]$hWnd, [int[]]$roi, [string]$path) {
  $r = New-Object TW32+RECT
  if (-not [TW32]::GetClientRect($hWnd, [ref]$r)) { throw 'GetClientRect 失败' }
  $o = New-Object TW32+POINT; $o.X = 0; $o.Y = 0
  if (-not [TW32]::ClientToScreen($hWnd, [ref]$o)) { throw 'ClientToScreen 失败' }
  $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
  if ($w -lt 40 -or $h -lt 40) { throw "窗口太小或已最小化 ($w x $h)" }
  $x  = $o.X + [int]($w * $roi[0] / 100)
  $y  = $o.Y + [int]($h * $roi[1] / 100)
  $cw = [Math]::Max(40, [int]($w * [Math]::Min($roi[2], 100 - $roi[0]) / 100))
  $ch = [Math]::Max(40, [int]($h * [Math]::Min($roi[3], 100 - $roi[1]) / 100))
  $bmp = New-Object System.Drawing.Bitmap($cw, $ch, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  try {
    $g.CopyFromScreen($x, $y, 0, 0, $bmp.Size)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  } finally { $g.Dispose(); $bmp.Dispose() }
  return $path
}

function Await-WinRT([object]$op, [type]$resultType) {
  $m = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.IsGenericMethodDefinition -and $_.GetGenericArguments().Count -eq 1 -and $_.GetParameters().Count -eq 1
  } | Select-Object -First 1
  return $m.MakeGenericMethod($resultType).Invoke($null, @($op)).GetAwaiter().GetResult()
}

function New-OcrEngine([string]$lang) {
  $engineType = [Windows.Media.Ocr.OcrEngine, Windows.Media, ContentType = WindowsRuntime]
  try {
    $languageType = [Windows.Globalization.Language, Windows.Globalization, ContentType = WindowsRuntime]
    $langObj = [Activator]::CreateInstance($languageType, $lang)
    $e = $engineType::TryCreateFromLanguage($langObj)
    if ($e) { return $e }
  } catch { }
  return $engineType::TryCreateFromUserProfileLanguages()
}

function Read-OcrText([string]$path, [object]$engine) {
  $fileType    = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
  $streamType  = [Windows.Storage.Streams.IRandomAccessStreamWithContentType, Windows.Storage, ContentType = WindowsRuntime]
  $decoderType = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics, ContentType = WindowsRuntime]
  $bitmapType  = [Windows.Graphics.Imaging.SoftwareBitmap, Windows.Graphics, ContentType = WindowsRuntime]
  $resultType  = [Windows.Media.Ocr.OcrResult, Windows.Media, ContentType = WindowsRuntime]
  $file = Await-WinRT ($fileType::GetFileFromPathAsync($path)) $fileType
  $stream = Await-WinRT ($file.OpenReadAsync()) $streamType
  try {
    $decoder = Await-WinRT ($decoderType::CreateAsync($stream)) $decoderType
    $bitmap  = Await-WinRT ($decoder.GetSoftwareBitmapAsync()) $bitmapType
    $res     = Await-WinRT ($engine.RecognizeAsync($bitmap)) $resultType
    return [string]$res.Text
  } finally {
    if ($bitmap) { try { $bitmap.Dispose() } catch { } }
    if ($stream) { try { $stream.Dispose() } catch { } }
  }
}

$script:TemplateList = @()
if ($Templates -and (Test-Path $Templates)) {
  $manifestPath = (Resolve-Path $Templates).Path
  $manifestDir  = Split-Path -Parent $manifestPath
  $json = Get-Content $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
  foreach ($t in $json.templates) {
    $p = if ([IO.Path]::IsPathRooted($t.file)) { $t.file } else { Join-Path $manifestDir $t.file }
    if (-not (Test-Path $p)) { Write-Warning "模板缺失: $p"; continue }
    $script:TemplateList += [pscustomobject]@{
      Name      = $t.name
      Path      = $p
      Bitmap    = [System.Drawing.Bitmap]::FromFile((Resolve-Path $p).Path)
      Threshold = if ($t.threshold) { [double]$t.threshold } else { $TemplateThreshold }
    }
  }
}

function Find-Templates([string]$framePath) {
  if ($script:TemplateList.Count -eq 0) { return @() }
  $frame = [System.Drawing.Bitmap]::FromFile($framePath)
  try {
    $hits = @()
    foreach ($t in $script:TemplateList) {
      $r = [Matcher]::Find($frame, $t.Bitmap, $MatchStep)
      if ($r.Score -ge $t.Threshold) {
        $hits += [pscustomobject]@{ Name = $t.Name; Score = [Math]::Round($r.Score, 3); X = $r.X; Y = $r.Y }
      }
    }
    return $hits
  } finally { $frame.Dispose() }
}

# ============================ [Decide] 角色池与资格 ============================
$script:SeasonData    = $null
$script:CharElements  = $null
$script:RosterSet     = $null
$script:EligLine      = ''

# GOOD 导出与本地表的名字差异（GOOD 用官方英文 key，本表用常用短名）
$script:GoodAlias = @{
  'kamisatoayaka'   = 'ayaka'
  'aratakiitto'     = 'itto'
  'sangonomiyakokomi' = 'kokomi'
  'kujousara'       = 'sara'
  'shikanoinheizou' = 'heizou'
  'kuki_shinobu'    = 'kuki shinobu'
  'kaedeharakazuha' = 'kazuha'
  'raiden_shogun'   = 'raiden shogun'
  'traveler'        = 'traveler'
}

function Normalize-Name([string]$n) {
  if (-not $n) { return '' }
  $k = ($n -replace '[^A-Za-z0-9]', '').ToLower()
  if ($script:GoodAlias.ContainsKey($k)) { return $script:GoodAlias[$k] }
  return $k
}

function Initialize-RosterData {
  $dataDir = Join-Path $root 'data'
  $seasonPath = Join-Path $dataDir 'season29.json'
  $charPath   = Join-Path $dataDir 'characters.json'
  if (-not (Test-Path $seasonPath)) { throw "缺数据文件: $seasonPath" }
  if (-not (Test-Path $charPath))   { throw "缺数据文件: $charPath" }
  $script:SeasonData = Get-Content $seasonPath -Raw -Encoding UTF8 | ConvertFrom-Json
  $charJson = Get-Content $charPath -Raw -Encoding UTF8 | ConvertFrom-Json
  $script:CharElements = @{}
  foreach ($p in $charJson.characters.PSObject.Properties) {
    $script:CharElements[(Normalize-Name $p.Name)] = $p.Value
  }
}

# 读 GOOD 导出（Inventory_Kamera / Irminsul / 米游社等导出格式）
function Import-GoodRoster([string]$path) {
  if (-not (Test-Path $path)) { throw "找不到角色池文件: $path" }
  $json = Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json
  if (-not $json.characters) { throw "不是 GOOD 格式（缺 characters 数组）: $path" }
  $set = @{}
  foreach ($ch in $json.characters) {
    $key = Normalize-Name $ch.key
    $lvl = if ($ch.level) { [int]$ch.level } else { 0 }
    if ($key) { $set[$key] = $lvl }
  }
  return $set
}

$DIFF_TABLE = @(
  @{ Name='轻简'; Key='Easy';      Min=8;  Cap=10; Level=60 },
  @{ Name='普通'; Key='Normal';    Min=12; Cap=14; Level=60 },
  @{ Name='困难'; Key='Hard';      Min=16; Cap=20; Level=70 },
  @{ Name='卓越'; Key='Visionary'; Min=22; Cap=26; Level=70 },
  @{ Name='月谕'; Key='Lunar';     Min=28; Cap=32; Level=70 }
)

function Get-Eligibility([hashtable]$owned) {
  $season   = @($script:SeasonData.elements)          # 例：Hydro/Electro/Geo
  $opening  = @($script:SeasonData.opening)           # 6 名开幕
  $guests   = @($script:SeasonData.guests)            # 4 名特邀
  $normOpen = $opening  | ForEach-Object { Normalize-Name $_ }
  $normGuest= $guests   | ForEach-Object { Normalize-Name $_ }

  $rows = @()
  foreach ($d in $DIFF_TABLE) {
    $count = 0
    $detail = @{ Element=0; OpeningTrial=0; Guest=0; Traveler=0 }
    foreach ($k in $owned.Keys) {
      if ($k -eq 'traveler') { continue }
      if ($owned[$k] -lt $d.Level) { continue }        # 等级不够一律不算
      if ($normGuest -contains $k) {                   # 特邀角色不受元素限制，先判
        $detail.Guest++; $count++; continue
      }
      $el = $script:CharElements[$k]
      if (-not $el) { continue }                       # 表里没有的新角色先不计，避免高估
      if ($season -notcontains $el) { continue }
      $detail.Element++; $count++
    }
    foreach ($ok in $normOpen) {
      if (-not $owned.ContainsKey($ok)) { $detail.OpeningTrial++; $count++ }   # 开幕没抽到 → 试用白送名额
    }
    $detail.Traveler = 1; $count += 1                  # 旅行者固定 +1
    $rows += [pscustomobject]@{
      Name=$d.Name; Key=$d.Key; Min=$d.Min; Cap=$d.Cap; Level=$d.Level; Count=$count
      Ok = ($count -ge $d.Min); Detail = ("元素{0}+开幕试用{1}+特邀{2}+旅行者{3}" -f $detail.Element,$detail.OpeningTrial,$detail.Guest,$detail.Traveler)
    }
  }
  return $rows
}

function Set-Eligibility([string]$rosterPath) {
  $owned = Import-GoodRoster $rosterPath
  $script:RosterSet = $owned
  $rows = Get-Eligibility $owned
  $best = ($rows | Where-Object Ok | Sort-Object Min -Descending | Select-Object -First 1)
  $fail = ($rows | Where-Object { -not $_.Ok } | Sort-Object Min | Select-Object -First 1)
  if ($best) {
    $script:EligLine = "池子 {0} 人：最高可进【{1}】（需要 {2} 人）" -f $best.Count, $best.Name, $best.Min
  } else {
    $script:EligLine = "池子 {0} 人：连轻简都进不去（要 8 人）" -f ($rows[0].Count)
  }
  $have = if ($best) { $best.Count } else { $rows[0].Count }
  if ($fail) { $script:EligLine += ("；再补 {0} 人可进【{1}】" -f [Math]::Max(0, $fail.Min - $have), $fail.Name) }
  return $rows
}
$CN_NUM = @{ '一'=1;'二'=2;'三'=3;'四'=4;'五'=5;'六'=6;'七'=7;'八'=8;'九'=9;'十'=10;'十一'=11;'十二'=12 }

function ConvertFrom-CnNum([string]$s) {
  if ($CN_NUM.ContainsKey($s)) { return $CN_NUM[$s] }
  $n = 0
  if ([int]::TryParse($s, [ref]$n)) { return $n }
  return $null
}

$BossAdvice = @{
  3  = '首领·翠翎恐蕈｜限时 1:45（星章 1:10）｜雷推怒气条（火 -34%）；条满后约 15 秒力竭 + 全抗 -25%，输出全压这个窗口'
  6  = '首领·深黯魇语之主｜限时 1:50（星章 1:15）｜45% 血量的护盾只吃元素/反应伤害；杀 4 个钓客各削 25%，破盾后瘫痪 10 秒 + 全抗 -70%'
  8  = '首领·强化丘丘萨满｜限时 1:50（星章 1:15）｜别只灭火：16 秒内用岩/钝击打碎锅（30U）；破锅 = 真伤 + 瘫痪 10 秒；失败会狂暴'
  10 = '首领·蕴光月守宫｜限时 1:45（星章 1:10）｜被标记时伤害 -50%、暴击 -100%，无敌帧躲开或把输出角色奶满解除；解除给暴击 +15%、增伤 +50%'
}
$EventAdvice = '三选一：缺人拿「伙伴」→ 有输出缺口拿「辉彩祝福」→ 都不缺才拿「神秘收获」；「伙伴·华丽阵容」清空花，花少才划算'

function Get-State($templateHits, [string]$text) {
  $st = [pscustomobject]@{ Kind='unknown'; Act=$null; Note=''; Source='none'; Score=$null }

  foreach ($hit in $templateHits) {
    if ($hit.Name -match '^act(\d{1,2})$') {
      $st.Kind='act'; $st.Act=[int]$Matches[1]; $st.Source='template'; $st.Score=$hit.Score; return $st
    }
    if ($hit.Name -eq 'choice') { $st.Kind='choice'; $st.Source='template'; $st.Score=$hit.Score; return $st }
    if ($hit.Name -eq 'result') { $st.Kind='result'; $st.Source='template'; $st.Score=$hit.Score; return $st }
  }

  $t = ($text -replace '\s+', '')
  $m = [regex]::Match($t, '第([一二三四五六七八九十0-9]{1,3})幕')
  if ($m.Success) {
    $n = ConvertFrom-CnNum $m.Groups[1].Value
    if ($n) { $st.Kind='act'; $st.Act=$n; $st.Source='ocr'; return $st }
  }
  foreach ($k in @('伙伴·','神秘收获','辉彩祝福','圣牌挑战','战斗·')) {
    if ($t.Contains($k)) { $st.Kind='choice'; $st.Note=$k; $st.Source='ocr'; return $st }
  }
  if ($t.Contains('剧诗') -or $t.Contains('幻剧') -or $t.Contains('演出')) { $st.Kind='theater'; $st.Source='ocr'; return $st }
  if ($t.Contains('重新演出') -or $t.Contains('巡演')) { $st.Kind='result'; $st.Source='ocr'; return $st }
  return $st
}

function Format-Advice($state, $hits) {
  $src = if ($state.Source -eq 'template') { "模板 $($state.Score)" } elseif ($state.Source -eq 'ocr') { 'OCR' } else { '—' }
  switch ($state.Kind) {
    'act' {
      $lines = @("【第 $($state.Act) 幕】  (来源: $src)")
      if ($BossAdvice.ContainsKey([int]$state.Act)) { $lines += $BossAdvice[[int]$state.Act] }
      else { $lines += '普通关｜先保通关再争星章；主力耐力留给 3/6/8/10 幕首领' }
      $lines += '耐力 2 点/人；别在头两幕把开幕主力耗光'
      return ($lines -join "`n")
    }
    'choice' { return "【三选一】  (来源: $src)`n$EventAdvice" }
    'theater' { return "【剧诗界面】`n还没进幕：先确认人数够档，再排前四幕" }
    'result' { return "【结算/回溯】`n要重来就走回溯：回到上一个首领关结束时的状态" }
    default {
      $th = if ($hits -and $hits.Count) { ($hits | ForEach-Object { "$($_.Name)=$($_.Score)" }) -join ', ' } else { '无' }
      return "【等待】没认出幕次`n模板命中: $th`nCtrl+Alt+D 存一帧给我做锚点"
    }
  }
}

$roiArr = $Roi.Split(',') | ForEach-Object { [int]$_ }
$engine = New-OcrEngine $Language
if (-not $engine) { throw "无法创建 OCR 引擎（缺 $Language 语言包：设置→时间和语言→语言→中文(简体)→OCR）" }

if ($RosterReport) {
  Initialize-RosterData
  if (-not $Roster) { throw '-RosterReport 需要同时给 -Roster <GOOD 导出.json>' }
  $rows = Set-Eligibility $Roster
  Write-Host ("角色池: {0} 个角色" -f $script:RosterSet.Count)
  Write-Host ("当期元素: {0} / 开幕 {1} / 特邀 {2}" -f ($script:SeasonData.elements -join "/"), ($script:SeasonData.opening -join ","), ($script:SeasonData.guests -join ","))
  $rows | Format-Table -AutoSize Name,Level,Min,Cap,Count,Ok,Detail
  Write-Host $script:EligLine
  return
}

# 判定自测：把一段文字喂给状态机，打印结论（用于标定锚点）
if ($Classify) {
  $st = Get-State @() $Classify
  $adv = Format-Advice $st @()
  Write-Host ("kind={0} act={1} src={2}" -f $st.Kind, $st.Act, $st.Source)
  Write-Host $adv
  return
}

if ($ListWindows) { Get-VisibleWindows | Sort-Object Class,Title | Format-Table -AutoSize Handle,Class,Title; return }

# 悬浮窗模式带角色池：先把池子算出来，之后每帧追加一行资格提示
if ($Roster) {
  Initialize-RosterData
  [void](Set-Eligibility $Roster)
  Write-Host $script:EligLine
}

if ($Learn) {
  if (-not $LearnTemplate) { throw '-Learn 需要同时给 -LearnTemplate <模板png>' }
  if (-not (Test-Path $Learn)) { throw "找不到帧: $Learn" }
  if (-not (Test-Path $LearnTemplate)) { throw "找不到模板: $LearnTemplate" }
  $frame = [System.Drawing.Bitmap]::FromFile((Resolve-Path $Learn).Path)
  $tpl   = [System.Drawing.Bitmap]::FromFile((Resolve-Path $LearnTemplate).Path)
  try {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $r = [Matcher]::Find($frame, $tpl, $MatchStep)
    $sw.Stop()
    Write-Host ("模板 {0} ({1}x{2}) 在 {3} ({4}x{5}) 中：score={6:N4} at ({7},{8})  用时 {9} ms" -f `
      (Split-Path -Leaf $LearnTemplate), $tpl.Width, $tpl.Height, (Split-Path -Leaf $Learn), $frame.Width, $frame.Height, `
      $r.Score, $r.X, $r.Y, $sw.ElapsedMilliseconds)
    Write-Host ("阈值 {0} 判定：{1}" -f $TemplateThreshold, $(if ($r.Score -ge $TemplateThreshold) { '命中' } else { '未命中' }))
  } finally { $frame.Dispose(); $tpl.Dispose() }
  return
}

if ($SelfTest) {
  Write-Host "OCR 语言: $($engine.RecognizerLanguage.LanguageTag)；模板数: $($script:TemplateList.Count)"
  $win = Find-GameWindow $WindowTitle
  if (-not $win) { throw "没找到窗口「$WindowTitle」，先用 -ListWindows 看看" }
  Write-Host "窗口: $($win.Title) [$($win.Class)] hwnd=$($win.Handle)"
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $png = Capture-Window $win.Handle $roiArr $framePng
  $t1 = $sw.ElapsedMilliseconds
  $txt = Read-OcrText $png $engine
  $t2 = $sw.ElapsedMilliseconds
  $hits = Find-Templates $png
  $t3 = $sw.ElapsedMilliseconds
  $st = Get-State $hits $txt
  Write-Host ("抓帧 {0} ms / OCR {1} ms ({2} 字) / 模板 {3} ms ({4} 个命中) / 判定 {5}" -f $t1, ($t2-$t1), $txt.Length, ($t3-$t2), @($hits).Count, $st.Kind)
  if (@($hits).Count) { $hits | Format-Table -AutoSize }
  $flat = ($txt -replace '\s+',' ')
  Write-Host ("前 160 字: " + $flat.Substring(0, [Math]::Min(160, $flat.Length)))
  return
}

if ($Collect) {
  $win = Find-GameWindow $WindowTitle
  if (-not $win) { throw "没找到窗口「$WindowTitle」" }
  $deadline = (Get-Date).AddSeconds($CollectSeconds)
  $n = 0
  Write-Host "采样中 → $samDir（$CollectSeconds 秒，每 $CollectEveryMs ms 一张；Ctrl+C 停止）"
  while ((Get-Date) -lt $deadline) {
    $p = Join-Path $samDir ("frame-{0}.png" -f (Get-Date -Format 'HHmmss-fff'))
    try { [void](Capture-Window $win.Handle $roiArr $p); $n++ } catch { Write-Warning $_.Exception.Message }
    Start-Sleep -Milliseconds $CollectEveryMs
  }
  Write-Host "采样完成：$n 张 → $samDir"
  return
}

$win = Find-GameWindow $WindowTitle
if (-not $win) { Write-Warning "没找到窗口「$WindowTitle」，悬浮窗仍启动，识别会等到窗口出现" }

$form = New-Object TheaterOverlayForm
$form.Location = New-Object System.Drawing.Point(40, 80)
$form.Lbl.Text = '启动中…'
$script:visible = $true
$script:clickThrough = $true
$script:busy = $false
$script:last = $null
$script:lastText = ''

function Invoke-Frame {
  if ($script:busy) { return }
  $script:busy = $true
  try {
    $w = Find-GameWindow $WindowTitle
    if (-not $w) { $form.Lbl.Text = "【等待】没找到窗口「$WindowTitle」`n请用窗口化 / 窗口全屏"; return }
    $png = Capture-Window $w.Handle $roiArr $framePng
    $hits = Find-Templates $png
    $txt  = Read-OcrText $png $engine
    $st   = Get-State $hits $txt
    $script:lastText = $txt
    $hitText = (($hits | ForEach-Object { "$($_.Name):$($_.Score)" }) -join ',')
    Add-Content -Path $script:LogFile -Encoding UTF8 -Value (
      '[' + (Get-Date -Format 'HH:mm:ss') + '] kind=' + $st.Kind + ' act=' + $st.Act + ' src=' + $st.Source +
      ' hits=' + $hitText + ' | ' + (($txt -replace '\s+',' ').Trim()))
    $advice = Format-Advice $st $hits
    if ($script:EligLine) { $advice += "`n" + $script:EligLine }
    if ([TW32]::GetForegroundWindow() -ne $w.Handle) { $advice = "（游戏不在前台，截到的是别的窗口）`n" + $advice }
    if ($advice -ne $script:last) { $form.Lbl.Text = $advice; $script:last = $advice }
  } catch {
    $form.Lbl.Text = "【错误】$($_.Exception.Message)"
  } finally { $script:busy = $false }
}

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = $IntervalMs
$timer.Add_Tick({ Invoke-Frame })

$hot = New-Object System.Windows.Forms.Timer
$hot.Interval = 120
$hot.Add_Tick({
  $ctrl = ([TW32]::GetAsyncKeyState(0x11) -band 0x8000) -ne 0
  $alt  = ([TW32]::GetAsyncKeyState(0x12) -band 0x8000) -ne 0
  if (-not ($ctrl -and $alt)) { return }
  if (([TW32]::GetAsyncKeyState(0x48) -band 0x8000) -ne 0) {
    if ($script:visible) { $form.Hide(); $script:visible=$false } else { $form.Show(); $script:visible=$true }
    Start-Sleep -Milliseconds 250
  }
  if (([TW32]::GetAsyncKeyState(0x4C) -band 0x8000) -ne 0) {
    $script:clickThrough = -not $script:clickThrough
    [TW32]::SetClickThrough($form.Handle, $script:clickThrough)
    Start-Sleep -Milliseconds 250
  }
  if (([TW32]::GetAsyncKeyState(0x44) -band 0x8000) -ne 0) {
    $p = Join-Path $samDir ("dump-{0}.png" -f (Get-Date -Format 'HHmmss'))
    try { $w2 = Find-GameWindow $WindowTitle; if ($w2) { [void](Capture-Window $w2.Handle $roiArr $p) } } catch { }
    if ($script:lastText) { Set-Content -Path ($p -replace '\.png$','.txt') -Value $script:lastText -Encoding UTF8 }
    Start-Sleep -Milliseconds 250
  }
})

[TW32]::SetExcludedFromCapture($form.Handle)
$form.Show()
$timer.Start(); $hot.Start()
Write-Host "v0.2 已启动；模板 $($script:TemplateList.Count) 个；日志 $script:LogFile"
Write-Host "热键：Ctrl+Alt+H 显隐 / Ctrl+Alt+L 穿透 / 可拖动 / Ctrl+Alt+D 存帧"
[System.Windows.Forms.Application]::Run($form)
