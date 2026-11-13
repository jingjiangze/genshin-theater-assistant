using System.Text.Json;

namespace TheaterAssistant;

/// <summary>
/// 决策策略（数据驱动）：**代码不再硬编码决策内容**。
///
/// 依据 docs/QUANT-DECISION-AUDIT-PLAN.md §10 的审计结论，原先散在代码里的这些内容全部搬到
/// `data/decision-policy.json`：
///   · 难度权重（"Lunar 最重要"这类取舍）
///   · 队形人数、每元素充分覆盖人数
///   · 治疗/破盾的**触发关键词**（之前在 3 处各写一份，且其中一处多了"力竭"→ 判定不一致）
///   · 抗性归一系数、职能加分、无效元素惩罚
///   · 各来源的置信度
///   · 角色别名（心海 → 珊瑚宫心海）
///
/// 缺失字段一律用**中性默认**并标记 `Source=default`，绝不假装"数据里有"。
/// </summary>
internal static class DecisionPolicy
{
    public static string Source { get; private set; } = "(未加载)";

    public static int TeamSize { get; private set; } = 4;
    public static int CoveragePerElement { get; private set; } = 2;

    /// <summary>
    /// 幕次连续未识别多久后弃权（秒）。此前 data/decision-policy.json 里写着这个字段，
    /// 但**全仓没有读取方**（改配置不生效，审计发现）；现在由 ActStabilizer 消费。
    /// </summary>
    public static int AbstainAfterSeconds { get; private set; } = 10;

    private static readonly Dictionary<string, double> _diffWeights = new(StringComparer.Ordinal);
    private static double _defaultDiffWeight = 0.5;

    private static List<string> _healerTriggers = new() { "奶", "治疗", "标记" };
    private static List<string> _breakerTriggers = new() { "破锅", "钝击", "破盾", "护盾" };

    private static double _resistNormalizer = 20;
    private static double _roleBonus = 0.15;
    private static double _wastePenalty = 0.04;
    private static double _roleNeutral = 0.5;
    private static double _candidateRoleBonus = 0.8;
    private static double _mainDpsBufferBonus = 0.15;

    private static readonly Dictionary<string, double> _confidence = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);

    public static void Load(string path)
    {
        Source = "(未加载)";
        try
        {
            if (!File.Exists(path)) { Source = "(缺 decision-policy.json：全部用中性默认)"; return; }
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;

            TeamSize = IntOf(r, "teamSize", TeamSize);
            CoveragePerElement = IntOf(r, "coveragePerElement", CoveragePerElement);

            if (r.TryGetProperty("difficultyWeights", out var dw) && dw.ValueKind == JsonValueKind.Object)
                foreach (var e in dw.EnumerateObject())
                    if (e.Value.ValueKind == JsonValueKind.Number) _diffWeights[e.Name] = e.Value.GetDouble();
            _defaultDiffWeight = NumOf(r, "defaultDifficultyWeight", _defaultDiffWeight);

            if (r.TryGetProperty("abstain", out var ab) && ab.ValueKind == JsonValueKind.Object)
                AbstainAfterSeconds = Math.Max(1, IntOf(ab, "abstainAfterSeconds", AbstainAfterSeconds));

            if (r.TryGetProperty("roleTriggers", out var rt) && rt.ValueKind == JsonValueKind.Object)
            {
                _healerTriggers = StrList(rt, "healer") ?? _healerTriggers;
                _breakerTriggers = StrList(rt, "breaker") ?? _breakerTriggers;
            }

            if (r.TryGetProperty("scoring", out var sc) && sc.ValueKind == JsonValueKind.Object)
            {
                _resistNormalizer = NumOf(sc, "resistNormalizer", _resistNormalizer);
                _roleBonus = NumOf(sc, "roleBonus", _roleBonus);
                _wastePenalty = NumOf(sc, "wastePenalty", _wastePenalty);
                _roleNeutral = NumOf(sc, "roleNeutralWhenNoNeed", _roleNeutral);
                _candidateRoleBonus = NumOf(sc, "candidateRoleBonus", _candidateRoleBonus);
                _mainDpsBufferBonus = NumOf(sc, "mainDpsBufferBonus", _mainDpsBufferBonus);
            }

            if (r.TryGetProperty("confidence", out var cf) && cf.ValueKind == JsonValueKind.Object)
                foreach (var e in cf.EnumerateObject())
                    if (e.Value.ValueKind == JsonValueKind.Number) _confidence[e.Name] = e.Value.GetDouble();

            if (r.TryGetProperty("aliases", out var al) && al.ValueKind == JsonValueKind.Object)
                foreach (var e in al.EnumerateObject())
                    if (e.Value.ValueKind == JsonValueKind.String && e.Value.GetString() is { Length: > 0 } v)
                        _aliases[e.Name] = v;

            Source = System.IO.Path.GetFileName(path);
        }
        catch { Source = "(解析失败：用中性默认)"; }
    }

    // ---------------- 查询 ----------------

    public static double DifficultyWeight(string difficulty) =>
        difficulty != null && _diffWeights.TryGetValue(difficulty, out var w) ? w : _defaultDiffWeight;

    public static bool NeedsHealer(string mechanicText) => ContainsAny(mechanicText, _healerTriggers);

    public static bool NeedsBreaker(string mechanicText) => ContainsAny(mechanicText, _breakerTriggers);

    public static double ResistNormalizer => _resistNormalizer;
    public static double RoleBonus => _roleBonus;
    public static double WastePenalty => _wastePenalty;
    public static double RoleNeutralWhenNoNeed => _roleNeutral;
    public static double CandidateRoleBonus => _candidateRoleBonus;
    public static double MainDpsBufferBonus => _mainDpsBufferBonus;

    public static double ConfidenceOf(string kind, double fallback) =>
        kind != null && _confidence.TryGetValue(kind, out var v) ? v : fallback;

    /// <summary>角色别名归一（心海 → 珊瑚宫心海）；查不到原样返回。</summary>
    public static string Canonical(string name) =>
        name != null && _aliases.TryGetValue(name.Trim(), out var v) ? v : (name ?? "");

    public static int AliasCount => _aliases.Count;
    public static int HealerTriggerCount => _healerTriggers.Count;
    public static int DifficultyWeightCount => _diffWeights.Count;

    private static bool ContainsAny(string text, List<string> words)
    {
        if (string.IsNullOrEmpty(text) || words == null) return false;
        foreach (var w in words)
            if (w.Length > 0 && text.Contains(w, StringComparison.Ordinal)) return true;
        return false;
    }

    private static int IntOf(JsonElement e, string name, int fallback) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;

    private static double NumOf(JsonElement e, string name, double fallback) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;

    private static List<string> StrList(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        var list = new List<string>();
        foreach (var n in arr.EnumerateArray())
            if (n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } s) list.Add(s);
        return list.Count > 0 ? list : null;
    }

    public static int RunCli()
    {
        Cli.Attach();
        Load(AppPaths.Data("decision-policy.json"));
        Cli.Say($"策略来源: {Source}");
        Cli.Say($"队形 {TeamSize} 人｜每元素充分覆盖 {CoveragePerElement} 人｜难度权重 {DifficultyWeightCount} 个");
        Cli.Say("难度权重: " + string.Join(", ", (new[] { "Easy", "Normal", "Hard", "Visionary", "Lunar" })
            .Select(d => $"{d}={DifficultyWeight(d):0.00}")) + $"（默认 {_defaultDiffWeight:0.00}）");
        Cli.Say($"触发词 healer={string.Join("/", _healerTriggers)}｜breaker={string.Join("/", _breakerTriggers)}");
        Cli.Say($"评分 resistNormalizer={ResistNormalizer} roleBonus={RoleBonus} wastePenalty={WastePenalty} candidateRoleBonus={CandidateRoleBonus}");
        Cli.Say("置信度: " + string.Join(", ", _confidence.Select(kv => $"{kv.Key}={kv.Value:0.00}")));
        Cli.Say($"别名 {AliasCount} 条: " + string.Join(", ", _aliases.Take(6).Select(kv => $"{kv.Key}→{kv.Value}")));
        return 0;
    }
}
