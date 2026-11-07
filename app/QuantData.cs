using System.Text.Json;
using System.Text.RegularExpressions;

namespace TheaterAssistant;

/// <summary>
/// P1 数据契约：把「敌人侧」和「我方侧」的数据**量化成可评分的结构**。
///
/// 设计边界（审计见 docs/QUANT-DECISION-AUDIT-PLAN.md）：
///   · 只用仓库里已有的结构化数据（season29.json 的抗性/编成、act-playbook.json 的机制文本、
///     characters.json 的元素表、substitutes.json 的职能规则）；
///   · 任何一项取不到就留空并标 `source=""`，**绝不外推**；
///   · 我方池子缺失（没有 roster.json）时 `Full=false`，调用方必须说明"按通用池"。
/// </summary>
internal static class QuantData
{
    // ---------------- 敌人侧 ----------------

    internal sealed class EnemyProfile
    {
        public int Act;
        public string Difficulty = "";
        public int Level;
        public bool Boss;
        public List<string> Waves = new();
        /// <summary>元素 → 抗性%（负=弱点，正=抗性）。来源 season29.json。</summary>
        public Dictionary<string, int> Resist = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>机制时间窗（秒）。0=未知。来源 act-playbook.json 文本。</summary>
        public int WindowSeconds;
        public string MechanicTrigger = "";
        public string MechanicEffect = "";
        /// <summary>需求向量：元素 → 权重 0..1。</summary>
        public Dictionary<string, double> Need = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Sources = new();
        public bool HasAnyData => Level > 0 || Resist.Count > 0 || Waves.Count > 0;
    }

    private static Dictionary<string, JsonElement> _difficulties = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, JsonElement> _actsOf = new(StringComparer.Ordinal);

    public static bool LoadEnemies(string seasonPath)
    {
        _difficulties.Clear();
        _actsOf.Clear();
        try
        {
            if (!File.Exists(seasonPath)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(seasonPath));
            if (!doc.RootElement.TryGetProperty("acts", out var acts) || acts.ValueKind != JsonValueKind.Object)
                return false;
            foreach (var d in acts.EnumerateObject())
                if (d.Value.ValueKind == JsonValueKind.Object)
                    _actsOf[d.Name] = d.Value.Clone();
            return _actsOf.Count > 0;
        }
        catch { return false; }
    }

    public static IReadOnlyList<string> Difficulties => _actsOf.Keys.ToList();

    /// <summary>取某难度某幕的量化档案；没有该难度/该幕就返回 null（不编）。</summary>
    public static EnemyProfile Enemy(int act, string difficulty)
    {
        if (string.IsNullOrWhiteSpace(difficulty)) difficulty = DefaultDifficulty();
        var dif = _actsOf.TryGetValue(difficulty, out var table) ? table
                : _actsOf.TryGetValue(DefaultDifficulty(), out var fb) ? fb : default;
        if (dif.ValueKind != JsonValueKind.Object) return null;
        if (!dif.TryGetProperty(act.ToString(), out var node) || node.ValueKind != JsonValueKind.Object) return null;

        var p = new EnemyProfile { Act = act, Difficulty = difficulty };
        p.Sources.Add("season29.json");

        if (node.TryGetProperty("level", out var lv) && lv.ValueKind == JsonValueKind.Number) p.Level = lv.GetInt32();
        if (node.TryGetProperty("boss", out var bs) && bs.ValueKind == JsonValueKind.True) p.Boss = true;

        if (node.TryGetProperty("encounters", out var enc) && enc.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in enc.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var name = Str(e, "name");
                if (name.Length > 0) p.Waves.Add(name);
                CollectResist(e, p.Resist);
            }
        }

        // 机制时间窗：从 act-playbook 的 boss 文本里抽「约 15 秒 / 16 秒内 / 10 秒」这类
        var boss = Playbook.Boss(act);
        if (!string.IsNullOrEmpty(boss))
        {
            var m = Regex.Match(boss, @"(\d{1,2})\s*秒");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var secs) && secs is > 0 and <= 60)
                p.WindowSeconds = secs;
            var trig = Regex.Match(boss, @"雷推|雷元素|怒气条");
            if (trig.Success) p.MechanicTrigger = "Electro";
            p.MechanicEffect = Trim(boss, 90);
            p.Sources.Add("act-playbook.json");
        }

        BuildNeed(p);
        return p;
    }

    private static void CollectResist(JsonElement encounter, Dictionary<string, int> into)
    {
        var resText = Str(encounter, "res");
        if (resText.Length == 0) return;
        // 形如 "Oprichniki Line Trooper: Physical -20%." / "X: Electro 10%."
        foreach (Match m in Regex.Matches(resText, @"([A-Za-z]+)\s*(-?\d{1,3})\s*%"))
        {
            var el = m.Groups[1].Value;
            if (!KnownElements.Contains(el)) continue;
            if (!int.TryParse(m.Groups[2].Value, out var v)) continue;
            into[el] = into.TryGetValue(el, out var old) ? Math.Min(old, v) : v;
        }
    }

    private static readonly HashSet<string> KnownElements = new(StringComparer.OrdinalIgnoreCase)
    { "Physical", "Anemo", "Geo", "Electro", "Hydro", "Pyro", "Cryo", "Dendro" };

    /// <summary>
    /// 需求向量：①抗性为负的元素（弱点）②playbook 明确要求的功能元素。
    /// 两者都取不到 → 空字典（调用方不得凭空加分）。
    /// </summary>
    private static void BuildNeed(EnemyProfile p)
    {
        foreach (var kv in p.Resist)
            if (kv.Value < 0)
                p.Need[kv.Key] = Math.Min(1.0, -kv.Value / 20.0);

        var (needEl, _) = Playbook.Need(p.Act);
        if (!string.IsNullOrEmpty(needEl))
            p.Need[needEl] = Math.Max(p.Need.TryGetValue(needEl, out var w) ? w : 0, 1.0);
    }

    // ---------------- 我方侧 ----------------

    internal sealed class Member
    {
        public string Name = "";
        public string Element = "";
        public string Role = "";
        public int Level;
        public bool Trial;
    }

    internal sealed class TeamProfile
    {
        public List<Member> Members = new();
        public Dictionary<string, int> Cover = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Roles = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>池子是否完整（读到真实 roster）。false = 通用池，调用方必须说明。</summary>
        public bool Full;
        public string Source = "";
        public bool HasData => Members.Count > 0;
    }

    private static Dictionary<string, string> _elementOf = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string> _roleOf = new(StringComparer.OrdinalIgnoreCase);
    private static List<string> _seasonElements = new();

    public static bool LoadCharacters(string charactersPath, string substitutesPath)
    {
        _elementOf = new(StringComparer.OrdinalIgnoreCase);
        _roleOf = new(StringComparer.OrdinalIgnoreCase);
        _seasonElements = new List<string>();
        try
        {
            if (File.Exists(charactersPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(charactersPath));
                var root = doc.RootElement;

                // characters 是 {英文名: "元素"}；zhNames 是 {英文名: 中文名}
                // 必须做英文→中文的反转，否则用中文名（行秋/菲谢尔）查不到元素，
                // 覆盖永远为 0、评分恒等于基线（断言「评分单调性」抓到过）。
                var enToElement = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (root.TryGetProperty("characters", out var chars) && chars.ValueKind == JsonValueKind.Object)
                    foreach (var e in chars.EnumerateObject())
                        if (e.Value.ValueKind == JsonValueKind.String && e.Value.GetString() is { Length: > 0 } el)
                        {
                            enToElement[e.Name] = el;
                            _elementOf[e.Name] = el;
                        }
                if (root.TryGetProperty("elements", out var els) && els.ValueKind == JsonValueKind.Object)
                    foreach (var e in els.EnumerateObject())
                        if (e.Value.ValueKind == JsonValueKind.String && e.Value.GetString() is { Length: > 0 } s)
                            _elementOf[e.Name] = s;
                if (root.TryGetProperty("zhNames", out var zh) && zh.ValueKind == JsonValueKind.Object)
                    foreach (var e in zh.EnumerateObject())
                        if (e.Value.ValueKind == JsonValueKind.String && e.Value.GetString() is { Length: > 0 } cn)
                            _elementOf[cn] = enToElement.TryGetValue(e.Name, out var el2) ? el2 : "";
            }
            if (File.Exists(substitutesPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(substitutesPath));
                var root = doc.RootElement;
                if (root.TryGetProperty("substitutionRules", out var rules) && rules.ValueKind == JsonValueKind.Array)
                    foreach (var r in rules.EnumerateArray())
                    {
                        var role = Str(r, "role");
                        if (role.Length == 0) continue;
                        if (r.TryGetProperty("prefer", out var pref) && pref.ValueKind == JsonValueKind.Array)
                            foreach (var n in pref.EnumerateArray())
                                if (n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } nm)
                                    _roleOf.TryAdd(nm, role);
                    }
                if (root.TryGetProperty("seasonElements", out var se) && se.ValueKind == JsonValueKind.Array)
                    foreach (var n in se.EnumerateArray())
                        if (n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } s)
                            _seasonElements.Add(s);
            }
            return _elementOf.Count > 0 || _roleOf.Count > 0;
        }
        catch { return false; }
    }

    public static IReadOnlyList<string> SeasonElements => _seasonElements;

    /// <summary>读真实池子；没有 roster.json 返回 Full=false 的空档案（绝不假装个性化）。</summary>
    public static TeamProfile Team(string rosterPath)
    {
        var t = new TeamProfile();
        if (!File.Exists(rosterPath)) { t.Full = false; t.Source = "(无 roster.json，按通用池)"; return t; }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(rosterPath));
            var root = doc.RootElement;
            JsonElement arr = default;
            if (root.ValueKind == JsonValueKind.Array) arr = root;
            else if (!root.TryGetProperty("characters", out arr) && !root.TryGetProperty("members", out arr)) return t;
            if (arr.ValueKind != JsonValueKind.Array) return t;
            foreach (var e in arr.EnumerateArray())
            {
                var name = e.ValueKind == JsonValueKind.String ? e.GetString() ?? ""
                         : Str(e, "name");
                if (name.Length == 0) continue;
                var m = new Member
                {
                    Name = name,
                    Element = e.ValueKind == JsonValueKind.Object ? Str(e, "element") : "",
                    Level = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("level", out var lv) && lv.ValueKind == JsonValueKind.Number ? lv.GetInt32() : 0,
                    Trial = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("trial", out var tr) && tr.ValueKind == JsonValueKind.True,
                };
                if (m.Element.Length == 0) m.Element = ElementOf(name);
                m.Role = _roleOf.TryGetValue(name, out var r) ? r : "";
                t.Members.Add(m);
            }
            t.Full = t.Members.Count > 0;
            t.Source = rosterPath;
            BuildCover(t);
            return t;
        }
        catch { return t; }
    }

    /// <summary>通用池：本季开放元素 × 职能规则里的代表角色（池子缺失时的占位，不冒充你的池子）。</summary>
    public static TeamProfile GenericTeam()
    {
        var t = new TeamProfile { Full = false, Source = "(通用池：seasonElements + 职能代表)" };
        foreach (var el in _seasonElements)
            t.Cover[el] = Math.Max(t.Cover.TryGetValue(el, out var c) ? c : 0, 1);
        foreach (var kv in _roleOf)
        {
            t.Roles[kv.Value] = Math.Max(t.Roles.TryGetValue(kv.Value, out var c) ? c : 0, 1);
            var el = ElementOf(kv.Key);
            if (el.Length > 0) t.Cover[el] = Math.Max(t.Cover.TryGetValue(el, out var c2) ? c2 : 0, 1);
        }
        return t;
    }

    private static void BuildCover(TeamProfile t)
    {
        foreach (var m in t.Members)
        {
            if (m.Element.Length > 0) t.Cover[m.Element] = t.Cover.TryGetValue(m.Element, out var c) ? c + 1 : 1;
            if (m.Role.Length > 0) t.Roles[m.Role] = t.Roles.TryGetValue(m.Role, out var r) ? r + 1 : 1;
        }
    }

    public static string ElementOf(string name) => _elementOf.TryGetValue(name, out var el) ? el : "";

    private static string DefaultDifficulty() =>
        _actsOf.ContainsKey("Lunar") ? "Lunar" : (_actsOf.Count > 0 ? _actsOf.Keys.First() : "Normal");

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static string Trim(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");

    // ---------------- CLI ----------------

    public static int RunEnemyCli(int act, string difficulty)
    {
        Cli.Attach();
        LoadEnemies(AppPaths.Data("season29.json"));
        Playbook.Load(AppPaths.Data("act-playbook.json"));
        if (_actsOf.Count == 0) { Cli.Say("season29.json 未加载"); return 2; }
        Cli.Say($"难度: {string.Join("/", Difficulties)}｜默认 {DefaultDifficulty()}");
        var acts = act > 0 ? new[] { act } : Enumerable.Range(1, 12).ToArray();
        int shown = 0;
        foreach (var a in acts)
        {
            var p = Enemy(a, difficulty);
            if (p == null || !p.HasAnyData) { Cli.Say($"  第{a}幕: 无数据"); continue; }
            shown++;
            var res = p.Resist.Count == 0 ? "-" : string.Join(",", p.Resist.OrderBy(k => k.Value).Select(k => $"{k.Key}{k.Value}%"));
            var need = p.Need.Count == 0 ? "-" : string.Join(",", p.Need.OrderByDescending(k => k.Value).Select(k => $"{k.Key}={k.Value:0.0}"));
            Cli.Say($"  第{a}幕 Lv{p.Level}{(p.Boss ? " BOSS" : "")}｜波次{p.Waves.Count}｜抗性 {res}｜需求 {need}｜窗口 {(p.WindowSeconds > 0 ? p.WindowSeconds + "s" : "-")}{(p.MechanicTrigger.Length > 0 ? "/" + p.MechanicTrigger : "")}");
        }
        Cli.Say($"有数据幕数 {shown}/12（来源: season29.json + act-playbook.json）");
        return 0;
    }

    public static int RunTeamCli()
    {
        Cli.Attach();
        LoadCharacters(AppPaths.Data("characters.json"), AppPaths.Data("substitutes.json"));
        var real = Team(Path.Combine(AppPaths.Dir, "roster.json"));
        var t = real.HasData ? real : GenericTeam();
        Cli.Say($"池子来源: {t.Source}｜Full={t.Full}｜成员 {t.Members.Count}");
        if (t.Members.Count > 0)
            foreach (var m in t.Members.Take(20))
                Cli.Say($"  {m.Name} {m.Element} {m.Role} Lv{m.Level}{(m.Trial ? " 试用" : "")}");
        Cli.Say("元素覆盖: " + string.Join(", ", t.Cover.Select(kv => $"{kv.Key}×{kv.Value}")));
        Cli.Say("职能覆盖: " + string.Join(", ", t.Roles.Select(kv => $"{kv.Key}×{kv.Value}")));
        Cli.Say("本季开放元素: " + (SeasonElements.Count > 0 ? string.Join("/", SeasonElements) : "(未读到)"));
        return 0;
    }
}
