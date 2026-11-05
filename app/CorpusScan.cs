using System.Drawing;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TheaterAssistant;

/// <summary>
/// 从视频帧/截图里抽「攻略文字」补充语料库。
///
/// 为什么有用：攻略视频里 UP 主会把要点直接打在画面上（"1幕需2雷岩、3幕需2水雷"、"队伍：钟离+菲谢尔"），
/// OCR 一抽就是高质量中文条目，比人工整理快得多。
///
///   TheaterAssistant.exe --corpus-scan frames --out data/guide-corpus-extra.json --bvid BV1eExm6dEmt
///
/// 过滤规则是纯函数（LineWorthy），CI 里可断言，避免把界面文案（演出准备/Lv.90/第1幕）当攻略收进去。
/// </summary>
internal static class CorpusScan
{
    /// <summary>界面固有文案：这些不是攻略，收进去只会污染检索。</summary>
    private static readonly string[] UiChrome =
    {
        "演出准备", "助演阵容", "演出回顾", "奖励一览", "队伍配置", "待命角色", "开幕角色", "可选角色",
        "特邀角色", "自选角色", "前往挑战", "装备配置", "下一步", "自动选择", "邀请助演", "本期辉彩祝福",
        "角色元素类型限制", "参演要求", "剩余时间", "UID", "难度选择", "卓越模式", "月谕模式", "常规挑战",
        "已完成", "已选", "统计", "主要对手", "试用角色", "我的角色", "已编入", "可编入",
    };

    /// <summary>攻略里常见、且界面文案里罕见的信号词。</summary>
    private static readonly string[] GuideSignals =
    {
        "需", "优先", "先", "再", "别", "不要", "建议", "打法", "队伍", "BUFF", "buff", "辅助", "主C", "主c",
        "自备", "外援", "挂件", "破盾", "护盾", "耐力", "圣牌", "祝福", "幻剧之花", "选购", "买", "抽",
    };

    private static readonly Regex Cjk = new(@"[\u4e00-\u9fff]", RegexOptions.Compiled);
    private static readonly Regex ActNo = new(@"第?\s*(\d{1,2})\s*幕", RegexOptions.Compiled);
    private static readonly Regex OnlyChrome = new(@"^(Lv\.?\d+|\d+|\d+/\d+|×\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>一行 OCR 文本值不值得当攻略收进语料（纯函数，可断言）。</summary>
    internal static bool LineWorthy(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();

        if (OnlyChrome.IsMatch(t)) return false;
        if (Cjk.Matches(t).Count < 6) return false;          // 太短的多半是标签

        foreach (var chrome in UiChrome)
            if (t.Contains(chrome, StringComparison.Ordinal) && t.Length <= chrome.Length + 8) return false;

        int signals = GuideSignals.Count(s => t.Contains(s, StringComparison.OrdinalIgnoreCase));
        if (signals >= 1) return true;

        // 没有任何信号词，但出现两个以上「元素/角色名+数量」结构（如 "3幕需2水雷"），也算
        return ActNo.IsMatch(t) && t.Any(char.IsDigit);
    }

    /// <summary>从文本里猜它讲的是第几幕（没有就 0 = 通篇通用）。</summary>
    internal static int GuessAct(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var m = ActNo.Match(text);
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : 0;
    }

    public static int Run(string frameDir, string outPath, string bvid)
    {
        Cli.Attach();
        frameDir = Path.GetFullPath(frameDir);
        outPath = string.IsNullOrWhiteSpace(outPath)
            ? Path.Combine(AppPaths.Dir, "data", "guide-corpus-extra.json")
            : Path.GetFullPath(outPath);

        if (!Directory.Exists(frameDir)) { Cli.Say("帧目录不存在: " + frameDir); return 2; }

        var files = Directory.GetFiles(frameDir)
            .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        if (files.Count == 0) { Cli.Say("没有图片"); return 2; }

        using var ocr = new OcrService();
        ocr.InitializeAsync(AppPaths.ModelsRoot).GetAwaiter().GetResult();
        Cli.Say("OCR: " + ocr.Status);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<object>();
        int scanned = 0, kept = 0;

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
                    var text = (b.Text ?? "").Trim();
                    if (!LineWorthy(text)) continue;
                    var key = Regex.Replace(text, @"[\s\p{P}]", "");
                    if (!seen.Add(key)) continue;

                    int act = GuessAct(text);
                    entries.Add(new
                    {
                        id = "scan-" + (string.IsNullOrEmpty(bvid) ? "" : bvid + "-") + (stamp.Length > 0 ? stamp + "s-" : "") + seen.Count,
                        act,
                        tags = new[] { act > 0 ? $"第{act}幕" : "通用", "视频OCR" },
                        title = text.Length > 16 ? text[..16] + "…" : text,
                        text,
                        source = string.IsNullOrEmpty(bvid) ? "scan" : $"{bvid}@{stamp}s",
                    });
                    kept++;
                }
            }
            catch (Exception ex) { Cli.Say($"  {Path.GetFileName(f)} 失败: {ex.Message}"); }
        }

        Cli.Say($"扫描 {scanned}/{files.Count} 帧，收录 {kept} 条（去重后）");
        foreach (var e in entries.Take(12))
        {
            var je = JsonSerializer.Serialize(e, TemplateFactory.JsonOpts).Replace("\n", " ");
            Cli.Say("  " + (je.Length > 150 ? je[..150] + "…" : je));
        }
        if (entries.Count > 12) Cli.Say($"  …… 其余 {entries.Count - 12} 条略");

        var payload = new
        {
            note = "由 --corpus-scan 从视频帧 OCR 抽出，自动过滤界面文案；合并进检索语料前建议人工扫一眼。",
            generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            bvid = string.IsNullOrEmpty(bvid) ? null : bvid,
            entries,
        };
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
            File.WriteAllText(outPath, JsonSerializer.Serialize(payload, TemplateFactory.JsonOpts), Encoding.UTF8);
            Cli.Say("已写: " + outPath);
        }
        catch (Exception ex) { Cli.Say("写文件失败: " + ex.Message); return 1; }

        return entries.Count > 0 ? 0 : 3;
    }
}
