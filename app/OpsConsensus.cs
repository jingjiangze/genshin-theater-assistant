using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TheaterAssistant;

/// <summary>
/// 跨视频操作候选：把 N 个 --extract-ops 产物聚合成"多个视频重复出现"的候选操作，
/// 用来产出赛季思路（而不是把单个视频的主观缩写当知识）。
///
///   TheaterAssistant.exe --consensus frames\ --out docs\experience\OPS-CONSENSUS.md
///
/// 规则：
///   · 按文本归一化分组，同时统计不同视频 ID 与稳定 creator_id；
///   · 只有 ≥2 个独立 creator_id 才进入作者共识候选区；缺少 ID 的视频不计独立来源；
///   · UI 文案（"可选角色"/"已选开幕与待命角色" 等）一律过滤。
/// </summary>
internal static class OpsConsensus
{
    internal sealed class Row
    {
        public string Text = "";
        public string Kind = "";
        public List<string> Sources = new();
        public List<string> Creators = new();
        public int Videos => Sources.Select(SplitBv).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).Count();
        public int IndependentCreators => Creators.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    }

    private static readonly HashSet<string> Noise = new(StringComparer.Ordinal)
    {
        "可选角色", "已选开幕与待命角色", "待命角色", "请选择一名待命角色转变为可出战角色",
        "选择1名角色转变为可出战角色", "队伍配置", "敌人选择", "返回", "确认", "取消", "跳过", "继续",
        "神秘收获", "辉彩祝福", "伙伴", "助演", "演出回顾", "关卡详情", "难度选择",
    };

    private static readonly Regex NoiseRe = new(@"^(可选角色|已选开幕与待命角色|待命角色|请选择|选择\d*名|第\s*\d{1,2}\s*幕|Lv\.?\s*\d+|\d+[\.、]|[\d\W]{1,8})$", RegexOptions.Compiled);

    // 游戏内说明/剧情文案（带句号、含世界观词）不是可执行动作；实测 42 视频里
    // "过去的魔女留下的挑战，需要许多伙伴组成的队伍共同攻克。"被 2 个视频重复当成共识。
    // 本机真跑命中：界面文案变体会被 extract-ops 当成"购买/选择"候选
    private static readonly Regex UiChromeRe = new(
        @"^(可选|请选择|选择一个|可从\s*\d+\s*个|需在此|被选用)|转变为可出战角色|预览$|选项效果$",
        RegexOptions.Compiled);

    private static readonly Regex ProseRe = new(@"(挑战|故事|演出|神秘房间|魔女|幻想与真实|永无休止|在故事|需要许多伙伴)[^\n]{0,40}。\s*$", RegexOptions.Compiled);

    internal static string Normalize(string text) =>
        Regex.Replace(text ?? "", @"[\s\p{P}]", "").ToLowerInvariant();

    internal static bool IsNoise(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var t = text.Trim();
        if (t.Length < 4) return true;
        if (Noise.Contains(t)) return true;
        if (NoiseRe.IsMatch(t)) return true;
        if (ProseRe.IsMatch(t)) return true;
        if (UiChromeRe.IsMatch(t)) return true;
        return false;
    }

    internal static string SplitBv(string source)
    {
        if (string.IsNullOrEmpty(source)) return "";
        var i = source.IndexOf('@');
        var head = i > 0 ? source[..i] : source;
        // yt-dlp 链路用 "bilibili:BVxxxx" / "youtube:xxxx" 标出处；去掉平台前缀后仍按视频去重
        var c = head.IndexOf(':');
        if (c > 0) head = head[(c + 1)..];
        if (head.StartsWith("BV", StringComparison.Ordinal)) return head;
        // YouTube 等平台的 id 也当作一个独立视频（只要不是纯时间码/数字）
        if (head.Length >= 6 && Regex.IsMatch(head, @"[A-Za-z]")) return head;
        return "";
    }

    internal sealed class Evidence
    {
        public string Text = "";
        public string Kind = "";
        public string Source = "";
        public string CreatorId = "";
    }

    /// <summary>兼容旧测试/调用方；缺少 creator_id 时不计作独立作者证据。</summary>
    internal static List<Row> Aggregate(IEnumerable<(string text, string kind, string source)> input) =>
        AggregateEvidence((input ?? Array.Empty<(string, string, string)>()).Select(x =>
            new Evidence { Text = x.text, Kind = x.kind, Source = x.source }));

    internal static List<Row> AggregateEvidence(IEnumerable<Evidence> input)
    {
        var map = new Dictionary<string, Row>(StringComparer.Ordinal);
        foreach (var evidence in input ?? Array.Empty<Evidence>())
        {
            if (IsNoise(evidence.Text)) continue;
            var key = Normalize(evidence.Text);
            if (key.Length == 0) continue;
            if (!map.TryGetValue(key, out var row))
            {
                row = new Row { Text = evidence.Text.Trim(), Kind = evidence.Kind ?? "" };
                map[key] = row;
            }
            row.Sources.Add(evidence.Source ?? "");
            if (!string.IsNullOrWhiteSpace(evidence.CreatorId))
                row.Creators.Add(evidence.CreatorId.Trim());
        }
        return map.Values.OrderByDescending(r => r.IndependentCreators)
            .ThenByDescending(r => r.Videos)
            .ThenBy(r => r.Text, StringComparer.Ordinal).ToList();
    }

    /// <summary>从 ops JSON 读取证据，并从同视频 meta.json 补齐稳定作者 ID。</summary>
    internal static IEnumerable<Evidence> ReadFileEvidence(string path)
    {
        if (!File.Exists(path)) yield break;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllText(path)); }
        catch { yield break; }
        var dir = Path.GetDirectoryName(path) ?? ".";
        var videoId = Path.GetFileName(path).Replace("-ops.json", "", StringComparison.OrdinalIgnoreCase);
        var creatorFromMeta = "";
        foreach (var metaPath in new[] {
            Path.Combine(dir, videoId, "meta.json"),
            Path.Combine(dir, "ingest", videoId, "meta.json")
        })
        {
            try
            {
                if (!File.Exists(metaPath)) continue;
                using var meta = JsonDocument.Parse(File.ReadAllText(metaPath));
                if (meta.RootElement.TryGetProperty("creator_id", out var id))
                    creatorFromMeta = JsonText(id);
                if (creatorFromMeta.Length > 0) break;
            }
            catch { }
        }
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("ops", out var ops) || ops.ValueKind != JsonValueKind.Array) yield break;
            foreach (var o in ops.EnumerateArray())
            {
                var creator = o.TryGetProperty("creator_id", out var id) ? JsonText(id) : creatorFromMeta;
                yield return new Evidence {
                    Text = o.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "",
                    Kind = o.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "",
                    Source = o.TryGetProperty("source", out var s) ? s.GetString() ?? "" : "",
                    CreatorId = creator,
                };
            }
        }
    }

    internal static IEnumerable<(string text, string kind, string source)> ReadFile(string path) =>
        ReadFileEvidence(path).Select(x => (x.Text, x.Kind, x.Source));

    private static string JsonText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.GetRawText(),
        _ => "",
    };

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

        var all = files.SelectMany(ReadFileEvidence).ToList();
        var rows = AggregateEvidence(all);
        var consensus = rows.Where(r => r.IndependentCreators >= 2).ToList();
        var singles = rows.Where(r => r.IndependentCreators < 2 && (r.Kind == "combo" || r.Kind == "action" || r.Kind == "buy" || r.Kind == "team")).ToList();

        outPath = string.IsNullOrWhiteSpace(outPath) ? Path.Combine(AppPaths.Dir, "docs", "experience", "OPS-CONSENSUS.md") : Path.GetFullPath(outPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");

        var md = new StringBuilder();
        md.AppendLine("# 跨视频操作候选（自动汇总）");
        md.AppendLine();
        md.AppendLine($"> 来自 {files.Count} 个视频的 {all.Count} 条操作候选；按**不同视频 ID**计数，同一视频重复帧只算 1。转载/同作者多视频不增加独立来源权重。");
        md.AppendLine($"> 独立作者共识条目（≥2 个 creator_id）：**{consensus.Count}** 条；其余候选：{singles.Count} 条。");
        md.AppendLine();
        md.AppendLine("## 跨视频重复候选（仅表示重复出现，不等同已验证知识）");
        md.AppendLine();
        md.AppendLine("| 独立作者数 | 视频数 | 类别 | 操作 | 出处 |");
        md.AppendLine("|---:|---:|---|---|---|");
        foreach (var r in consensus)
            md.AppendLine($"| {r.IndependentCreators} | {r.Videos} | {r.Kind} | {Esc(r.Text)} | {Esc(string.Join("、", r.Sources.Select(SplitBv).Where(x => x.Length > 0).Distinct().Take(4)))} |");
        if (consensus.Count == 0) md.AppendLine("| - | - | 无 | - |");

        md.AppendLine();
        md.AppendLine("## 低证据候选（自动隔离，不进入正式决策）");
        md.AppendLine();
        foreach (var r in singles.Take(30)) md.AppendLine($"- [{r.Kind}] {Esc(r.Text)} ← {Esc(r.Sources.FirstOrDefault() ?? "")}");
        if (singles.Count == 0) md.AppendLine("无");

        md.AppendLine();
        md.AppendLine("## 过滤说明");
        md.AppendLine();
        md.AppendLine("- 过滤 UI 文案：`可选角色`、`已选开幕与待命角色`、`请选择一名待命角色…`、`队伍配置` 等；");
        md.AppendLine("- 没有至少 2 个不同 creator_id 的候选不会进入作者共识区；缺少作者 ID 的来源不计权；");
        md.AppendLine("- 本文件仅生成候选；只有通过自动来源独立性、版本适用性和固定回归门禁后，才允许自动晋级。");

        try
        {
            File.WriteAllText(outPath, md.ToString(), Encoding.UTF8);
            Cli.Say($"独立作者共识 {consensus.Count} 条 / 隔离候选 {singles.Count} 条 / 视频 {files.Count} 个 → {outPath}");
            foreach (var r in consensus.Take(10)) Cli.Say($"  [{r.IndependentCreators} creators / {r.Videos} videos] {r.Text}");
        }
        catch (Exception ex) { Cli.Say("写文件失败: " + ex.Message); return 1; }
        return 0;
    }

    private static string Esc(string s) => (s ?? "").Replace("|", "/").Replace("\n", " ").Trim();
}
