using System.Text.Json;

namespace TheaterAssistant;

/// <summary>
/// 配队/分配的质量闸门：把"只打印结果"升级为"逐条断言"。
///
/// 审计背景（docs/FLOW.md §3）：云端 17 步里 `--team-solve` / `--global-alloc` 属"B 层——
/// 有结构但不判对错"，因此 PASS 并不代表配队合理。本类补上判据，使其进入 A 层。
///
/// 闸门全部可判真假，且阈值来自 data/decision-policy.json 的 qualityGates 段：
///   G1 队伍人数 = teamSize（默认 4）
///   G2 同一难度内角色不重复（剧诗锁定只作用于同一次挑战）
///   G3 有元素需求的幕：每个需求元素至少 1 人覆盖
///   G4 机制需要治疗 ⇒ 队里有 Healer
///   G5 机制需要破盾 ⇒ 队里有 GeoBreak
///   G6 全员本季可带（元素在本季开放列表内）
///   G7 无元素需求的幕 ⇒ 分数必须为 0（不给需求外的"伪得分"）
/// </summary>
internal static class QualityGates
{
    internal sealed class Violation
    {
        public string Gate = "";
        public string Where = "";
        public string Detail = "";
        public override string ToString() => $"[{Gate}] {Where}: {Detail}";
    }

    internal sealed class Report
    {
        public int Checked;
        public List<Violation> Violations = new();
        public bool Passed => Violations.Count == 0;
    }

    public static int TeamSize { get; private set; } = 4;
    public static bool RequireHealer { get; private set; } = true;
    public static bool RequireBreaker { get; private set; } = true;

    public static void Load(string policyPath)
    {
        try
        {
            if (!File.Exists(policyPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(policyPath));
            if (!doc.RootElement.TryGetProperty("qualityGates", out var g) || g.ValueKind != JsonValueKind.Object)
                return;
            if (g.TryGetProperty("teamSize", out var ts) && ts.ValueKind == JsonValueKind.Number)
                TeamSize = ts.GetInt32();
            if (g.TryGetProperty("requireHealer", out var rh) &&
                (rh.ValueKind == JsonValueKind.True || rh.ValueKind == JsonValueKind.False))
                RequireHealer = rh.GetBoolean();
            if (g.TryGetProperty("requireBreaker", out var rb) &&
                (rb.ValueKind == JsonValueKind.True || rb.ValueKind == JsonValueKind.False))
                RequireBreaker = rb.GetBoolean();
        }
        catch { }
    }

    /// <summary>校验单幕的配队（来自 TeamSolver）。</summary>
    public static void CheckPlan(TeamSolver.Plan plan, QuantData.EnemyProfile enemy, Report into)
    {
        if (plan == null) { into.Violations.Add(new Violation { Gate = "G0", Where = "-", Detail = "无方案" }); return; }
        var where = $"{plan.Difficulty} 第{plan.Act}幕";
        into.Checked++;

        // G1 人数
        if (plan.Team.Count != TeamSize)
            into.Violations.Add(new Violation { Gate = "G1", Where = where, Detail = $"队伍 {plan.Team.Count} 人 ≠ {TeamSize}" });

        // G3 需求覆盖
        foreach (var kv in plan.Need)
        {
            var covered = plan.Cover.TryGetValue(kv.Key, out var c) && c > 0;
            if (!covered)
                into.Violations.Add(new Violation { Gate = "G3", Where = where, Detail = $"需求 {kv.Key}（权重 {kv.Value:0.0}）无人覆盖" });
        }

        // G7 无需求 ⇒ 分数 0
        if (plan.Need.Count == 0 && Math.Abs(plan.Score) > 1e-9)
            into.Violations.Add(new Violation { Gate = "G7", Where = where, Detail = $"无元素需求却得分 {plan.Score:0.00}" });

        // G4 / G5 职能硬约束
        if (enemy != null)
        {
            var why = enemy.MechanicEffect ?? "";
            if (RequireHealer && DecisionPolicy.NeedsHealer(why)
                && !plan.Team.Any(nm => TeamSolver.HasRole(nm, "Healer")))
                into.Violations.Add(new Violation { Gate = "G4", Where = where, Detail = "机制需要治疗但队中无 Healer" });
            if (RequireBreaker && DecisionPolicy.NeedsBreaker(why)
                && !plan.Team.Any(nm => TeamSolver.HasRole(nm, "GeoBreak")))
                into.Violations.Add(new Violation { Gate = "G5", Where = where, Detail = "机制需要破盾但队中无 GeoBreak" });
        }

        // G6 本季可带
        foreach (var nm in plan.Team)
            if (!nm.StartsWith("（") && !TeamSolver.IsAvailableThisSeason(nm))
                into.Violations.Add(new Violation { Gate = "G6", Where = where, Detail = $"{nm} 本季不可带" });
    }

    /// <summary>校验跨幕全局分配（来自 GlobalAllocator）。</summary>
    public static void CheckAllocation(GlobalAllocator.Result alloc, Report into)
    {
        if (alloc == null) { into.Violations.Add(new Violation { Gate = "G0", Where = "-", Detail = "无分配结果" }); return; }

        foreach (var g in alloc.Assigns.Where(a => a.UsedMainForce).GroupBy(a => a.Difficulty))
        {
            into.Checked++;
            // G2 同难度内不重复
            var counts = g.SelectMany(a => a.Team)
                          .Where(nm => !nm.StartsWith("（"))
                          .GroupBy(nm => nm, StringComparer.Ordinal)
                          .Where(x => x.Count() > 1)
                          .ToList();
            foreach (var d in counts)
                into.Violations.Add(new Violation
                {
                    Gate = "G2",
                    Where = $"{g.Key}（跨幕）",
                    Detail = $"{d.Key} 被用 {d.Count()} 次（同难度内必须唯一）",
                });
        }

        // G1 每幕人数
        foreach (var a in alloc.Assigns.Where(a => a.UsedMainForce))
        {
            var n = a.Team.Count(nm => !nm.StartsWith("（"));
            if (n != TeamSize)
                into.Violations.Add(new Violation
                {
                    Gate = "G1",
                    Where = $"{a.Difficulty} 第{a.Act}幕",
                    Detail = $"分配了 {n} 人 ≠ {TeamSize}",
                });
        }
    }

    public static int RunCli(int season)
    {
        Cli.Attach();
        DecisionPolicy.Load(AppPaths.Data("decision-policy.json"));
        Load(AppPaths.Data("decision-policy.json"));
        var seasons = SeasonRegistry.Load();
        var target = SeasonRegistry.For(season) ?? seasons.FirstOrDefault();
        if (target == null) { Cli.Say("无赛季数据"); return 2; }

        QuantData.LoadEnemies(target.Path);
        QuantData.LoadCharacters(AppPaths.Data("characters.json"), AppPaths.Data("substitutes.json"));
        Playbook.Load(AppPaths.Data("act-playbook.json"));
        if (!TeamSolver.LoadPool(target.Elements))
        {
            Cli.Say("角色池未加载");
            return 2;
        }

        var report = new Report();
        Cli.Say($"第{target.Season}赛季 质量闸门｜队形 {TeamSize}｜治疗必带={RequireHealer}｜破盾必带={RequireBreaker}");
        Cli.Say("");

        foreach (var diff in target.Difficulties)
        {
            int maxAct = QuantData.MaxAct(diff);
            for (int a = 1; a <= maxAct; a++)
            {
                var enemy = QuantData.Enemy(a, diff);
                if (enemy == null || !enemy.HasAnyData) continue;
                var plan = TeamSolver.Solve(enemy, a);
                CheckPlan(plan, enemy, report);
            }
        }

        var allocReport = new Report();
        try { CheckAllocation(GlobalAllocator.Solve(season), allocReport); }
        catch (Exception ex) { allocReport.Violations.Add(new Violation { Gate = "G0", Where = "global-alloc", Detail = ex.GetType().Name }); }

        report.Checked += allocReport.Checked;
        report.Violations.AddRange(allocReport.Violations);

        if (report.Passed)
        {
            Cli.Say($"✅ 全部通过：{report.Checked} 项检查无违规（G1 人数 / G2 同难度唯一 / G3 需求覆盖 / G4 治疗 / G5 破盾 / G6 本季可带 / G7 无需求零分）");
            return 0;
        }

        Cli.Say($"❌ {report.Violations.Count} 项违规（共检查 {report.Checked} 项）：");
        foreach (var g in report.Violations.GroupBy(v => v.Gate).OrderBy(g => g.Key))
        {
            Cli.Say($"  {g.Key}: {g.Count()} 项");
            foreach (var v in g.Take(4)) Cli.Say("    " + v);
            if (g.Count() > 4) Cli.Say($"    …另有 {g.Count() - 4} 项");
        }
        return 1;
    }
}
