using System.Drawing;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TheaterAssistant;

/// <summary>
/// 免登录获取「角色池」：拿几张游戏内角色界面截图，OCR 出文字 + 用角色名词典比对，
/// 生成 roster.json（格式与手动导入/ GOOD 导入一致）。
///
/// 能力边界（实测过，不要夸大）：
///   · 有**名字**的界面（角色一览、自定义队伍、角色详情）→ 能认出角色名与等级；
///   · 只有头像 + Lv 的网格（如剧诗「待命角色」页）→ 认不出名字，只能拿到等级分布；
///   · 头像模板匹配是另一条路（要给 129 个角色做头像模板），暂未做。
///
///   TheaterAssistant.exe --roster-scan samples\auto --out roster.json
/// </summary>
internal static class RosterScan
{
    private static readonly Regex LevelRegex = new(@"Lv\.?\s*(\d{1,3})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex JunkRegex = new(@"[\s\u3000·・\-—_:：,，。\.\(\)（）\[\]【】Lv\.?0-9]", RegexOptions.Compiled);

    /// <summary>把一段 OCR 文本对到角色名；对不上返回 null。纯函数，便于自测。</summary>
    internal static string MatchName(string text, IEnumerable<string> names)
    {
        if (string.IsNullOrWhiteSpace(text) || names == null) return null;
        var t = JunkRegex.Replace(text, "");
        if (t.Length < 2) return null;

        string best = null;
        foreach (var raw in names)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var n = JunkRegex.Replace(raw, "");
            if (n.Length < 2) continue;

            if (t == n) return raw;                       // 完全相等，优先返回
            if (!t.Contains(n, StringComparison.Ordinal)) continue;
            if (best == null || n.Length > JunkRegex.Replace(best, "").Length) best = raw;
        }
        return best;
    }

    internal static int? MatchLevel(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = LevelRegex.Match(text);
        return m.Success && int.TryParse(m.Groups[1].Value, out var lv) ? lv : null;
    }

    public static int Run(string frameDir, string outPath)
    {
        Cli.Attach();
        frameDir = Path.GetFullPath(frameDir);
        outPath = string.IsNullOrWhiteSpace(outPath) ? Path.Combine(AppPaths.Dir, "roster.json") : Path.GetFullPath(outPath);

        if (!Directory.Exists(frameDir)) { Cli.Say("目录不存在: " + frameDir); return 2; }

        var files = Directory.GetFiles(frameDir)
            .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        if (files.Count == 0) { Cli.Say("目录里没有图片: " + frameDir); return 2; }

        // 角色名词典：data/characters.json 的键就是中文名
        var names = new List<string>();
        var charPath = AppPaths.Data("characters.json");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(charPath));
            if (doc.RootElement.TryGetProperty("characters", out var chars))
                foreach (var p in chars.EnumerateObject()) names.Add(p.Name);
        }
        catch (Exception ex)
        {
            Cli.Say("读 characters.json 失败: " + ex.Message);
            return 2;
        }
        Cli.Say($"角色词典 {names.Count} 个名字");

        using var ocr = new OcrService();
        ocr.InitializeAsync(AppPaths.ModelsRoot).GetAwaiter().GetResult();
        Cli.Say("OCR: " + ocr.Status);

        var found = new Dictionary<string, (int Count, int? Level)>(StringComparer.Ordinal);
        var perFrame = new List<string>();

        foreach (var f in files)
        {
            try
            {
                using var img = new Bitmap(f);
                var boxes = ocr.RecognizeBoxesAsync(img, null, img.Width < 900 ? 2 : 1).GetAwaiter().GetResult();
                if (boxes == null) { perFrame.Add($"{Path.GetFileName(f)}: OCR 未就绪"); continue; }

                int hit = 0;
                foreach (var b in boxes)
                {
                    var name = MatchName(b.Text, names);
                    if (name == null) continue;

                    int? level = MatchLevel(b.Text);
                    if (level == null)
                    {
                        // 名字框同一行附近找 Lv.NN（头像网格里等级常单独一行）
                        int cy = b.Rect.Y + b.Rect.Height / 2;
                        foreach (var o in boxes)
                        {
                            if (ReferenceEquals(o, b)) continue;
                            int ocy = o.Rect.Y + o.Rect.Height / 2;
                            if (Math.Abs(ocy - cy) > Math.Max(28, b.Rect.Height * 2)) continue;
                            var lv = MatchLevel(o.Text);
                            if (lv.HasValue) { level = lv; break; }
                        }
                    }

                    hit++;
                    if (found.TryGetValue(name, out var cur))
                        found[name] = (cur.Count + 1, cur.Level ?? level);
                    else
                        found[name] = (1, level);
                }
                perFrame.Add($"{Path.GetFileName(f)}: 文本框 {boxes.Count}，命中角色 {hit}");
            }
            catch (Exception ex) { perFrame.Add($"{Path.GetFileName(f)}: 读取失败 {ex.Message}"); }
        }

        var ordered = found.OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        Cli.Say("");
        foreach (var l in perFrame) Cli.Say(l);
        Cli.Say("");
        Cli.Say($"识别到角色 {ordered.Count} 个");
        foreach (var kv in ordered)
            Cli.Say($"  {kv.Key,-8} 出现 {kv.Value.Count} 次  等级 {kv.Value.Level?.ToString() ?? "-"}");

        var elements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(charPath));
            if (doc.RootElement.TryGetProperty("characters", out var chars))
                foreach (var p in chars.EnumerateObject()) elements[p.Name] = p.Value.GetString() ?? "";
        }
        catch { }

        var payload = new
        {
            source = "roster-scan",
            generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            frames = files.Count,
            characters = ordered.Select(kv => new
            {
                key = kv.Key,
                name = kv.Key,
                element = elements.TryGetValue(kv.Key, out var el) ? el : "",
                level = kv.Value.Level ?? 0,
                seen = kv.Value.Count,
            }).ToList(),
        };

        try
        {
            File.WriteAllText(outPath, JsonSerializer.Serialize(payload, TemplateFactory.JsonOpts), Encoding.UTF8);
            Cli.Say("已写: " + outPath);
        }
        catch (Exception ex) { Cli.Say("写 roster 失败: " + ex.Message); return 1; }

        return ordered.Count > 0 ? 0 : 3;
    }
}
