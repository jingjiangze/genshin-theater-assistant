using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace TheaterAssistant;

/// <summary>
/// 悬浮窗（WPF 版）。
///
/// 与 WinForms 版的能力对齐，但渲染换成 WPF 矢量：
///   · 圆角/阴影/渐变抗锯齿 —— 解决"很差"的核心观感问题；
///   · 窗口样式仍走 Win32（NOACTIVATE / TRANSPARENT / TOOLWINDOW / 防截屏），这些 WPF 不管；
///   · 点击穿透与防截屏行为与 WinForms 版完全一致（同一套 P/Invoke）。
///
/// 只读红线不变：本窗口只画自己，不碰游戏进程。
/// </summary>
public partial class OverlayWindow : Window
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

    private bool _clickThrough = true;

    public OverlayWindow()
    {
        InitializeComponent();

        // 位置：按物理像素钳制（高 DPI 下 WinForms/WPF 的 Size 都会被虚拟化）
        var size = new System.Drawing.Size((int)Width, (int)Height);
        var p = ScreenMetrics.ClampToScreen(new System.Drawing.Point(40, 80), size);
        Left = p.X;
        Top = p.Y;

        SourceInitialized += (_, _) => ApplyWindowStyles();
        Loaded += (_, _) => SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, WDA_EXCLUDEFROMCAPTURE);

        // 首帧占位，避免空白卡片（真实内容由 OnTick 驱动，下一步接入）
        SetLines(new[]
        {
            ("◆ 正在初始化识别与决策…", "量化"),
            ("· 只读像素，不注入 / 不读内存 / 不模拟键鼠", "规则"),
        });
        SetStatus("就绪");
    }

    private void ApplyWindowStyles()
    {
        var h = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(h, GWL_EXSTYLE);
        ex |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        if (_clickThrough) ex |= WS_EX_TRANSPARENT;
        SetWindowLong(h, GWL_EXSTYLE, ex);
    }

    /// <summary>切换点击穿透（Ctrl+Alt+L 拖动时需要关掉，否则收不到鼠标）。</summary>
    public void SetClickThrough(bool on)
    {
        _clickThrough = on;
        ApplyWindowStyles();
        FootText.Text = on
            ? "Ctrl+Alt+L 拖动 · Ctrl+Alt+H 显隐 · Ctrl+Alt+D 存帧 · Ctrl+Alt+G 攻略"
            : "拖动中（穿透已关）· Ctrl+Alt+L 恢复穿透";
    }

    public void SetStatus(string s) => StatusText.Text = s ?? "";

    /// <summary>按来源标记分色的正文行（与 WinForms 版同一套配色口径）。</summary>
    public void SetLines(IEnumerable<(string Text, string Source)> lines)
    {
        var list = new List<LineVm>();
        foreach (var (text, src) in lines)
            list.Add(new LineVm { Text = text, Brush = BrushOf(text) });
        LineList.ItemsSource = list;
    }

    internal sealed class LineVm
    {
        public string Text { get; set; } = "";
        public Brush Brush { get; set; } = Brushes.White;
    }

    /// <summary>与 WinForms 版一致的来源配色：◆蓝 ★金 ◇紫 ○青 ·灰 ⚠橙。</summary>
    private static Brush BrushOf(string line)
    {
        var c = string.IsNullOrEmpty(line) ? ' ' : line[0];
        return c switch
        {
            '◆' => new SolidColorBrush(Color.FromRgb(96, 168, 232)),
            '★' => new SolidColorBrush(Color.FromRgb(214, 182, 96)),
            '◇' => new SolidColorBrush(Color.FromRgb(160, 136, 220)),
            '○' => new SolidColorBrush(Color.FromRgb(96, 196, 184)),
            '⚠' => new SolidColorBrush(Color.FromRgb(232, 152, 88)),
            '·' => new SolidColorBrush(Color.FromRgb(150, 158, 178)),
            _ => new SolidColorBrush(Color.FromRgb(214, 222, 238)),
        };
    }
}
