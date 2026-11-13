using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;

namespace TheaterAssistant;

/// <summary>
/// 悬浮球 —— 常态形态。照抄「空荧酒馆」地图悬浮窗（MapsBalloon, GPL-3.0）的交互：
///   单击/双击 → 打开详细面板；左键拖动 → 移动；右键 → 托盘菜单（由 App 提供）。
///
/// 为什么是"球"而不是面板：
///   游戏内悬浮辅助的通例（MapsBalloon 75×72、BetterGI 的遮罩小窗）都是**极小**的常驻元素，
///   只在需要时展开。大面板会挡视线、也会让玩家关掉它。
///
/// 状态色：◆量化=蓝、◇模型=紫、★已验证=金、·规则=灰、…睡眠
/// </summary>
public partial class FloatingBall : Window
{
    public enum BallState { Idle, Thinking, Waiting, Done, Sleeping }

    private BallState _state = BallState.Idle;
    private readonly DispatcherTimer _anim = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private double _t;
    private bool _dragging;
    private Point _dragStart;      // 屏幕 DIP
    private Point _winStart;       // 窗口 DIP（Left/Top）

    // 右下角留白（参考尺寸，DIP）
    private const double CornerInsetX = 36, CornerInsetY = 96;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    public FloatingBall()
    {
        InitializeComponent();

        // 位置：右下角（照 MapsBalloon 的"屏幕右下角常驻"）。
        // 口径：ScreenMetrics.PhysicalSize 是**物理像素**，Left/Top 是 **DIP** ——
        // 必须按 DPI 换算成 DIP 再钳位；直接把物理像素塞给 Left/Top，在 150%/200% 屏上球会跑到屏幕外。
        PlaceAtDefaultCorner();

        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            var ex = GetWindowLong(h, GWL_EXSTYLE);
            SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            // 自己不被截进画面（防止被其他程序/反作弊看到）
            SetWindowDisplayAffinity(h, WDA_EXCLUDEFROMCAPTURE);

            // HWND 建立后能拿到窗口所在显示器的真实 DPI（混合 DPI / 副屏），初始位置按它再校正一次
            PlaceAtDefaultCorner();
        };

        Loaded += (_, _) =>
        {
            // 淡入：XAML 默认 Opacity=1（保证无消息泵/自检时也能渲染出内容），
            // 这里先显式置 0 再动画到 1 —— 视觉上仍是淡入，但不依赖"初始为 0"。
            Opacity = 0;
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(500));
            BeginAnimation(OpacityProperty, fade);
            StartAnim();
        };

        Root.MouseLeftButtonDown += OnMouseDown;
        Root.MouseMove += OnMouseMove;
        Root.MouseLeftButtonUp += OnMouseUp;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        // WPF 没有 MouseDoubleClick（那是 WinForms 事件），双击靠 ClickCount 判
        if (e.ClickCount >= 2)
        {
            App.CurrentApp?.OpenDetail();
            return;
        }
        _dragging = true;
        _dragStart = ScreenDip(e.GetPosition(this));
        _winStart = new Point(Left, Top);
        Root.CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;

        // 全程 DIP：窗口内位置（DIP）+ Left/Top（DIP）= 指针的屏幕 DIP。
        // 旧代码拿 PointToScreen（物理像素）的增量去加 Left/Top（DIP），150% 屏上拖动会越拖越偏。
        var cur = ScreenDip(e.GetPosition(this));
        var c = ClampToScreenDip(
            new System.Drawing.Point((int)Math.Round(_winStart.X + cur.X - _dragStart.X),
                                     (int)Math.Round(_winStart.Y + cur.Y - _dragStart.Y)),
            WindowSizeDip(), ScreenSizeDip());
        Left = c.X;
        Top = c.Y;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Root.ReleaseMouseCapture();
    }

    // ── DPI / 屏幕几何 ──────────────────────────────────────────
    /// <summary>窗口当前 DPI 缩放（物理像素 → DIP）。优先用窗口所在显示器的真实 DPI。</summary>
    private double DpiScaleForWindow()
    {
        try
        {
            var h = new WindowInteropHelper(this).Handle;
            if (h != IntPtr.Zero)
            {
                var scale = MaskWindowHost.DpiScaleFor(h);
                if (scale > 0) return scale;
            }
        }
        catch { }

        var fallback = ScreenMetrics.ScaleFactor();
        return fallback > 0 ? fallback : 1.0;
    }

    /// <summary>窗口内位置（DIP）→ 屏幕 DIP（Left/Top 本身就是 DIP，直接相加即可）。</summary>
    private Point ScreenDip(Point clientDip) => new(Left + clientDip.X, Top + clientDip.Y);

    private System.Drawing.Size WindowSizeDip()
        => new((int)Math.Round(Width), (int)Math.Round(Height));

    /// <summary>屏幕 DIP 尺寸（物理分辨率 ÷ DPI）。</summary>
    private System.Drawing.Size ScreenSizeDip()
    {
        var physical = ScreenMetrics.PhysicalSize;
        var scale = DpiScaleForWindow();
        return new System.Drawing.Size(
            (int)Math.Round(physical.Width / scale),
            (int)Math.Round(physical.Height / scale));
    }

    private void PlaceAtDefaultCorner()
    {
        var screenDip = ScreenSizeDip();
        var size = WindowSizeDip();
        var p = ClampToScreenDip(
            new System.Drawing.Point(
                screenDip.Width - size.Width - (int)Math.Round(CornerInsetX),
                screenDip.Height - size.Height - (int)Math.Round(CornerInsetY)),
            size, screenDip);
        Left = p.X;
        Top = p.Y;
    }

    /// <summary>
    /// DIP 坐标系内的钳位。不能复用 ScreenMetrics.ClampToScreen：
    /// 那个函数用 PhysicalSize（物理像素）做边界，喂 DIP 进去等于把窗口当物理像素判断。
    /// </summary>
    private static System.Drawing.Point ClampToScreenDip(
        System.Drawing.Point p, System.Drawing.Size windowDip, System.Drawing.Size screenDip)
    {
        if (screenDip.Width <= 0 || screenDip.Height <= 0) return p;
        var maxX = Math.Max(0, screenDip.Width - Math.Min(windowDip.Width, screenDip.Width));
        var maxY = Math.Max(0, screenDip.Height - Math.Min(windowDip.Height, screenDip.Height));
        return new System.Drawing.Point(
            Math.Max(0, Math.Min(p.X, maxX)),
            Math.Max(0, Math.Min(p.Y, maxY)));
    }

    /// <summary>设置状态（决定角标与动画节奏）。</summary>
    public void SetState(BallState s)
    {
        _state = s;
        BadgeText.Text = s switch
        {
            BallState.Thinking => "◇",
            BallState.Waiting => "·",
            BallState.Done => "★",
            BallState.Sleeping => "…",
            _ => "◆",
        };
        var c = s switch
        {
            BallState.Done => Color.FromRgb(0xD6, 0xB6, 0x60),
            BallState.Thinking => Color.FromRgb(0xA0, 0x88, 0xDC),
            BallState.Waiting => Color.FromRgb(0x8C, 0x94, 0xA8),
            _ => Color.FromRgb(0x7E, 0xB8, 0xE8),
        };
        Halo.Fill = new SolidColorBrush(Color.FromArgb(0x4C, c.R, c.G, c.B));
        Ball.Stroke = new SolidColorBrush(Color.FromArgb(0x8C, c.R, c.G, c.B));
    }

    private void StartAnim()
    {
        if (_anim.IsEnabled) return;
        _anim.Tick += (_, _) => { _t += _anim.Interval.TotalSeconds; UpdateAnim(); };
        _anim.Start();
        SetState(BallState.Idle);
    }

    private void UpdateAnim()
    {
        double speed = _state switch
        {
            BallState.Thinking => 5.0,
            BallState.Done => 3.0,
            BallState.Sleeping => 0.6,
            BallState.Waiting => 1.2,
            _ => 1.6,
        };

        // 呼吸：球体轻微缩放 + 光晕明暗
        double breath = 1.0 + Math.Sin(_t * speed) * 0.045;
        Ball.RenderTransformOrigin = new Point(0.5, 0.5);
        Ball.RenderTransform = new ScaleTransform(breath, breath);
        Halo.Opacity = 0.55 + Math.Sin(_t * speed) * 0.25;

        // 星形缓慢自转（Done 时快转一圈）
        StarSpin.Angle = _state == BallState.Done
            ? (_t * 180) % 360
            : Math.Sin(_t * 0.8) * 12;
    }
}
