using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;

namespace TheaterAssistant;

/// <summary>
/// 桌宠窗口 —— 收起形态的助手。
///
/// 设计来源（用户要求「照抄开源桌宠」）：对齐 Codex v2 pet 规范的状态机
/// `Idle / Working / Thinking / Waiting / Done / Sleeping`，用一张形象 + 气泡承载信息，
/// 不占屏幕、不挡视线；点一下才展开详细面板。
///
/// 与 OverlayWindow 的分工：
///   PetWindow      = 常态（小、可爱、一眼看到"下一步做什么"）
///   OverlayWindow  = 展开态（完整指示 + 决策来源 + 诊断）
///
/// 素材策略：默认用内置矢量形象（几何星灵，规避游戏立绘版权）；
/// 用户可放 `pets/&lt;id&gt;/spritesheet.png` + `pet.json` 替换（格式同 Codex v2 pet）。
/// </summary>
public partial class PetWindow : Window
{
    // 状态机：与 Codex v2 pet 规范的动画状态对齐
    public enum PetState
    {
        Idle,       // 待机：轻微呼吸
        Thinking,   // 识别/决策中：快速浮动
        Waiting,    // 弃权/等玩家：慢速左右晃
        Done,       // 已给出指示：发光提示
        Sleeping,   // 长时间无输入：闭眼
    }

    private PetState _state = PetState.Idle;
    private readonly DispatcherTimer _anim = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private double _t;
    private bool _dragging;
    private Point _dragStart;      // 屏幕 DIP
    private Point _winStart;       // 窗口 DIP（Left/Top）

    // 右下角留白（参考尺寸，DIP）
    private const double CornerInsetX = 40, CornerInsetY = 80;

    private OverlayWindow _detail;
    private bool _eyeClosed;

    public PetWindow()
    {
        InitializeComponent();

        // 位置：右下角（桌宠惯例）。
        // 口径：ScreenMetrics.PhysicalSize 是物理像素，Left/Top 是 DIP —— 先按窗口所在屏的 DPI
        // 换算成 DIP（物理分辨率 ÷ scale）再钳位，否则 150%/200% 上桌宠会贴到屏幕外。
        PlaceAtDefaultCorner();

        SourceInitialized += (_, _) =>
        {
            ApplyNoActivate();
            // HWND 建立后能拿到真实显示器 DPI，初始位置按它再校正一次
            PlaceAtDefaultCorner();
        };
        Loaded += (_, _) => StartAnim();

        // 交互：单击展开详细信息，拖动移动，双击收起/展开
        PetCanvas.MouseLeftButtonDown += OnPetMouseDown;
        PetCanvas.MouseMove += OnPetMouseMove;
        PetCanvas.MouseLeftButtonUp += OnPetMouseUp;
    }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private void ApplyNoActivate()
    {
        var h = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(h, GWL_EXSTYLE);
        SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    // ── 拖动 ────────────────────────────────────────────────────
    private void OnPetMouseDown(object sender, MouseButtonEventArgs e)
    {
        // WPF 的 UIElement 没有 MouseDoubleClick 事件（那是 WinForms 的），
        // 双击必须用 MouseLeftButtonDown 的 ClickCount 判断。
        if (e.ClickCount >= 2) { ToggleDetail(); return; }
        _dragging = true;
        _dragStart = ScreenDip(e.GetPosition(this));
        _winStart = new Point(Left, Top);
        PetCanvas.CaptureMouse();
    }

    private void OnPetMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;

        // 全程 DIP：窗口内位置 + Left/Top。旧代码把 PointToScreen（物理像素）的增量
        // 加到 Left/Top（DIP）上，非 100% 缩放下拖动会跟手不一致。
        var cur = ScreenDip(e.GetPosition(this));
        var clamped = ClampToScreenDip(
            new System.Drawing.Point((int)Math.Round(_winStart.X + cur.X - _dragStart.X),
                                     (int)Math.Round(_winStart.Y + cur.Y - _dragStart.Y)),
            WindowSizeDip(), ScreenSizeDip());
        Left = clamped.X;
        Top = clamped.Y;
    }

    private void OnPetMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        PetCanvas.ReleaseMouseCapture();
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

    // ── 展开详细面板 ────────────────────────────────────────────
    private void ToggleDetail()
    {
        try
        {
            if (_detail != null && _detail.IsVisible)
            {
                _detail.Hide();
                return;
            }
            _detail ??= new OverlayWindow();
            _detail.Show();
            _detail.Activate();
        }
        catch (Exception ex)
        {
            App.LogCrash(ex, "ToggleDetail");
        }
    }

    // ── 状态与内容 ──────────────────────────────────────────────
    public void SetState(PetState s)
    {
        _state = s;
        BadgeText.Text = s switch
        {
            PetState.Thinking => "◇",
            PetState.Waiting => "·",
            PetState.Done => "★",
            PetState.Sleeping => "…",
            _ => "◆",
        };
        Badge.Background = s switch
        {
            PetState.Done => new SolidColorBrush(Color.FromArgb(0xE6, 0xD6, 0xB6, 0x60)),
            PetState.Thinking => new SolidColorBrush(Color.FromArgb(0xE6, 0xA0, 0x88, 0xDC)),
            _ => new SolidColorBrush(Color.FromArgb(0xE6, 0x1C, 0x24, 0x30)),
        };
    }

    /// <summary>气泡显示当前指示（桌宠的核心输出）。传空则收起气泡。</summary>
    public void ShowAdvice(string header, IEnumerable<(string Text, string Source)> lines)
    {
        if (string.IsNullOrWhiteSpace(header) && lines == null)
        {
            Bubble.Visibility = Visibility.Collapsed;
        BubbleTail.Visibility = Visibility.Collapsed;
            return;
        }
        BubbleHead.Text = header ?? "";
        var vm = new List<LineVm>();
        if (lines != null)
            foreach (var (text, _) in lines)
                vm.Add(new LineVm { Text = text, Brush = BrushOf(text) });
        BubbleLines.ItemsSource = vm;
        Bubble.Visibility = Visibility.Visible;
        BubbleTail.Visibility = Visibility.Visible;
    }

    internal sealed class LineVm
    {
        public string Text { get; set; } = "";
        public Brush Brush { get; set; } = Brushes.Black;
    }

    private static Brush BrushOf(string line)
    {
        var c = string.IsNullOrEmpty(line) ? ' ' : line[0];
        return c switch
        {
            '◆' => new SolidColorBrush(Color.FromRgb(0x7E, 0xB8, 0xE8)),
            '★' => new SolidColorBrush(Color.FromRgb(0xD6, 0xB6, 0x60)),
            '◇' => new SolidColorBrush(Color.FromRgb(0xA0, 0x88, 0xDC)),
            '○' => new SolidColorBrush(Color.FromRgb(0x60, 0xC4, 0xB8)),
            '⚠' => new SolidColorBrush(Color.FromRgb(0xE8, 0x98, 0x58)),
            '·' => new SolidColorBrush(Color.FromRgb(0x55, 0x5E, 0x70)),
            _ => new SolidColorBrush(Color.FromRgb(0x1C, 0x24, 0x30)),
        };
    }

    // ── 动画：呼吸 / 浮动 / 眨眼 ─────────────────────────────────
    private void StartAnim()
    {
        if (_anim.IsEnabled) return;
        _anim.Tick += (_, _) =>
        {
            _t += _anim.Interval.TotalSeconds;
            UpdateAnim();
        };
        _anim.Start();
        SetState(PetState.Idle);
    }

    private void UpdateAnim()
    {
        double y, scale;
        switch (_state)
        {
            case PetState.Thinking:   // 快速浮动
                y = Math.Sin(_t * 5.0) * 6;
                scale = 1.0;
                break;
            case PetState.Waiting:    // 慢速左右晃
                y = Math.Sin(_t * 1.2) * 3;
                scale = 1.0 + Math.Sin(_t * 1.2) * 0.02;
                break;
            case PetState.Done:       // 弹性一下
                y = Math.Sin(_t * 3.0) * 4;
                scale = 1.0 + Math.Abs(Math.Sin(_t * 3.0)) * 0.05;
                break;
            case PetState.Sleeping:   // 几乎不动
                y = Math.Sin(_t * 0.6) * 1.5;
                scale = 1.0;
                break;
            default:                  // Idle：轻微呼吸
                y = Math.Sin(_t * 1.6) * 3;
                scale = 1.0 + Math.Sin(_t * 1.6) * 0.015;
                break;
        }

        BodyOffset.Y = y;
        PetBody.RenderTransformOrigin = new Point(0.5, 0.5);
        if (PetBody.RenderTransform is System.Windows.Media.TransformGroup)
        {
            // 只改 Translate，缩放交给下面的 ScaleTransform
        }
        if (PetBody.RenderTransform is TranslateTransform tt) tt.Y = y;

        if (PetBody.LayoutTransform is not ScaleTransform st)
        {
            st = new ScaleTransform(1, 1);
            PetBody.LayoutTransform = st;
        }
        st.ScaleX = scale;
        st.ScaleY = scale;

        Glow.Opacity = 0.45 + Math.Sin(_t * 1.6) * 0.15;

        // 眨眼：每 ~4 秒闭 0.12 秒（睡眠时一直闭）
        bool shouldClose = _state == PetState.Sleeping
            || (_t % 4.0) < 0.12;
        if (shouldClose != _eyeClosed)
        {
            _eyeClosed = shouldClose;
            EyeL.Visibility = shouldClose ? Visibility.Collapsed : Visibility.Visible;
            EyeR.Visibility = shouldClose ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
