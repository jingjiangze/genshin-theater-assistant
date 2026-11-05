using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TheaterAssistant;

/// <summary>
/// 头像模板认角色池：剧诗「待命角色」那种"只有头像 + Lv"的网格，OCR 认不出名字，
/// 就用角色头像做模板去比对。
///
/// 定位方式：先用 OCR 找到每个格子的 `Lv.NN` 标签，把它当锚点，
/// 在锚点上方按几个候选尺寸试裁剪，取"全场最佳分"对应的尺寸作为格子几何（自校准，不写死分辨率）。
///
///   TheaterAssistant.exe --roster-avatars frames --avatars assets\avatars --out roster.json
///   TheaterAssistant.exe --roster-avatars frames --avatars assets\avatars --probe   # 只打诊断
///
/// 头像素材：node tools/fetch-avatars.mjs --out assets/avatars
/// </summary>
internal static class AvatarRoster
{
    private static readonly Regex LevelRegex = new(@"Lv\.?\s*(\d{1,3})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly float[] ScaleFactors = { 1.5f, 1.7f, 1.9f, 2.1f, 2.3f, 2.6f };

    internal sealed class AvatarTpl
    {
        public string Name;
        public Bitmap Bitmap;
    }

    public static int Run(string frameDir, string avatarDir, string outPath, bool probe, double minScore)
    {
        Cli.Attach();
        frameDir = Path.GetFullPath(frameDir);
        avatarDir = string.IsNullOrWhiteSpace(avatarDir)
            ? AppPaths.Resolve(Path.Combine("assets", "avatars"))
            : Path.GetFullPath(avatarDir);
        outPath = string.IsNullOrWhiteSpace(outPath) ? Path.Combine(AppPaths.Dir, "roster.json") : Path.GetFullPath(outPath);

        if (!Directory.Exists(frameDir)) { Cli.Say("帧目录不存在: " + frameDir); return 2; }
        if (!Directory.Exists(avatarDir)) { Cli.Say($"头像目录不存在: {avatarDir}（先跑 node tools/fetch-avatars.mjs）"); return 2; }

        var tpls = LoadAvatars(avatarDir);
        if (tpls.Count == 0) { Cli.Say("头像目录里没有 png"); return 2; }
        Cli.Say($"头像模板 {tpls.Count} 个（{avatarDir}）");

        var frames = Directory.GetFiles(frameDir)
            .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        if (frames.Count == 0) { Cli.Say("没有图片"); return 2; }

        using var ocr = new OcrService();
        ocr.InitializeAsync(AppPaths.ModelsRoot).GetAwaiter().GetResult();
        Cli.Say("OCR: " + ocr.Status);

        var found = new Dictionary<string, (int Count, int? Level)>(StringComparer.Ordinal);
        var lines = new List<string>();

        foreach (var f in frames)
        {
            using var img = new Bitmap(f);
            var boxes = ocr.RecognizeBoxesAsync(img, null, img.Width < 900 ? 2 : 1).GetAwaiter().GetResult();
            if (boxes == null) { lines.Add($"{Path.GetFileName(f)}: OCR 未就绪"); continue; }

            var anchors = boxes
                .Select(b => new { Box = b, M = LevelRegex.Match(b.Text ?? "") })
                .Where(x => x.M.Success)
                .Select(x => new { x.Box, Level = int.Parse(x.M.Groups[1].Value) })
                .ToList();

            lines.Add($"{Path.GetFileName(f)}: 文本框 {boxes.Count}，Lv 锚点 {anchors.Count}");
            if (anchors.Count == 0) continue;

            var frameGray = TemplateFactory.GrayFrame.From(img);

            // 自校准：先用第一个锚点试各种格子尺寸，选全场平均分最高的那个尺寸
            float bestScale = 1.9f;
            double bestScaleScore = -1;
            foreach (var sf in ScaleFactors)
            {
                double sum = 0; int n = 0;
                foreach (var a in anchors.Take(6))
                {
                    var tile = TileFor(a.Box.Rect, sf, img.Width, img.Height);
                    if (tile.Width < 24 || tile.Height < 24) continue;
                    var (name, score) = BestMatch(img, frameGray, tile, tpls, null);
                    sum += score; n++;
                }
                if (n == 0) continue;
                double avg = sum / n;
                if (avg > bestScaleScore) { bestScaleScore = avg; bestScale = sf; }
            }
            lines.Add($"  自校准格子比例 ×{bestScale}（平均分 {bestScaleScore:0.000}）");

            int hit = 0;
            foreach (var a in anchors)
            {
                var tile = TileFor(a.Box.Rect, bestScale, img.Width, img.Height);
                if (tile.Width < 24 || tile.Height < 24) continue;

                var top = new List<(string Name, double Score)>();
                var (name, score) = BestMatch(img, frameGray, tile, tpls, top);
                if (probe)
                {
                    lines.Add($"  Lv{a.Level,-3} @({a.Box.Rect.X},{a.Box.Rect.Y}) → " +
                              string.Join(" | ", top.Take(5).Select(t => $"{t.Name}={t.Score:0.000}")));
                }
                if (name == null || score < minScore) continue;

                hit++;
                if (found.TryGetValue(name, out var cur)) found[name] = (cur.Count + 1, cur.Level ?? a.Level);
                else found[name] = (1, a.Level);
            }
            lines.Add($"  采纳 {hit} 个（阈值 {minScore:0.00}）");
        }

        foreach (var l in lines) Cli.Say(l);

        var ordered = found.OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        Cli.Say("");
        Cli.Say($"识别到角色 {ordered.Count} 个");
        foreach (var kv in ordered) Cli.Say($"  {kv.Key,-10} ×{kv.Value.Count} 等级 {kv.Value.Level?.ToString() ?? "-"}");

        if (probe) { Cli.Say("（--probe：不写 roster.json）"); return ordered.Count > 0 ? 0 : 3; }

        var payload = new
        {
            source = "avatar-match",
            generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            frames = frames.Count,
            characters = ordered.Select(kv => new { key = kv.Key, name = kv.Key, level = kv.Value.Level ?? 0, seen = kv.Value.Count }).ToList(),
        };
        try
        {
            File.WriteAllText(outPath, JsonSerializer.Serialize(payload, TemplateFactory.JsonOpts), Encoding.UTF8);
            Cli.Say("已写: " + outPath);
        }
        catch (Exception ex) { Cli.Say("写 roster 失败: " + ex.Message); return 1; }

        return ordered.Count > 0 ? 0 : 3;
    }

    /// <summary>把 Lv 标签当锚点推格子：标签在格子底部内侧，格子是正方形。</summary>
    private static Rectangle TileFor(Rectangle lv, float scale, int frameW, int frameH)
    {
        int w = Math.Max(24, (int)Math.Round(lv.Width * scale));
        int h = w;
        int x = lv.X + lv.Width / 2 - w / 2;
        int y = lv.Y + lv.Height - h + lv.Height / 4;   // 标签稍微上移到格子底边内
        if (x < 0) x = 0;
        if (y < 0) y = 0;
        if (x + w > frameW) w = frameW - x;
        if (y + h > frameH) h = frameH - y;
        return new Rectangle(x, y, w, h);
    }

    private static (string Name, double Score) BestMatch(Bitmap frame, TemplateFactory.GrayFrame frameGray,
        Rectangle tile, List<AvatarTpl> tpls, List<(string, double)> topSink)
    {
        // 只比格子中间 88%：边缘有元素底色描边/等级条，头像本体在中间
        int inset = (int)(tile.Width * 0.06);
        var inner = new Rectangle(tile.X + inset, tile.Y + inset, tile.Width - inset * 2, tile.Height - inset * 2);
        if (inner.Width < 16 || inner.Height < 16) return (null, 0);

        string bestName = null;
        double bestScore = -1;

        // 用边缘图相关性而不是原始像素 SSD：游戏格子里有元素底色、等级条、描边，
        // 而头像是"卡面图标"，直接比像素只能到 0.4–0.6（实测）。边缘图对配色不敏感，能拉开差距。
        var tileEdges = Edges(frameGray, inner);

        foreach (var t in tpls)
        {
            using var scaled = Resize(t.Bitmap, inner.Width, inner.Height);
            var g = TemplateFactory.GrayFrame.From(scaled);
            var tplEdges = Edges(g, new Rectangle(0, 0, g.W, g.H));
            double s = Corr(tileEdges, tplEdges);
            topSink?.Add((t.Name, Math.Round(s, 4)));
            if (s > bestScore) { bestScore = s; bestName = t.Name; }
        }

        if (topSink != null) topSink.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        return (bestName, bestScore);
    }

    /// <summary>取一块区域的梯度幅值图（|dx|+|dy|，0~255），用来做边缘相关性。</summary>
    private static float[] Edges(TemplateFactory.GrayFrame f, Rectangle r)
    {
        int w = Math.Max(1, r.Width), h = Math.Max(1, r.Height);
        var e = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            int fy = Math.Min(f.H - 2, Math.Max(1, r.Y + y));
            for (int x = 0; x < w; x++)
            {
                int fx = Math.Min(f.W - 2, Math.Max(1, r.X + x));
                int gx = f.Data[fy * f.W + (fx + 1)] - f.Data[fy * f.W + (fx - 1)];
                int gy = f.Data[(fy + 1) * f.W + fx] - f.Data[(fy - 1) * f.W + fx];
                e[y * w + x] = Math.Abs(gx) + Math.Abs(gy);
            }
        }
        return e;
    }

    /// <summary>皮尔逊相关系数（-1~1）：只看形状是否一致，不受整体亮度/配色影响。</summary>
    private static double Corr(float[] a, float[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        if (n == 0) return 0;
        double ma = 0, mb = 0;
        for (int i = 0; i < n; i++) { ma += a[i]; mb += b[i]; }
        ma /= n; mb /= n;
        double num = 0, da = 0, db = 0;
        for (int i = 0; i < n; i++)
        {
            double x = a[i] - ma, y = b[i] - mb;
            num += x * y; da += x * x; db += y * y;
        }
        if (da <= 0 || db <= 0) return 0;
        return num / Math.Sqrt(da * db);
    }

    private static Bitmap Resize(Bitmap src, int w, int h)
    {
        var dst = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(src, new Rectangle(0, 0, w, h));
        return dst;
    }

    private static List<AvatarTpl> LoadAvatars(string dir)
    {
        var list = new List<AvatarTpl>();
        var nameByFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var idx = Path.Combine(dir, "index.json");
        if (File.Exists(idx))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(idx));
                if (doc.RootElement.TryGetProperty("avatars", out var arr))
                    foreach (var a in arr.EnumerateArray())
                    {
                        var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                        var file = a.TryGetProperty("file", out var fl) ? fl.GetString() : null;
                        if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(file)) nameByFile[file] = name;
                    }
            }
            catch { }
        }

        foreach (var f in Directory.GetFiles(dir, "*.png"))
        {
            var file = Path.GetFileName(f);
            var name = nameByFile.TryGetValue(file, out var n) ? n : Path.GetFileNameWithoutExtension(f);
            try { list.Add(new AvatarTpl { Name = name, Bitmap = new Bitmap(f) }); } catch { }
        }
        return list;
    }
}
