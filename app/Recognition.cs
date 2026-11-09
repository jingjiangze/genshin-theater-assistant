// ─────────────────────────────────────────────────────────────────────────────
// 本文件包含对 BetterGI（https://github.com/babalae/better-genshin-impact）实现的
// 参考或衍生。原版权：Copyright (c) babalae 及 BetterGI contributors（GPL-3.0）。
// 本项目在 WinForms + .NET 8 下重写/改写，修改时间 2026-11。
// 本文件以 GPL-3.0 分发，全文见仓库根 LICENSE，来源说明见 NOTICE.md。
// ─────────────────────────────────────────────────────────────────────────────
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
    ///   preferPid 命中 +200（自检用：真游戏在跑时也能锁定假窗口）、
    ///   进程名像原神 +100、UnityWndClass +50、标题完全等于 needle +40、
    ///   标题只是把 needle 当子串且不长 +10；客户区小于 640x360 直接不算。
    /// 最低分线 40：只有"标题包含"这一条弱证据不算找到 ——
    /// 否则游戏没开时，浏览器开着原神攻略页就会被当成游戏窗口（审计实测抓到过）。
    /// </summary>
    public static IntPtr Find(string titleNeedle, int preferPid = 0)
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
            if (preferPid != 0 && pid == (uint)preferPid) score += 200;
            if (LooksLikeGameProcess(pid)) score += 100;
            if (string.Equals(cls.ToString(), "UnityWndClass", StringComparison.OrdinalIgnoreCase)) score += 50;
            if (needle.Length > 0)
            {
                if (string.Equals(title, needle, StringComparison.OrdinalIgnoreCase)) score += 40;
                else if (title.Contains(needle, StringComparison.OrdinalIgnoreCase) && title.Length <= needle.Length + 24) score += 10;
            }

            if (score > bestScore) { bestScore = score; best = h; }
            return true;
        }, IntPtr.Zero);

        return bestScore >= 40 ? best : IntPtr.Zero;        // 弱证据不算命中
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

        // 半分辨率预筛：整帧扫描的成本与「帧面积 × 模板面积」成正比，
        // 直接在全分辨率上对每个模板扫 5 次（1 次 + 4 档缩放）在 1080p 下要 30 秒/帧（实测 39s）。
        // 改成先在 1/2 图上粗筛定位，再回到全分辨率只在该点 ±6px 内精确比对。
        using var halfBmp = Resize(frame, 0.5f);
        byte[] h = Gray(halfBmp, out int hw, out int hh);

        foreach (var t in templates)
        {
            byte[] tpl = Gray(t.Bitmap, out int tw, out int th);
            if (tw > fw || th > fh) continue;

            double best = -1;
            int bx = 0, by = 0;
            float usedScale = 1.0f;

            // 1) 半分辨率上试 1.00 与各缩放档，取最优（这里便宜：面积只有 1/4）
            foreach (var s in Scales)
            {
                using var scaledTpl = Resize(t.Bitmap, s * 0.5f);
                byte[] ht = Gray(scaledTpl, out int htw, out int hth);
                if (htw > hw || hth > hh) continue;

                double sc = Coarse(h, hw, hh, ht, htw, hth, 2, 2, out int hx, out int hy);
                if (sc > best) { best = sc; bx = (int)Math.Round(hx * 2.0); by = (int)Math.Round(hy * 2.0); usedScale = s; }
            }
            if (best < 0) continue;

            // 明显不像就跳过（避免为不存在的模板做精确比对）
            if (best < t.Threshold - 0.22) continue;

            // 2) 回全分辨率，在粗筛点附近 ±6px 精确比对（用对应缩放后的模板）
            byte[] fineTpl;
            int ftw, fth;
            if (Math.Abs(usedScale - 1.0f) < 0.001f) { fineTpl = tpl; ftw = tw; fth = th; }
            else
            {
                using var sc2 = Resize(t.Bitmap, usedScale);
                fineTpl = Gray(sc2, out ftw, out fth);
            }

            double bestFine = -1;
            int rx = bx, ry = by;
            for (int y = Math.Max(0, by - 6); y <= Math.Min(fh - fth, by + 6); y++)
            {
                for (int x = Math.Max(0, bx - 6); x <= Math.Min(fw - ftw, bx + 6); x++)
                {
                    long sum = 0;
                    for (int j = 0; j < fth; j++)
                    {
                        int fo = (y + j) * fw + x;
                        int to = j * ftw;
                        for (int i = 0; i < ftw; i++)
                        {
                            int d = f[fo + i] - fineTpl[to + i];
                            sum += d * d;
                        }
                    }
                    double rmse = Math.Sqrt(sum / Math.Max(1.0, ftw * (double)fth));
                    double sc3 = 1.0 - rmse / 255.0;
                    if (sc3 > bestFine) { bestFine = sc3; rx = x; ry = y; }
                }
            }

            if (bestFine >= t.Threshold)
                result.Add(new Hit { Name = t.Name, Score = Math.Round(bestFine, 4), X = rx, Y = ry });
        }

        return result;
    }

    /// <summary>粗扫：位置与像素双重抽样，返回最优分与位置。</summary>
    private static double Coarse(byte[] frame, int fw, int fh, byte[] tpl, int tw, int th,
        int step, int sample, out int bx, out int by)
    {
        double best = -1;
        bx = 0; by = 0;
        int cols = (tw + sample - 1) / sample;
        int rows = (th + sample - 1) / sample;
        double norm = Math.Max(1.0, cols * (double)rows);

        for (int y = 0; y + th <= fh; y += step)
        {
            for (int x = 0; x + tw <= fw; x += step)
            {
                long sum = 0;
                for (int j = 0; j < th; j += sample)
                {
                    int fo = (y + j) * fw + x;
                    int to = j * tw;
                    for (int i = 0; i < tw; i += sample)
                    {
                        int d = frame[fo + i] - tpl[to + i];
                        sum += (long)d * d;
                    }
                }
                double rmse = Math.Sqrt(sum / norm);
                double sc = 1.0 - rmse / 255.0;
                if (sc > best) { best = sc; bx = x; by = y; }
            }
        }
        return best;
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
