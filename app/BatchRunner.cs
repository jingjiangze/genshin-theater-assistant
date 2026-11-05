using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace TheaterAssistant;

/// <summary>控制台桥：WinExe 默认没有 stdout，从终端启动时挂到父进程控制台再打印。</summary>
internal static class Cli
{
    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int dwProcessId);
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();

    public static void Attach()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (!AttachConsole(ATTACH_PARENT_PROCESS))
            {
                FreeConsole();
                AttachConsole(ATTACH_PARENT_PROCESS);
            }
        }
        catch { }
    }

    public static void Say(string line)
    {
        try { Console.WriteLine(line); } catch { }
    }
}

/// <summary>
/// 批量适配：吃一个帧目录（游戏截图 / 录屏抽帧都行），按分辨率分组，
/// 用 OCR 文字锚点自动派生模板，做「在场一致性 + 缺席不误报」双向量检后装进 templates/&lt;宽x高&gt;/。
///
///   TheaterAssistant.exe --batch samples\auto
///   TheaterAssistant.exe --batch samples\auto --out templates --min-present 0.92 --max-absent 0.80
///   TheaterAssistant.exe --synth tests\_tmp\batch     生成本机自检用的合成帧
/// </summary>
internal static class BatchRunner
{
    internal sealed class FrameRec
    {
        public string Path;
        public Bitmap Image;
        public int W, H;                       // 载入时快照：Image 稍后会被 Dispose，不能靠它算宽高
        public TemplateFactory.GrayFrame Gray;
        public List<OcrBox> Boxes = new();
        public List<TemplateFactory.SpecRow> Specs = new();
        public int? Act;
        public string ActSource = "";
    }

    internal sealed class TemplateResult
    {
        public string Bucket;
        public string Name;
        public string Text;
        public int W, H;
        public int X, Y;
        public string Source = "";
        public double PresentMin = 1.0;
        public double AbsentMax = 0.0;
        public int PresentCount, AbsentCount;
        public bool Verified;                 // 有缺席帧才算真正验证过"不误报"
        public bool Installed;
        public string Note = "";
        public TemplateFactory.SpecRow Spec;
    }

    public static int Run(string frameDir, string outDir, double minPresent, double maxAbsent)
    {
        Cli.Attach();
        var started = Stopwatch.StartNew();
        var lines = new List<string>();
        var report = new Dictionary<string, object>();

        frameDir = Path.GetFullPath(frameDir);
        outDir = string.IsNullOrWhiteSpace(outDir) ? Path.Combine(AppPaths.Dir, "templates") : Path.GetFullPath(outDir);

        if (!Directory.Exists(frameDir))
        {
            Cli.Say("目录不存在: " + frameDir);
            return 2;
        }

        var files = Directory.GetFiles(frameDir)
            .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        if (files.Count == 0)
        {
            Cli.Say("目录里没有帧: " + frameDir);
            return 2;
        }

        using var ocr = new OcrService();
        ocr.InitializeAsync(Path.Combine(AppPaths.Dir, "models")).GetAwaiter().GetResult();
        Cli.Say("OCR: " + ocr.Status);

        var theater = TheaterData.Load(Path.Combine(AppPaths.Dir, "data", "season29.json"));
        var roster = RosterData.TryLoad(Path.Combine(AppPaths.Dir, "roster.json"));

        // ---- 阶段 1：逐帧 OCR + 派生规格 + 幕次判定 ----
        var frames = new List<FrameRec>();
        foreach (var f in files)
        {
            try
            {
                var rec = new FrameRec { Path = f, Image = new Bitmap(f) };
                rec.W = rec.Image.Width;
                rec.H = rec.Image.Height;
                int scale = rec.W < 900 ? 2 : 1;                    // 小图放大后再识别，否则小字全丢
                rec.Boxes = ocr.RecognizeBoxesAsync(rec.Image, null, scale).GetAwaiter().GetResult() ?? new List<OcrBox>();
                rec.Specs = TemplateFactory.Derive(rec.Boxes, rec.W, rec.H);

                // 幕次：整帧文本 + 底部条带（0.28,0.82,0.46,0.14）+3x，与运行时同一套
                var text = string.Join("\n", rec.Boxes.Select(b => b.Text));
                var band = ocr.RecognizeRegionAsync(rec.Image, new RectangleF(0.28f, 0.82f, 0.46f, 0.14f), 3)
                    .GetAwaiter().GetResult();
                var advice = TheaterLogic.Evaluate(new List<Matcher.Hit>(), text, theater, roster, band);
                rec.Act = advice.Act;
                rec.ActSource = advice.Diagnostics ?? "";

                rec.Gray = TemplateFactory.GrayFrame.From(rec.Image);
                frames.Add(rec);

                lines.Add($"帧 {Path.GetFileName(f)} {rec.W}x{rec.H} 文本框 {rec.Boxes.Count} 标签 {rec.Specs.Count} 幕次 {(rec.Act?.ToString() ?? "-")}");
            }
            catch (Exception ex)
            {
                lines.Add($"帧 {Path.GetFileName(f)} 读取失败: {ex.Message}");
            }
        }

        if (frames.Count == 0)
        {
            foreach (var l in lines) Cli.Say(l);
            Cli.Say("没有可用帧");
            return 2;
        }

        // ---- 阶段 2：按分辨率分组，逐标签质检 ----
        var results = new List<TemplateResult>();
        var buckets = frames.GroupBy(r => TemplateFactory.BucketName(r.W, r.H))
                            .OrderBy(g => g.Key, StringComparer.Ordinal)
                            .ToList();

        foreach (var bucket in buckets)
        {
            var members = bucket.ToList();
            var dirOut = Path.Combine(outDir, bucket.Key);
            Directory.CreateDirectory(dirOut);

            var names = members.SelectMany(m => m.Specs.Select(s => s.Name))
                               .Distinct(StringComparer.Ordinal)
                               .OrderBy(n => n, StringComparer.Ordinal)
                               .ToList();

            // 实际安装用的规格：_specs.json 必须与装出来的模板同源，不能另取"同名第一条"
            int scanStep = Math.Max(4, members.Max(m => m.W) / 320);   // 1080p → 6，2K → 8，720p → 4

            foreach (var name in names)
            {
                // 源帧：同名规格里 OCR 置信度最高的那一帧
                var srcPair = members
                    .Select(m => new { Frame = m, Spec = m.Specs.FirstOrDefault(s => s.Name == name) })
                    .Where(x => x.Spec != null)
                    .OrderByDescending(x => x.Spec.Score)
                    .First();

                var spec = srcPair.Spec;
                var tr = new TemplateResult
                {
                    Bucket = bucket.Key, Name = name, Text = spec.Text,
                    W = spec.W, H = spec.H, X = spec.X, Y = spec.Y,
                    Source = Path.GetFileName(srcPair.Frame.Path),
                };

                using var crop = TemplateFactory.Crop(srcPair.Frame.Image, spec);
                var tplGray = TemplateFactory.GrayFrame.From(crop);

                // (1) 在场一致性：同分辨率下同样识别出该标签的其它帧，必须仍在原位附近命中
                var presentScores = new List<double>();
                foreach (var other in members)
                {
                    if (ReferenceEquals(other, srcPair.Frame)) continue;
                    if (other.Specs.All(s => s.Name != name)) continue;
                    var win = new Rectangle(spec.X - 24, spec.Y - 24, 48, 48);
                    var hit = TemplateFactory.BestIn(other.Gray, tplGray, win);
                    presentScores.Add(hit.Score);
                }
                tr.PresentCount = presentScores.Count + 1;                 // +1 = 源帧自身（构造上 1.0）
                tr.PresentMin = presentScores.Count == 0 ? 1.0 : Math.Round(presentScores.Min(), 4);

                // (2) 缺席不误报：同分辨率下没有该标签的帧，整帧都不该命中
                //     1080p/2K 全帧扫描很贵，所以按分辨率自适应步长，并限制缺席样本数（取文件名序前 N 个）
                var absentFrames = members.Where(o => o.Specs.All(s => s.Name != name)).Take(6).ToList();
                var absentScores = new List<double>();
                foreach (var other in absentFrames)
                {
                    absentScores.Add(TemplateFactory.BestInFrame(other.Gray, tplGray, scanStep));
                }
                tr.AbsentCount = absentScores.Count;
                tr.AbsentMax = absentScores.Count == 0 ? 0.0 : Math.Round(absentScores.Max(), 4);

                bool passPresent = tr.PresentMin >= minPresent;
                bool passAbsent = tr.AbsentCount == 0 || tr.AbsentMax < maxAbsent;
                tr.Installed = passPresent && passAbsent;
                tr.Verified = tr.AbsentCount > 0;                       // 没有缺席帧 = 只做了构造性自证
                if (!passPresent) tr.Note = "在场一致性不足";
                else if (!passAbsent) tr.Note = "缺席帧出现误报";
                else if (!tr.Verified) tr.Note = "单一样本（该分辨率下没有缺席帧可验误报）";
                tr.Spec = spec;

                if (tr.Installed)
                {
                    crop.Save(Path.Combine(dirOut, name + ".png"), ImageFormat.Png);
                }

                results.Add(tr);
            }

            // 该分辨率的规格表一起落盘，方便用 tools/make-templates.ps1 复算。
            // 只写"实际装出来的那一条"（results 里的 Spec 就是裁剪源规格），否则 specs 与模板不同源。
            var specFile = Path.Combine(dirOut, "_specs.json");
            var installedSpecs = results
                .Where(r => r.Bucket == bucket.Key && r.Installed && r.Spec != null)
                .Select(r => r.Spec)
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .ToList();
            try
            {
                File.WriteAllText(specFile, JsonSerializer.Serialize(installedSpecs, TemplateFactory.JsonOpts), Encoding.UTF8);
            }
            catch { }
        }

        // ---- 阶段 3：报告 ----
        int installed = results.Count(r => r.Installed);
        int verified = results.Count(r => r.Installed && r.Verified);
        lines.Add("");
        lines.Add("分辨率      模板       文本                 尺寸      在场最低  缺席最高  验证  结论");
        foreach (var r in results)
        {
            var size = r.W + "x" + r.H;
            var verdict = r.Installed ? "安装" : "拒绝:" + r.Note;
            lines.Add($"{r.Bucket,-11} {r.Name,-10} {Trim(r.Text, 18),-20} {size,-9} " +
                      $"{r.PresentMin,8:0.000} {r.AbsentMax,9:0.000}  {(r.Verified ? "双向" : "单向"),-4} {verdict}");
        }
        lines.Add("");
        lines.Add($"帧 {frames.Count} · 分辨率 {buckets.Count} · 候选模板 {results.Count} · 安装 {installed}（双向量检 {verified}）· 拒绝 {results.Count - installed} · 用时 {started.ElapsedMilliseconds} ms");
        lines.Add("输出目录: " + outDir);

        report["frames"] = files.Select(Path.GetFileName).ToList();
        report["ocr"] = ocr.Status;
        report["installed"] = installed;
        report["verified"] = verified;
        report["candidates"] = results.Count;
        report["templates"] = results.Select(r => new
        {
            r.Bucket, r.Name, r.Text, r.W, r.H, r.X, r.Y, r.Source,
            r.PresentMin, r.AbsentMax, r.PresentCount, r.AbsentCount, r.Verified, r.Installed, r.Note,
        }).ToList();
        report["frameDetail"] = frames.Select(f => new
        {
            file = Path.GetFileName(f.Path), f.W, f.H,
            boxes = f.Boxes.Count, labels = f.Specs.Select(s => s.Name).ToList(),
            act = f.Act, actSource = f.ActSource,
        }).ToList();

        try
        {
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "batch-report.json"),
                JsonSerializer.Serialize(report, TemplateFactory.JsonOpts), Encoding.UTF8);
            File.WriteAllText(Path.Combine(outDir, "batch-report.md"), Md(lines), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            lines.Add("写报告失败: " + ex.Message);
        }

        foreach (var f in frames) f.Image?.Dispose();

        foreach (var l in lines) Cli.Say(l);
        return installed > 0 || results.Count == 0 ? 0 : 1;
    }

    private static string Trim(string s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "-";
        s = s.Replace("\n", " ").Replace("|", "/");
        return s.Length <= n ? s : s[..n] + "…";
    }

    private static string Md(List<string> lines)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 批量适配报告");
        sb.AppendLine();
        foreach (var l in lines) sb.AppendLine(l);
        return sb.ToString();
    }

    // ---------------- 合成帧（自检/CI 用，不依赖用户的游戏截图） ----------------

    /// <summary>
    /// 生成一组合成帧：同一分辨率的「同屏/异屏」两张 + 另一分辨率两张，
    /// 用来在 CI 里端到端验证「派生 → 质检 → 安装」全链路。
    /// 中文标签在无中文字体的机器上可能渲染成方框，因此每帧都带一个 ASCII 锚点 TESTLABEL。
    /// </summary>
    public static int WriteSynth(string dir)
    {
        Cli.Attach();
        dir = Path.GetFullPath(dir);
        Directory.CreateDirectory(dir);

        var written = new List<string>();

        void Frame(string name, int w, int h, Color bg, Color fg, float k, string[] labels, int seed)
        {
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using (var brush = new LinearGradientBrush(new Rectangle(0, 0, w, h), bg, Color.FromArgb(bg.R / 2, bg.G / 2, bg.B / 2), 45f))
                {
                    g.FillRectangle(brush, 0, 0, w, h);
                }

                // 一点与标签无关的纹理，避免整帧纯色导致误报检测失效
                var rnd = new Random(seed);
                using (var pen = new Pen(Color.FromArgb(60, 255, 255, 255), Math.Max(1f, 2f * k)))
                {
                    for (int i = 0; i < 6; i++)
                    {
                        g.DrawEllipse(pen, rnd.Next(0, w / 2), rnd.Next(h / 3, h), w / 6 + rnd.Next(0, w / 5), h / 6);
                    }
                }

                // 标签按纵向等分铺开：让「第N幕」落在下半部（符合真实局内标签位置，也满足派生规则）
                for (int i = 0; i < labels.Length; i++)
                {
                    float y = h * (0.15f + 0.18f * i);
                    var font = PickFont(40f * k);
                    using (font)
                    using (var brush = new SolidBrush(fg))
                    {
                        g.DrawString(labels[i], font, brush, 80 * k, y);
                    }
                }
            }

            var path = Path.Combine(dir, name);
            bmp.Save(path, ImageFormat.Png);
            written.Add(path);
        }

        var gold = Color.FromArgb(230, 200, 150);
        var dark = Color.FromArgb(26, 30, 44);
        var dark2 = Color.FromArgb(18, 20, 30);

        Frame("a1-1280x720.png", 1280, 720, dark, gold, 1.0f,
            new[] { "TESTLABEL", "开始挑战", "助演嘉宾", "第3幕" }, 11);
        Frame("a2-1280x720.png", 1280, 720, dark, gold, 1.0f,
            new[] { "TESTLABEL", "开始挑战", "助演嘉宾", "第3幕" }, 23);
        Frame("b-1280x720.png", 1280, 720, dark2, Color.FromArgb(120, 200, 255), 1.0f,
            new[] { "演出结算" }, 37);
        Frame("c1-1920x1080.png", 1920, 1080, dark, gold, 1.5f,
            new[] { "TESTLABEL", "开始挑战", "助演嘉宾", "第3幕" }, 51);
        Frame("c2-1920x1080.png", 1920, 1080, dark, gold, 1.5f,
            new[] { "TESTLABEL", "开始挑战", "助演嘉宾", "第3幕" }, 67);

        foreach (var p in written) Cli.Say("合成: " + p);
        Cli.Say($"共 {written.Count} 帧 → {dir}");
        return 0;
    }

    /// <summary>优先中文字体，拿不到就退到 Arial（此时中文会缺字符，但 ASCII 锚点仍可识别）。</summary>
    private static Font PickFont(float px)
    {
        foreach (var name in new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimSun", "SimHei", "NSimSun" })
        {
            try
            {
                var f = new Font(name, px, FontStyle.Bold, GraphicsUnit.Pixel);
                // 字体不存在时 GDI+ 会静默换成默认字体，族名对不上就说明没拿到
                if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return f;
                f.Dispose();
            }
            catch { }
        }
        return new Font("Arial", px, FontStyle.Bold, GraphicsUnit.Pixel);
    }
}
