using System.Text.Json;

namespace TheaterAssistant;

/// <summary>
/// 幕级推荐（curated picks）：把仓库里**已有的**两处推荐信号汇成一张「幕 → 推荐角色」表，
/// 供配队选人时加权。
///
/// 数据源（都是已有设施，不新增采集）：
///   1. `data/rotations.json` 的 `communityRecommended`——跨 UP 主共识的该幕推荐角色；
///   2. `data/knowledge-release.json` 的 VERIFIED 候选，文本形如「第N幕 推荐 X」——
///      经固定 holdout + ≥2 独立作者验证后才发布。
///
/// 为什么需要（决策层审计）：
///   此前 `Playbook.VerifiedFor(act)` 只把已验证知识当**一行提示文本**显示，**没参与选人**；
///   而配队只按「元素匹配 + 职能」打分，完全不知道"这一幕社区推荐谁"。
///   结果是：第3幕明明有 2 位作者共识推荐「爱诺/赛诺」，选人逻辑对此一无所知。
/// </summary>
internal static class CuratedPicks
{
    /// <summary>幕 → (角色 → 权重)。权重=来源数（社区共识 1 + 已验证 1，最多 2）。</summary>
    private static readonly Dictionary<int, Dictionary<string, double>> _picks = new();

    public static string Source { get; private set; } = "(未加载)";
    public static int ActCount => _picks.Count;
    public static int TotalPicks => _picks.Values.Sum(d => d.Count);

    public static void Load(string rotationsPath, string releasePath)
    {
        _picks.Clear();
        var parts = new List<string>();

        try
        {
            if (File.Exists(rotationsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(rotationsPath));
                if (doc.RootElement.TryGetProperty("communityRecommended", out var cr)
                    && cr.ValueKind == JsonValueKind.Object)
                {
                    int n = 0;
                    foreach (var actProp in cr.EnumerateObject())
                    {
                        if (!int.TryParse(actProp.Name, out var act)) continue;
                        foreach (var name in ReadNames(actProp.Value))
                        {
                            Add(act, name, 1.0);
                            n++;
                        }
                    }
                    if (n > 0) parts.Add($"社区共识 {n} 条");
                }
            }
        }
        catch { }

        try
        {
            if (File.Exists(releasePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(releasePath));
                if (doc.RootElement.TryGetProperty("candidates", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    int n = 0;
                    foreach (var c in arr.EnumerateArray())
                    {
                        if (c.ValueKind != JsonValueKind.Object) continue;
                        var status = c.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : "";
                        if (!string.IsNullOrEmpty(status) && status != "VERIFIED") continue;
                        var text = c.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String ? tx.GetString() ?? "" : "";
                        var m = System.Text.RegularExpressions.Regex.Match(text, @"^第\s*(\d{1,2})\s*幕\s*推荐\s*([\u4e00-\u9fff]{2,8})$");
                        if (!m.Success) continue;
                        if (!int.TryParse(m.Groups[1].Value, out var act)) continue;
                        Add(act, m.Groups[2].Value, 1.0);
                        n++;
                    }
                    if (n > 0) parts.Add($"已验证知识 {n} 条");
                }
            }
        }
        catch { }

        Source = parts.Count > 0
            ? string.Join(" + ", parts)
            : "(无幕级推荐数据)";
    }

    private static IEnumerable<string> ReadNames(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in v.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 1 } s)
                    yield return s.Trim();
        }
        else if (v.ValueKind == JsonValueKind.Object)
        {
            // 兼容 { chars: [...] } / { recommended: [...] } 形态
            foreach (var key in new[] { "chars", "recommended", "roles" })
                if (v.TryGetProperty(key, out var inner))
                    foreach (var s in ReadNames(inner)) yield return s;
        }
    }

    private static void Add(int act, string name, double w)
    {
        var canon = DecisionPolicy.Canonical(name);
        if (!_picks.TryGetValue(act, out var map)) _picks[act] = map = new Dictionary<string, double>(StringComparer.Ordinal);
        map[canon] = map.TryGetValue(canon, out var old) ? Math.Min(2.0, old + w) : w;
    }

    /// <summary>该幕的推荐角色（已做别名归一）；没有返回空。</summary>
    public static IReadOnlyDictionary<string, double> For(int act) =>
        _picks.TryGetValue(act, out var map) ? map : new Dictionary<string, double>();

    /// <summary>该角色在本幕是否被推荐；返回权重（0=未推荐）。</summary>
    public static double WeightOf(int act, string name)
    {
        var canon = DecisionPolicy.Canonical(name);
        return _picks.TryGetValue(act, out var map) && map.TryGetValue(canon, out var w) ? w : 0;
    }

    public static int RunCli()
    {
        Cli.Attach();
        DecisionPolicy.Load(AppPaths.Data("decision-policy.json"));
        Load(AppPaths.Data("rotations.json"), AppPaths.Data("knowledge-release.json"));
        Cli.Say($"幕级推荐来源: {Source}");
        Cli.Say($"覆盖 {ActCount} 幕 / {TotalPicks} 条推荐");
        foreach (var kv in _picks.OrderBy(k => k.Key))
            Cli.Say($"  第{kv.Key}幕: " + string.Join("、", kv.Value.Select(x => $"{x.Key}({x.Value:0.#})")));
        return ActCount > 0 ? 0 : 3;
    }
}
