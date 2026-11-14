// ─────────────────────────────────────────────────────────────────────────────
// 本文件包含对 BetterGI（https://github.com/babalae/better-genshin-impact）实现的
// 参考或衍生。原版权：Copyright (c) babalae 及 BetterGI contributors（GPL-3.0）。
// 出处：BetterGenshinImpact/Helpers/PrimaryScreen.cs 与 DpiHelper.cs
// 本项目改写为无第三方 P/Invoke 依赖（原用 Vanara.PInvoke），修改时间 2026-11。
// 本文件以 GPL-3.0 分发，全文见仓库根 LICENSE，来源说明见 NOTICE.md。
// ─────────────────────────────────────────────────────────────────────────────
using System.Drawing;
using System.Runtime.InteropServices;

namespace TheaterAssistant;

/// <summary>
/// 屏幕与 DPI 度量 —— 照抄 BetterGI 的关键口径：**拿物理像素，不拿逻辑像素**。
///
/// 为什么必须这样（我们踩过的坑）：
///   WinForms 的 `Screen.PrimaryScreen.Bounds` 在系统缩放 125%/150%/175% 下返回的是
///   **被 DPI 虚拟化的逻辑尺寸**（例如 2560×1440 @175% 会报 1463×823）。
///   用它给悬浮窗定位，窗口会落在错误的位置、甚至跑到可见区域之外 ——
///   用户看到的现象就是"窗口没出现"。
///   BetterGI 用 `GetDeviceCaps(hdc, HORZRES/VERTRES)` 直接问显示设备要物理像素，规避此问题。
///   纵向缩放则用 `GetDpiForMonitor(MDT_EFFECTIVE_DPI)`（Win8.1+），拿不到时回落到 LOGPIXELSY。
/// </summary>
internal static class ScreenMetrics
{
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr hdc, int index);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    private const int HORZRES = 8;
    private const int VERTRES = 10;
    private const int LOGPIXELSX = 88;
    private const int LOGPIXELSY = 90;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;   // 整块屏（含任务栏区域），物理像素，可为负
        public RECT rcWork;      // 工作区（排除任务栏），物理像素，可为负
        public uint dwFlags;
    }

    /// <summary>主显示器物理分辨率（不受 DPI 虚拟化影响）。</summary>
    public static Size PhysicalSize
    {
        get
        {
            var hdc = GetDC(IntPtr.Zero);
            try
            {
                return new Size(GetDeviceCaps(hdc, HORZRES), GetDeviceCaps(hdc, VERTRES));
            }
            finally { ReleaseDC(IntPtr.Zero, hdc); }
        }
    }

    /// <summary>当前 DPI 缩放（1.0 = 96 DPI）。优先 GetDpiForMonitor，失败回落 GetDeviceCaps。</summary>
    public static float ScaleFactor(IntPtr hWnd = default)
    {
        try
        {
            // Win8.1+ 才有 GetDpiForMonitor；取窗口所在显示器更准
            if (Environment.OSVersion.Version >= new Version(6, 3))
            {
                var mon = hWnd != IntPtr.Zero
                    ? MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST)
                    : MonitorFromWindow(IntPtr.Zero, MONITOR_DEFAULTTONEAREST);
                if (GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, out _, out var dpiY) == 0 && dpiY > 0)
                    return dpiY / 96f;
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }

        var hdc = GetDC(IntPtr.Zero);
        try
        {
            var dpi = GetDeviceCaps(hdc, LOGPIXELSY);
            return dpi > 0 ? dpi / 96f : 1f;
        }
        finally { ReleaseDC(IntPtr.Zero, hdc); }
    }

    /// <summary>把尺寸/坐标钳制在**主显示器**物理屏幕内（旧口径，WinForms 版 OverlayForm 仍用）。</summary>
    public static Point ClampToScreen(Point p, Size windowSize)
    {
        var screen = PhysicalSize;
        if (screen.Width <= 0 || screen.Height <= 0) return p;
        int maxX = Math.Max(0, screen.Width - Math.Min(windowSize.Width, screen.Width));
        int maxY = Math.Max(0, screen.Height - Math.Min(windowSize.Height, screen.Height));
        return new Point(Math.Max(0, Math.Min(p.X, maxX)), Math.Max(0, Math.Min(p.Y, maxY)));
    }

    // ── 多显示器：按显示器取工作区 ──────────────────────────────────────────
    // PhysicalSize 用 GetDC(NULL)+HORZRES/VERTRES，**只代表主显示器**；
    // 拿它给副屏上的悬浮窗做边界，副屏在主屏左侧/上方时（rcWork.X/Y 为负）
    // Math.Max(0, …) 会把窗口硬拽回主屏。这里改用 MonitorFromWindow/MonitorFromPoint
    // 拿该显示器自己的 rcWork（物理像素），负坐标原样保留。

    /// <summary>
    /// 窗口所在显示器的工作区（rcWork，物理像素，已排除任务栏）。
    /// 副屏在主屏左侧/上方时 X/Y 为负 —— 调用方必须原样使用，不得归零。
    /// 句柄为 0 或信息拿不到时退回主屏全屏（0,0,PhysicalSize），与旧行为一致。
    /// </summary>
    public static (int X, int Y, int W, int H) WorkAreaOf(IntPtr hwnd)
    {
        try
        {
            var mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (mon != IntPtr.Zero && TryGetWorkArea(mon, out var work))
                return (work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top);
        }
        catch { }

        return PrimaryFallback();
    }

    /// <summary>包含该屏幕点（物理像素）的显示器工作区。拖动时用指针位置定位，跨屏才不会被边界卡住。</summary>
    public static (int X, int Y, int W, int H) WorkAreaOf(Point screenPoint)
    {
        try
        {
            var pt = new POINT { X = screenPoint.X, Y = screenPoint.Y };
            var mon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
            if (mon != IntPtr.Zero && TryGetWorkArea(mon, out var work))
                return (work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top);
        }
        catch { }

        return PrimaryFallback();
    }

    /// <summary>物理像素工作区 → DIP 工作区（除以该显示器缩放）。原点可为负。</summary>
    public static (int X, int Y, int W, int H) WorkAreaDip((int X, int Y, int W, int H) physical, double scale)
    {
        if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale)) scale = 1.0;
        return ((int)Math.Round(physical.X / scale),
                (int)Math.Round(physical.Y / scale),
                (int)Math.Round(physical.W / scale),
                (int)Math.Round(physical.H / scale));
    }

    /// <summary>
    /// 把窗口左上角（DIP）钳制在给定工作区（DIP，可含负原点）内。
    /// 与 <see cref="ClampToScreen"/> 的区别：① 按调用方所在显示器而不是主屏 ② 允许负坐标 ③ 单位是 DIP。
    /// </summary>
    public static Point ClampToAreaDip(Point pDip, Size windowDip, (int X, int Y, int W, int H) area)
    {
        if (area.W <= 0 || area.H <= 0) return pDip;
        int maxX = area.X + Math.Max(0, area.W - Math.Min(windowDip.Width, area.W));
        int maxY = area.Y + Math.Max(0, area.H - Math.Min(windowDip.Height, area.H));
        return new Point(
            Math.Max(area.X, Math.Min(pDip.X, maxX)),
            Math.Max(area.Y, Math.Min(pDip.Y, maxY)));
    }

    private static bool TryGetWorkArea(IntPtr hMonitor, out RECT work)
    {
        work = default;
        try
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(hMonitor, ref info)) return false;
            work = info.rcWork;
            return true;
        }
        catch { return false; }
    }

    /// <summary>主显示器全屏兜底（拿不到显示器信息时用，保持旧行为：原点 0,0）。</summary>
    private static (int X, int Y, int W, int H) PrimaryFallback()
    {
        try
        {
            var s = PhysicalSize;
            if (s.Width > 0 && s.Height > 0) return (0, 0, s.Width, s.Height);
        }
        catch { }
        return (0, 0, 0, 0);
    }
}
