using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TheaterAssistant;

/// <summary>
/// 两个能力（都基于"已有项目/已跑通链路"，不新造轮子）：
///
///  1) --substitutes <角色名> [--act N]
///     回答「没有这个角色时带谁」：按职能相似度 + 本期元素限制给替代项。
///     数据源 data/substitutes.json（公开静态知识 + 50 视频跨 UP 主共识）。
///
///  2) --compare-ops <视频目录> [--out report.md]
///     **模拟运行后再对比视频原操作**：
///       · 原操作真值来自 ingest.py 抽出的 UP 简介「第N幕：A+B+C」（subs.txt），
///         这是 UP 主主动写下的配队，比 OCR 猜"选中态"可靠；
///       · 预测来自本项目 Routine 的 recommended（跨 UP 主共识推荐）；
///       · 对比命中率 = 我们的推荐里有多少出现在该视频的实际配队里。
///     注意：这是**跨视频/跨 UP 主**的对比，不是"同一帧里读选中高亮"。
/// </summary>
internal static class OpCompare
{
    internal sealed class CharInfo
    {
        public string Element = "";
        public List<string> Roles = new();
        public string Tier = "";
        public string Note = "";
        public string AliasOf = "";
    }

    internal sealed class SubRule
    {
        public string Role = "";
        public List<string> Prefer = new();
        public string Why = "";
    }

    private static readonly Dictionary<string, CharInfo> _chars = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> _roleLabels = new(StringComparer.Ordinal);
    private static readonly List<SubRule> _rules = new();
    private static readonly HashSet<string> _seasonElements = new(StringComparer.Ordinal);
    private static bool _loaded;

    public static bool Loaded => _loaded;

    public static void Load(string path)
    {
        _chars.Clear(); _roleLabels.Clear(); _rules.Clear(); _seasonElements.Clear();
        _loaded = false;
        try
        {
            if (!File.Exists(path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            if (root.TryGetProperty("roles", out var roles))
                foreach (var p in roles.EnumerateObject())
                {
                    var label = p.Value.TryGetProperty("label", out var l) ? l.GetString() ?? p.Name : p.Name;
                    _roleLabels[p.Name] = label;
                }

            if (root.TryGetProperty("characters", out var chars))
                foreach (var p in chars.EnumerateObject())
                {
                    var ci = new CharInfo
                    {
                        Element = p.Value.TryGetProperty("element", out var e) ? e.GetString() ?? "" : "",
                        Tier = p.Value.TryGetProperty("tier", out var t) ? t.GetString() ?? "" : "",
                        Note = p.Value.TryGetProperty("note", out var n) ? n.GetString() ?? "" : "",
                        AliasOf = p.Value.TryGetProperty("aliasOf", out var a) ? a.GetString() ?? "" : "",
                    };
                    if (p.Value.TryGetProperty("roles", out var rs) && rs.ValueKind == JsonValueKind.Array)
                        foreach (var r in rs.EnumerateArray())
                            if (r.ValueKind == JsonValueKind.String && r.GetString() is { Length: > 0 } s) ci.Roles.Add(s);
                    _chars[p.Name] = ci;
                }

            if (root.TryGetProperty("substitutionRules", out var rules) && rules.ValueKind == JsonValueKind.Array)
                foreach (var r in rules.EnumerateArray())
                {
                    var sr = new SubRule
                    {
                        Role = r.TryGetProperty("role", out var k) ? k.GetString() ?? "" : "",
                        Why = r.TryGetProperty("why", out var w) ? w.GetString() ?? "" : "",
                    };
                    if (r.TryGetProperty("prefer", out var pf) && pf.ValueKind == JsonValueKind.Array)
                        foreach (var x in pf.EnumerateArray())
                            if (x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 } s) sr.Prefer.Add(s);
                    _rules.Add(sr);
                }

            if (root.TryGetProperty("seasonElements", out var se) && se.ValueKind == JsonValueKind.Array)
                foreach (var x in se.EnumerateArray())
                    if (x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 } s) _seasonElements.Add(s);

            _loaded = _chars.Count > 0 && _rules.Count > 0;
        }
        catch { _loaded = false; }
    }

    /// <summary>把别称归一到正式名（皇女→菲谢尔、一斗→荒泷一斗）。</summary>
    internal static string Canonical(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var n = name.Trim();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (_chars.TryGetValue(n, out var ci) && !string.IsNullOrEmpty(ci.AliasOf) && seen.Add(n))
            n = ci.AliasOf;
        return n;
    }

    /// <summary>纯函数：给某角色的替代列表（同职能优先、本期元素加权、排除自身/别称）。</summary>
    internal static List<(string Name, string Why)> SubstitutesFor(string name, int? act = null)
    {
        var outList = new List<(string, string)>();
        if (!_loaded) return outList;
        var canon = Canonical(name);
        if (!_chars.TryGetValue(canon, out var src)) return outList;

        var scored = new Dictionary<string, (int Score, string Why)>(StringComparer.Ordinal);
        foreach (var role in src.Roles)
        {
            var rule = _rules.FirstOrDefault(r => r.Role == role);
            if (rule == null) continue;
            foreach (var cand in rule.Prefer)
            {
                var c = Canonical(cand);
                if (c == canon || !_chars.TryGetValue(c, out var ci)) continue;
                int s = 3;
                if (ci.Element == src.Element) s += 2;
                if (_seasonElements.Contains(ci.Element)) s += 1;
                if (ci.Tier == "S") s += 1;
                var why = $"{(_roleLabels.TryGetValue(role, out var lb) ? lb : role)}：{rule.Why}";
                if (!scored.TryGetValue(c, out var old) || s > old.Score) scored[c] = (s, why);
            }
        }
        foreach (var kv in scored.OrderByDescending(k => k.Value.Score).ThenBy(k => k.Key, StringComparer.Ordinal))
            outList.Add((kv.Key, kv.Value.Why));
        return outList;
    }

    // ---------------- 原操作真值解析 ----------------

    private static readonly Regex DescLine = new(@"^\[description\|\]\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex ActTeam = new(@"(?:第)?\s*(\d{1,2})\s*幕\s*[：:]\s*([^\n]{2,80})", RegexOptions.Compiled);
    private static readonly Regex NeedTeam = new(@"需要选出角色[：:]\s*([^\n]{2,120})", RegexOptions.Compiled);
    private static readonly Regex SegAct = new(@"(?<!\d)(\d{1,2})\s*幕\s*([^、，,；;]{2,40})", RegexOptions.Compiled);
    private static readonly Regex RoleToken = new(@"[\u4e00-\u9fff]{2,8}", RegexOptions.Compiled);
    private static readonly Regex RoleNoise = new(@"(挂件|主C|主c|辅助|后台|破盾|奶|盾|角色|尽量|剩下|需要|自备|偏强|偏弱|高频|组合|自带|水后台|岩挂件|水岩|水雷|感电|双岩|双水)", RegexOptions.Compiled);

    /// <summary>从 subs.txt 解析「幕次 → 实际带的角色」真值表（纯函数，可断言）。</summary>
    internal static Dictionary<int, List<string>> ParseActualTeams(string subsText)
    {
        var map = new Dictionary<int, List<string>>();
        if (string.IsNullOrEmpty(subsText)) return map;

        foreach (var raw in subsText.Split('\n'))
        {
            var line = raw.Trim();
            var m = DescLine.Match(line);
            var body = m.Success ? m.Groups[1].Value.Trim() : line;
            if (body.Length < 4) continue;

            var pairs = new List<(int Act, string Body)>();
            foreach (Match am in ActTeam.Matches(body)) pairs.Add((int.Parse(am.Groups[1].Value), am.Groups[2].Value));
            var nm = NeedTeam.Match(body);
            if (nm.Success)
                foreach (Match sm in SegAct.Matches(nm.Groups[1].Value)) pairs.Add((int.Parse(sm.Groups[1].Value), sm.Groups[2].Value));
            if (pairs.Count == 0) continue;

            foreach (var (act, seg) in pairs)
            {
                if (act < 1 || act > 12) continue;
                if (!map.TryGetValue(act, out var list)) { list = new List<string>(); map[act] = list; }
                foreach (Match rm in RoleToken.Matches(seg))
                {
                    var role = Canonical(rm.Value);
                    if (role.Length < 2 || RoleNoise.IsMatch(role)) continue;
                    if (!_chars.ContainsKey(role)) continue;      // 只认我们收录的角色名
                    if (!list.Contains(role, StringComparer.Ordinal)) list.Add(role);
                }
            }
        }
        return map;
    }

    /// <summary>预测：本项目 Routine 对每幕给出的跨 UP 主共识推荐。</summary>
    internal static List<string> PredictedFor(int act)
    {
        var lines = Routine.LinesFor(act, 1);
        foreach (var l in lines)
        {
            var i = l.IndexOf("优先带：", StringComparison.Ordinal);
            if (i < 0) continue;
            var tail = l[(i + "优先带：".Length)..];
            var cut = tail.IndexOf('（');
            if (cut > 0) tail = tail[..cut];
            return tail.Split('、', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                       .Select(Canonical).Where(x => x.Length > 0).ToList();
        }
        return new List<string>();
    }

    // ---------------- CLI ----------------

    public static int RunSubstitutes(string name, int? act)
    {
        Cli.Attach();
        Load(AppPaths.Data("substitutes.json"));
        if (!_loaded) { Cli.Say("substitutes.json 未加载"); return 2; }
        var canon = Canonical(name);
        if (!_chars.TryGetValue(canon, out var ci)) { Cli.Say("没收录角色: " + name); return 3; }
        var subs = SubstitutesFor(name, act);
        Cli.Say($"▶ {name}{(canon != name ? "（=" + canon + "）" : "")}  {ci.Element} / {string.Join("+", ci.Roles.Select(r => _roleLabels.TryGetValue(r, out var l) ? l : r))}");
        if (ci.Note.Length > 0) Cli.Say("  备注：" + ci.Note);
        Cli.Say("  可替代：");
        foreach (var s in subs.Take(6)) Cli.Say($"    {s.Name} — {s.Why}");
        if (subs.Count == 0) Cli.Say("    （无同职能替代）");
        return 0;
    }

    public static int RunCompare(string dir, string outMd)
    {
        Cli.Attach();
        Load(AppPaths.Data("substitutes.json"));
        Routine.Load(AppPaths.Data("rotations.json"));
        if (!_loaded) { Cli.Say("substitutes.json 未加载"); return 2; }
        dir = Path.GetFullPath(dir);
        if (!Directory.Exists(dir)) { Cli.Say("目录不存在: " + dir); return 2; }

        var subsFiles = Directory.GetFiles(dir, "subs.txt", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToList();
        if (subsFiles.Count == 0) { Cli.Say("没有 subs.txt（先跑 video-ingest 产出文本语料）"); return 2; }

        var report = new StringBuilder();
        report.AppendLine("# 模拟运行 vs 视频原操作（配队对答案）");
        report.AppendLine();
        report.AppendLine("> 原操作 = UP 主简介里写下的「第N幕：A+B+C」；预测 = 本项目跨 UP 主共识推荐。");
        report.AppendLine("> 这是跨视频对比，不是读单帧选中高亮。");
        report.AppendLine();
        report.AppendLine("| 视频 | 幕 | 视频实际带的角色 | 我们的推荐 | 命中 | 命中率 |");
        report.AppendLine("|---|---|---|---|---|---|");

        int totalActs = 0, hitAny = 0, totalPred = 0, totalHit = 0, totalRoleHit = 0, roleHitAny = 0;
        foreach (var f in subsFiles)
        {
            var text = File.ReadAllText(f);
            var actual = ParseActualTeams(text);
            var vid = new DirectoryInfo(Path.GetDirectoryName(f)!).Name;
            foreach (var kv in actual.OrderBy(k => k.Key))
            {
                var pred = PredictedFor(kv.Key);
                var hits = pred.Where(p => kv.Value.Contains(p, StringComparer.Ordinal)).ToList();
                // 职能命中：推荐没带，但带了同职能替代（例如推荐夜兰、实际带行秋）
                var roleHits = new List<string>();
                foreach (var p in pred.Where(p => !kv.Value.Contains(p, StringComparer.Ordinal)))
                {
                    // FirstOrDefault 对元组返回 default((string,string))，Name 为 null，不能直接 .Length
                    var alt = SubstitutesFor(p).FirstOrDefault(a =>
                        !string.IsNullOrEmpty(a.Name) && kv.Value.Contains(a.Name, StringComparer.Ordinal));
                    if (!string.IsNullOrEmpty(alt.Name)) roleHits.Add($"{p}→{alt.Name}");
                }
                totalActs++; totalPred += pred.Count; totalHit += hits.Count; totalRoleHit += roleHits.Count;
                if (hits.Count > 0) hitAny++;
                if (hits.Count + roleHits.Count > 0) roleHitAny++;
                var cell = string.Join("、", hits.Concat(roleHits));
                report.AppendLine($"| {vid} | {kv.Key} | {string.Join("、", kv.Value)} | {(pred.Count == 0 ? "-" : string.Join("、", pred))} | {(cell.Length == 0 ? "-" : cell)} | {(pred.Count == 0 ? "n/a" : (100.0 * hits.Count / pred.Count).ToString("0") + "%")} |");
            }
        }
        double anyRate = totalActs == 0 ? 0 : 100.0 * hitAny / totalActs;
        double exactRate = totalPred == 0 ? 0 : 100.0 * totalHit / totalPred;
        double roleRate = totalPred == 0 ? 0 : 100.0 * (totalHit + totalRoleHit) / totalPred;
        double roleAnyRate = totalActs == 0 ? 0 : 100.0 * roleHitAny / totalActs;
        report.AppendLine();
        report.AppendLine($"- 视频 {subsFiles.Count} 个，有原操作记录的幕次 {totalActs} 个");
        report.AppendLine($"- **精确命中（同一个角色）**：至少 1 个 {hitAny}/{totalActs} = **{anyRate:0.0}%**；逐项 {totalHit}/{totalPred} = **{exactRate:0.0}%**");
        report.AppendLine($"- **职能命中（含同职能替代）**：至少 1 个 {roleHitAny}/{totalActs} = **{roleAnyRate:0.0}%**；逐项 {totalHit + totalRoleHit}/{totalPred} = **{roleRate:0.0}%**");
        report.AppendLine("- 说明：共识推荐是统计倾向，UP 主会按自己账号换同职能角色；**职能命中率**才是这套推荐是否有用的指标。");
        report.AppendLine();
        report.AppendLine("## 未命中时的可替代建议");
        report.AppendLine();
        foreach (var f in subsFiles)
        {
            var actual = ParseActualTeams(File.ReadAllText(f));
            var vid = new DirectoryInfo(Path.GetDirectoryName(f)!).Name;
            foreach (var kv in actual.OrderBy(k => k.Key))
            {
                var pred = PredictedFor(kv.Key);
                foreach (var p in pred.Where(p => !kv.Value.Contains(p, StringComparer.Ordinal)))
                {
                    var alts = SubstitutesFor(p).Where(a => !string.IsNullOrEmpty(a.Name) && kv.Value.Contains(a.Name, StringComparer.Ordinal)).Take(2).ToList();
                    if (alts.Count > 0)
                        report.AppendLine($"- {vid} 第{kv.Key}幕 推荐「{p}」未带，但该视频用的是同职能替代：**{string.Join("、", alts.Select(a => a.Name))}**");
                }
            }
        }

        outMd = string.IsNullOrWhiteSpace(outMd) ? Path.Combine(AppPaths.Dir, "ops-compare.md") : Path.GetFullPath(outMd);
        try { File.WriteAllText(outMd, report.ToString(), Encoding.UTF8); Cli.Say("已写: " + outMd); }
        catch (Exception ex) { Cli.Say("写报告失败: " + ex.Message); return 1; }
        Cli.Say($"对答案：{totalActs} 幕，至少命中 {hitAny}（{anyRate:0.0}%），逐项命中 {totalHit}/{totalPred}（{exactRate:0.0}%）");
        return 0;
    }
}
