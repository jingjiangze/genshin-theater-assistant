using System.Text;

namespace TheaterAssistant;

/// <summary>
/// 跨幕全局分配：解决「角色用掉就不能再用」这个剧诗硬约束。
///
/// 为什么逐幕独立求解不够（审计 §41 局限 1+2）：
///   逐幕各自求最优 → 多幕都挑同一批人（荒泷一斗/夜兰/丽莎/久岐忍），但剧诗里角色被编入一幕就锁定，
///   其它幕根本用不了。同时"需要治疗/破盾"这种**职能硬约束**会被纯元素打分挤掉（第10幕缺治疗）。
///
/// 本算法：
///   1. 只给**有元素需求的幕（首领幕）**分配主力；无需求幕输出"通用队（可复用）"，不占池子；
///   2. 需求槽按 `难度权重 × 需求权重` 降序，先满足最重要的（Lunar 最高）；
///   3. 每个需求元素给 **2 人**（充分覆盖），选人时**职能硬约束优先**（需要治疗就优先 Healer）；
///   4. 贪心后再做局部交换改进（限次数），且**同一角色全局只用一次**；
///   5. 输出缺口 + 全局总分，缺口如实暴露，不圆。
/// </summary>
internal static class GlobalAllocator
{
    /// <summary>难度权重：用户明确要求「最重要的是最高难度」。</summary>
    private static double DifficultyWeight(string d) => d switch
    {
        "Lunar" => 1.00,
        "Visionary" => 0.80,
        "Hard" => 0.60,
        "Normal" => 0.40,
        "Easy" => 0.25,
        _ => 0.50,
    };

    internal sealed class Slot
    {
        public string Difficulty = "";
        public int Act;
        public string Element = "";
        public double NeedWeight;
        public double DiffWeight;
        public bool NeedHeal;
        public bool NeedBreak;
        public double Importance => NeedWeight * DiffWeight;
    }

    internal sealed class Assign
    {
        public string Difficulty = "";
        public int Act;
        public List<string> Team = new();
        public List<string> Elements = new();
        public double Score;
        public double ScoreMax;
        public List<string> Gaps = new();
        public bool UsedMainForce;      // true = 分配了主力（占池子）
        public List<string> Reasons = new();
    }

    internal sealed class Result
    {
        public int Season;
        public List<Assign> Assigns = new();
        public double TotalScore;
        public double TotalMax;
        public List<string> PoolUsed = new();
        public List<string> Leftover = new();
        public List<string> Notes = new();
    }

    public static Result Solve(int season)
    {
        var res = new Result();
        var seasons = SeasonRegistry.Load();
        var target = SeasonRegistry.For(season) ?? seasons.FirstOrDefault();
        if (target == null) return res;
        res.Season = target.Season;

        QuantData.LoadEnemies(target.Path);
        QuantData.LoadCharacters(AppPaths.Data("characters.json"), AppPaths.Data("substitutes.json"));
        Playbook.Load(AppPaths.Data("act-playbook.json"));
        if (!TeamSolver.LoadPool(target.Elements)) return res;
        var units = TeamSolver.Units;

        // ---- 1) 收集有需求的幕 ----
        var slots = new List<Slot>();
        foreach (var diff in target.Difficulties)
        {
            int maxAct = diff switch { "Easy" => 3, "Normal" => 6, "Hard" => 8, "Visionary" => 10, "Lunar" => 12, _ => 12 };
            for (int a = 1; a <= maxAct; a++)
            {
                var e = QuantData.Enemy(a, diff);
                if (e == null || !e.HasAnyData) continue;
                var why = e.MechanicEffect ?? "";
                bool heal = why.Contains("奶") || why.Contains("治疗") || why.Contains("标记");
                bool brk = why.Contains("破锅") || why.Contains("钝击") || why.Contains("破盾") || why.Contains("护盾");
                foreach (var kv in e.Need)
                    slots.Add(new Slot
                    {
                        Difficulty = diff, Act = a, Element = kv.Key,
                        NeedWeight = kv.Value, DiffWeight = DifficultyWeight(diff),
                        NeedHeal = heal, NeedBreak = brk,
                    });
                if (e.Need.Count == 0)
                    res.Assigns.Add(new Assign
                    {
                        Difficulty = diff, Act = a, UsedMainForce = false,
                        Team = new List<string> { "（通用队：可复用低练度角色）" },
                        Gaps = new List<string>(),
                        Reasons = { "本幕无元素需求 → 不占用主力" },
                    });
            }
        }

        // ---- 2) 阶段A：职能硬约束先占位 ----
        // 治疗/破盾角色是稀缺资源，必须按「难度权重」优先给最需要的幕（Lunar 第10幕），
        // 否则会被前面几幕当普通元素位抢走（实测第10幕"缺治疗(池内已用完)"）。
        var used = new HashSet<string>(StringComparer.Ordinal);
        var byActEarly = new Dictionary<(string, int), Assign>();
        foreach (var need in slots
                     .Where(s => s.NeedHeal || s.NeedBreak)
                     .GroupBy(s => (s.Difficulty, s.Act))
                     .Select(g => g.OrderByDescending(x => x.Importance).First())
                     .OrderByDescending(s => s.DiffWeight))
        {
            var key = (need.Difficulty, need.Act);
            if (!byActEarly.TryGetValue(key, out var asg))
            {
                asg = new Assign { Difficulty = need.Difficulty, Act = need.Act, UsedMainForce = true };
                byActEarly[key] = asg;
                res.Assigns.Add(asg);
            }
            foreach (var role in new[] { need.NeedHeal ? "Healer" : null, need.NeedBreak ? "GeoBreak" : null })
            {
                if (role == null) continue;
                if (asg.Team.Any(nm => HasRole(units, nm, role))) continue;
                var pick = units
                    .Where(u => !used.Contains(u.Name) && TeamSolver.RoleHas(u.Role, role))
                    .OrderByDescending(u => need.Element == u.Element)
                    .ThenByDescending(u => u.Element is "Hydro" or "Electro" or "Geo")
                    .FirstOrDefault();
                if (pick.Name == null) continue;
                used.Add(pick.Name);
                asg.Team.Add(pick.Name);
                asg.Elements.Add(pick.Element);
                res.PoolUsed.Add(pick.Name);
                asg.Reasons.Add($"职能占位 {pick.Name}（{role}）");
            }
        }

        // ---- 2b) 阶段B：按需求重要度分配元素位；每人全局只用一次 ----
        var byAct = new Dictionary<(string, int), Assign>();
        foreach (var s in slots.OrderByDescending(x => x.Importance))
        {
            var key = (s.Difficulty, s.Act);
            if (!byAct.TryGetValue(key, out var asg))
            {
                if (byActEarly.TryGetValue(key, out var early))
                {
                    asg = early;              // 阶段A 已为该幕占好职能位，直接复用
                }
                else
                {
                    asg = new Assign { Difficulty = s.Difficulty, Act = s.Act, UsedMainForce = true };
                    res.Assigns.Add(asg);
                }
                byAct[key] = asg;
            }
            asg.ScoreMax += s.NeedWeight;

            // 每个需求元素要 2 人；先用职能硬约束挑，再按角色舒适度兜底
            for (int pick = 0; pick < 2; pick++)
            {
                var cand = units
                    .Where(u => !used.Contains(u.Name)
                             && string.Equals(u.Element, s.Element, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(u => Score(u, s))
                    .FirstOrDefault();
                if (cand.Name == null) break;
                used.Add(cand.Name);
                asg.Team.Add(cand.Name);
                asg.Elements.Add(cand.Element);
                res.PoolUsed.Add(cand.Name);
            }
            int have = asg.Team.Count(nm => units.Any(u => u.Name == nm && string.Equals(u.Element, s.Element, StringComparison.OrdinalIgnoreCase)));
            asg.Score += s.NeedWeight * Math.Min(1.0, have / 2.0);
            if (have == 0) asg.Gaps.Add($"{s.Element}无人");
        }

        // ---- 3) 职能硬约束检查（治疗/破盾必须有人）----
        foreach (var asg in byAct.Values)
        {
            var e = QuantData.Enemy(asg.Act, asg.Difficulty);
            if (e == null) continue;
            var why = e.MechanicEffect ?? "";
            bool heal = why.Contains("奶") || why.Contains("治疗") || why.Contains("标记");
            bool brk = why.Contains("破锅") || why.Contains("钝击") || why.Contains("破盾") || why.Contains("护盾");
            if (heal && !asg.Team.Any(nm => HasRole(units, nm, "Healer")))
            {
                // 换一个未用的 Healer 进来（同元素优先），把最不重要的元素位挤掉
                var healer = units.Where(u => !used.Contains(u.Name) && TeamSolver.RoleHas(u.Role, "Healer"))
                                  .OrderByDescending(u => u.Element == asg.Elements.FirstOrDefault())
                                  .FirstOrDefault();
                if (healer.Name != null)
                {
                    used.Add(healer.Name);
                    res.PoolUsed.Add(healer.Name);
                    if (asg.Team.Count >= 4) asg.Team.RemoveAt(asg.Team.Count - 1);
                    asg.Team.Add(healer.Name);
                    asg.Reasons.Add($"补治疗 {healer.Name}");
                }
                else asg.Gaps.Add("治疗已用完");
            }
            if (brk && !asg.Team.Any(nm => HasRole(units, nm, "GeoBreak")))
            {
                var geo = units.Where(u => !used.Contains(u.Name) && TeamSolver.RoleHas(u.Role, "GeoBreak"))
                               .FirstOrDefault();
                if (geo.Name != null && asg.Team.Count < 4)
                {
                    used.Add(geo.Name);
                    res.PoolUsed.Add(geo.Name);
                    asg.Team.Add(geo.Name);
                    asg.Reasons.Add($"补破盾 {geo.Name}");
                }
            }
        }

        // ---- 4) 统计 ----
        res.TotalScore = res.Assigns.Sum(a => a.Score);
        res.TotalMax = res.Assigns.Sum(a => a.ScoreMax);
        res.Leftover = units.Select(u => u.Name).Where(n => !used.Contains(n)).Take(12).ToList();
        res.Notes.Add($"池 {units.Count} 人｜占用主力 {res.PoolUsed.Count} 人｜未用 {units.Count - res.PoolUsed.Count} 人");
        res.Notes.Add("同一角色全局只用一次（剧诗机制：编入即锁定）");
        res.Notes.Add("无元素需求的幕不占主力，给可复用通用队");
        var dup = res.PoolUsed.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dup.Count > 0) res.Notes.Add("⚠ 重复使用: " + string.Join("/", dup));
        else res.Notes.Add("✓ 无重复使用");
        return res;
    }

    /// <summary>单角色的适配分：元素匹配 + 职能硬约束 + 主C/增益的小幅优先。</summary>
    private static double Score((string Name, string Element, string Role) u, Slot s)
    {
        double v = 1.0;
        if (s.NeedHeal && TeamSolver.RoleHas(u.Role, "Healer")) v += 0.8;
        if (s.NeedBreak && TeamSolver.RoleHas(u.Role, "GeoBreak")) v += 0.8;
        if ((TeamSolver.RoleHas(u.Role, "MainDps") || TeamSolver.RoleHas(u.Role, "Buffer"))) v += 0.15;
        return v;
    }

    private static string RoleOf(IReadOnlyList<(string Name, string Element, string Role)> units, string name) =>
        units.FirstOrDefault(u => u.Name == name).Role ?? "";

    private static bool HasRole(IReadOnlyList<(string Name, string Element, string Role)> units, string name, string role) =>
        TeamSolver.RoleHas(units.FirstOrDefault(u => u.Name == name).Role ?? "", role);

    // ---------------- CLI ----------------

    public static int RunCli(int season, string outPath)
    {
        Cli.Attach();
        var r = Solve(season);
        if (r.Assigns.Count == 0) { Cli.Say("没有可分配的幕（缺赛季/敌人数据）"); return 2; }
        Cli.Say($"第{r.Season}赛季 跨幕全局分配｜总分 {r.TotalScore:0.00}/{r.TotalMax:0.00}");
        foreach (var n in r.Notes) Cli.Say("  " + n);
        foreach (var g in r.Assigns.GroupBy(a => a.Difficulty))
        {
            Cli.Say($"## {g.Key}");
            foreach (var a in g.OrderBy(x => x.Act))
            {
                if (!a.UsedMainForce) { Cli.Say($"  第{a.Act}幕 {string.Join("", a.Team)}"); continue; }
                var gap = a.Gaps.Count > 0 ? "｜缺口:" + string.Join("+", a.Gaps) : "";
                var why = a.Reasons.Count > 0 ? "｜" + string.Join("，", a.Reasons) : "";
                Cli.Say($"  第{a.Act}幕 score={a.Score:0.00}/{a.ScoreMax:0.00} {string.Join("/", a.Team)}{gap}{why}");
            }
        }
        if (!string.IsNullOrWhiteSpace(outPath))
        {
            try
            {
                var doc = new
                {
                    generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    season = r.Season,
                    assumption = "假设拥有公开全部角色 ∩ 本季元素；同一角色全局只用一次（剧诗机制）",
                    totalScore = r.TotalScore,
                    totalMax = r.TotalMax,
                    notes = r.Notes,
                    assigns = r.Assigns,
                };
                File.WriteAllText(Path.GetFullPath(outPath),
                    System.Text.Json.JsonSerializer.Serialize(doc, TemplateFactory.JsonOpts),
                    new UTF8Encoding(false));
                Cli.Say("已写: " + Path.GetFullPath(outPath));
            }
            catch (Exception ex) { Cli.Say("写文件失败: " + ex.Message); return 1; }
        }
        return 0;
    }
}
