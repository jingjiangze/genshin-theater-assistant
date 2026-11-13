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
    private static readonly SolidColorBrush OkBrush = Freeze(Color.FromRgb(0x5B, 0xD4, 0x8E));

    private readonly OverlayLogDocument _logDocument;
    private int _lineCount;
    private bool _logExpanded;

    // ── 面板等比缩放 ─────────────────────────────────────────────
    // 参考客户区 = 1920×1080@100%（面板按这个尺度设计，字号/留白都是照它调的）。
    // 更小的客户区整体缩小：404 DIP 固定宽的面板在 1280×720 上会压住左下角的幕次条带
    // （条带 x 0.28..0.74 / y 0.828..0.878，即 x 358..947 / y 596..632），
    // 1280@150%（1920×1080 物理的 DIP 客户区）同理。
    private const double ReferenceWidth = 1920, ReferenceHeight = 1080;
    private const double PanelBaseWidth = 404, PanelBaseMarginLeft = 14, PanelBaseMarginBottom = 92;
    private const double MinPanelScale = 0.5;

    private double _panelScale = double.NaN;   // NaN = 还没应用过

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

        // 客户区大小由 MaskWindowHost 跟随游戏窗口设置；这里只负责"面板随客户区等比缩"。
        // 先按参考尺寸（s=1）初始化，真正的系数在 OnRenderSizeChanged 里按实际尺寸重算。
        ApplyPanelScale(PanelScaleFor(ReferenceWidth, ReferenceHeight));
    }

    /// <summary>
    /// RenderSize 变化时重算面板缩放。用 OnRenderSizeChanged 而不是 SizeChanged 事件：
    /// 自检里是手工 Measure/Arrange 的（没有真实 HWND 尺寸通知），RenderSize 一定会跟着布局更新。
    /// </summary>
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        ApplyPanelScale(PanelScaleFor(sizeInfo.NewSize.Width, sizeInfo.NewSize.Height));
    }

    /// <summary>面板缩放系数：s = min(1, min(客户区宽/1920, 客户区高/1080))，下限 0.5。</summary>
    internal static double PanelScaleFor(double clientWidthDip, double clientHeightDip)
    {
        if (clientWidthDip <= 0 || clientHeightDip <= 0) return 1.0;   // 还没布局：先按参考尺寸
        var s = Math.Min(clientWidthDip / ReferenceWidth, clientHeightDip / ReferenceHeight);
        return Math.Max(MinPanelScale, Math.Min(1.0, s));
    }

    /// <summary>面板矩形（窗口 DIP 坐标）。纯函数版本供自检做几何判定：
    /// 面板左/下贴边并留 14/92 的参考外边距，两者都乘 s；面板高度由调用方给出（折叠态实际布局值）。
    /// </summary>
    internal static Rect PanelRectFor(double clientWidthDip, double clientHeightDip, double panelHeightDip)
    {
        var s = PanelScaleFor(clientWidthDip, clientHeightDip);
        return new Rect(
            PanelBaseMarginLeft * s,
            clientHeightDip - PanelBaseMarginBottom * s - panelHeightDip * s,
            PanelBaseWidth * s,
            panelHeightDip * s);
    }

    /// <summary>
    /// 把面板整体缩放到 s（不改内部排版，只缩放最终布局尺寸），外边距同步乘 s——
    /// 否则字号缩了、留白没缩，面板会显得"贴边"且离屏幕边缘的比例失真。
    /// </summary>
    internal void ApplyPanelScale(double scale)
    {
        if (double.IsNaN(scale) || scale <= 0) scale = 1.0;
        if (!double.IsNaN(_panelScale) && Math.Abs(scale - _panelScale) < 1e-6) return;
        _panelScale = scale;

        if (Panel.LayoutTransform is ScaleTransform st)
        {
            st.ScaleX = scale;
            st.ScaleY = scale;
        }
        else
        {
            Panel.LayoutTransform = new ScaleTransform(scale, scale);
        }

        Panel.Margin = new Thickness(PanelBaseMarginLeft * scale, 0, 0, PanelBaseMarginBottom * scale);
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
            ? "Ctrl+Alt+M 收起日志"
            : "穿透已关（可拖动/排查）· Ctrl+Alt+L 恢复";
    }

    /// <summary>面板标题跟随玩法（剧诗/深渊…）。</summary>
    public void SetMode(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return;
        TitleText.Text = displayName.EndsWith("助手", StringComparison.Ordinal) ? displayName : displayName + "助手";
    }

    /// <summary>
    /// 日志展开/收起。默认收起：只留「现在做」——
    /// 玩家在打的时候需要的是指令，不是解释；要排查时一键展开。
    /// </summary>
    public void SetLogExpanded(bool on)
    {
        _logExpanded = on;
        LogBlock.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Divider.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Chevron.Text = on ? "▾" : "▸";

        // 折叠状态下 ScrollToEnd 是空操作（没有 viewport），所以展开后必须再滚一次，
        // 否则玩家按 Ctrl+Alt+M 看到的是"最旧的 8 行"，最新一条在下面看不到（视觉验收实测踩到）。
        if (on)
        {
            Dispatcher.BeginInvoke(new Action(ScrollLogToEnd), System.Windows.Threading.DispatcherPriority.Loaded);
            Dispatcher.BeginInvoke(new Action(ScrollLogToEnd), System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    public bool LogExpanded => _logExpanded;

    /// <summary>右上角状态胶囊：幕次 + 决策来源（颜色跟来源标记走）。</summary>
    public void SetStatus(string actText, string source, string extra = "")
    {
        var text = string.IsNullOrEmpty(actText) ? source : $"{actText} · {source}";
        if (!string.IsNullOrEmpty(extra)) text += " · " + extra;
        StatusText.Text = string.IsNullOrEmpty(text) ? "待识别…" : text;
        StatusText.Foreground = SourceBrush(source);
    }

    /// <summary>
    /// 「现在做」一行（与悬浮窗/桌宠同一句）。空字符串 = 整行收起。
    /// warn=true 时用警示色（弃权/异常），避免玩家照着过期指令操作。
    /// guide = 战斗中的手法指导（可选第二条，小一号；空 = 收起）。
    /// </summary>
    public void SetAdvice(string text, bool warn = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            AdviceRow.Visibility = Visibility.Collapsed;
            AdviceText.Text = "";
            SetGuide(null);
            return;
        }

        AdviceText.Text = text;
        AdviceText.Foreground = warn ? WarnBrush : TextBrush;
        AdviceMark.Foreground = warn ? WarnBrush : OkBrush;
        AdviceMark.Text = warn ? "⚠" : "▶";
        AdviceRow.Visibility = Visibility.Visible;
    }

    /// <summary>战斗中的手法指导（第二条）。null/空 = 收起。</summary>
    public void SetGuide(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            GuideText.Visibility = Visibility.Collapsed;
            GuideText.Text = "";
            return;
        }
        GuideText.Text = text;
        GuideText.Visibility = Visibility.Visible;
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
        var inlines = _logDocument.MaintainNow();
        // 裁剪后强制滚到最新（验收发现日志停在顶部，"最近 50 行"看不到最新一条）
        Dispatcher.BeginInvoke(new Action(ScrollLogToEnd), System.Windows.Threading.DispatcherPriority.Background);   // UI 线程同步裁剪：行数有上限，内存恒定（自检也走这条）
        var retained = Math.Max(1, (inlines + 1) / 4);   // 每行 = 3 个 inline + 1 个换行
        CountText.Text = $"共 {_lineCount} 行 · 最近 {retained} 行";
    }

    /// <summary>滚到日志最新一行（自检在 Render 前也会调一次，覆盖重排后的重置）。</summary>
    /// <summary>
    /// 应用设置窗的 ui.json（此前这些选项只写不读，等于空转；审计发现）。
    /// 不透明度作用在整窗（遮罩本就是纯覆盖层），字号只改正文两行，不动标题/页脚。
    /// </summary>
    internal void ApplyPrefs(UiPrefs p)
    {
        if (p == null) return;
        try
        {
            Opacity = Math.Clamp(p.OpacityPct, 30, 100) / 100.0;
            AdviceText.FontSize = p.BodyFontPt;
            GuideText.FontSize = Math.Max(9, p.BodyFontPt - 0.5);
            StatusChip.Visibility = p.ShowSourceMark ? Visibility.Visible : Visibility.Collapsed;
        }
        catch { }
    }

    internal void ScrollLogToEnd() => _logDocument.ScrollToLatest();

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
