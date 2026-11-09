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
    /// <summary>角色池来源说明（供悬浮窗标注，玩家要能看出池从哪来的）。</summary>
    public static string PoolNote { get; private set; } = "";

    /// <summary>从 GOOD/合并文件里读出角色中文名（传给 TeamSolver 限定候选）。</summary>
    private static List<string> ReadRosterNames(string path)
    {
        var names = new List<string>();
        try
        {
            if (!File.Exists(path)) return names;
            var zh = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var cp = AppPaths.Data("characters.json");
                if (File.Exists(cp))
                {
                    using var cd = System.Text.Json.JsonDocument.Parse(File.ReadAllText(cp));
                    if (cd.RootElement.TryGetProperty("zhNames", out var z) && z.ValueKind == System.Text.Json.JsonValueKind.Object)
                        foreach (var e in z.EnumerateObject())
                            if (e.Value.ValueKind == System.Text.Json.JsonValueKind.String && e.Value.GetString() is { Length: > 0 } cn)
                                zh[e.Name] = cn;
                }
            }
            catch { }

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("characters", out var arr) || arr.ValueKind != System.Text.Json.JsonValueKind.Array)
                return names;
            foreach (var c in arr.EnumerateArray())
            {
                if (c.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                var key = c.TryGetProperty("key", out var k) && k.ValueKind == System.Text.Json.JsonValueKind.String ? k.GetString() : null;
                if (string.IsNullOrEmpty(key)) continue;
                names.Add(zh.TryGetValue(key, out var cn) ? cn : key);
            }
        }
        catch { }
        return names;
    }

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
            }

            // ── 角色池：双轨合并（用户要求「本地 GOOD + 后台异步 Enka 补全」）──
            // GOOD 可能很全但没有强度字段；Enka 只有展柜角色但强度齐全。
            // 合并取并集，强度字段优先取有值一方 —— 不能直接替换，否则池会变小。
            var goodPath = Path.Combine(AppPaths.Dir, "roster.json");
            var enkaPath = Path.Combine(AppPaths.Dir, "roster-enka.json");
            var mergedPath = Path.Combine(AppPaths.Dir, "roster-merged.json");
            var mg = RosterMerge.Merge(File.Exists(goodPath) ? goodPath : null,
                                       File.Exists(enkaPath) ? enkaPath : null, mergedPath);
            var rosterPath = mg.Ok ? mg.Path : goodPath;

            // 关键：把玩家角色名传给配队，否则"按你的角色池判定"根本没生效（此前一直是全角色）。
            var rosterNames = ReadRosterNames(rosterPath);
            if (season != null)
                TeamSolver.LoadPool(season.Elements, rosterNames);

            // 语料索引必须显式加载——WPF 版迁移时漏了这行，
            // 导致 ○语料兜底层永远是空、设置页显示"语料库 0 条"（本轮实测的真因，
            // 不是打包失败：data 早已随嵌入资源进了 exe）。
            GuideIndex.LoadWithExtra(AppPaths.Data("guide-corpus.json"));
            CuratedPicks.Load(AppPaths.Data("rotations.json"), AppPaths.Data("knowledge-release.json"));
            CharacterStrength.LoadModel(AppPaths.Data("strength-model.json"));
            CharacterStrength.Load(rosterPath, AppPaths.Data("characters.json"));

            PoolNote = mg.Ok
                ? $"角色池 {rosterNames.Count} 个（{mg.Detail}）"
                : $"角色池来源不可用（{mg.Detail}）→ 按全角色估算，置信度已降级";
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
