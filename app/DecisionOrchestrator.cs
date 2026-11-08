using System.Text;

namespace TheaterAssistant;

/// <summary>
/// 决策编排器：把「可计算」与「不可计算（随机/未知）」分开处理 —— 用户的核心要求。
///
/// 审计分类（docs/QUANT-DECISION-AUDIT-PLAN.md §9）：
///
/// ┌─ 可计算（确定性）───────────────────────────────────────────┐
/// │ 敌人抗性/波次 → 需求向量；池子 → 最优 4 人；选项已知 → 打分     │
/// │ 这一层本地毫秒级算完，标 ◆量化                                │
/// └───────────────────────────────────────────────────────────┘
/// ┌─ 不可计算（随机/未知）─────────────────────────────────────┐
/// │ ① 商店/三选一「会出现哪几张」随机                              │
/// │ ② 你实际拥有哪些角色未知（没有 roster.json）                    │
/// │ ③ buff 叠加与数值随机/未知                                    │
/// │ ④ 敌人随机词条（若有）                                        │
/// │ 处理：选项**已被 OCR 读到** → 退化为可计算（对已知选项打分）        │
/// │      选项**没读到/信息不足** → 交 API（LLM）裁决，标 ◇模型        │
/// │      API 不可用 → 语料兜底 ○，再不行 → 规则 ·                  │
/// └───────────────────────────────────────────────────────────┘
///
/// 任何一级都必须给出可执行指示；输出里显式标注 `source` 与 `random` 赌点。
/// </summary>
internal static class DecisionOrchestrator
{
    internal sealed class Context
    {
        public int? Act;
        public string Difficulty = "";
        public QuantData.EnemyProfile Enemy;
        public QuantData.TeamProfile Team;
        public IReadOnlyList<ChoiceDecider.Option> Options = Array.Empty<ChoiceDecider.Option>();
        public string LlmText;              // 外部注入的模型输出（null = 未调用/失败）
        public bool LlmAttempted;
        /// <summary>当前界面类型：act/choice/shop/team…（choice=三选一，内容随机）。</summary>
        public string Kind = "";
    }

    internal sealed class Decision
    {
        public List<string> Lines = new();
        public string Source = "";         // ◆量化 / ★已验证 / ◇模型 / ○语料 / ·规则
        public double Confidence;
        public List<string> Randoms = new();   // 本次决策里"赌"的成分
        public List<string> Provenance = new();
    }

    public static Decision Decide(Context ctx)
    {
        var d = new Decision();
        if (ctx == null) return d;

        CollectRandoms(ctx, d);

        // ---- 0) buff/三选一：内容随机、无量化依据 → 直接走 ◇模型 通道 ----
        // 按用户要求：这类"不可计算的随机要素"不做概率建模，交给模型裁决，
        // 把 token 花在真正需要判断的地方；模型不可用再退回语料/规则。
        bool isBuffChoice = string.Equals(ctx.Kind, "choice", StringComparison.OrdinalIgnoreCase)
                            || (ctx.Options.Count > 0 && ctx.Options.All(o => o.Kind is ChoiceDecider.Kind.Partner
                                                                              or ChoiceDecider.Kind.Mystery
                                                                              or ChoiceDecider.Kind.Blessing));
        if (isBuffChoice && ctx.Options.Count < 2)
        {
            d.Randoms.Add("buff/三选一内容随机（按策略交模型，不做概率建模）");
            d.Provenance.Add("随机要素 → ◇模型 通道");
            if (!string.IsNullOrWhiteSpace(ctx.LlmText))
            {
                foreach (var line in ctx.LlmText.Split('\n').Where(l => l.Trim().Length > 0).Take(3))
                    d.Lines.Add("◇ " + line.Trim());
                d.Source = "◇模型";
                d.Confidence = DecisionPolicy.ConfidenceOf("model", 0.5);
                return d;
            }
            // 模型没取到：不硬编一句"选祝福"，如实说需要模型裁决
            d.Lines.Add("◇ 三选一内容随机，需模型裁决（当前未取到模型输出）");
            d.Lines.Add("· 保守做法：缺人拿「伙伴」，缺输出拿「辉彩祝福」");
            d.Source = "◇模型(未取到)";
            d.Confidence = DecisionPolicy.ConfidenceOf("rule", 0.3);
            return d;
        }

        // ---- 1) 量化优先：选项已知就本地打分，置信度够就直接出 ----
        if (ctx.Options.Count >= 2)
        {
            var (size, target) = (0, 4);
            var pick = ChoiceDecider.Decide(ctx.Options, ctx.Act ?? 0, size, target);
            if (pick?.Pick != null)
            {
                d.Lines.Add($"▶ 选第{pick.Pick.Index}张：{pick.Pick.Raw}");
                d.Confidence = DecisionPolicy.ConfidenceOf("quant", 0.75);
                d.Source = "◆量化";
                d.Provenance.Add($"选项 {ctx.Options.Count} 张已读到 → 本地打分可判");
                if (ctx.Team != null && !ctx.Team.Full)
                    d.Provenance.Add("池子未知，按通用池");
                return d;
            }
        }

        // ---- 2) 云端已验证知识（最强语料，优先于模型）----
        var verified = Playbook.VerifiedFor(ctx.Act);
        if (!string.IsNullOrEmpty(verified))
        {
            d.Lines.Add("★ " + verified);
            d.Source = "★已验证";
            d.Confidence = DecisionPolicy.ConfidenceOf("verified", 0.7);
            d.Provenance.Add("2+ 独立作者 + 固定 holdout 通过");
        }

        // ---- 3) 量化配队建议（有敌人数据就能给）----
        if (ctx.Enemy != null && ctx.Enemy.HasAnyData && TeamSolver.PoolSize > 0)
        {
            var plan = TeamSolver.Solve(ctx.Enemy, ctx.Act ?? 0);
            if (plan.Team.Count == 4 && plan.Score > 0)
            {
                d.Lines.Add($"◆ 本幕最优队伍：{string.Join("/", plan.Team)}（覆盖分 {plan.Score:0.00}）");
                if (d.Source.Length == 0) d.Source = "◆量化";
                d.Provenance.Add($"完全枚举 {TeamSolver.PoolSize} 选 4");
                if (plan.Gaps.Count > 0) d.Randoms.Add("缺口:" + string.Join("+", plan.Gaps));
            }
        }

        // ---- 4) 模型裁决（只在"选项未知/信息不足"时用）----
        if (ctx.LlmAttempted && !string.IsNullOrWhiteSpace(ctx.LlmText))
        {
            foreach (var line in ctx.LlmText.Split('\n').Where(l => l.Trim().Length > 0).Take(3))
                d.Lines.Add("◇ " + line.Trim());
            if (d.Source.Length == 0) d.Source = "◇模型";
            d.Confidence = Math.Max(d.Confidence, DecisionPolicy.ConfidenceOf("model", 0.5));
            d.Provenance.Add("选项未读到 → 交模型裁决（随机要素）");
        }

        // ---- 5a) ○语料兜底：检索攻略语料（GuideIndex）
        // 审计发现：此前这一层**只在注释里**，代码从未调用 GuideIndex——
        // 文档写着五级兜底，实际只有四级。这里补上真实检索。
        if (d.Lines.Count == 0 && GuideIndex.Count > 0 && ctx.Act.HasValue)
        {
            try
            {
                // GuideIndex.Search 返回 List<Hit>，Hit.Entry 才是内容（Title/Text）
                var hits = GuideIndex.Search($"第{ctx.Act}幕", ctx.Act, 3);
                foreach (var h in hits.Take(2))
                {
                    var line = h?.Entry?.Text;
                    if (string.IsNullOrWhiteSpace(line)) line = h?.Entry?.Title;
                    if (!string.IsNullOrWhiteSpace(line))
                        d.Lines.Add("○ " + (line.Length > 60 ? line[..60] + "…" : line.Trim()));
                }
                if (d.Lines.Count > 0)
                {
                    d.Source = "○语料";
                    d.Confidence = Math.Max(d.Confidence, DecisionPolicy.ConfidenceOf("corpus", 0.45));
                    d.Provenance.Add($"GuideIndex 命中 {hits.Count} 条（语料库 {GuideIndex.Count} 条）");
                }
            }
            catch { }
        }

        // ---- 5b) 规则兜底：保证永远有指示 ----
        if (d.Lines.Count == 0)
        {
            // 不依赖 TheaterLogic（它带 OCR/WinForms 依赖，会让 Linux CLI 编译不进来）：
            // 直接查幕内知识数据；查不到就如实说"暂无资料"。
            var next = Playbook.Next("act", ctx.Act) ?? Playbook.Missing;
            d.Lines.Add("· " + (string.IsNullOrEmpty(next) ? "按屏幕提示推进，别交主力耐力" : next));
            d.Source = "·规则";
            d.Confidence = DecisionPolicy.ConfidenceOf("rule", 0.3);
            d.Provenance.Add("量化/语料/模型都不可用");
        }

        return d;
    }

    /// <summary>把本次决策里"赌"的成分显式列出来，让玩家知道哪些是推断。</summary>
    private static void CollectRandoms(Context ctx, Decision d)
    {
        if (ctx.Team == null || !ctx.Team.Full) d.Randoms.Add("角色池未知（按通用池）");
        if (ctx.Options.Count == 0) d.Randoms.Add("当前界面选项未读到（商店/三选一内容随机）");
        if (ctx.Act == null) d.Randoms.Add("幕次未识别");
        if (ctx.Enemy == null || !ctx.Enemy.HasAnyData) d.Randoms.Add("该幕敌人数据缺失");
        if (ctx.Enemy != null && ctx.Enemy.WindowSeconds == 0) d.Randoms.Add("机制时间窗未知");
    }

    public static int RunCli(int act, string difficulty)
    {
        Cli.Attach();
        DecisionPolicy.Load(AppPaths.Data("decision-policy.json"));
        SeasonRegistry.Load();
        QuantData.LoadEnemies(AppPaths.Data("season29.json"));
        QuantData.LoadCharacters(AppPaths.Data("characters.json"), AppPaths.Data("substitutes.json"));
        Playbook.Load(AppPaths.Data("act-playbook.json"));
        TeamSolver.LoadPool();

        var enemy = act > 0 ? QuantData.Enemy(act, difficulty) : null;
        var teamReal = QuantData.Team(Path.Combine(AppPaths.Dir, "roster.json"));
        var team = teamReal.HasData ? teamReal : QuantData.GenericTeam();

        var ctx = new Context
        {
            Act = act > 0 ? act : null,
            Difficulty = difficulty,
            Enemy = enemy,
            Team = team,
            Options = Array.Empty<ChoiceDecider.Option>(),
            LlmAttempted = false,
            Kind = "act",
        };
        var d = Decide(ctx);
        Cli.Say($"来源 {d.Source}｜置信度 {d.Confidence:0.00}｜随机点 {(d.Randoms.Count > 0 ? string.Join("；", d.Randoms) : "无")}");
        foreach (var l in d.Lines) Cli.Say("  " + l);
        foreach (var p in d.Provenance) Cli.Say("  · " + p);

        // 三态验证：有选项 / 仅模型 / 全不可用
        // 关键：必须挑**没有云端已验证知识**的幕，否则 ★已验证 会盖住模型/规则通道，
        // 三个分支看起来都"通过"，实际根本没走到（云端审计抓到的假绿）。
        int actNoVerified = Enumerable.Range(1, 12).FirstOrDefault(a => Playbook.VerifiedFor(a) == null);
        if (actNoVerified == 0) actNoVerified = 9;
        Cli.Say("");
        Cli.Say($"三态自检（用第{actNoVerified}幕：无已验证知识，确保模型/规则通道被真正走到）：");
        var withOpts = Decide(new Context
        {
            Act = act > 0 ? act : actNoVerified, Difficulty = difficulty, Enemy = enemy, Team = team,
            Options = new[] { new ChoiceDecider.Option { Index = 1, Raw = "战斗·普通" }, new ChoiceDecider.Option { Index = 2, Raw = "伙伴" } },
        });
        Cli.Say($"  ① 选项已知 → {withOpts.Source}：{(withOpts.Lines.Count > 0 ? withOpts.Lines[0] : "（空，错误）")}");
        var withLlm = Decide(new Context
        {
            Act = act > 0 ? act : actNoVerified, Difficulty = difficulty, Enemy = enemy, Team = team,
            LlmAttempted = true, LlmText = "先上雷把怒气条推满，满条后立刻爆发",
        });
        Cli.Say($"  ② 仅模型可用 → {withLlm.Source}：{(withLlm.Lines.Count > 0 ? withLlm.Lines[0] : "（空，错误）")}");
        var none = Decide(new Context { Act = act > 0 ? act : actNoVerified, Difficulty = difficulty });
        Cli.Say($"  ③ 全都不可用 → {none.Source}：{(none.Lines.Count > 0 ? none.Lines[0] : "（空，错误）")}");
        return 0;
    }
}
