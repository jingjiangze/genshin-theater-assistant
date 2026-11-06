using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TheaterAssistant;

/// <summary>
/// 跨视频操作共识：把 N 个 --extract-ops 产物聚合成"多个 UP 主都这么说"的操作，
/// 用来产出赛季思路（而不是把单个视频的主观缩写当知识）。
///
///   TheaterAssistant.exe --consensus frames\ --out docs\experience\OPS-CONSENSUS.md
///
/// 规则：
///   · 按文本归一化分组，统计**不同 BV 号**的个数（同一视频重复帧只算 1）；
///   · ≥2 个视频 → 共识区（高可信）；只有 1 个 → 单例区（combo/action 保留供人工看）；
///   · UI 文案（"可选角色"/"已选开幕与待命角色" 等）一律过滤。
/// </summary>
internal static class OpsConsensus
{
    internal sealed class Row
    {
        public string Text = "";
        public string Kind = "";
        public List<string> Sources = new();
        public int Videos => Sources.Select(SplitBv).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).Count();
    }

    private static readonly HashSet<string> Noise = new(StringComparer.Ordinal)
    {
        "可选角色", "已选开幕与待命角色", "待命角色", "请选择一名待命角色转变为可出战角色",
        "选择1名角色转变为可出战角色", "队伍配置", "敌人选择", "返回", "确认", "取消", "跳过", "继续",
        "神秘收获", "辉彩祝福", "伙伴", "助演", "演出回顾", "关卡详情", "难度选择",
    };

    private static readonly Regex NoiseRe = new(@"^(可选角色|已选开幕与待命角色|待命角色|请选择|选择\d*名|第\s*\d{1,2}\s*幕|Lv\.?\s*\d+|\d+[\.、]|[\d\W]{1,8})$", RegexOptions.Compiled);

    internal static string Normalize(string text) =>
        Regex.Replace(text ?? "", @"[\s\p{P}]", "").ToLowerInvariant();

    internal static bool IsNoise(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var t = text.Trim();
        if (t.Length < 4) return true;
        if (Noise.Contains(t)) return true;
        if (NoiseRe.IsMatch(t)) return true;
        return false;
    }

    internal static string SplitBv(string source)
    {
        if (string.IsNullOrEmpty(source)) return "";
        var i = source.IndexOf('@');
        var head = i > 0 ? source[..i] : source;
        return head.StartsWith("BV", StringComparison.Ordinal) ? head : "";
    }

    /// <summary>纯函数：聚合 (文本,类别,出处) 三元组 → 按不同视频数降序。</summary>
    internal static List<Row> Aggregate(IEnumerable<(string text, string kind, string source)> input)
    {
        var map = new Dictionary<string, Row>(StringComparer.Ordinal);
        foreach (var (text, kind, source) in input ?? Array.Empty<(string, string, string)>())
        {
            if (IsNoise(text)) continue;
            var key = Normalize(text);
            if (key.Length == 0) continue;
            if (!map.TryGetValue(key, out var row))
            {
                row = new Row { Text = text.Trim(), Kind = kind ?? "" };
                map[key] = row;
            }
            row.Sources.Add(source ?? "");
        }
        return map.Values.OrderByDescending(r => r.Videos).ThenBy(r => r.Text, StringComparer.Ordinal).ToList();
    }

    /// <summary>从一个 ops JSON 里读出三元组（供文件循环调用）。</summary>
    internal static IEnumerable<(string text, string kind, string source)> ReadFile(string path)
    {
        if (!File.Exists(path)) yield break;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllText(path)); }
        catch { yield break; }
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("ops", out var ops) || ops.ValueKind != JsonValueKind.Array) yield break;
            foreach (var o in ops.EnumerateArray())
            {
                yield return (
                    o.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "",
                    o.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "",
                    o.TryGetProperty("source", out var s) ? s.GetString() ?? "" : "");
            }
        }
    }

    public static int Run(IEnumerable<string> inputs, string outPath)
    {
        Cli.Attach();
        var files = new List<string>();
        foreach (var i in inputs ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(i)) continue;
            if (Directory.Exists(i)) files.AddRange(Directory.GetFiles(i, "*-ops.json", SearchOption.AllDirectories));
            else if (File.Exists(i) && i.EndsWith("-ops.json", StringComparison.OrdinalIgnoreCase)) files.Add(i);
        }
        files = files.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.Ordinal).ToList();
        if (files.Count == 0) { Cli.Say("没有 *-ops.json（先跑 --extract-ops）"); return 2; }

        var all = files.SelectMany(ReadFile).ToList();
        var rows = Aggregate(all);
        var consensus = rows.Where(r => r.Videos >= 2).ToList();
        var singles = rows.Where(r => r.Videos < 2 && (r.Kind == "combo" || r.Kind == "action")).ToList();

        outPath = string.IsNullOrWhiteSpace(outPath) ? Path.Combine(AppPaths.Dir, "docs", "experience", "OPS-CONSENSUS.md") : Path.GetFullPath(outPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");

        var md = new StringBuilder();
        md.AppendLine("# 跨视频操作共识（自动汇总）");
        md.AppendLine();
        md.AppendLine($"> 来自 {files.Count} 个视频的 {all.Count} 条操作候选；按**不同 BV 号**计数，同一视频重复帧只算 1。");
        md.AppendLine($"> 共识条目（≥2 个视频）：**{consensus.Count}** 条；单例手法/行动：{singles.Count} 条。");
        md.AppendLine();
        md.AppendLine("## 共识操作（多个 UP 主都这么写，可信度最高）");
        md.AppendLine();
        md.AppendLine("| 视频数 | 类别 | 操作 | 出处 |");
        md.AppendLine("|---|---|---|---|");
        foreach (var r in consensus)
            md.AppendLine($"| {r.Videos} | {r.Kind} | {Esc(r.Text)} | {Esc(string.Join("、", r.Sources.Select(SplitBv).Where(x => x.Length > 0).Distinct().Take(4)))} |");
        if (consensus.Count == 0) md.AppendLine("| - | - | 无 | - |");

        md.AppendLine();
        md.AppendLine("## 单例手法 / 行动（需人工复核，不直接进生产数据）");
        md.AppendLine();
        foreach (var r in singles.Take(30)) md.AppendLine($"- [{r.Kind}] {Esc(r.Text)} ← {Esc(r.Sources.FirstOrDefault() ?? "")}");
        if (singles.Count == 0) md.AppendLine("无");

        md.AppendLine();
        md.AppendLine("## 过滤说明");
        md.AppendLine();
        md.AppendLine("- 过滤 UI 文案：`可选角色`、`已选开幕与待命角色`、`请选择一名待命角色…`、`队伍配置` 等；");
        md.AppendLine("- 只有 1 个视频支持、且不是 combo/action 的条目不列出（噪声大）；");
        md.AppendLine("- 本文件是**候选证据**，要进 `data/rotations.json` 仍需人工确认。");

        try
        {
            File.WriteAllText(outPath, md.ToString(), Encoding.UTF8);
            Cli.Say($"共识 {consensus.Count} 条 / 单例 {singles.Count} 条 / 视频 {files.Count} 个 → {outPath}");
            foreach (var r in consensus.Take(10)) Cli.Say($"  [{r.Videos}x] {r.Text}");
        }
        catch (Exception ex) { Cli.Say("写文件失败: " + ex.Message); return 1; }
        return 0;
    }

    private static string Esc(string s) => (s ?? "").Replace("|", "/").Replace("\n", " ").Trim();
}
