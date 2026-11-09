namespace TheaterAssistant;

/// <summary>
/// 决策层与生产主循环之间的桥。
///
/// 存在理由（见 docs/AUDIT-OVERALL.md 的 P0）：
///   生产主循环此前只调用 `TheaterLogic.Evaluate`（纯规则），而量化/配队/推荐/强度
///   约 2400 行决策代码只在 CI/CLI 里跑——玩家看到的指示完全没有用到它们。
///   把接入逻辑集中到这里，才能**被断言覆盖**（私有方法没法测）。
///
/// 安全前提：决策层是**增益**，不是单点故障——任何异常都回退到旧逻辑。
/// </summary>
internal static class DecisionBridge
{
    private static bool _ready;
    public static bool Ready => _ready;

    /// <summary>初始化决策层所需数据（幂等，失败只标记不可用）。</summary>
    public static bool Init()
    {
        try
        {
            DecisionPolicy.Load(AppPaths.Data("decision-policy.json"));
            var season = SeasonRegistry.For(0);
            if (season != null)
            {
                QuantData.LoadEnemies(season.Path);
                QuantData.LoadCharacters(AppPaths.Data("characters.json"), AppPaths.Data("substitutes.json"));
                TeamSolver.LoadPool(season.Elements);
            }
            CuratedPicks.Load(AppPaths.Data("rotations.json"), AppPaths.Data("knowledge-release.json"));
            CharacterStrength.LoadModel(AppPaths.Data("strength-model.json"));
            CharacterStrength.Load(Path.Combine(AppPaths.Dir, "roster.json"), AppPaths.Data("characters.json"));
            _ready = true;
        }
        catch
        {
            _ready = false;
        }
        return _ready;
    }

    /// <summary>
    /// 把决策层的结论**追加**到旧逻辑的指示上（只做加法，不改旧内容）。
    /// 返回新 Advice；任何异常都原样返回入参。
    /// </summary>
    public static Advice Append(Advice a)
    {
        if (!_ready || a == null) return a;
        try
        {
            // 难度不硬编码：取数据里声明的最高难度（玩家的主要目标），缺省为空。
            var diff = QuantData.Difficulties.LastOrDefault() ?? "";
            var enemy = a.Act.HasValue ? QuantData.Enemy(a.Act.Value, diff) : null;
            var d = DecisionOrchestrator.Decide(new DecisionOrchestrator.Context
            {
                Act = a.Act,
                Difficulty = diff,
                Enemy = enemy,
                Team = TeamSolver.AsTeamProfile(),
                Options = Array.Empty<ChoiceDecider.Option>(),
                Kind = a.Kind ?? "",
            });
            if (d.Lines.Count == 0) return a;

            var merged = a.Lines.ToList();
            foreach (var line in d.Lines)
                if (!merged.Any(x => string.Equals(x, line, StringComparison.Ordinal)))
                    merged.Add(line);

            return a with
            {
                Lines = merged,
                Diagnostics = (a.Diagnostics ?? "") + $" | 决策层:{d.Source}({d.Confidence:0.00})",
            };
        }
        catch
        {
            return a;
        }
    }

    /// <summary>CLI：报告桥接状态，便于云端/CI 观察是否真的初始化成功。</summary>
    public static int RunCli()
    {
        Cli.Attach();
        var ok = Init();
        Cli.Say($"决策层桥接: {_ready}｜赛季 {SeasonRegistry.For(0)?.Season}｜池 {TeamSolver.PoolSize}｜推荐 {CuratedPicks.ActCount} 幕｜强度 {CharacterStrength.ScoredCount}/{CharacterStrength.Count}");
        var demo = new Advice("测试指示", new List<string> { "· 旧逻辑行" }, "", 3, "act");
        var merged = Append(demo);
        foreach (var l in merged.Lines) Cli.Say("  " + l);
        Cli.Say($"诊断: {merged.Diagnostics}");
        return ok && merged.Lines.Count > demo.Lines.Count ? 0 : 1;
    }
}
