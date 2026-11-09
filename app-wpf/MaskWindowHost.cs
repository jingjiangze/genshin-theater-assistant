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
            capturing: visible && !minimized);
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
/// </summary>
internal static class MaskPolicy
{
    public static bool? Decide(MaskGameState state, bool manualHidden)
    {
        if (manualHidden) return false;        // 用户手动关掉：优先于一切
        if (!state.IsCapturing) return false;  // 游戏不在/最小化：收起
        if (state.IsMinimized) return null;
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
    private DispatcherOperation? _pending;
    private MaskWindow? _created;

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
            if (ReferenceEquals(_window, window)) { _window = null; _appliedBounds = default; }
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

        if (state.HasBounds && !SameBounds(state.Bounds, _desiredBounds))
        {
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

                var wasVisible = window.IsVisible;

                // 先按已知 DPI 摆一次（未 Show 时用系统 DPI，可能不准，避免在 0,0 闪一下）
                if (bounds.Right > bounds.Left && bounds.Bottom > bounds.Top && !SameBounds(bounds, _appliedBounds))
                {
                    Position(window, gameHandle, bounds);
                    _appliedBounds = bounds;
                    bringToTop = true;
                }

                if (!ReferenceEquals(_window, window)) _window = window;
                if (!wasVisible) window.Show();

                // Show 之后再摆一次：此时 HWND 已建、PerMonitorV2 的真实 DPI 已知
                if (!wasVisible && bounds.Right > bounds.Left && bounds.Bottom > bounds.Top)
                {
                    Position(window, gameHandle, bounds);
                    _appliedBounds = bounds;
                }

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

    private static void Position(Window window, IntPtr gameHandle, GameWindow.RECT bounds)
    {
        var scale = DpiScaleFor(gameHandle);
        window.Left = bounds.Left / scale;
        window.Top = bounds.Top / scale;
        window.Width = (bounds.Right - bounds.Left) / scale;
        window.Height = (bounds.Bottom - bounds.Top) / scale;
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
