using System.Drawing;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TheaterAssistant;

/// <summary>
/// 从视频帧里提取"玩家/UP 的操作"候选：连招（E/Q 序列）、购买与选择（买/选/优先/先…）、
/// 以及带数量的配队提示。产出候选 JSON，**需人工复核后再并入 data/rotations.json**。
///
///   TheaterAssistant.exe --extract-ops frames\BV1xxx --out ops.json --bvid BV1xxx
///
/// 为什么只出候选：视频里的连招常带 UP 主观缩写（"钟E-菲Q-卡Qe"），
/// 直接进生产数据会把噪声当知识；所以本命令只做"收集 + 去重 + 标出处"。
/// </summary>
internal static class OpsExtract
{
    private static readonly Regex Combo = new(@"[A-Za-z\u4e00-\u9fff]{1,4}\s*[EQeEqQ](?:\s*[-—>→]\s*[A-Za-z\u4e00-\u9fff]{0,4}\s*[EQeEqQ]){1,8}", RegexOptions.Compiled);
    private static readonly Regex Action = new(@"(先|优先|建议|需要|别|不要|第一时?间|开局)[^\n。；]{4,40}", RegexOptions.Compiled);
    private static readonly Regex BuySel = new(@"(买|购入|选)[^\n。；]{2,30}", RegexOptions.Compiled);
    private static readonly Regex Team = new(@"(队伍|配队|助演|外援)[：:][^\n]{4,40}", RegexOptions.Compiled);

    internal static string KindOf(string line)
    {
        if (Combo.IsMatch(line)) return "combo";
        if (BuySel.IsMatch(line)) return "buy";
        if (Team.IsMatch(line)) return "team";
        if (Action.IsMatch(line)) return "action";
        return null;
    }

    public static int Run(string frameDir, string outPath, string bvid, string actHint)
    {
        Cli.Attach();
        frameDir = Path.GetFullPath(frameDir);
        if (!Directory.Exists(frameDir)) { Cli.Say("帧目录不存在: " + frameDir); return 2; }
        outPath = string.IsNullOrWhiteSpace(outPath) ? Path.Combine(AppPaths.Dir, "ops-candidates.json") : Path.GetFullPath(outPath);

        var files = Directory.GetFiles(frameDir)
            .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        if (files.Count == 0) { Cli.Say("没有帧"); return 2; }

        using var ocr = new OcrService();
        ocr.InitializeAsync(AppPaths.ModelsRoot).GetAwaiter().GetResult();
        Cli.Say("OCR: " + ocr.Status);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<object>();
        int scanned = 0;

        foreach (var f in files)
        {
            var stamp = Regex.Match(Path.GetFileNameWithoutExtension(f), @"-(\d+)s").Groups[1].Value;
            try
            {
                using var img = new Bitmap(f);
                var boxes = ocr.RecognizeBoxesAsync(img, null, img.Width < 900 ? 2 : 1).GetAwaiter().GetResult();
                if (boxes == null) continue;
                scanned++;

                foreach (var b in boxes)
                {
                    var line = (b.Text ?? "").Trim();
                    if (line.Length < 4) continue;
                    var kind = KindOf(line);
                    if (kind == null) continue;
                    var key = kind + ":" + Regex.Replace(line, @"[\s\p{P}]", "");
                    if (!seen.Add(key)) continue;

                    rows.Add(new
                    {
                        kind,
                        text = line,
                        act = int.TryParse(Regex.Match(line, @"第?\s*(\d{1,2})\s*幕").Groups[1].Value, out var a) ? a
                              : (int.TryParse(actHint, out var ah) ? ah : 0),
                        source = string.IsNullOrEmpty(bvid) ? "scan" : $"{bvid}@{stamp}s",
                        needsReview = true,
                    });
                }
            }
            catch { }
        }

        var payload = new
        {
            note = "视频里提取的操作用法候选（连招/购买/配队/行动），需人工复核后并入 data/rotations.json。",
            generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            bvid, frames = scanned, candidates = rows.Count, ops = rows,
        };
        try
        {
            File.WriteAllText(outPath, JsonSerializer.Serialize(payload, TemplateFactory.JsonOpts), Encoding.UTF8);
            Cli.Say($"扫描 {scanned}/{files.Count} 帧，提取候选 {rows.Count} 条 → {outPath}");
            foreach (var r in rows.Take(12)) Cli.Say("  " + JsonSerializer.Serialize(r, TemplateFactory.JsonOpts).Replace("\n", " "));
        }
        catch (Exception ex) { Cli.Say("写文件失败: " + ex.Message); return 1; }

        return rows.Count > 0 ? 0 : 3;
    }

    /// <summary>纯函数：一行文字属于哪类操作（供自检断言）。</summary>
    internal static string Classify(string line) => KindOf(line ?? "");
}
