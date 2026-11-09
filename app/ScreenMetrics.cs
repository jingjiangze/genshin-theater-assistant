// ─────────────────────────────────────────────────────────────────────────────
// 本文件包含对 BetterGI（https://github.com/babalae/better-genshin-impact）实现的
// 参考或衍生。原版权：Copyright (c) babalae 及 BetterGI contributors（GPL-3.0）。
// 出处：BetterGenshinImpact/Helpers/PrimaryScreen.cs 与 DpiHelper.cs
// 本项目改写为无第三方 P/Invoke 依赖（原用 Vanara.PInvoke），修改时间 2026-11。
// 本文件以 GPL-3.0 分发，全文见仓库根 LICENSE，来源说明见 NOTICE.md。
// ─────────────────────────────────────────────────────────────────────────────
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
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    private const int HORZRES = 8;
    private const int VERTRES = 10;
    private const int LOGPIXELSX = 88;
    private const int LOGPIXELSY = 90;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;

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

    /// <summary>把尺寸/坐标钳制在物理屏幕内，避免窗口被放到可见区域之外。</summary>
    public static Point ClampToScreen(Point p, Size windowSize)
    {
        var screen = PhysicalSize;
        if (screen.Width <= 0 || screen.Height <= 0) return p;
        int maxX = Math.Max(0, screen.Width - Math.Min(windowSize.Width, screen.Width));
        int maxY = Math.Max(0, screen.Height - Math.Min(windowSize.Height, screen.Height));
        return new Point(Math.Max(0, Math.Min(p.X, maxX)), Math.Max(0, Math.Min(p.Y, maxY)));
    }
}
