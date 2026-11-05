using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace TheaterAssistant;

/// <summary>只做窗口定位：EnumWindows + GetClientRect，不碰游戏进程。</summary>
internal static class GameWindow
{
    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] internal static extern bool GetClientRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] internal static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int X, Y; }

    /// <summary>游戏进程名特征（b服/国际服/云原神都覆盖）。</summary>
    private static readonly string[] GameProcessHints = { "yuanshen", "genshin", "yuanshencloud", "cloudgenshin" };

    private static bool LooksLikeGameProcess(uint pid)
    {
        try
        {
            var name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
            if (string.IsNullOrEmpty(name)) return false;
            name = name.ToLowerInvariant();
            foreach (var hint in GameProcessHints)
                if (name.Contains(hint, StringComparison.Ordinal)) return true;
        }
        catch { }
        return false;
    }

    /// <summary>
    /// 按多个信号打分选窗口，而不是"第一个标题包含就算"：
    ///   进程名像原神（YuanShen/Genshin…） +100、UnityWndClass +50、标题完全等于 needle +40、
    ///   标题只是把 needle 当子串且不长 +10；客户区小于 640x360 直接不算。
    /// 之前的宽松匹配会把"浏览器开着原神攻略页""聊天窗口标题提到原神"当成游戏窗口。
    /// </summary>
    public static IntPtr Find(string titleNeedle)
    {
        IntPtr best = IntPtr.Zero;
        int bestScore = 0;
        var needle = (titleNeedle ?? "").Trim();

        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;

            GetWindowThreadProcessId(h, out uint pid);
            var cls = new StringBuilder(256);
            GetClassName(h, cls, 256);
            var sb = new StringBuilder(512);
            GetWindowText(h, sb, 512);
            var title = sb.ToString().Trim();

            GetClientRect(h, out var r);
            int w = r.Right - r.Left, hh = r.Bottom - r.Top;
            if (w < 640 || hh < 360) return true;          // 太小，不可能是游戏画面

            int score = 0;
            if (LooksLikeGameProcess(pid)) score += 100;
            if (cls.ToString() == "UnityWndClass") score += 50;
            if (needle.Length > 0)
            {
                if (string.Equals(title, needle, StringComparison.OrdinalIgnoreCase)) score += 40;
                else if (title.Contains(needle, StringComparison.OrdinalIgnoreCase) && title.Length <= needle.Length + 24) score += 10;
            }

            if (score > bestScore) { bestScore = score; best = h; }
            return true;
        }, IntPtr.Zero);

        return bestScore > 0 ? best : IntPtr.Zero;
    }

    /// <summary>窗口归属进程（用于诊断"到底抓到了谁的窗口"）。</summary>
    public static int ProcessIdOf(IntPtr hWnd)
    {
        GetWindowThreadProcessId(hWnd, out uint pid);
        return (int)pid;
    }
}

/// <summary>GDI 抓取窗口客户区像素（等价 BetterGI 的 BitBlt 后端，不做任何注入）。</summary>
internal static class ScreenCapture
{
    public static Bitmap CaptureClient(IntPtr hWnd)
    {
        if (!GameWindow.GetClientRect(hWnd, out var r)) return null;
        int w = r.Right - r.Left;
        int h = r.Bottom - r.Top;
        if (w < 40 || h < 40) return null;

        var origin = new GameWindow.POINT { X = 0, Y = 0 };
        if (!GameWindow.ClientToScreen(hWnd, ref origin)) return null;

        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(origin.X, origin.Y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
        return bmp;
    }
}

/// <summary>
/// 灰度模板匹配（与 PowerShell 版同一算法：步长粗扫 + 全分辨率细化）。
/// 返回每个模板的全局最优分数；只在分数过阈值时视为命中。
/// </summary>
internal static class Matcher
{
    public sealed class Hit
    {
        public string Name;
        public double Score;
        public int X, Y;
    }

    // 常用分辨率候选缩放：模板只需一份，运行时自动尝试这几档
    private static readonly float[] Scales = { 1.00f, 0.90f, 1.12f, 1.25f };

    public static List<Hit> Scan(Bitmap frame, IEnumerable<TemplateEntry> templates, int step = 2)
    {
        var result = new List<Hit>();
        if (frame == null) return result;

        byte[] f = Gray(frame, out int fw, out int fh);

        foreach (var t in templates)
        {
            byte[] tpl = Gray(t.Bitmap, out int tw, out int th);
            if (tw > fw || th > fh) continue;

            double best = double.MaxValue;
            int bx = 0, by = 0;

            for (int y = 0; y + th <= fh; y += step)
            {
                for (int x = 0; x + tw <= fw; x += step)
                {
                    double sum = 0;
                    for (int j = 0; j < th; j += 2)
                    {
                        int fo = (y + j) * fw + x;
                        int to = j * tw;
                        for (int i = 0; i < tw; i += 2)
                        {
                            double d = f[fo + i] - tpl[to + i];
                            sum += d * d;
                        }
                    }
                    if (sum < best) { best = sum; bx = x; by = y; }
                }
            }

            // 细化：全分辨率，避免与粗扫抽样口径混比
            int rx = bx, ry = by;
            double bestFine = double.MaxValue;
            int y0 = Math.Max(0, by - step), y1 = Math.Min(fh - th, by + step);
            int x0 = Math.Max(0, bx - step), x1 = Math.Min(fw - tw, bx + step);
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    double sum = 0;
                    for (int j = 0; j < th; j++)
                    {
                        int fo = (y + j) * fw + x;
                        int to = j * tw;
                        for (int i = 0; i < tw; i++)
                        {
                            double d = f[fo + i] - tpl[to + i];
                            sum += d * d;
                        }
                    }
                    if (sum < bestFine) { bestFine = sum; rx = x; ry = y; }
                }
            }

            double rmse = Math.Sqrt(bestFine / Math.Max(1.0, tw * th));
            double score = 1.0 - rmse / 255.0;

            // 单尺度未过阈值时，换几个缩放再试（模板只做一份，兼容 90%/112%/125% 等缩放）
            if (score < t.Threshold)
            {
                foreach (var s in Scales)
                {
                    if (Math.Abs(s - 1.0f) < 0.001f) continue;
                    using var scaled = Resize(t.Bitmap, s);
                    byte[] st = Gray(scaled, out int sw, out int sh);
                    if (sw > fw || sh > fh) continue;

                    double sb = double.MaxValue; int sx = 0, sy = 0;
                    for (int y = 0; y + sh <= fh; y += step)
                        for (int x = 0; x + sw <= fw; x += step)
                        {
                            double sum = 0;
                            for (int j = 0; j < sh; j += 2)
                            {
                                int fo = (y + j) * fw + x, to = j * sw;
                                for (int i = 0; i < sw; i += 2)
                                {
                                    double d = f[fo + i] - st[to + i];
                                    sum += d * d;
                                }
                            }
                            if (sum < sb) { sb = sum; sx = x; sy = y; }
                        }

                    double sRmse = Math.Sqrt(sb / Math.Max(1.0, (sw / 2) * (sh / 2)));
                    double sScore = 1.0 - sRmse / 255.0;
                    if (sScore > score) { score = sScore; rx = sx; ry = sy; }
                }
            }

            if (score >= t.Threshold)
            {
                result.Add(new Hit { Name = t.Name, Score = Math.Round(score, 4), X = rx, Y = ry });
            }
        }

        return result;
    }

    private static Bitmap Resize(Bitmap src, float scale)
    {
        int w = Math.Max(8, (int)Math.Round(src.Width * scale));
        int h = Math.Max(8, (int)Math.Round(src.Height * scale));
        var dst = new Bitmap(w, h);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(src, 0, 0, w, h);
        return dst;
    }

    private static byte[] Gray(Bitmap src, out int w, out int h)
    {
        w = src.Width;
        h = src.Height;
        var bytes = new byte[w * h];

        using var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.DrawImage(src, 0, 0, w, h);
        }

        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            int stride = data.Stride;
            var buf = new byte[stride * h];
            Marshal.Copy(data.Scan0, buf, 0, buf.Length);
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 3;
                    bytes[y * w + x] = (byte)((buf[i] * 29 + buf[i + 1] * 150 + buf[i + 2] * 77) >> 8);
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        return bytes;
    }
}
