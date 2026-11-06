namespace TheaterAssistant;

/// <summary>
/// 多幕规划器：先复用单幕 PlanRanker，再用 Beam Search 对跨幕角色复用做软约束。
/// 这是规划候选，不假设 GOOD 导出包含游戏内耐力状态；角色复用惩罚不是硬性可用性证明。
/// </summary>
internal static class ActPlanner
{
    internal sealed record PlannedAct(
        int Act,
        string Encounter,
        int LocalScore,
        int ReusePenalty,
        string Reason,
        string[] RecommendedCharacters);

    internal sealed record TheaterPlan(
        string Difficulty,
        int TotalScore,
        IReadOnlyList<PlannedAct> Acts,
        bool RosterLoaded,
        string Note);

    private sealed class State
    {
        public int Score;
        public List<PlannedAct> Acts = new();
        public Dictionary<string, int> Usage = new(StringComparer.OrdinalIgnoreCase);
    }

    public static int RunCli(string difficulty, string rosterPath, string outPath)
    {
        Cli.Attach();
        difficulty = string.IsNullOrWhiteSpace(difficulty) ? "Visionary" : difficulty;
        rosterPath = string.IsNullOrWhiteSpace(rosterPath)
            ? Path.Combine(AppPaths.Dir, "roster.json") : Path.GetFullPath(rosterPath);
        var data = TheaterData.Load(AppPaths.Data("season29.json"));
        var roster = RosterData.TryLoad(rosterPath);
        var plan = Build(data, roster, difficulty);
        var lines = new List<string>
        {
            $"# 第 {data.Season} 期跨幕规划（{difficulty}）",
            "",
            $"> 总分 {plan.TotalScore}；角色池 {(plan.RosterLoaded ? "已加载" : "未加载")}。",
            $"> {plan.Note}",
            "",
            "| 幕 | 遭遇方案 | 单幕分 | 复用惩罚 | 推荐角色 | 依据 |",
            "|---:|---|---:|---:|---|---|",
        };
        foreach (var act in plan.Acts)
            lines.Add($"| {act.Act} | {act.Encounter} | {act.LocalScore} | -{act.ReusePenalty} | {string.Join("、", act.RecommendedCharacters)} | {act.Reason} |");
        if (plan.Acts.Count == 0) lines.Add("| - | 无可规划遭遇 | - | - | - | 请检查赛季数据与难度 |");

        foreach (var line in lines.Take(12)) Cli.Say(line);
        if (!string.IsNullOrWhiteSpace(outPath))
        {
            var path = Path.GetFullPath(outPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllText(path, string.Join(Environment.NewLine, lines), System.Text.Encoding.UTF8);
            Cli.Say("规划报告: " + path);
        }
        return plan.Acts.Count > 0 ? 0 : 2;
    }

    public static TheaterPlan Build(
        TheaterData data,
        RosterData roster,
        string difficulty,
        int beamWidth = 32)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        if (roster == null) throw new ArgumentNullException(nameof(roster));
        beamWidth = Math.Clamp(beamWidth, 1, 256);

        var beam = new List<State> { new() };
        int planned = 0;

        foreach (int actNo in Enumerable.Range(1, 12))
        {
            var act = data.Get(difficulty, actNo);
            if (act == null || act.Encounters.Count == 0) continue;

            var ranked = PlanRanker.Rank(act.Encounters, roster, data);
            if (ranked.Count == 0) continue;
            planned++;

            var next = new List<State>();
            foreach (var state in beam)
            foreach (var option in ranked)
            {
                var chars = (option.Encounter.Chars ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(roster.Owns)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                int reusePenalty = 0;
                var usage = new Dictionary<string, int>(state.Usage, StringComparer.OrdinalIgnoreCase);
                foreach (var name in chars)
                {
                    var key = name.Trim();
                    int used = usage.TryGetValue(key, out var count) ? count : 0;
                    // 第一次复用只做轻惩罚；超过两次明显惩罚。真实耐力未从本地角色池推断。
                    reusePenalty += used == 0 ? 0 : used == 1 ? 2 : 6;
                    usage[key] = used + 1;
                }

                var item = new PlannedAct(
                    actNo,
                    option.Encounter.Name,
                    option.Score,
                    reusePenalty,
                    reusePenalty == 0 ? option.Reason : $"{option.Reason}；跨幕复用惩罚 -{reusePenalty}",
                    chars);
                var candidate = new State
                {
                    Score = state.Score + option.Score - reusePenalty,
                    Usage = usage,
                    Acts = new List<PlannedAct>(state.Acts) { item },
                };
                next.Add(candidate);
            }

            beam = next
                .OrderByDescending(x => x.Score)
                .ThenBy(x => string.Join("|", x.Acts.Select(a => a.Encounter)), StringComparer.Ordinal)
                .Take(beamWidth)
                .ToList();
        }

        var best = beam.OrderByDescending(x => x.Score).FirstOrDefault() ?? new State();
        return new TheaterPlan(
            difficulty,
            best.Score,
            best.Acts,
            roster.Loaded,
            planned == 0
                ? "没有可规划的幕次数据"
                : "Beam Search 结果；角色复用仅作软惩罚，不代表游戏内耐力/可用性已被确认");
    }
}
