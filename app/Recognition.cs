// ─────────────────────────────────────────────────────────────────────────────
// 本文件包含对 BetterGI（https://github.com/babalae/better-genshin-impact）实现的
// 参考或衍生。原版权：Copyright (c) babalae 及 BetterGI contributors（GPL-3.0）。
// 本项目在 WinForms + .NET 8 下重写/改写，修改时间 2026-11。
// 本文件以 GPL-3.0 分发，全文见仓库根 LICENSE，来源说明见 NOTICE.md。
// ─────────────────────────────────────────────────────────────────────────────
using System.Drawing;
using System.Drawing.Imaging;
using System.Collections.Generic;
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

/// <summary>
/// 抓窗口客户区像素。优先 BitBlt(窗口 DC)——它取的是**窗口自己的画面**，被别的窗口压住也照样能抓；
/// 取到全黑（某些 D3D 独占交换链会这样）时回落到 CopyFromScreen。
///
/// 与 BetterGI 的 BitBlt 后端同一思路，但代码自写（不搬它的 Fischless.GameCapture，
/// 那个子模块的许可证在离线环境无法确认），也不做它会做的注册表改写。
/// </summary>
internal static class ScreenCapture
{
    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h,
        IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);

    public static Bitmap CaptureClient(IntPtr hWnd)
    {
        if (!GameWindow.GetClientRect(hWnd, out var r)) return null;
        int w = r.Right - r.Left;
        int h = r.Bottom - r.Top;
        if (w < 40 || h < 40) return null;

        var viaBlt = CaptureByBitBlt(hWnd, w, h);
        if (viaBlt != null && !LooksBlank(viaBlt)) return viaBlt;
        viaBlt?.Dispose();

        return CaptureFromScreen(hWnd, w, h);
    }

    /// <summary>BitBlt(窗口 DC)：受窗口自身画面驱动，不依赖屏幕坐标/遮挡关系。</summary>
    private static Bitmap CaptureByBitBlt(IntPtr hWnd, int w, int h)
    {
        IntPtr srcDc = IntPtr.Zero, memDc = IntPtr.Zero, hBmp = IntPtr.Zero, oldObj = IntPtr.Zero;
        try
        {
            srcDc = GetDC(hWnd);
            if (srcDc == IntPtr.Zero) return null;

            memDc = CreateCompatibleDC(srcDc);
            hBmp = CreateCompatibleBitmap(srcDc, w, h);
            if (memDc == IntPtr.Zero || hBmp == IntPtr.Zero) return null;

            oldObj = SelectObject(memDc, hBmp);
            if (!BitBlt(memDc, 0, 0, w, h, srcDc, 0, 0, SRCCOPY | CAPTUREBLT)) return null;

            using var raw = Image.FromHbitmap(hBmp);
            return raw.Clone(new Rectangle(0, 0, w, h), PixelFormat.Format32bppArgb);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (memDc != IntPtr.Zero && oldObj != IntPtr.Zero) SelectObject(memDc, oldObj);
            if (hBmp != IntPtr.Zero) DeleteObject(hBmp);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            if (srcDc != IntPtr.Zero) ReleaseDC(hWnd, srcDc);
        }
    }

    /// <summary>屏幕坐标拷贝（回落路径）：窗口被遮挡时会抓到别人的像素，所以只当兜底。</summary>
    private static Bitmap CaptureFromScreen(IntPtr hWnd, int w, int h)
    {
        var origin = new GameWindow.POINT { X = 0, Y = 0 };
        if (!GameWindow.ClientToScreen(hWnd, ref origin)) return null;

        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(origin.X, origin.Y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
        return bmp;
    }

    /// <summary>抽样判断是否全黑（BitBlt 失败在 D3D 窗口上表现为整幅黑图而不是返回 false）。</summary>
    private static bool LooksBlank(Bitmap bmp)
    {
        int hit = 0, total = 0;
        for (int y = 0; y < bmp.Height; y += 16)
        {
            for (int x = 0; x < bmp.Width; x += 16)
            {
                var c = bmp.GetPixel(x, y);
                total++;
                if (c.R + c.G + c.B > 24) hit++;
            }
        }
        return total == 0 || hit * 100 / total < 2;   // 非黑像素 <2% 视为没抓到
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
        if (frame == null) return new List<Hit>();

        // 引擎选择：优先 OpenCV matchTemplate（SIMD C++，BetterGI 用的就是它）；
        // 原生库不可用时回退自写灰度 RMSE（保证功能不因加速缺失而挂）。
        try
        {
            return ScanCv(frame, templates);
        }
        catch (Exception ex)
        {
            CvFallbackReason = ex.GetType().Name + ": " + ex.Message;
            return ScanRmse(frame, templates, step);
        }
    }

    /// <summary>最后一次 OpenCV 失败原因（诊断用；为空表示 OpenCV 在用）。</summary>
    public static string CvFallbackReason { get; private set; } = "";

    /// <summary>
    /// 参考画布 + OpenCV：整帧按宽度归一到一个固定画布（>1100 缩到 960），
    /// 模板按同样比例缩放后做 CCoeffNormed，坐标再映射回原分辨率。
    /// 为什么这样做：1080p 上自写 RMSE 全帧精比要 2.2s/帧（实测占整帧 74%），
    /// 而 OpenCV 在 960 画布上 8 个模板 x 2 档缩放约 40-80ms，且阈值语义与 BetterGI 一致。
    /// </summary>
    private static List<Hit> ScanCv(Bitmap frame, IEnumerable<TemplateEntry> templates)
    {
        const int CanvasW = 960;
        var k = frame.Width > 1100 ? CanvasW / (double)frame.Width : 1.0;
        var list = new List<Hit>();

        using var src = OpenCvSharp.Extensions.BitmapConverter.ToMat(frame);
        using var gray = new OpenCvSharp.Mat();
        OpenCvSharp.Cv2.CvtColor(src, gray, OpenCvSharp.ColorConversionCodes.BGRA2GRAY);

        OpenCvSharp.Mat canvas = gray;
        try
        {
            if (Math.Abs(k - 1.0) > 1e-6)
            {
                canvas = new OpenCvSharp.Mat();
                OpenCvSharp.Cv2.Resize(gray, canvas,
                    new OpenCvSharp.Size(Math.Max(16, (int)(frame.Width * k)), Math.Max(16, (int)(frame.Height * k))),
                    0, 0, OpenCvSharp.InterpolationFlags.Area);
            }

            // 只试两档缩放：画布已归一，模板与画布本就同基准（多档是给非整数缩放留的余量）
            float[] scales = { 1.00f, 1.10f };

            foreach (var t in templates)
            {
                using var tplSrc = OpenCvSharp.Extensions.BitmapConverter.ToMat(t.Bitmap);
                using var tplGray = new OpenCvSharp.Mat();
                OpenCvSharp.Cv2.CvtColor(tplSrc, tplGray, OpenCvSharp.ColorConversionCodes.BGRA2GRAY);

                // 模板像素基准是 templates\<宽x高> 桶的宽度（如 1920x1080），不是当前帧宽。
                // 正确缩放 = k × (帧宽 / 模板源宽)：
                //   · 模板在 1920 窗口里占 W/1920，画布再把帧乘 k，合起来 tk = (W/1920)×k；
                //   · 旧代码 tk = sc × k，非 1920 帧上会把模板放大 20%–50%（1280 下 0.75 vs 0.5）→ 漏检。
                // SourceWidth==0 表示根目录模板（源宽未知），退化为 tk = sc × k，保持旧行为。
                var tplBase = k * (t.SourceWidth > 0 ? frame.Width / (double)t.SourceWidth : 1.0);

                var roiRect = RoiFor(t.Name, canvas.Cols, canvas.Rows);
                using var region = new OpenCvSharp.Mat(canvas, roiRect);

                Hit best = null;
                foreach (var sc in scales)
                {
                    var tk = sc * tplBase;
                    using var tplScaled = new OpenCvSharp.Mat();
                    if (Math.Abs(tk - 1.0) > 1e-6)
                        OpenCvSharp.Cv2.Resize(tplGray, tplScaled,
                            new OpenCvSharp.Size(Math.Max(4, (int)(tplGray.Width * tk)), Math.Max(4, (int)(tplGray.Height * tk))),
                            0, 0, OpenCvSharp.InterpolationFlags.Area);
                    else
                        tplGray.CopyTo(tplScaled);

                    if (tplScaled.Rows > region.Rows || tplScaled.Cols > region.Cols) continue;

                    using var score = new OpenCvSharp.Mat();
                    OpenCvSharp.Cv2.MatchTemplate(region, tplScaled, score, OpenCvSharp.TemplateMatchModes.CCoeffNormed);
                    OpenCvSharp.Cv2.MinMaxLoc(score, out _, out double maxV, out _, out OpenCvSharp.Point maxLoc);

                    if (maxV < t.Threshold - 0.05) continue;
                    if (best != null && maxV <= best.Score) continue;

                    best = new Hit
                    {
                        Name = t.Name,
                        Score = Math.Round(maxV, 4),
                        X = (int)Math.Round((roiRect.X + maxLoc.X) / k),
                        Y = (int)Math.Round((roiRect.Y + maxLoc.Y) / k),
                    };
                }
                if (best != null && best.Score >= t.Threshold) list.Add(best);
            }
        }
        finally
        {
            if (!ReferenceEquals(canvas, gray)) canvas.Dispose();
        }
        return list;
    }

    /// <summary>
    /// 模板搜索区域（画布坐标的比例）。默认全画布；data/template-rois.json 可覆盖。
    /// 有 ROI 时匹配成本按面积线性下降（BetterGI 的做法：每个识别对象自带区域）。
    /// </summary>
    private static OpenCvSharp.Rect RoiFor(string name, int w, int h)
    {
        var r = Roimap.Lookup(name);
        if (r == null) return new OpenCvSharp.Rect(0, 0, w, h);
        var x = (int)Math.Max(0, Math.Min(w - 2, r[0] * w));
        var y = (int)Math.Max(0, Math.Min(h - 2, r[1] * h));
        var rw = (int)Math.Max(4, Math.Min(w - x, r[2] * w));
        var rh = (int)Math.Max(4, Math.Min(h - y, r[3] * h));
        return new OpenCvSharp.Rect(x, y, rw, rh);
    }

    private static List<Hit> ScanRmse(Bitmap frame, IEnumerable<TemplateEntry> templates, int step)
    {
        // 大分辨率先降到「参考画布」再匹配：半分辨率画布下 act 徽章仍有 66x26。
        if (frame.Width >= 1200)
        {
            using var half = Resize(frame, 0.5f);
            var hitsHalf = ScanAt(half, templates, step, 0.5f);
            foreach (var h in hitsHalf) { h.X *= 2; h.Y *= 2; }
            return hitsHalf;
        }
        return ScanAt(frame, templates, step, 1f);
    }

    private static List<Hit> ScanAt(Bitmap frame, IEnumerable<TemplateEntry> templates, int step, float baseScale)
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
                using var scaledTpl = Resize(t.Bitmap, s * baseScale * 0.5f);
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
                using var sc2 = Resize(t.Bitmap, usedScale * baseScale);
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

    /// <summary>
    /// 按窗口宽度就近加载模板（替代调用方"±12% 命中否则回根目录"的逻辑）。
    ///
    /// 在 <paramref name="templatesRoot"/> 下找 `<宽x高>` 命名的桶，按 |桶宽 − frameWidth| 最小挑一个
    /// （不再限制 ±12%：窗口缩放/DPI 变化时也能落到最近的桶，避免 0 模板静默）；
    /// 同时把该桶的宽高写回 <see cref="TemplateEntry.SourceWidth"/>/<see cref="TemplateEntry.SourceHeight"/>，
    /// 供 ScanCv 按正确基准缩放模板。找不到任何桶时回根目录（旧行为，SourceWidth=0 表示源宽未知）。
    /// 调用方负责 Dispose 返回的 Bitmap。
    /// </summary>
    public static List<TemplateEntry> LoadTemplatesNearest(string templatesRoot, int frameWidth)
    {
        var list = new List<TemplateEntry>();
        if (string.IsNullOrWhiteSpace(templatesRoot) || !Directory.Exists(templatesRoot)) return list;

        var dir = templatesRoot;
        int sourceW = 0, sourceH = 0;
        int bestDelta = int.MaxValue;

        // frameWidth<=0 表示还没拿到窗口尺寸（如窗体初始化首帧）：不猜桶，直接回根目录。
        if (frameWidth > 0)
        {
            foreach (var d in Directory.GetDirectories(templatesRoot))
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    Path.GetFileName(d), @"^(\d{3,5})x(\d{3,5})$");
                if (!m.Success) continue;
                if (!int.TryParse(m.Groups[1].Value, out int w) || !int.TryParse(m.Groups[2].Value, out int h)) continue;

                int delta = Math.Abs(w - frameWidth);
                // 距离相同取更小的桶：结果稳定可复现，不依赖磁盘枚举顺序
                if (delta < bestDelta || (delta == bestDelta && w < sourceW))
                {
                    bestDelta = delta;
                    dir = d;
                    sourceW = w;
                    sourceH = h;
                }
            }
        }

        foreach (var f in Directory.GetFiles(dir, "*.png"))
        {
            var name = Path.GetFileNameWithoutExtension(f);
            if (name.StartsWith('_')) continue;
            try
            {
                list.Add(new TemplateEntry(name, new Bitmap(f), TemplateEntry.ThresholdFor(name))
                {
                    SourceWidth = sourceW,
                    SourceHeight = sourceH,
                });
            }
            catch { }
        }
        return list;
    }
}
