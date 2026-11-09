// ─────────────────────────────────────────────────────────────────────────────
// 本文件是 BetterGI（https://github.com/babalae/better-genshin-impact）的衍生实现。
// 原版权：Copyright (c) babalae 及 BetterGI contributors（GPL-3.0）。
// 上游文件：BetterGenshinImpact/View/MaskWindow.xaml.cs、View/MaskWindow.xaml
// 本项目在 WPF + .NET 8 下移植（结构照抄、依赖剪掉），修改时间 2026-11-09。
// 以 GPL-3.0 分发，全文见仓库根 LICENSE，来源说明见 NOTICE.md。
// ─────────────────────────────────────────────────────────────────────────────
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;

namespace TheaterAssistant;

/// <summary>
/// 遮罩窗口：盖在游戏客户区上的全透明置顶窗，左下角一块日志面板 + 一层识别框。
///
/// 照抄 BetterGI 的三处关键做法（其余依赖全部剪掉）：
///   ① 窗口样式走 Win32：WS_EX_TRANSPARENT（点击穿透）+ NOACTIVATE（不抢焦点）+ TOOLWINDOW（不进 Alt+Tab）；
///   ② 日志用合并式文档维护（<see cref="OverlayLogDocument"/>），不在 TextChanged 里递归裁剪；
///   ③ SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)：遮罩自己不被截进画面——
///      这一条对本项目是**必需品**，否则我们自己的 GDI 抓屏会把日志文字一起 OCR 进去。
///
/// 与上游的差异：不挂 WS_CHILD 到游戏窗口（不改变其他进程的窗口树），
/// 位置/尺寸由 <see cref="MaskWindowHost"/> 按客户区跟随。
/// </summary>
public partial class MaskWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    private static readonly SolidColorBrush DimBrush = Freeze(Color.FromRgb(0x6E, 0x79, 0x8A));
    private static readonly SolidColorBrush TextBrush = Freeze(Color.FromRgb(0xD6, 0xDE, 0xEE));
    private static readonly SolidColorBrush WarnBrush = Freeze(Color.FromRgb(0xE8, 0x98, 0x58));
    private static readonly SolidColorBrush ErrBrush = Freeze(Color.FromRgb(0xE8, 0x72, 0x72));
    private static readonly SolidColorBrush InfoBrush = Freeze(Color.FromRgb(0x7E, 0xB8, 0xE8));
    private static readonly SolidColorBrush DecideBrush = Freeze(Color.FromRgb(0xD6, 0xB6, 0x60));
    private static readonly SolidColorBrush OcrBrush = Freeze(Color.FromRgb(0x60, 0xC4, 0xB8));

    private readonly OverlayLogDocument _logDocument;
    private int _lineCount;

    /// <summary>默认点击穿透（遮罩只看不点）。Ctrl+Alt+L 可切换，用于拖动/排查。</summary>
    public bool ClickThrough { get; private set; } = true;

    /// <summary>已写入的日志行数（自检断言用；裁剪不影响这个计数）。</summary>
    public int LogLineCount => _lineCount;

    public MaskWindow()
    {
        InitializeComponent();

        _logDocument = new OverlayLogDocument(LogTextBox);
        if (LogTextBox.Document.Blocks.LastBlock is Paragraph p)
        {
            p.Margin = new Thickness(0);
            p.LineHeight = 16.5;
            p.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        }

        SourceInitialized += (_, _) => ApplyWindowStyles();
        Loaded += (_, _) => ExcludeFromCapture();
        Closed += (_, _) => _logDocument.Dispose();
    }

    private void ApplyWindowStyles()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return;

        var ex = GetWindowLong(h, GWL_EXSTYLE);
        ex |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        if (ClickThrough) ex |= WS_EX_TRANSPARENT;
        else ex &= ~WS_EX_TRANSPARENT;
        SetWindowLong(h, GWL_EXSTYLE, ex);
    }

    private void ExcludeFromCapture()
    {
        try
        {
            var h = new WindowInteropHelper(this).Handle;
            if (h != IntPtr.Zero) SetWindowDisplayAffinity(h, WDA_EXCLUDEFROMCAPTURE);
        }
        catch { }
    }

    /// <summary>切换点击穿透（true = 鼠标穿透到游戏）。</summary>
    public void SetClickThrough(bool on)
    {
        ClickThrough = on;
        ApplyWindowStyles();
        FootText.Text = on
            ? "只读屏幕 · 不注入 · Ctrl+Alt+H 显隐"
            : "穿透已关（可拖动/排查）· Ctrl+Alt+L 恢复";
    }

    /// <summary>右上角状态胶囊：幕次 + 决策来源（颜色跟来源标记走）。</summary>
    public void SetStatus(string actText, string source, string extra = "")
    {
        var text = string.IsNullOrEmpty(actText) ? source : $"{actText} · {source}";
        if (!string.IsNullOrEmpty(extra)) text += " · " + extra;
        StatusText.Text = string.IsNullOrEmpty(text) ? "待识别…" : text;
        StatusText.Foreground = SourceBrush(source);
    }

    /// <summary>
    /// 「现在做」一行（与悬浮窗/桌宠同一句）。空字符串 = 整行收起——
    /// 弃权时不显示旧指令，避免玩家照着过期的提示操作。
    /// </summary>
    public void SetAdvice(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            AdviceRow.Visibility = Visibility.Collapsed;
            AdviceText.Text = "";
            return;
        }

        AdviceText.Text = text;
        AdviceRow.Visibility = Visibility.Visible;
    }

    /// <summary>本帧的识别框（游戏帧像素坐标）+ 帧尺寸。</summary>
    internal void SetFrame(IReadOnlyList<MaskBox> boxes, int gameWidth, int gameHeight)
        => DrawLayer.SetFrame(boxes, gameWidth, gameHeight);

    /// <summary>追加一行日志：时间（灰） + 级别（着色） + 正文。</summary>
    public void AppendLog(string level, string message)
    {
        if (LogTextBox.Document.Blocks.LastBlock is not Paragraph p)
        {
            p = new Paragraph { Margin = new Thickness(0), LineHeight = 16.5, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
            LogTextBox.Document.Blocks.Add(p);
        }

        if (_lineCount > 0) p.Inlines.Add(new LineBreak());
        p.Inlines.Add(new Run($"{DateTime.Now:HH:mm:ss} ") { Foreground = DimBrush });

        level = (level ?? "INF").Trim().ToUpperInvariant();
        if (level.Length > 3) level = level[..3];
        p.Inlines.Add(new Run(level.PadRight(3))
        {
            Foreground = LevelBrush(level),
            FontWeight = FontWeights.SemiBold,
        });
        // 正文始终用正文色：只有级别着色，否则连续两条 WRN 会把整块面板染成橙色
        p.Inlines.Add(new Run(" " + (message ?? "")) { Foreground = TextBrush });

        _lineCount++;
        var inlines = _logDocument.MaintainNow();   // UI 线程同步裁剪：行数有上限，内存恒定（自检也走这条）
        var retained = Math.Max(1, (inlines + 1) / 4);   // 每行 = 3 个 inline + 1 个换行
        CountText.Text = $"共 {_lineCount} 行 · 最近 {retained} 行";
    }

    /// <summary>同步执行日志裁剪并返回尾部段落 inline 数（自检专用）。</summary>
    internal int TrimLogsNow() => _logDocument.MaintainNow();

    private static Brush SourceBrush(string source)
    {
        var c = string.IsNullOrEmpty(source) ? ' ' : source[0];
        return c switch
        {
            '★' => DecideBrush,
            '◆' => InfoBrush,
            '◇' => Freeze(Color.FromRgb(0xA0, 0x88, 0xDC)),
            '○' => OcrBrush,
            '⚠' => WarnBrush,
            _ => DimBrush,
        };
    }

    private static Brush LevelBrush(string level) => level switch
    {
        "ERR" => ErrBrush,
        "WRN" => WarnBrush,
        "OCR" => OcrBrush,
        "DEC" => DecideBrush,
        _ => InfoBrush,
    };

    private static SolidColorBrush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
