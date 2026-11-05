using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace TheaterAssistant;

/// <summary>
/// 真机模拟实测：不开游戏，也能把「找窗口 → GDI 抓客户区 → 模板匹配 → OCR → 状态判定」整条真链路跑一遍。
///
///   --fakegame &lt;png&gt; [--size 1920x1080] [--title 原神]   起一个标题可被 Find 命中的假游戏窗口（手动观察用）
///   --probe-live [--size ...] [--frames N]              直接找真窗口/假窗口，抓 N 帧出诊断报告
///   --selftest-live [--png ...] [--size ...]            自动：起假窗口 → 抓帧 → 断言 → 退出码
///
/// 退出码：0=通过，1=断言失败，4=抓屏为空白（没有桌面会话，跳过而不是失败）。
/// 注意：这是**模拟**真机——验证的是窗口发现、客户区矩形、GDI 抓屏、识别与判定这条链路，
/// 不等于在真实游戏客户端里跑过（后者还需要独占/窗口化全屏等环境差异确认）。
/// </summary>
internal static class LiveProbe
{
    internal const string DefaultTitle = "原神";

    /// <summary>假游戏窗口：无边框、置顶、把一张帧铺满客户区；标题可被 GameWindow.Find 的标题兜底命中。</summary>
    internal sealed class FakeGameForm : Form
    {
        private readonly Bitmap _frame;

        public FakeGameForm(string png, int w, int h, string title)
        {
            _frame = new Bitmap(png);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(0, 0);
            ClientSize = new Size(w, h);
            Text = title;
            ShowInTaskbar = true;
            TopMost = true;
            BackColor = Color.Black;
            DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (_frame == null) return;
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.DrawImage(_frame, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _frame?.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>手动：把一张帧当"游戏画面"显示出来，直到用户关掉窗口。</summary>
    public static int RunFakeGame(string png, int w, int h, string title)
    {
        Cli.Attach();
        if (!File.Exists(png)) { Cli.Say("找不到图片: " + png); return 2; }
        Cli.Say($"假游戏窗口：{Path.GetFileName(png)} → {w}x{h}，标题「{title}」，关掉窗口结束");
        using var form = new FakeGameForm(png, w, h, string.IsNullOrWhiteSpace(title) ? DefaultTitle : title);
        Application.Run(form);
        return 0;
    }

    /// <summary>抓若干帧做诊断（真窗口或假窗口都行）。</summary>
    public static int RunProbe(string titleNeedle, int frames, string outDir)
    {
        Cli.Attach();
        var lines = new List<string>();
        var needle = string.IsNullOrWhiteSpace(titleNeedle) ? DefaultTitle : titleNeedle;
        var win = GameWindow.Find(needle);
        if (win == IntPtr.Zero)
        {
            Cli.Say($"没找到窗口（标题含「{needle}」或 UnityWndClass）");
            return 2;
        }

        GameWindow.GetClientRect(win, out var cr);
        int cw = cr.Right - cr.Left, ch = cr.Bottom - cr.Top;
        lines.Add($"窗口 0x{win:X} 客户区 {cw}x{ch}");

        var baseDir = AppPaths.Dir;
        var theater = TheaterData.Load(Path.Combine(baseDir, "data", "season29.json"));
        var roster = RosterData.TryLoad(Path.Combine(baseDir, "roster.json"));

        using var ocr = new OcrService();
        ocr.InitializeAsync(Path.Combine(baseDir, "models")).GetAwaiter().GetResult();
        lines.Add("OCR: " + ocr.Status);

        var templates = LoadTemplates(cw);
        lines.Add($"模板 {templates.Count} 条（按宽度 {cw} 选桶）");

        for (int i = 0; i < Math.Max(1, frames); i++)
        {
            using var bmp = ScreenCapture.CaptureClient(win);
            if (bmp == null) { lines.Add($"第 {i + 1} 帧：抓取失败"); continue; }

            var hits = Matcher.Scan(bmp, templates);
            var text = ocr.RecognizeAsync(bmp).GetAwaiter().GetResult() ?? "";
            var advice = TheaterLogic.Evaluate(hits, text, theater, roster);
            string band = null;
            if (!advice.Act.HasValue)
            {
                band = ocr.RecognizeRegionAsync(bmp, new RectangleF(0.28f, 0.82f, 0.46f, 0.14f), 3).GetAwaiter().GetResult();
                if (!string.IsNullOrWhiteSpace(band))
                    advice = TheaterLogic.Evaluate(hits, text + " " + band, theater, roster, band);
            }

            var mean = MeanGray(bmp);
            lines.Add($"第 {i + 1} 帧 {bmp.Width}x{bmp.Height} 灰度均值 {mean:0.0}｜{(advice.Kind ?? "unknown")} act={advice.Act?.ToString() ?? "-"}｜" +
                      $"命中 {(hits.Count > 0 ? string.Join(", ", hits.Select(h => $"{h.Name}={h.Score}")) : "无")}");

            try
            {
                var dir = string.IsNullOrWhiteSpace(outDir) ? Path.Combine(baseDir, "samples", "probe") : Path.GetFullPath(outDir);
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, $"probe-{DateTime.Now:HHmmss}-{bmp.Width}x{bmp.Height}.png");
                bmp.Save(file, ImageFormat.Png);
                lines.Add("  已存: " + file);
            }
            catch { }
        }

        foreach (var l in lines) Cli.Say(l);
        return 0;
    }

    /// <summary>
    /// 自动化真机模拟：起假窗口 → 抓帧 → 与源帧比对 → 走识别 → 断言。
    /// 抓屏空白（无桌面会话/被锁屏）时返回 4，调用方按"跳过"处理。
    /// </summary>
    public static int RunSelfTest(string png, int w, int h, string title)
    {
        Cli.Attach();
        var lines = new List<string>();
        int fails = 0;
        void Check(string name, bool ok, string detail)
        {
            lines.Add((ok ? "PASS  " : "FAIL  ") + name + "  " + detail);
            if (!ok) fails++;
        }

        // 没有指定图片时，现场画一张"像剧诗界面"的帧（不依赖任何外部素材）
        string srcPath = png;
        Bitmap owned = null;
        if (string.IsNullOrWhiteSpace(srcPath) || !File.Exists(srcPath))
        {
            owned = SynthFrame(w, h);
            srcPath = Path.Combine(Path.GetTempPath(), $"ta-live-{Guid.NewGuid():N}.png");
            owned.Save(srcPath, ImageFormat.Png);
            lines.Add("未给 --png，现场合成源帧: " + srcPath);
        }

        using var source = new Bitmap(srcPath);
        lines.Add($"源帧 {source.Width}x{source.Height}");

        var needle = string.IsNullOrWhiteSpace(title) ? DefaultTitle : title;
        using var form = new FakeGameForm(srcPath, w, h, needle);
        form.Show();

        try
        {
            // 等窗口真正画出来（无消息循环时手动泵）
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1500) { Application.DoEvents(); Thread.Sleep(40); }

            var win = GameWindow.Find(needle);
            Check("窗口可被发现", win != IntPtr.Zero, win == IntPtr.Zero ? "没找到" : $"0x{win:X}");

            if (win != IntPtr.Zero)
            {
                GameWindow.GetClientRect(win, out var cr);
                int cw = cr.Right - cr.Left, ch = cr.Bottom - cr.Top;
                Check("客户区尺寸", cw == w && ch == h, $"{cw}x{ch}（期望 {w}x{h}）");

                using var bmp = ScreenCapture.CaptureClient(win);
                if (bmp == null)
                {
                    Check("GDI 抓屏", false, "CaptureClient 返回 null");
                }
                else
                {
                    Check("抓帧尺寸", bmp.Width == w && bmp.Height == h, $"{bmp.Width}x{bmp.Height}");
                    var mean = MeanGray(bmp);
                    if (mean < 3.0)
                    {
                        lines.Add($"SKIP  抓屏为空白（灰度均值 {mean:0.00}）：当前没有可捕获的桌面会话");
                        foreach (var l in lines) Cli.Say(l);
                        return 4;
                    }

                    // 抓到的画面必须真的是那张帧（缩放后仍应高度相似）
                    using var probeScaled = new Bitmap(source, new Size(w, h));
                    var gCaptured = TemplateFactory.GrayFrame.From(bmp);
                    var gSource = TemplateFactory.GrayFrame.From(probeScaled);
                    double sim = TemplateFactory.ScoreAt(gCaptured, gSource, 0, 0);
                    Check("抓到的画面=源帧", sim >= 0.95, $"相似度 {sim:0.0000}");

                    // 识别链路
                    using var ocr = new OcrService();
                    ocr.InitializeAsync(Path.Combine(AppPaths.Dir, "models")).GetAwaiter().GetResult();
                    var boxes = ocr.RecognizeBoxesAsync(bmp, null, bmp.Width < 900 ? 2 : 1).GetAwaiter().GetResult();
                    Check("OCR 可用", boxes != null && boxes.Count > 0, boxes == null ? "未就绪" : $"文本框 {boxes.Count} 条");
                    if (boxes != null)
                    {
                        var specs = TemplateFactory.Derive(boxes, bmp.Width, bmp.Height);
                        Check("从抓帧派生规格", specs.Count > 0, specs.Count == 0 ? "无" : string.Join(",", specs.Select(s => s.Name)));
                    }

                    var theater = TheaterData.Load(Path.Combine(AppPaths.Dir, "data", "season29.json"));
                    var roster = RosterData.TryLoad(Path.Combine(AppPaths.Dir, "roster.json"));
                    var hits = Matcher.Scan(bmp, LoadTemplates(bmp.Width));
                    var text = string.Join("\n", (boxes ?? new List<OcrBox>()).Select(b => b.Text));
                    var advice = TheaterLogic.Evaluate(hits, text, theater, roster,
                        ocr.RecognizeRegionAsync(bmp, new RectangleF(0.28f, 0.82f, 0.46f, 0.14f), 3).GetAwaiter().GetResult());
                    lines.Add($"      判定: kind={advice.Kind ?? "unknown"} act={advice.Act?.ToString() ?? "-"} 命中 " +
                              $"{(hits.Count > 0 ? string.Join(", ", hits.Select(h => $"{h.Name}={h.Score}")) : "无")}");
                    Check("判定不为空", !string.IsNullOrWhiteSpace(advice.Header), advice.Header);
                }
            }
        }
        finally
        {
            try { form.Close(); } catch { }
            try { owned?.Dispose(); } catch { }
            try { if (File.Exists(srcPath) && png != srcPath) File.Delete(srcPath); } catch { }
        }

        lines.Add($"通过 {lines.Count(l => l.StartsWith("PASS"))} / 失败 {fails}");
        foreach (var l in lines) Cli.Say(l);
        try
        {
            File.WriteAllText(Path.Combine(AppPaths.Dir, "selftest-live-report.txt"),
                string.Join(Environment.NewLine, lines), Encoding.UTF8);
        }
        catch { }
        return fails == 0 ? 0 : 1;
    }

    private static List<TemplateEntry> LoadTemplates(int width)
    {
        var list = new List<TemplateEntry>();
        var root = Path.Combine(AppPaths.Dir, "templates");
        if (!Directory.Exists(root)) return list;

        var dir = root;
        var best = Directory.GetDirectories(root)
            .Select(d => new { Dir = d, W = ParseWidth(Path.GetFileName(d)) })
            .Where(x => x.W > 0 && Math.Abs(x.W - width) <= Math.Max(32, (int)(width * 0.12)))
            .OrderBy(x => Math.Abs(x.W - width))
            .FirstOrDefault();
        if (best != null) dir = best.Dir;

        foreach (var f in Directory.GetFiles(dir, "*.png"))
        {
            var name = Path.GetFileNameWithoutExtension(f);
            if (name.StartsWith('_')) continue;
            try { list.Add(new TemplateEntry(name, new Bitmap(f), 0.86)); } catch { }
        }
        return list;
    }

    private static int ParseWidth(string name)
    {
        var m = System.Text.RegularExpressions.Regex.Match(name, @"^(\d{3,5})x\d{3,5}$");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    /// <summary>抓帧灰度均值：用来识别"抓到的是黑屏"（无桌面会话）。</summary>
    private static double MeanGray(Bitmap bmp)
    {
        var g = TemplateFactory.GrayFrame.From(bmp);
        long sum = 0;
        for (int i = 0; i < g.Data.Length; i++) sum += g.Data[i];
        return g.Data.Length == 0 ? 0 : sum / (double)g.Data.Length;
    }

    /// <summary>现场合成一张"剧诗大厅"样式的帧：暗底 + 金色文字 + 底部幕次标签。</summary>
    private static Bitmap SynthFrame(int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using (var brush = new LinearGradientBrush(new Rectangle(0, 0, w, h),
                   Color.FromArgb(26, 30, 44), Color.FromArgb(12, 14, 22), 45f))
        {
            g.FillRectangle(brush, 0, 0, w, h);
        }

        var gold = Color.FromArgb(230, 200, 150);
        Draw(g, "幻想真境剧诗", w * 0.03f, h * 0.14f, h * 0.045f, gold);
        Draw(g, "演出准备", w * 0.03f, h * 0.42f, h * 0.040f, gold);
        Draw(g, "第3幕", w * 0.45f, h * 0.80f, h * 0.045f, gold);
        return bmp;
    }

    private static void Draw(Graphics g, string s, float x, float y, float px, Color c)
    {
        Font font = null;
        foreach (var name in new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "SimSun" })
        {
            var f = new Font(name, px, FontStyle.Bold, GraphicsUnit.Pixel);
            if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) { font = f; break; }
            f.Dispose();
        }
        font ??= new Font("Arial", px, FontStyle.Bold, GraphicsUnit.Pixel);
        using (font)
        using (var brush = new SolidBrush(c))
        {
            g.DrawString(s, font, brush, x, y);
        }
    }
}
