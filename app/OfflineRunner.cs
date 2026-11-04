using System.Drawing;
using System.Text;
using System.Text.Json;

namespace TheaterAssistant;

/// <summary>
/// 离线自检：不抓屏幕，直接把一张图片喂给「模板匹配 + OCR + 状态机 + 攻略」全链路，
/// 结果同时写 stdout 和图片同目录的 *.result.json。
/// 用途：验证模型/识别/判定是否真的工作，不需要开游戏。
///   TheaterAssistant.exe --frame 某张截图.png
/// </summary>
internal static class OfflineRunner
{
    public static void Run(string imagePath)
    {
        var lines = new List<string>();
        var report = new Dictionary<string, object>();

        try
        {
            if (!File.Exists(imagePath)) throw new FileNotFoundException(imagePath);

            var baseDir = AppContext.BaseDirectory;
            var theater = TheaterData.Load(Path.Combine(baseDir, "data", "season29.json"));
            var roster = RosterData.TryLoad(Path.Combine(baseDir, "roster.json"));
            var notes = GuideNotes.Load(Path.Combine(baseDir, "data", "guide-notes.json"));

            using var img = new Bitmap(imagePath);

            // 模板：按图片尺寸挑桶（与运行时同一套规则）
            var templates = new List<TemplateEntry>();
            var tplRoot = Path.Combine(baseDir, "templates");
            if (Directory.Exists(tplRoot))
            {
                var dir = tplRoot;
                var best = Directory.GetDirectories(tplRoot)
                    .Select(d => new { Dir = d, W = ParseWidth(Path.GetFileName(d)) })
                    .Where(x => x.W > 0 && Math.Abs(x.W - img.Width) <= Math.Max(32, (int)(img.Width * 0.12)))
                    .OrderBy(x => Math.Abs(x.W - img.Width))
                    .FirstOrDefault();
                if (best != null) dir = best.Dir;

                foreach (var f in Directory.GetFiles(dir, "*.png"))
                {
                    var name = Path.GetFileNameWithoutExtension(f);
                    if (name.StartsWith('_')) continue;
                    templates.Add(new TemplateEntry(name, new Bitmap(f), 0.86));
                }
            }

            var hits = Matcher.Scan(img, templates);

            var ocr = new OcrService();
            ocr.InitializeAsync(Path.Combine(baseDir, "models")).GetAwaiter().GetResult();
            var text = ocr.RecognizeAsync(img).GetAwaiter().GetResult() ?? "";
            ocr.Dispose();

            var advice = TheaterLogic.Evaluate(hits, text, theater, roster);

            lines.Add($"image   : {Path.GetFileName(imagePath)} ({img.Width}x{img.Height})");
            lines.Add($"detect  : kind={advice.Diagnostics} header={advice.Header}");
            lines.Add($"act     : {advice.Act?.ToString() ?? "-"}");
            lines.Add($"hits    : {(hits.Count > 0 ? string.Join(", ", hits.Select(h => $"{h.Name}={h.Score}")) : "无")}");
            lines.Add($"ocr     : {text.Replace("\n", " ").Trim()}");
            lines.Add("----- 建议 -----");
            lines.AddRange(advice.Lines);
            if (advice.Act.HasValue)
            {
                var note = notes.ActNote(advice.Act.Value);
                if (!string.IsNullOrWhiteSpace(note)) lines.Add("节奏: " + note);
            }

            report["image"] = imagePath;
            report["width"] = img.Width;
            report["height"] = img.Height;
            report["act"] = advice.Act;
            report["header"] = advice.Header;
            report["hits"] = hits.Select(h => new { h.Name, h.Score, h.X, h.Y }).ToList();
            report["ocrChars"] = text.Length;
            report["lines"] = advice.Lines;
        }
        catch (Exception ex)
        {
            lines.Add("ERROR: " + ex);
            report["error"] = ex.ToString();
        }

        foreach (var l in lines) Console.WriteLine(l);

        var sb = new StringBuilder();
        foreach (var l in lines) sb.AppendLine(l);
        try
        {
            var outTxt = imagePath + ".txt";
            File.WriteAllText(outTxt, sb.ToString(), Encoding.UTF8);
            File.WriteAllText(imagePath + ".result.json", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), Encoding.UTF8);
            Console.WriteLine("written: " + outTxt);
        }
        catch { }
    }

    private static int ParseWidth(string name)
    {
        var m = System.Text.RegularExpressions.Regex.Match(name, @"^(\d{3,5})x\d{3,5}$");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }
}
