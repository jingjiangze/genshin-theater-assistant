using System.Text;
using System.Text.Json;

namespace TheaterAssistant;

/// <summary>
/// 通用最优配队求解器（假设「拥有公开全角色」）。
///
/// 审计要点（docs/QUANT-DECISION-AUDIT-PLAN.md §8）：
///   · 池子 = 本季开放元素 ∩ 公开角色表（129 人里 55 人可带），**不是你的真实池子**；
///   · 每幕每难度独立求解：不同难度敌人等级/抗性不同 → 需求向量不同 → 队伍可以不同；
///   · 4 人队伍从 55 人选：C(55,4)=341,055，可**完全枚举**，不用启发式；
///   · 无需求向量的幕返回"通用队"（治疗 + 主C + 兜底），并显式标注"无元素需求"；
///   · 输出必须含覆盖证明（谁覆盖了哪个需求），玩家才能判断该不该信。
/// </summary>
internal static class TeamSolver
{
    internal sealed class Unit
    {
        public string Name = "";
        public string Element = "";
        public string Role = "";
        public bool Trial;          // 本季可用的试用角色（开场/额外编入常见）
    }

    internal sealed class Plan
    {
        public string Difficulty = "";
        public int Act;
        public List<string> Team = new();
        public List<List<string>> Alts = new();
        public double Score;
        public double ScoreMax;
        public Dictionary<string, double> Need = new();
        public Dictionary<string, int> Cover = new();
        public List<string> Gaps = new();
        public List<string> Reasons = new();
        public bool HasElementNeed;
    }

    private static int TeamSize => DecisionPolicy.TeamSize;

    private static List<Unit> _pool = new();
    private static List<string> _seasonElements = new();

    /// <summary>
    /// 加载角色池。
    /// seasonElementsOverride：指定赛季的开放元素；
    /// rosterNames：**玩家实际拥有的角色名**（null/空 = 假设拥有公开全角色，通用最优）。
    /// </summary>
    public static bool LoadPool(IReadOnlyList<string> seasonElementsOverride = null,
        IReadOnlyCollection<string> rosterNames = null)
    {
        _pool = new List<Unit>();
        _seasonElements = new List<string>();
        try
        {
            var charsPath = AppPaths.Data("characters.json");
            var subsPath = AppPaths.Data("substitutes.json");
            if (!File.Exists(charsPath)) return false;

            var enToElement = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var enToZh = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var doc = JsonDocument.Parse(File.ReadAllText(charsPath)))
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("characters", out var ch) && ch.ValueKind == JsonValueKind.Object)
                    foreach (var e in ch.EnumerateObject())
                        if (e.Value.ValueKind == JsonValueKind.String && e.Value.GetString() is { Length: > 0 } el)
                            enToElement[e.Name] = el;
                if (root.TryGetProperty("zhNames", out var zh) && zh.ValueKind == JsonValueKind.Object)
                    foreach (var e in zh.EnumerateObject())
                        if (e.Value.ValueKind == JsonValueKind.String && e.Value.GetString() is { Length: > 0 } cn)
                            enToZh[e.Name] = cn;
            }

            // 一个角色可以同时是多个职能（芭芭拉/诺艾尔 = 挂水/破盾 + 治疗）。
            // 之前用 Dictionary<string,string> + TryAdd 只保留第一个命中的规则，
            // 结果 Healer 身份被 HydroApplier 覆盖 → 第10幕永远补不到治疗（断言抓到「池内治疗为空」）。
            var roleOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var roles = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (File.Exists(subsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(subsPath));
                var root = doc.RootElement;
                if (root.TryGetProperty("substitutionRules", out var rules) && rules.ValueKind == JsonValueKind.Array)
                    foreach (var r in rules.EnumerateArray())
                    {
                        var role = r.TryGetProperty("role", out var rv) && rv.ValueKind == JsonValueKind.String ? rv.GetString() ?? "" : "";
                        if (role.Length == 0) continue;
                        var list = new List<string>();
                        if (r.TryGetProperty("prefer", out var pref) && pref.ValueKind == JsonValueKind.Array)
                            foreach (var n in pref.EnumerateArray())
                                if (n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } nm)
                                {
                                    list.Add(nm);
                                    var canon = DecisionPolicy.Canonical(nm);   // 心海→珊瑚宫心海 等别名归一
                                    if (!roleOf.TryGetValue(canon, out var rs)) { rs = new List<string>(); roleOf[canon] = rs; }
                                    if (!rs.Contains(role)) rs.Add(role);
                                    if (canon != nm) roleOf[nm] = rs;   // 别名也保留一份，兼容旧数据
                                }
                        roles[role] = list;
                    }
                if (root.TryGetProperty("seasonElements", out var se) && se.ValueKind == JsonValueKind.Array)
                    foreach (var n in se.EnumerateArray())
                        if (n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } s)
                            _seasonElements.Add(s);
            }

            // 指定赛季时用该赛季的元素覆盖 substitutes.json 里的静态值（赛季无关复用）
            if (seasonElementsOverride != null && seasonElementsOverride.Count > 0)
                _seasonElements = seasonElementsOverride.ToList();

            foreach (var kv in enToElement)
            {
                if (_seasonElements.Count > 0 && !_seasonElements.Contains(kv.Value, StringComparer.OrdinalIgnoreCase))
                    continue;   // 本季不带这个元素
                var zh = enToZh.TryGetValue(kv.Key, out var c) && c.Length > 0 ? c : kv.Key;
                _pool.Add(new Unit
                {
                    Name = zh,
                    Element = kv.Value,
                    Role = roleOf.TryGetValue(DecisionPolicy.Canonical(zh), out var rolesFor)
                    ? string.Join("|", rolesFor)
                    : (roleOf.TryGetValue(zh, out var rf2) ? string.Join("|", rf2) : ""),
                });
            }
            // 试用角色补齐：职能规则里点名但池中没有的，**必须先确认本季带得动**。
            // 之前不看元素直接补，导致火元素角色（班尼特）混进本季 Hydro/Electro/Geo 的队伍里，
            // 求解器还给了他职能加分 —— 这类"带不动的人"必须排除（断言「配队·队员本季可带」抓到）。
            var zhToElement = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in enToElement)
                if (enToZh.TryGetValue(kv.Key, out var cn) && cn.Length > 0)
                    zhToElement[cn] = kv.Value;
            // 别名与官方名是同一个角色：池里已有"珊瑚宫心海"就不能再加"心海"，
            // 否则同一角色占两个名额（云端实测 Lunar 第10幕出现「心海/芭芭拉」）。
            var poolKeys = new HashSet<string>(_pool.Select(u => DecisionPolicy.Canonical(u.Name)), StringComparer.Ordinal);
            foreach (var kv in roleOf)
            {
                if (_pool.Any(u => u.Name == kv.Key)) continue;
                if (poolKeys.Contains(DecisionPolicy.Canonical(kv.Key))) continue;
                // 别名先归一到官方名再查元素（心海→珊瑚宫心海→Hydro）：
                // 否则试用角色元素为空，本季可带判断与元素覆盖都会漏掉它。
                var canonKey = DecisionPolicy.Canonical(kv.Key);
                var el = zhToElement.TryGetValue(canonKey, out var known) ? known
                       : (zhToElement.TryGetValue(kv.Key, out var known2) ? known2 : "");
                if (el.Length > 0 && _seasonElements.Count > 0
                    && !_seasonElements.Contains(el, StringComparer.OrdinalIgnoreCase))
                    continue;   // 元素已知且本季不带 → 不进池
                _pool.Add(new Unit { Name = kv.Key, Element = el, Role = string.Join("|", kv.Value), Trial = true });
                poolKeys.Add(DecisionPolicy.Canonical(kv.Key));
            }

            // 玩家池适配：只保留该玩家拥有的角色（别名归一再比）
            if (rosterNames != null && rosterNames.Count > 0)
            {
                var owned = new HashSet<string>(rosterNames.Select(DecisionPolicy.Canonical), StringComparer.Ordinal);
                foreach (var nm in rosterNames) owned.Add(nm.Trim());
                var before = _pool.Count;
                _pool = _pool.Where(u => owned.Contains(DecisionPolicy.Canonical(u.Name)) || owned.Contains(u.Name)).ToList();
                LastPoolNote = $"玩家池 {rosterNames.Count} 人 ∩ 本季可带 = {_pool.Count} 人（全池 {before} 人）";
            }
            else LastPoolNote = "假设拥有公开全角色（通用最优）";

            _pool = _pool.OrderBy(u => u.Name, StringComparer.Ordinal).ToList();
            return _pool.Count >= TeamSize;
        }
        catch { return false; }
    }

    /// <summary>该角色本季是否带得动（元素必须在本季开放元素内；元素未知视为可用）。</summary>
    public static bool IsAvailableThisSeason(string name)
    {
        var u = _pool.FirstOrDefault(x => x.Name == name);
        if (u == null) return false;
        if (u.Element.Length == 0) return true;
        return _seasonElements.Count == 0 || _seasonElements.Contains(u.Element, StringComparer.OrdinalIgnoreCase);
    }

    public static string ElementOfMember(string name) => _pool.FirstOrDefault(x => x.Name == name)?.Element ?? "";

    /// <summary>把当前池转成 TeamProfile（供量化评分与配队共用同一口径）。</summary>
    public static QuantData.TeamProfile AsTeamProfile()
    {
        var t = new QuantData.TeamProfile { Full = false, Source = "(TeamSolver 池: " + LastPoolNote + ")" };
        foreach (var u in _pool)
        {
            t.Members.Add(new QuantData.Member { Name = u.Name, Element = u.Element, Role = u.Role, Trial = u.Trial });
            if (u.Element.Length > 0) t.Cover[u.Element] = t.Cover.TryGetValue(u.Element, out var c) ? c + 1 : 1;
            foreach (var role in (u.Role ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries))
                t.Roles[role] = t.Roles.TryGetValue(role, out var r) ? r + 1 : 1;
        }
        return t;
    }

    /// <summary>最近一次 LoadPool 的池子说明（诊断/展示用）。</summary>
    public static string LastPoolNote { get; private set; } = "";

    public static int PoolSize => _pool.Count;

    /// <summary>池内单位（名字/元素/职能），供全局分配使用。</summary>
    public static IReadOnlyList<(string Name, string Element, string Role)> Units =>
        _pool.Select(u => (u.Name, u.Element, u.Role)).ToList();

    public static IReadOnlyList<string> SeasonElements => _seasonElements;

    /// <summary>对一幕一难度求最优 4 人队；完全枚举 + 提前结束（分数达理论上限即停）。</summary>
    public static Plan Solve(QuantData.EnemyProfile enemy, int act)
    {
        var plan = new Plan { Act = act, Difficulty = enemy?.Difficulty ?? "" };
        if (enemy != null)
            foreach (var kv in enemy.Need) plan.Need[kv.Key] = kv.Value;
        plan.HasElementNeed = plan.Need.Count > 0;
        plan.ScoreMax = plan.Need.Values.Sum() + 0.30;   // 元素满分 + 两个职能奖励上限

        var needEls = plan.Need.Keys.ToList();
        var why = enemy?.MechanicEffect ?? "";
        // 注意：力竭是「输出窗口」不是治疗需求，误加会让所有首领幕都硬要 Healer
        bool needHeal = why.Contains("奶") || why.Contains("治疗") || why.Contains("标记");
        bool needBreak = why.Contains("破锅") || why.Contains("钝击") || why.Contains("破盾") || why.Contains("护盾");

        if (_pool.Count < TeamSize) { plan.Gaps.Add("池子不足 4 人"); return plan; }
        if (!plan.HasElementNeed)
        {
            plan.Team = GenericTeam(needHeal, needBreak);
            plan.Score = 0;          // 无需求 → 分数无意义，固定 0（不施加"浪费元素"惩罚）
            plan.ScoreMax = 0;
            plan.Reasons.Add("本幕无元素需求 → 通用队（治疗+主C+兜底）");
            FillCover(plan);
            return plan;
        }

        double best = double.MinValue;
        var bestTeam = new List<string>();
        var top = new List<(double Score, List<string> Team)>();
        var n = _pool.Count;
        var idx = new int[TeamSize];

        double TheoreticalMax = plan.Need.Values.Sum() + (needHeal ? 0.15 : 0) + (needBreak ? 0.15 : 0);

        for (int a = 0; a < n - 3; a++)
        for (int b = a + 1; b < n - 2; b++)
        for (int c = b + 1; c < n - 1; c++)
        for (int d = c + 1; d < n; d++)
        {
            var score = Eval(a, b, c, d, plan.Need, needHeal, needBreak, out var cover);
            if (score > best)
            {
                best = score;
                bestTeam = new List<string> { _pool[a].Name, _pool[b].Name, _pool[c].Name, _pool[d].Name };
                top.Add((score, bestTeam));
                if (top.Count > 64) top = top.OrderByDescending(t => t.Score).Take(32).ToList();
                if (best >= TheoreticalMax - 1e-9) goto done;   // 达上限，提前结束
            }
        }
    done:
        plan.Team = bestTeam;
        plan.Score = best <= double.MinValue ? 0 : best;
        plan.Alts = top.OrderByDescending(t => t.Score).Where(t => t.Score < best - 1e-9)
                       .Select(t => t.Team).Distinct(TeamKeyComparer()).Take(3).ToList();
        FillCover(plan);
        foreach (var e in needEls)
            if (plan.Cover.TryGetValue(e, out var cnt) && cnt == 0)
                plan.Gaps.Add($"{e}(需 {plan.Need[e]:0.0})无人覆盖");
        if (needHeal && !plan.Team.Any(nm => RoleOf(nm) == "Healer")) plan.Gaps.Add("缺治疗");
        if (needBreak && !plan.Team.Any(nm => RoleOf(nm) == "GeoBreak")) plan.Gaps.Add("缺破盾/钝击");
        plan.Reasons.Add($"枚举 {n} 选 4（C={CombinationCount(n, TeamSize)}），最优分 {plan.Score:0.00}/{TheoreticalMax:0.00}");
        return plan;
    }

    private static double Eval(int a, int b, int c, int d,
        Dictionary<string, double> need, bool needHeal, bool needBreak, out Dictionary<string, int> cover)
    {
        cover = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        double score = 0;
        int waste = 0;
        foreach (var i in new[] { a, b, c, d })
        {
            var el = _pool[i].Element;
            if (el.Length == 0) continue;
            if (need.ContainsKey(el)) cover[el] = cover.TryGetValue(el, out var cnt) ? cnt + 1 : 1;
            else waste++;
        }
        foreach (var kv in need)
            if (cover.TryGetValue(kv.Key, out var cnt) && cnt > 0) score += kv.Value;
        if (needHeal && (RoleHas(_pool[a].Role, "Healer") || RoleHas(_pool[b].Role, "Healer")
                      || RoleHas(_pool[c].Role, "Healer") || RoleHas(_pool[d].Role, "Healer"))) score += 0.15;
        if (needBreak && (RoleHas(_pool[a].Role, "GeoBreak") || RoleHas(_pool[b].Role, "GeoBreak")
                       || RoleHas(_pool[c].Role, "GeoBreak") || RoleHas(_pool[d].Role, "GeoBreak"))) score += 0.15;
        score -= 0.04 * waste;    // 非需求元素占位的轻惩罚，避免"凑元素"
        return score;
    }

    private static List<string> GenericTeam(bool needHeal, bool needBreak)
    {
        var team = new List<string>();
        void Take(string role)
        {
            if (team.Count >= TeamSize) return;
            var u = _pool.FirstOrDefault(x => RoleHas(x.Role, role) && !team.Contains(x.Name))
                 ?? _pool.FirstOrDefault(x => RoleHas(x.Role, role));
            if (u != null && !team.Contains(u.Name)) team.Add(u.Name);
        }
        if (needHeal) Take("Healer");
        if (needBreak) Take("GeoBreak");
        Take("MainDps");
        Take("Buffer");
        Take("HydroApplier");
        foreach (var u in _pool)
        {
            if (team.Count >= TeamSize) break;
            if (!team.Contains(u.Name)) team.Add(u.Name);
        }
        return team.Take(TeamSize).ToList();
    }

    private static string RoleOf(string name) => _pool.FirstOrDefault(u => u.Name == name)?.Role ?? "";

    /// <summary>角色是否具备某职能（支持 "HydroApplier|Healer" 这种多职能）。</summary>
    internal static bool RoleHas(string roles, string role) =>
        !string.IsNullOrEmpty(roles) && roles.Split('|').Contains(role, StringComparer.Ordinal);

    /// <summary>某角色是否具备某职能（供外部断言/编排使用）。</summary>
    public static bool HasRole(string name, string role) =>
        RoleHas(_pool.FirstOrDefault(u => u.Name == name)?.Role ?? "", role);

    private static void FillCover(Plan p)
    {
        foreach (var nm in p.Team)
        {
            var u = _pool.FirstOrDefault(x => x.Name == nm);
            if (u == null || u.Element.Length == 0) continue;
            p.Cover[u.Element] = p.Cover.TryGetValue(u.Element, out var c) ? c + 1 : 1;
        }
    }

    private static IEqualityComparer<List<string>> TeamKeyComparer() => new TeamCmp();

    private sealed class TeamCmp : IEqualityComparer<List<string>>
    {
        public bool Equals(List<string> x, List<string> y) =>
            x != null && y != null && x.Count == y.Count && x.OrderBy(s => s).SequenceEqual(y.OrderBy(s => s));
        public int GetHashCode(List<string> obj) => string.Join("|", obj.OrderBy(s => s)).GetHashCode();
    }

    private static long CombinationCount(int n, int k)
    {
        long r = 1;
        for (int i = 0; i < k; i++) r = r * (n - i) / (i + 1);
        return r;
    }

    // ---------------- CLI ----------------

    public static int RunCli(string difficulty, int act, string outPath, int season = 0, string rosterPath = null)
    {
        Cli.Attach();
        // 赛季无关：按 Registry 自动发现的文件加载，不再硬编码 season29
        DecisionPolicy.Load(AppPaths.Data("decision-policy.json"));
        var seasons = SeasonRegistry.Load();
        var target = SeasonRegistry.For(season) ?? seasons.FirstOrDefault();
        if (target == null) { Cli.Say("没有赛季数据（data/season*.json）"); return 2; }
        Cli.Say($"赛季 {target.Season}（{Path.GetFileName(target.Path)}）元素 {string.Join("/", target.Elements)}");
        QuantData.LoadEnemies(target.Path);
        Playbook.Load(AppPaths.Data("act-playbook.json"));
        var roster = string.IsNullOrWhiteSpace(rosterPath) ? null : QuantData.Team(rosterPath);
        var rosterNames = roster != null && roster.HasData ? roster.Members.Select(m => m.Name).ToList() : null;
        if (!LoadPool(target.Elements, rosterNames)) { Cli.Say("角色池未加载（characters.json/substitutes.json）"); return 2; }
        Cli.Say(TeamSolver.LastPoolNote);
        Cli.Say($"假设池：公开全角色 ∩ 本季元素（{string.Join("/", _seasonElements)}）= **{_pool.Count}** 人；每队 {TeamSize} 人，组合数 C={CombinationCount(_pool.Count, TeamSize)}");

        var diffs = string.IsNullOrWhiteSpace(difficulty) || difficulty == "all"
            ? QuantData.Difficulties.ToList()
            : new List<string> { difficulty };
        var plans = new List<Plan>();
        var sb = new StringBuilder();

        foreach (var d in diffs)
        {
            var maxAct = QuantData.MaxAct(d);
            sb.AppendLine($"## {d}");
            for (int a = 1; a <= maxAct; a++)
            {
                var enemy = QuantData.Enemy(a, d);
                if (enemy == null || !enemy.HasAnyData) { sb.AppendLine($"- 第{a}幕: 无敌人数据"); continue; }
                var p = Solve(enemy, a);
                plans.Add(p);
                var gap = p.Gaps.Count > 0 ? "｜缺口: " + string.Join("+", p.Gaps) : "";
                sb.AppendLine($"- 第{a}幕 score={p.Score:0.00}/{p.ScoreMax:0.00} 队={string.Join("/", p.Team)}{gap}");
            }
        }
        foreach (var line in sb.ToString().Split('\n')) if (line.Trim().Length > 0) Cli.Say(line);

        if (!string.IsNullOrWhiteSpace(outPath))
        {
            try
            {
                var doc = new
                {
                    generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    season = target.Season,
                    assumption = $"假设拥有公开全部角色，且只带本季开放元素（{string.Join("/", _seasonElements)}），池 {_pool.Count} 人",
                    teamSize = TeamSize,
                    plans,
                };
                File.WriteAllText(Path.GetFullPath(outPath),
                    JsonSerializer.Serialize(doc, TemplateFactory.JsonOpts), new UTF8Encoding(false));
                Cli.Say("已写: " + Path.GetFullPath(outPath));
            }
            catch (Exception ex) { Cli.Say("写文件失败: " + ex.Message); return 1; }
        }
        return plans.Count > 0 ? 0 : 2;
    }
}
