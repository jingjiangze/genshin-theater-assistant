// ─────────────────────────────────────────────────────────────────────────────
// 本文件是 BetterGI（https://github.com/babalae/better-genshin-impact）的衍生实现。
// 原版权：Copyright (c) babalae 及 BetterGI contributors（GPL-3.0）。
// 上游文件：BetterGenshinImpact/View/Mask/MaskWindowHost.cs
// 本项目在 WPF + .NET 8 下移植（策略/合并投递照抄，DI/配置/日志依赖剪掉），修改时间 2026-11-09。
// 以 GPL-3.0 分发，全文见仓库根 LICENSE，来源说明见 NOTICE.md。
// ─────────────────────────────────────────────────────────────────────────────
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace TheaterAssistant;

/// <summary>游戏窗口事实（一次采样）：客户区像素、是否前台、是否最小化、是否还抓得到。</summary>
internal readonly struct MaskGameState
{
    public readonly IntPtr Handle;
    public readonly GameWindow.RECT Bounds;   // 客户区，屏幕物理像素
    public readonly bool IsActive;
    public readonly bool IsMinimized;
    public readonly bool IsForegroundOwnedByGameOrUs;

    /// <summary>
    /// 游戏窗口还在不在、还抓不抓得到 —— 只取决于 IsWindowVisible。
    ///
    /// 这里**不能**再 `&amp;&amp; !IsMinimized`：最小化窗口的 WS_VISIBLE 仍然置着，它是"可见但最小化"。
    /// 旧写法把最小化算成"没捕获"，于是 <see cref="MaskPolicy"/> 第一段就直接收起，
    /// 后面的 `IsMinimized → 保持现状` 永远走不到（文档写"保持"、实际是"隐藏"）。
    /// 最小化要单独一路交给策略层判断。
    /// </summary>
    public readonly bool IsCapturing;

    public MaskGameState(IntPtr handle, GameWindow.RECT bounds, bool active, bool minimized, bool fgOwned, bool capturing)
    {
        Handle = handle;
        Bounds = bounds;
        IsActive = active;
        IsMinimized = minimized;
        IsForegroundOwnedByGameOrUs = fgOwned;
        IsCapturing = capturing;
    }

    public static MaskGameState None => new(IntPtr.Zero, default, false, false, false, false);

    public bool HasBounds => Bounds.Right > Bounds.Left && Bounds.Bottom > Bounds.Top;

    public static MaskGameState Read(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return None;

        if (!GameWindow.GetClientRect(hWnd, out var r)) return None;

        var origin = new GameWindow.POINT { X = 0, Y = 0 };
        if (!GameWindow.ClientToScreen(hWnd, ref origin)) return None;

        var bounds = new GameWindow.RECT
        {
            Left = origin.X,
            Top = origin.Y,
            Right = origin.X + (r.Right - r.Left),
            Bottom = origin.Y + (r.Bottom - r.Top),
        };

        GameWindow.GetWindowThreadProcessId(hWnd, out uint gamePid);
        var fg = GameWindow.GetForegroundWindow();
        GameWindow.GetWindowThreadProcessId(fg, out uint fgPid);

        var active = fg != IntPtr.Zero && fgPid == gamePid;
        var fgOwned = active || (fg != IntPtr.Zero && fgPid == (uint)Environment.ProcessId);
        var minimized = Native.IsIconic(hWnd);
        var visible = Native.IsWindowVisible(hWnd);

        return new MaskGameState(hWnd, bounds, active, minimized, fgOwned,
            capturing: visible);
    }

    private static class Native
    {
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hWnd);
    }
}

/// <summary>
/// 遮罩显隐策略（纯函数，便于断言——上游把这段埋在 EvaluateLocked 里，无法单独测）。
/// 返回 null 表示「保持现状」，避免游戏窗口短暂失焦时闪烁。
///
/// 判据顺序（改过一次，别再动）：
///   手动隐藏 → 收起；窗口已不可见（关掉/切走）→ 收起；
///   **最小化 → 保持**（保持的意思是"维持当前显隐"，不是"隐藏"）——最小化的游戏窗口
///   WS_VISIBLE 仍是 true，所以 IsCapturing 为 true，能走到这一条；
///   前台 → 显示；前景是游戏子窗或我们自己 → 保持；切到别的程序 → 收起。
/// </summary>
internal static class MaskPolicy
{
    public static bool? Decide(MaskGameState state, bool manualHidden)
    {
        if (manualHidden) return false;        // 用户手动关掉：优先于一切
        if (!state.IsCapturing) return false;  // 游戏窗口不在了：收起
        if (state.IsMinimized) return null;    // 最小化：保持现状（不要跟着最小化后的假客户区乱跑）
        if (state.IsActive) return true;       // 游戏在前台：显示
        if (state.IsForegroundOwnedByGameOrUs) return null;   // 前景是游戏子窗或我们自己：不闪
        return false;                          // 切到别的程序：收起
    }
}

/// <summary>
/// 遮罩窗口宿主：显隐、置顶、跟随即在这里。
///
/// 照抄上游的核心设计：**调用方只上报事实**（<see cref="Report"/>），
/// 宿主在 UI 线程上算期望状态，变化时才合并投递一次 Apply——
/// 主循环每秒上报一次，窗口属性却不会每帧都写（写 Left/Top 会触发 WM_WINDOWPOSCHANGING）。
/// </summary>
internal sealed class MaskWindowHost
{
    private readonly Func<MaskWindow> _windowFactory;
    private readonly object _lock = new();

    // 期望状态（受 _lock 保护）
    private bool _attached;
    private IntPtr _gameHandle;
    private bool _desiredVisible;
    private GameWindow.RECT _desiredBounds;
    private bool _bringToTopPending;
    private bool _lastGameActive;
    private bool _manualHidden;
    private MaskGameState? _lastReport;

    // 以下只在 UI 线程访问
    private MaskWindow? _window;
    private GameWindow.RECT _appliedBounds;
    private double _appliedScale;          // 上次摆位用的 DPI 缩放（0 = 还没摆过）
    private DispatcherOperation? _pending;
    private MaskWindow? _created;
    private HwndSource? _hookedSource;
    private HwndSourceHook? _dpiHook;

    public MaskWindowHost(Func<MaskWindow> windowFactory)
        => _windowFactory = windowFactory ?? throw new ArgumentNullException(nameof(windowFactory));

    /// <summary>当前遮罩窗口（未创建时为 null）。只在 UI 线程读。</summary>
    public MaskWindow? Window => _window;

    public bool IsVisible => _window?.IsVisible == true;

    /// <summary>手动隐藏开关（托盘菜单）。</summary>
    public bool ManualHidden
    {
        get { lock (_lock) return _manualHidden; }
        set
        {
            lock (_lock) _manualHidden = value;
            Reevaluate();
        }
    }

    public void Attach(IntPtr gameHandle)
    {
        lock (_lock)
        {
            _attached = true;
            _gameHandle = gameHandle;
            _lastReport = null;
            _lastGameActive = true;
            _desiredVisible = true;
            _desiredBounds = default;
            _bringToTopPending = true;
        }
        RunOnUiThread(Apply);
    }

    /// <summary>把最新的 ui.json 应用到已存在的遮罩窗（设置窗保存后调用）。</summary>
    public void ApplyPrefs(UiPrefs prefs)
    {
        if (prefs == null) return;
        try { _window?.ApplyPrefs(prefs); } catch { }
    }

    public void Detach()
    {
        lock (_lock)
        {
            _attached = false;
            _lastReport = null;
            _desiredVisible = false;
            _bringToTopPending = false;
        }
        RunOnUiThread(Apply);
    }

    public void Close()
    {
        lock (_lock)
        {
            _attached = false;
            _desiredVisible = false;
        }

        RunOnUiThread(() =>
        {
            var w = _window;
            _window = null;
            _appliedBounds = default;
            _appliedScale = 0;
            UnhookDpiChanges();
            try { w?.Close(); } catch { }
        });
    }

    /// <summary>主循环每帧上报一次游戏窗口事实。</summary>
    public void Report(MaskGameState state)
    {
        bool changed;
        lock (_lock)
        {
            if (!_attached) return;
            _lastReport = state;

            if (state.Handle != _gameHandle && state.Handle != IntPtr.Zero)
            {
                _gameHandle = state.Handle;
                changed = true;
            }
            else changed = false;

            changed |= EvaluateLocked(state);
        }

        if (changed) RequestApply();
    }

    /// <summary>强制显示（布局编辑/排查用）。</summary>
    public void SetForcedVisible(bool on)
    {
        lock (_lock)
        {
            _manualHidden = !on;
            if (on) _desiredVisible = true;
        }
        Reevaluate();
    }

    /// <summary>确保窗口已创建（UI 线程）。窗口创建后可以先不 Show，内容照常写入。</summary>
    public MaskWindow? EnsureWindow()
    {
        if (_window != null) return _window;
        if (_created != null) return _created;

        var window = _windowFactory();
        window.Closed += (_, _) =>
        {
            UnhookDpiChanges();
            if (ReferenceEquals(_window, window)) { _window = null; _appliedBounds = default; _appliedScale = 0; }
            if (ReferenceEquals(_created, window)) _created = null;
        };
        _created = window;
        return window;
    }

    private void Reevaluate()
    {
        bool changed = false;
        lock (_lock)
        {
            if (_attached && _lastReport is { } report)
            {
                changed = EvaluateLocked(report);
            }
            else
            {
                var forced = !_manualHidden;
                changed = forced != _desiredVisible;
                _desiredVisible = forced;
            }
        }

        if (changed) RequestApply();
    }

    private bool EvaluateLocked(MaskGameState state)
    {
        var changed = false;

        if (MaskPolicy.Decide(state, _manualHidden) is { } visible && visible != _desiredVisible)
        {
            _desiredVisible = visible;
            changed = true;
        }

        // 游戏从后台回到前台时置顶一次（否则可能被别的置顶窗压住）
        if (state.IsActive && !_lastGameActive && _desiredVisible)
        {
            _bringToTopPending = true;
            changed = true;
        }

        if (!state.IsMinimized) _lastGameActive = state.IsActive;

        if (!state.IsMinimized && state.HasBounds && !SameBounds(state.Bounds, _desiredBounds))
        {
            // 最小化时 GetClientRect/ClientToScreen 给的是"还原尺寸 + 屏外坐标"，跟过去遮罩会跑到 -32000。
            // 保持现状 = 停在最小化之前的位置，所以只在非最小化时接受新矩形。
            _desiredBounds = state.Bounds;
            changed = true;
        }

        return changed;
    }

    private static bool SameBounds(GameWindow.RECT a, GameWindow.RECT b)
        => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    private void RequestApply()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) { Apply(); return; }
        if (dispatcher.HasShutdownStarted) return;

        _pending ??= dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            _pending = null;
            Apply();
        }));
    }

    private static void RunOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) { action(); return; }
        if (!dispatcher.HasShutdownStarted) dispatcher.Invoke(action);
    }

    private void Apply()
    {
        bool visible, bringToTop;
        GameWindow.RECT bounds;
        IntPtr gameHandle;

        lock (_lock)
        {
            visible = _attached && _desiredVisible;
            bringToTop = _bringToTopPending;
            bounds = _desiredBounds;
            gameHandle = _gameHandle;
            _bringToTopPending = false;
        }

        try
        {
            if (visible)
            {
                var window = EnsureWindow();
                if (window == null) return;

                var hasBounds = bounds.Right > bounds.Left && bounds.Bottom > bounds.Top;

                // 先把位置摆好再 Show。`Attach` 只写了 _desiredVisible，_desiredBounds 还是 default：
                // 那时 Show 会让窗口按 XAML 的 1280×720 在屏幕左上角闪一帧（1280×720@100% 尤其明显）。
                // 没有客户区就干脆不显示，等第一次 Report 带来客户区再显示。
                if (hasBounds && PositionIfNeeded(window, gameHandle, bounds)) bringToTop = true;

                if (!ReferenceEquals(_window, window)) _window = window;

                if (!window.IsVisible)
                {
                    if (!hasBounds) return;

                    window.Show();

                    // Show 之后 HWND 已建、PerMonitorV2 的真实 DPI 才可用，按真实缩放再摆一次
                    PositionIfNeeded(window, gameHandle, bounds, force: true);
                }

                HookDpiChanges(window);

                if (bringToTop) BringToTop(window);
            }
            else if (_window is { IsVisible: true } window)
            {
                window.Hide();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or TaskCanceledException)
        {
            // 程序退出过程中窗口可能已关闭；不把异常抛回主循环
            App.LogCrash(ex, "MaskWindowHost.Apply");
        }
    }

    /// <summary>
    /// 按游戏客户区摆位；bounds 与 DPI 都没变就跳过——
    /// 写 Left/Top 会触发 WM_WINDOWPOSCHANGING，主循环每秒上报一次，没必要每帧都写。
    /// </summary>
    private bool PositionIfNeeded(Window window, IntPtr gameHandle, GameWindow.RECT bounds,
        bool force = false, double? scaleOverride = null)
    {
        var scale = scaleOverride is { } s && s > 0 ? s : DpiScaleFor(gameHandle);

        if (!force && _appliedScale > 0
            && Math.Abs(scale - _appliedScale) < 1e-9
            && SameBounds(bounds, _appliedBounds))
            return false;

        var dip = ToDipRect(bounds, scale);
        window.Left = dip.Left;
        window.Top = dip.Top;
        window.Width = dip.Width;
        window.Height = dip.Height;

        _appliedBounds = bounds;
        _appliedScale = scale;
        return true;
    }

    /// <summary>
    /// 物理像素客户区 → WPF 的 Left/Top/Width/Height（DIP）。
    /// 纯函数（不碰窗口），自检直接断言 96/120/144/192 DPI 下的期望值。
    /// </summary>
    internal static Rect ToDipRect(GameWindow.RECT bounds, double scale)
    {
        if (scale <= 0) scale = 1.0;
        return new Rect(
            bounds.Left / scale,
            bounds.Top / scale,
            (bounds.Right - bounds.Left) / scale,
            (bounds.Bottom - bounds.Top) / scale);
    }

    /// <summary>
    /// 跟随 DPI 变更。PerMonitorV2 下窗口被拖到另一块缩放不同的显示器时，系统会发 WM_DPICHANGED；
    /// 只让 WPF 自己改 Width/Height 不够——游戏客户区的**物理**矩形没变，换算后的 DIP 尺寸变了，
    /// 必须重新摆位，否则遮罩与游戏画面错开（150% 屏上会差出一整块）。
    /// </summary>
    private void HookDpiChanges(MaskWindow window)
    {
        var hWnd = new WindowInteropHelper(window).Handle;
        if (hWnd == IntPtr.Zero) return;

        var source = HwndSource.FromHwnd(hWnd);
        if (source == null || ReferenceEquals(source, _hookedSource)) return;

        UnhookDpiChanges();
        _dpiHook = OnWindowMessage;
        source.AddHook(_dpiHook);
        _hookedSource = source;
    }

    private void UnhookDpiChanges()
    {
        try
        {
            if (_hookedSource != null && _dpiHook != null) _hookedSource.RemoveHook(_dpiHook);
        }
        catch { }
        _hookedSource = null;
        _dpiHook = null;
    }

    private IntPtr OnWindowMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_DPICHANGED = 0x02E0;
        if (msg != WM_DPICHANGED) return IntPtr.Zero;

        // wParam 高 16 位 = 新 DPI（与 X/Y 同值）；这就是"更新缓存 scale"的来源
        var newDpi = (wParam.ToInt64() >> 16) & 0xFFFF;
        var scale = newDpi > 0 ? newDpi / 96.0 : _appliedScale;

        GameWindow.RECT bounds;
        IntPtr gameHandle;
        lock (_lock)
        {
            bounds = _desiredBounds;
            gameHandle = _gameHandle;
        }

        if (_window is { } window && bounds.Right > bounds.Left && bounds.Bottom > bounds.Top)
        {
            // 按新 scale 重新把遮罩摆回游戏客户区（物理矩形不变、DIP 矩形变了）
            PositionIfNeeded(window, gameHandle, bounds, force: true, scaleOverride: scale);
            BringToTop(window);
        }

        // WPF 自己也会处理 WM_DPICHANGED（可能按 lParam 的建议矩形再调一次窗口），
        // 所以再投递一次 Normal 优先级的 Apply（早于渲染）：缓存失效后按游戏客户区重新摆位，
        // 保证这条消息处理完窗口最终落在游戏客户区，而不是系统建议的位置。
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.HasShutdownStarted)
        {
            dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                _appliedScale = 0;
                Apply();
            }));
        }

        handled = false;
        return IntPtr.Zero;
    }

    private static void BringToTop(Window window)
    {
        var hWnd = new WindowInteropHelper(window).Handle;
        if (hWnd == IntPtr.Zero) return;
        Native.SetWindowPos(hWnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// <summary>游戏窗口所在显示器的 DPI 缩放（物理像素 → DIP）。</summary>
    internal static double DpiScaleFor(IntPtr hWnd)
    {
        try
        {
            if (hWnd != IntPtr.Zero)
            {
                var dpi = Native.GetDpiForWindow(hWnd);
                if (dpi > 0) return dpi / 96.0;
            }
        }
        catch (EntryPointNotFoundException) { }
        catch (DllNotFoundException) { }

        return 1.0;
    }

    private static class Native
    {
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        internal static extern uint GetDpiForWindow(IntPtr hWnd);

        internal static readonly IntPtr HWND_TOPMOST = new(-1);
        internal const uint SWP_NOSIZE = 0x0001;
        internal const uint SWP_NOMOVE = 0x0002;
        internal const uint SWP_NOACTIVATE = 0x0010;
    }
}
