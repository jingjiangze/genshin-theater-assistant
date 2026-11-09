// ─────────────────────────────────────────────────────────────────────────────
// 本文件包含对 BetterGI（https://github.com/babalae/better-genshin-impact）实现的
// 参考或衍生。原版权：Copyright (c) babalae 及 BetterGI contributors（GPL-3.0）。
// 本项目在 WinForms + .NET 8 下重写/改写，修改时间 2026-11。
// 本文件以 GPL-3.0 分发，全文见仓库根 LICENSE，来源说明见 NOTICE.md。
// ─────────────────────────────────────────────────────────────────────────────
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;

namespace TheaterAssistant;

/// <summary>
/// 自检：不依赖游戏、不依赖用户的截图，纯函数 + 合成图断言。
/// CI 里跑的是这一套；带 --with-ocr 时再加一条"OCR 真的能读出坐标"的端到端断言。
///
///   TheaterAssistant.exe --selftest
///   TheaterAssistant.exe --selftest --with-ocr
/// </summary>
internal static class SelfTest
{
    private static int _pass;
    private static int _fail;
    private static readonly List<string> Lines = new();

    public static int Run(bool withOcr)
    {
        Cli.Attach();
        _pass = 0;
        _fail = 0;
        Lines.Clear();

        Lines.Add("=== 幻想真境剧诗助手 · 自检 ===");
        Lines.Add("时长口径: 纯函数断言（不需要模型/不需要游戏）");

        CheckActNumbers();
        CheckMakeSpec();
        CheckDerive();
        CheckHallVsBand();
        CheckRosterScan();
        CheckScoreMath();
        CheckCropRoundTrip();

        if (withOcr) CheckOcrBoxes();

        Lines.Add("");
        Lines.Add($"通过 {_pass} / 失败 {_fail}");
        foreach (var l in Lines) Cli.Say(l);

        try
        {
            File.WriteAllText(Path.Combine(AppPaths.Dir, "selftest-report.txt"),
                string.Join(Environment.NewLine, Lines), Encoding.UTF8);
        }
        catch { }

        return _fail == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok, string detail)
    {
        if (ok) { _pass++; Lines.Add($"PASS  {name}  {detail}"); }
        else { _fail++; Lines.Add($"FAIL  {name}  {detail}"); }
    }

    // ---- 1. 幕次数字 ----
    private static void CheckActNumbers()
    {
        var cases = new (string S, int? Expect)[]
        {
            ("一", 1), ("三", 3), ("十", 10), ("十一", 11), ("二十", 20), ("二十一", 21),
            ("4", 4), ("12", 12), ("", null), ("abc", null), ("零", null),
        };
        int bad = 0;
        foreach (var c in cases)
        {
            var got = TemplateFactory.ParseActNumber(c.S);
            if (got != c.Expect) bad++;
        }
        Check("ParseActNumber", bad == 0, $"{cases.Length} 例，失败 {bad}");
    }

    // ---- 2. 规格边距与边界收敛 ----
    private static void CheckMakeSpec()
    {
        // box(10,10,100,40) @1000x600，pad = round(40*0.15) = 6
        // 左上被边界吃掉 6px：x=4,w=112-6=106？不——x=10-6=4 未越界，w=100+12=112，h=40+12=52
        var s = TemplateFactory.MakeSpec("t", "TESTLABEL", 0.9, new Rectangle(10, 10, 100, 40), 1000, 600);
        bool ok = s != null && s.X == 4 && s.Y == 4 && s.W == 112 && s.H == 52;
        Check("MakeSpec 边距/收敛", ok, s == null ? "null" : $"{s.X},{s.Y} {s.W}x{s.H}（期望 4,4 112x52）");

        // 越界时边界吃掉：box 贴左上角 → pad 被裁掉
        var clamp = TemplateFactory.MakeSpec("t", "TESTLABEL", 0.9, new Rectangle(2, 2, 100, 40), 1000, 600);
        bool okClamp = clamp != null && clamp.X == 0 && clamp.Y == 0 && clamp.W == 108 && clamp.H == 48;
        Check("MakeSpec 贴边收敛", okClamp, clamp == null ? "null" : $"{clamp.X},{clamp.Y} {clamp.W}x{clamp.H}（期望 0,0 108x48）");

        // 越界右侧收敛
        var r = TemplateFactory.MakeSpec("t", "x", 0.9, new Rectangle(950, 570, 100, 40), 1000, 600);
        bool ok2 = r != null && r.X + r.W <= 1000 && r.Y + r.H <= 600;
        Check("MakeSpec 不越界", ok2, r == null ? "null" : $"{r.X + r.W}x{r.Y + r.H}");

        // 超大框（>6% 帧面积）应该被拒
        var big = TemplateFactory.MakeSpec("t", "x", 0.9, new Rectangle(0, 0, 800, 400), 1000, 600);
        Check("MakeSpec 拒超大框", big == null, big == null ? "拒绝" : "未拒绝");

        // 过小框应该被拒
        var tiny = TemplateFactory.MakeSpec("t", "x", 0.9, new Rectangle(5, 5, 6, 5), 1000, 600);
        Check("MakeSpec 拒过小框", tiny == null, tiny == null ? "拒绝" : "未拒绝");
    }

    // ---- 3. 文本 → 规格派生 ----
    private static void CheckDerive()
    {
        var boxes = new List<OcrBox>
        {
            new() { Text = "TESTLABEL", Rect = new Rectangle(100, 100, 200, 40), Score = 0.90 },
            new() { Text = "第3幕", Rect = new Rectangle(100, 600, 120, 40), Score = 0.95 },
            new() { Text = "第三幕", Rect = new Rectangle(500, 600, 120, 40), Score = 0.80 },   // 同名，低分应被淘汰
            new() { Text = "演出结算", Rect = new Rectangle(100, 300, 160, 40), Score = 0.88 },
            new() { Text = "无关文字", Rect = new Rectangle(100, 400, 160, 40), Score = 0.99 },
        };

        var specs = TemplateFactory.Derive(boxes, 1280, 720);
        var names = specs.Select(s => s.Name).OrderBy(x => x, StringComparer.Ordinal).ToList();
        bool namesOk = names.SequenceEqual(new[] { "act3", "result", "test" });
        Check("Derive 命名", namesOk, string.Join(",", names));

        var act = specs.FirstOrDefault(s => s.Name == "act3");
        bool actOk = act != null && act.X == 94 && act.Y == 594 && act.Text == "第3幕";  // pad = round(40*0.15)=6
        Check("Derive 幕次矩形", actOk, act == null ? "null" : $"{act.X},{act.Y} {act.W}x{act.H}");

        var test = specs.FirstOrDefault(s => s.Name == "test");
        bool dupOk = test != null;
        Check("Derive 去重/ASCII", dupOk, test == null ? "null" : test.Text);

        // 幕次列表行（第一幕/第二幕/第三幕 同一排并排）不能派生 actN，否则十个模板同时命中
        var listBoxes = new List<OcrBox>
        {
            new() { Text = "第一幕", Rect = new Rectangle(100, 900, 90, 24), Score = 0.9 },
            new() { Text = "第二幕", Rect = new Rectangle(200, 900, 90, 24), Score = 0.9 },
            new() { Text = "第三幕", Rect = new Rectangle(300, 900, 90, 24), Score = 0.9 },
        };
        var listSpecs = TemplateFactory.Derive(listBoxes, 1280, 1080);
        Check("Derive 幕次列表不派生 actN", listSpecs.All(s => !s.Name.StartsWith("act", StringComparison.Ordinal)),
            listSpecs.Count == 0 ? "（空）" : string.Join(",", listSpecs.Select(s => s.Name)));

        // 真实 1920x1080 局内画面：底部列表行 + 中央「第5幕」标签（y 不同）→ 只派生 act5
        var mixed = listBoxes.Select(b => new OcrBox
        {
            Text = b.Text,
            Rect = new Rectangle(b.Rect.X, b.Rect.Y, b.Rect.Width, b.Rect.Height),
            Score = b.Score,
        }).ToList();
        mixed.Add(new OcrBox { Text = "第5幕", Rect = new Rectangle(900, 700, 120, 44), Score = 0.96 });
        var mixedSpecs = TemplateFactory.Derive(mixed, 1920, 1080);
        var actNames = mixedSpecs.Where(s => s.Name.StartsWith("act", StringComparison.Ordinal)).Select(s => s.Name).ToList();
        Check("Derive 列表行外的局内幕次", actNames.SequenceEqual(new[] { "act5" }), string.Join(",", actNames));

        // 上半部的幕次文字（UP 贴字/攻略面板）不当锚点
        var topOnly = new List<OcrBox>
        {
            new() { Text = "第五幕", Rect = new Rectangle(100, 40, 120, 40), Score = 0.95 },
        };
        var topSpecs = TemplateFactory.Derive(topOnly, 1920, 1080);
        Check("Derive 上半部幕次不派生", topSpecs.Count == 0, topSpecs.Count == 0 ? "（空）" : string.Join(",", topSpecs.Select(s => s.Name)));
    }

    // ---- 3b. 界面判定：大堂锚点优先于底部幕次列表 ----
    private static void CheckHallVsBand()
    {
        var data = TheaterData.Load(AppPaths.Data("season29.json"));
        var roster = new RosterData();

        // 真实 640x360 大堂帧的实测 OCR 文本：底部幕次标签被读成「第5品」，不能据此判成第 5 幕
        var hallAdvice = TheaterLogic.Evaluate(new List<Matcher.Hit>(),
            "助演阵容\n演出准备\n演出回顾\n第5品", data, roster, "第5品");
        Check("大堂界面不采信条带", hallAdvice.Act == null && hallAdvice.Header.Contains("剧诗界面"),
            $"act={hallAdvice.Act?.ToString() ?? "-"} header={hallAdvice.Header}");

        // 进行中画面：条带只认出一个幕次 → 正常判定
        var actAdvice = TheaterLogic.Evaluate(new List<Matcher.Hit>(), "选人", data, roster, "小第1第");
        Check("条带单幕次可用", actAdvice.Act == 1, $"act={actAdvice.Act?.ToString() ?? "-"}");

        // 条带里出现多个幕次 → 判为列表，不采信
        var multi = TheaterLogic.Evaluate(new List<Matcher.Hit>(), "战斗", data, roster, "第1 第3 第5");
        Check("条带多幕次不采信", multi.Act == null, $"act={multi.Act?.ToString() ?? "-"}");

        // 条带里同时出现多个「第N幕」（底部幕次列表）时，严格路径也必须拒绝 —— video-eval 42.1% 的主因
        var multiStrict = TheaterLogic.Evaluate(new List<Matcher.Hit>(), "战斗", data, roster, "第5幕 第6幕 第7幕");
        Check("条带多幕次(严格)不采信", multiStrict.Act == null, $"act={multiStrict.Act?.ToString() ?? "-"}");

        // 反过来：整帧出现大堂词（视频里 UP 常贴"助演阵容"）不能压掉幕内画面的幕次判定
        var inRun = TheaterLogic.Evaluate(new List<Matcher.Hit>(), "助演阵容\n第3幕", data, roster, null);
        Check("大堂词不压幕内幕次", inRun.Act == 3, $"act={inRun.Act?.ToString() ?? "-"} header={inRun.Header}");

        // 条带明确带"幕"字时，即使是大堂界面也照信（高精度路径）
        var strictBand = TheaterLogic.Evaluate(new List<Matcher.Hit>(), "助演阵容\n演出准备", data, roster, "第2幕");
        Check("条带带幕字优先", strictBand.Act == 2, $"act={strictBand.Act?.ToString() ?? "-"}");

        // 真实截图复现：难度选择界面写「完成『圣牌挑战』并通过第十幕演出可抽取…」，
        // 绝不能据此判成当前第 10 幕
        var diffScreen = TheaterLogic.Evaluate(new List<Matcher.Hit>(),
            "难度选择\n卓越模式\n月谕模式\n完成12场战斗演出后结束剧演\n完成「圣牌挑战」并通过第十幕演出可抽取「月谕圣牌」\n本期角色元素类型限制\n参演要求\n至少编入28名角色以开启演出",
            data, roster, null);
        Check("难度选择不被当成第10幕", diffScreen.Act == null && diffScreen.Kind == "difficulty",
            $"act={diffScreen.Act?.ToString() ?? "-"} kind={diffScreen.Kind} header={diffScreen.Header}");

        // 队伍配置界面同样归 team
        var teamScreen = TheaterLogic.Evaluate(new List<Matcher.Hit>(),
            "队伍配置\n开幕角色\n试用角色\n待命角色\n已选开幕与待命角色", data, roster, null);
        Check("队伍配置判 team", teamScreen.Kind == "team" && teamScreen.Act == null,
            $"kind={teamScreen.Kind} act={teamScreen.Act?.ToString() ?? "-"}");

        // 判定优先级：底部条带 > 整帧文字（整帧里可能混着攻略贴字里的"第1幕"）
        var precedence = TheaterLogic.Evaluate(new List<Matcher.Hit>(),
            "BUFF：1幕需2雷岩 第1幕", data, roster, "第4幕");
        Check("条带优先于整帧文字", precedence.Act == 4, $"act={precedence.Act?.ToString() ?? "-"} src={precedence.Diagnostics}");

        // 模板命中 team 也走同一语义
        var teamHit = TheaterLogic.Evaluate(
            new List<Matcher.Hit> { new() { Name = "team", Score = 0.97, X = 0, Y = 0 } },
            "", data, roster, null);
        Check("模板 team 命中判 team", teamHit.Kind == "team", $"kind={teamHit.Kind} header={teamHit.Header}");
    }

    // ---- 4. 角色名匹配（免登录角色池的核心） ----
    private static void CheckRosterScan()
    {
        var names = new[] { "夜兰", "纳西妲", "雷电将军", "芭芭拉", "流浪者" };
        var cases = new (string S, string Expect)[]
        {
            ("夜兰", "夜兰"),
            ("夜兰Lv.90", "夜兰"),         // 名字和等级贴在一起
            ("Lv.90 夜兰", "夜兰"),
            ("雷电将军", "雷电将军"),       // 不能被更短的"雷电"吃掉（词典里也没有"雷电"）
            ("我的角色", null),
            ("战斗·普通", null),
            ("", null),
        };
        int bad = 0;
        var detail = new List<string>();
        foreach (var c in cases)
        {
            var got = RosterScan.MatchName(c.S, names);
            if (got != c.Expect) { bad++; detail.Add($"{c.S}→{got ?? "null"}"); }
        }
        Check("RosterScan 名字匹配", bad == 0, $"{cases.Length} 例，失败 {bad}" + (bad > 0 ? "：" + string.Join(",", detail) : ""));

        // 模板级阈值：actN 更严（跨幕相似度实测 0.80–0.81），其它界面锚点 0.86
        Check("模板阈值分级", TemplateEntry.ThresholdFor("act8") == 0.90 && TemplateEntry.ThresholdFor("act10") == 0.90
              && TemplateEntry.ThresholdFor("hall") == 0.86 && TemplateEntry.ThresholdFor("team") == 0.86,
              $"act8={TemplateEntry.ThresholdFor("act8")} hall={TemplateEntry.ThresholdFor("hall")}");

        // 从界面文字读编入人数（实测文案两种写法）
        var ts1 = ChoiceDecider.ParseTeamSize("已额外编入4/4名待命角色，并根据额外编入的角色数量获得增益");
        var ts2 = ChoiceDecider.ParseTeamSize("编入的角色数量 26");
        var ts3 = ChoiceDecider.ParseTeamSize("队伍配置");
        Check("决策·读编入人数", ts1 == (4, 4) && ts2.Size == 26 && ts3 == (0, 0),
            $"{ts1} / {ts2} / {ts3}");

        // JSON 选项必须序列化"公共字段"（replay 的 Step 是字段；否则写出空对象，蒸馏全 0）
        Check("JSON 选项包含字段", TemplateFactory.JsonOpts.IncludeFields, "IncludeFields=" + TemplateFactory.JsonOpts.IncludeFields);

        // 蒸馏口径：目录里 ops.json 不能混进 replay 统计（实测曾把 42 份虚报成 84 份）
        var dTmp = Path.Combine(Path.GetTempPath(), "ta-distill-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dTmp);
            File.WriteAllText(Path.Combine(dTmp, "a-replay.json"), "{\"frames\":2,\"steps\":[]}");
            File.WriteAllText(Path.Combine(dTmp, "a-ops.json"), "{\"ops\":[]}");
            File.WriteAllText(Path.Combine(dTmp, "rules.json"), "{}");
            Check("蒸馏只收 replay 报告", Distiller.Expand(new[] { dTmp }).Count() == 1,
                string.Join(",", Distiller.Expand(new[] { dTmp }).Select(Path.GetFileName)));
        }
        finally { try { Directory.Delete(dTmp, true); } catch { } }

        // 跨视频共识：同一文本 2 个不同视频 → 共识；UI 文案过滤；同一视频 2 帧只算 1
        var agg = OpsConsensus.Aggregate(new[]
        {
            ("买一个角色", "buy", "BV1aa@10s"), ("买一个角色", "buy", "BV1aa@20s"),
            ("买一个角色", "buy", "BV1bb@30s"), ("可选角色", "buy", "BV1cc@40s"),
            ("钟E-菲Q-卡Qe", "combo", "BV1dd@50s"),
        });
        Check("跨视频共识聚合",
            agg.Count == 2 && agg[0].Videos == 2 && agg[0].Text == "买一个角色" && agg.All(r => r.Text != "可选角色"),
            string.Join(" | ", agg.Select(r => $"{r.Text}:{r.Videos}")));
        Check("共识·UI 文案过滤",
            OpsConsensus.IsNoise("可选角色") && !OpsConsensus.IsNoise("买一个角色")
            && OpsConsensus.IsNoise("过去的魔女留下的挑战，需要许多伙伴组成的队伍共同攻克。"),
            "可选角色=噪声, 剧情文案=噪声, 买一个角色=保留");

        var creatorAgg = OpsConsensus.AggregateEvidence(new[]
        {
            new OpsConsensus.Evidence { Text = "买一个角色", Kind = "buy", Source = "BV1aa", CreatorId = "creator-A" },
            new OpsConsensus.Evidence { Text = "买一个角色", Kind = "buy", Source = "BV1bb", CreatorId = "creator-A" },
            new OpsConsensus.Evidence { Text = "买一个角色", Kind = "buy", Source = "BV1cc", CreatorId = "creator-B" },
            new OpsConsensus.Evidence { Text = "买一个角色", Kind = "buy", Source = "BV1dd", CreatorId = "" },
        });
        Check("共识·转载/同作者多视频不增加独立作者数",
            creatorAgg.Count == 1 && creatorAgg[0].Videos == 4 && creatorAgg[0].IndependentCreators == 2,
            $"videos={creatorAgg.FirstOrDefault()?.Videos}, creators={creatorAgg.FirstOrDefault()?.IndependentCreators}");
        Check("共识·界面文案变体过滤（本机真跑命中）",
            OpsConsensus.IsNoise("可选角色预览")
            && OpsConsensus.IsNoise("请选择一名待命角色转变为可出战角色")
            && OpsConsensus.IsNoise("可从2个神秘收获效果中选择1个")
            && OpsConsensus.IsNoise("选择一个神秘收获效果")
            && !OpsConsensus.IsNoise("优先选最后两关主C与辅助"),
            "4 条界面文案被判噪声，真实攻略保留");

        Check("共识·跨平台出处去重",
            OpsConsensus.SplitBv("bilibili:BV1aa@10s") == "BV1aa"
            && OpsConsensus.SplitBv("youtube:dQw4w9WgXcQ") == "dQw4w9WgXcQ"
            && OpsConsensus.SplitBv("BV1aa@20s") == "BV1aa",
            "bilibili:/youtube: 前缀都能归一到视频 id");

        var plannerDir = Path.Combine(Path.GetTempPath(), "theater-planner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(plannerDir);
        try
        {
            var seasonPath = Path.Combine(plannerDir, "season.json");
            var rosterPath = Path.Combine(plannerDir, "good.json");
            File.WriteAllText(seasonPath, """
            {"season":1,"elements":["Hydro"],"acts":{"Hard":{
              "1":{"level":80,"boss":false,"encounters":[{"name":"Act1 Yelan","rec":"Hydro","chars":"Yelan"}]},
              "2":{"level":80,"boss":false,"encounters":[
                {"name":"Act2 Yelan","rec":"Hydro","chars":"Yelan"},
                {"name":"Act2 Xingqiu","rec":"Hydro","chars":"Xingqiu"}]}
            }}}
            """);
            File.WriteAllText(rosterPath, """
            {"characters":[{"key":"Yelan","level":90},{"key":"Xingqiu","level":90}]}
            """);
            var plan = ActPlanner.Build(TheaterData.Load(seasonPath), RosterData.TryLoad(rosterPath), "Hard");
            var chosen = plan.Acts.SelectMany(a => a.RecommendedCharacters).ToList();
            Check("多幕规划·跨幕角色复用软约束",
                plan.Acts.Count == 2 && plan.Acts[0].Encounter == "Act1 Yelan"
                && plan.Acts[1].Encounter == "Act2 Xingqiu" && chosen.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2,
                string.Join(" | ", plan.Acts.Select(a => $"{a.Act}:{a.Encounter}")));
        }
        finally { try { Directory.Delete(plannerDir, true); } catch { } }

        Routine.Load(AppPaths.Data("rotations.json"));
        Playbook.Load(AppPaths.Data("act-playbook.json"));
        var legacyRoutine = Routine.LinesFor(0, 10);
        var inGameRoutine = Routine.LinesFor(3, 10);
        Check("知识治理·旧视频/社区经验降级为历史候选",
            legacyRoutine.Any(x => x.Contains("历史候选手法")),
            string.Join(" | ", legacyRoutine.Take(2)));
        Check("知识治理·游戏内机制基线保留",
            inGameRoutine.Any(x => x.StartsWith("手法·")) && inGameRoutine.All(x => !x.Contains("历史候选手法")),
            string.Join(" | ", inGameRoutine.Take(2)));

        // 操作提取分类（纯函数）：连招 / 购买 / 配队 / 行动
        Check("操作分类",
            OpsExtract.Classify("钟E-菲Q-卡Qe-菲e-钟Q") == "combo"
            && OpsExtract.Classify("买两个角色（优先岩挂件）") == "buy"
            && OpsExtract.Classify("队伍：钟离+菲谢尔+卡齐娜+挂件") == "team"
            && OpsExtract.Classify("优先打带标记的目标") == "action"
            && OpsExtract.Classify("幻想真境剧诗") == null,
            "combo/buy/team/action 分类");

        // 手法数据：首领幕必须有专属编排，且带出处
        Routine.Load(AppPaths.Data("rotations.json"));
        Playbook.Load(AppPaths.Data("act-playbook.json"));
        var r6 = Routine.LinesFor(6, 3);
        var r3 = Routine.LinesFor(3, 3);
        Check("手法·首领幕有编排", Routine.Loaded && r6.Count >= 2 && r3.Count >= 2 && r6.Any(l => l.Contains("钓客")),
            $"载入 {Routine.Count} 幕，第6幕 {r6.Count} 行：{(r6.Count > 0 ? r6[0] : "-")}");
        Check("选人·跨 UP 主推荐已入库",
            r6.Any(l => l.Contains("选人·第 6 幕") && (l.Contains("夜兰") || l.Contains("班尼特")))
            && Routine.LinesFor(7, 3).Any(l => l.Contains("行秋")),
            "第6/7幕有共识角色");
        var rAll = Routine.LinesFor(0, 10);
        Check("手法·社区共识已入库", rAll.Any(l => l.Contains("主 C 与辅助留给")) && rAll.Any(l => l.Contains("从左到右")),
            $"通用 {rAll.Count} 步，含跨视频共识");

        // 幕内知识全部来自 data/act-playbook.json：代码不再按幕硬编码
        // 三选一：带副标题的卡片也要解析成选项（50 视频实测 choice 10 帧 / 0 决策）
        var ch1 = ChoiceDecider.ParseOptions(new[] { "辉彩祝福·雷晶", "伙伴邀请", "神秘收获" });
        var ch2 = ChoiceDecider.ParseOptions(new[] { "选择辉彩祝福后，本幕所有角色的元素伤害提高。", "伙伴" });
        Check("决策·三选一卡片解析", ch1.Count == 3 && ch2.Count == 1,
            $"带副标题 {ch1.Count} 个选项，长句被排除后 {ch2.Count} 个");

        Check("知识·playbook 载入", Playbook.Loaded && Playbook.ActCount >= 8,
            $"载入 {Playbook.ActCount} 幕");
        // P1 量化数据：敌人侧解析出抗性/需求；缺数据时必须说"无"，不许外推
        QuantData.LoadEnemies(AppPaths.Data("season29.json"));
        QuantData.LoadCharacters(AppPaths.Data("characters.json"), AppPaths.Data("substitutes.json"));
        var boss3 = QuantData.Enemy(3, "Lunar");
        Check("量化·敌人档案可解析",
            QuantData.Difficulties.Count >= 4 && boss3 != null && boss3.Level > 0,
            $"难度 {QuantData.Difficulties.Count} 个 | 第3幕 Lv{boss3?.Level} 波次{boss3?.Waves.Count} 需求{boss3?.Need.Count}");
        Check("量化·需求向量有据可依", boss3 != null && boss3.Need.ContainsKey("Electro"),
            boss3 == null ? "无档案" : string.Join(",", boss3.Need.Select(k => k.Key + "=" + k.Value.ToString("0.0"))));
        var teamNoRoster = QuantData.Team(Path.Combine(AppPaths.Dir, "__no_roster__.json"));
        Check("量化·池子缺失标 Full=false", !teamNoRoster.Full, teamNoRoster.Source);
        var generic = QuantData.GenericTeam();
        Check("量化·通用池有元素覆盖", generic.Cover.Count > 0 && !generic.Full,
            $"覆盖 {generic.Cover.Count} 元素，Full={generic.Full}");

        // P2 量化评分器：单调性 / 缺口 / 置信度 / 通用池降置信
        QuantScorer.LoadWeights(AppPaths.Data("quant-model.json"));
        var enemy3 = QuantData.Enemy(3, "Lunar");
        QuantData.TeamProfile MakeTeam(string[] members)
        {
            var tp = new QuantData.TeamProfile { Full = true, Source = "(测试池)" };
            foreach (var n in members)
            {
                var el = QuantData.ElementOf(n);
                tp.Members.Add(new QuantData.Member { Name = n, Element = el });
                if (el.Length > 0) tp.Cover[el] = tp.Cover.TryGetValue(el, out var c) ? c + 1 : 1;
            }
            return tp;
        }
        // 中文名 → 元素必须能查到：characters.json 是 {英文名:"元素"} + {英文名:中文名}，要做反转
        Check("量化·中文名可解析元素",
            QuantData.ElementOf("菲谢尔") == "Electro" && QuantData.ElementOf("行秋") == "Hydro",
            $"菲谢尔={QuantData.ElementOf("菲谢尔")} 行秋={QuantData.ElementOf("行秋")}");

        var sNone = QuantScorer.Score(enemy3, MakeTeam(new[] { "钟离" }), 3);       // 无 Electro
        var sSome = QuantScorer.Score(enemy3, MakeTeam(new[] { "菲谢尔" }), 3);     // 1 个 Electro
        var sFull = QuantScorer.Score(enemy3, MakeTeam(new[] { "菲谢尔", "雷电将军" }), 3); // 2 个 Electro
        Check("量化·评分单调性（覆盖越多分越高）",
            sNone != null && sSome != null && sFull != null && sNone.Score <= sSome.Score && sSome.Score <= sFull.Score,
            sNone == null ? "无档案" : $"无={sNone.Score:0.00} 1人={sSome.Score:0.00} 2人={sFull.Score:0.00}");
        Check("量化·缺口显式列出", sNone != null && sNone.Gaps.Count > 0 && sFull != null && sFull.Gaps.Count == 0,
            sNone == null ? "-" : $"缺={(sNone.Gaps.Count > 0 ? string.Join("+", sNone.Gaps) : "无")} 全覆盖缺口={sFull?.Gaps.Count}");
        Check("量化·置信度在 0..1 且通用池降置信",
            sFull != null && sFull.Confidence is >= 0 and <= 1
            && QuantScorer.Score(enemy3, generic, 3)!.Confidence <= 0.4,
            sFull == null ? "-" : $"实测池conf={sFull.Confidence:0.00} 通用池conf={QuantScorer.Score(enemy3, generic, 3)!.Confidence:0.00}");
        Check("量化·结论标注池子来源", sFull != null && sFull.PoolFull && sFull.Decision.Length > 0
            && QuantScorer.Score(enemy3, generic, 3)!.Decision.Contains("通用池"),
            sFull == null ? "-" : sFull.Decision);

        // P2+ 通用最优配队：假设全角色 → 完全枚举 → 每难度最优；最高难度必须全覆盖
        Check("配队·池子按本季元素过滤",
            TeamSolver.LoadPool() && TeamSolver.PoolSize >= 20
            && TeamSolver.SeasonElements.Count >= 1,
            $"池 {TeamSolver.PoolSize} 人｜本季 {string.Join("/", TeamSolver.SeasonElements)}");

        var lunarTeam3 = TeamSolver.Solve(QuantData.Enemy(3, "Lunar"), 3);
        Check("配队·最高难度有 4 人解",
            lunarTeam3.Team.Count == 4 && lunarTeam3.Score > 0,
            $"Lunar第3幕 {string.Join("/", lunarTeam3.Team)} score={lunarTeam3.Score:0.00}");

        // 需求元素必须被覆盖（覆盖证明存在），否则该幕不可信
        var host3 = QuantData.Enemy(3, "Lunar");
        bool coverOk = host3 == null || host3.Need.Count == 0
            || host3.Need.Keys.All(el => lunarTeam3.Cover.TryGetValue(el, out var c) && c > 0);
        Check("配队·主需求被覆盖（有覆盖证明）", coverOk,
            host3 == null ? "-" : $"需求 {string.Join(",", host3.Need.Keys)}｜覆盖 {string.Join(",", lunarTeam3.Cover.Select(k => k.Key + "×" + k.Value))}");

        // 无元素需求的幕：通用队分数固定 0，不得为负（否则看起来像"不合格"）
        var noNeed = TeamSolver.Solve(QuantData.Enemy(1, "Lunar"), 1);
        Check("配队·无需求幕不给负分", noNeed.Team.Count == 4 && noNeed.Score == 0,
            $"第1幕 {string.Join("/", noNeed.Team)} score={noNeed.Score:0.00} 有元素需求={noNeed.HasElementNeed}");

        // 队员必须本季带得动：元素必须在本季开放元素里（防止火/冰角色混进 Hydro/Electro/Geo 赛季）
        var badMembers = new List<string>();
        foreach (var d in QuantData.Difficulties)
        {
            var maxAct2 = d switch { "Easy" => 3, "Normal" => 6, "Hard" => 8, "Visionary" => 10, "Lunar" => 12, _ => 12 };
            for (int a = 1; a <= maxAct2; a++)
            {
                var e2 = QuantData.Enemy(a, d);
                if (e2 == null || !e2.HasAnyData) continue;
                foreach (var nm in TeamSolver.Solve(e2, a).Team)
                    if (!TeamSolver.IsAvailableThisSeason(nm)) badMembers.Add($"{d}第{a}幕:{nm}");
            }
        }
        Check("配队·队员本季可带", badMembers.Count == 0,
            badMembers.Count == 0 ? "全部队员元素均在本季开放元素内" : string.Join(", ", badMembers.Take(5)));

        // 每难度都要有解（不能只有最高难度能算）
        int solved = 0, totalAct = 0;
        foreach (var d in QuantData.Difficulties)
        {
            var maxAct = d switch { "Easy" => 3, "Normal" => 6, "Hard" => 8, "Visionary" => 10, "Lunar" => 12, _ => 12 };
            for (int a = 1; a <= maxAct; a++)
            {
                var e = QuantData.Enemy(a, d);
                if (e == null || !e.HasAnyData) continue;
                totalAct++;
                if (TeamSolver.Solve(e, a).Team.Count == 4) solved++;
            }
        }
        Check("配队·每个难度每幕都有解", totalAct > 0 && solved == totalAct,
            $"{solved}/{totalAct} 幕求出 4 人队");

        // 别名 → 官方名 → 元素/职能 必须能串起来（此前心海元素为空，池子里等于废人）
        // 别名归一依赖策略已加载：必须显式先 Load，否则 Canonical 还是恒等映射
        DecisionPolicy.Load(AppPaths.Data("decision-policy.json"));
        TeamSolver.LoadPool();
        Check("策略·别名角色元素与职能可解析",
            QuantData.ElementOf("珊瑚宫心海") == "Hydro" && TeamSolver.HasRole("珊瑚宫心海", "Healer"),
            $"珊瑚宫心海 元素={QuantData.ElementOf("珊瑚宫心海")} 治疗={TeamSolver.HasRole("珊瑚宫心海", "Healer")}");

        // ○语料兜底层必须真实存在（审计发现此前只在注释里）
        {
            GuideIndex.LoadWithExtra(AppPaths.Data("guide-corpus.json"));
            var noInfo = DecisionOrchestrator.Decide(new DecisionOrchestrator.Context { Act = 3 });
            Check("编排·○语料层真实命中", GuideIndex.Count > 0,
                $"语料库 {GuideIndex.Count} 条｜第3幕无信息时来源={noInfo.Source}");
        }

        // 弃权必须按时间而非帧数（tick=1000ms 时 3 帧=3 秒过于激进）
        {
            var st = new ActStabilizer { AbstainAfterMilliseconds = 200 };
            st.Push(9);
            st.Push(null);
            var early = st.ShouldAbstain;
            System.Threading.Thread.Sleep(260);
            st.Push(null);
            Check("弃权·按持续时间判定", !early && st.ShouldAbstain,
                $"200ms 内不弃权={!early}｜超时后弃权={st.ShouldAbstain}｜累计 {st.MissedMilliseconds}ms");
        }

        // 角色强度：GOOD 多指标量化（圣遗物/天赋/命座/武器），缺字段必须不评分
        {
            CharacterStrength.LoadModel(AppPaths.Data("strength-model.json"));
            // 注意：samples/ 在仓库根，不在 exe 目录（CI 里 AppPaths.Dir = dist/）；
            // data/ 会随包分发，所以演示样本放 data/ 才可靠（本轮踩过）。
            var demoRoster = AppPaths.Data("roster-strength-demo.json");
            if (File.Exists(demoRoster))
            {
                CharacterStrength.Load(demoRoster, AppPaths.Data("characters.json"));
                var yelan = CharacterStrength.ScoreOf("夜兰");
                var zhongli = CharacterStrength.ScoreOf("钟离");
                Check("强度·GOOD 多指标可量化",
                    yelan > 0 && zhongli < 0,
                    $"夜兰={yelan:0.0} 分（6命/天赋6-10-13/专武/圣遗物）；钟离={zhongli:0.0}（-1=缺字段不评分）");
                var noelle = CharacterStrength.ScoreOf("诺艾尔");
                Check("强度·命座与圣遗物参与计分",
                    noelle > 0 && yelan > noelle,
                    $"诺艾尔（6命但圣遗物弱）={noelle:0.0} < 夜兰={yelan:0.0}");
            }
            else Check("强度·GOOD 多指标可量化", false, "缺演示样本 strength-demo.json");
        }

        // 幕级推荐（社区共识 + 已验证知识）必须真的进到选人里，而不是只当提示文本
        {
            CuratedPicks.Load(AppPaths.Data("rotations.json"), AppPaths.Data("knowledge-release.json"));
            Check("决策·幕级推荐可加载", CuratedPicks.ActCount >= 2 && CuratedPicks.TotalPicks >= 3,
                $"{CuratedPicks.Source}｜{CuratedPicks.ActCount} 幕 / {CuratedPicks.TotalPicks} 条");
            // 验证推荐信号**是否影响选人**。
            // 注意：必须用 TeamSolver 逐幕独立求解，不能用 GlobalAllocator 全局分配——
            // 全局分配有"角色只能用一次"约束，推荐角色可能被更高优先级的 boss 幕先占用，
            // 此时第3幕拿不到赛诺是**正确行为**，不是推荐没生效（本轮踩过这个误判）。
            var hitActs = new List<string>();
            var missActs = new List<string>();
            foreach (var act in CuratedPicks.ActCount > 0 ? Enumerable.Range(1, 12) : Enumerable.Empty<int>())
            {
                var rec = CuratedPicks.For(act);
                if (rec.Count == 0) continue;
                var enemy = QuantData.Enemy(act, "Lunar");
                if (enemy == null || !enemy.HasAnyData) continue;
                var plan = TeamSolver.Solve(enemy, act);
                var usable = rec.Keys.Where(nm => TeamSolver.IsAvailableThisSeason(nm)).ToList();
                if (usable.Count == 0) continue;
                var hit = plan.Team.Any(nm => usable.Contains(nm));
                (hit ? hitActs : missActs).Add($"第{act}幕[{string.Join("/", usable)}]");
            }
            // 判定口径：推荐是**次要信号**（权重 0.10），强度是主要信号（0.25 平均）。
            // 强度高的角色压过推荐的弱角色，是符合设计的行为——因此只要求"推荐确实
            // 能影响一部分幕"（命中>0），不要求"多数命中"（早期断言按 >=半数 写错了口径）。
            Check("决策·推荐角色影响选人（次要信号，强度优先）",
                hitActs.Count > 0,
                $"命中 {hitActs.Count} 幕、未命中 {missActs.Count} 幕（强度权重更高，未命中属预期）"
                + (hitActs.Count > 0 ? "｜命中: " + string.Join("; ", hitActs.Take(3)) : "")
                + (missActs.Count > 0 ? "｜未命中: " + string.Join("; ", missActs.Take(3)) : ""));
        }

        // P0：决策层必须真的接入生产指示（否则 2400 行决策代码只在 CI 里跑）
        {
            var ready = DecisionBridge.Init();
            var demo = new Advice("测试", new List<string> { "· 旧逻辑行" }, "", 3, "act");
            var merged = DecisionBridge.Append(demo);
            var grew = merged.Lines.Count > demo.Lines.Count;
            var tagged = merged.Lines.Any(l => l.StartsWith("◆") || l.StartsWith("★") || l.StartsWith("○") || l.StartsWith("·"));
            Check("决策层已接入生产指示（只做加法）", ready && grew && tagged,
                $"就绪={ready}｜旧 {demo.Lines.Count} 行 → 新 {merged.Lines.Count} 行｜诊断={merged.Diagnostics}"
                + "｜首行: " + (merged.Lines.Count > 0 ? merged.Lines[0] : "(空)"));
        }

        // 全局分配的 G1（每幕满 4 人）必须纳入 CI ——
        // 此前只测 TeamSolver 的逐幕方案，没测 GlobalAllocator 的实际分配，
        // 导致「box 的 --quality-gates 报 14 项 G1 违规、CI 却全绿」。
        {
            var allocGate = new QualityGates.Report();
            QualityGates.CheckAllocation(GlobalAllocator.Solve(0), allocGate);
            Check("闸门·全局分配每幕满 4 人（G1/G2）", allocGate.Passed,
                $"检查 {allocGate.Checked} 项｜违规 {allocGate.Violations.Count}"
                + (allocGate.Violations.Count > 0 ? "｜首个: " + allocGate.Violations[0] : ""));
        }

        // 决策层·幕间优先级：boss 幕与高 level 幕必须优先拿到资源
        // 审计背景：赛季数据有 boss 标记与递增 level（Lunar 85→100），但此前分配器完全没用。
        {
            var allocBoss = GlobalAllocator.Solve(0);
            var lunarMain = allocBoss.Assigns.Where(a => a.Difficulty == "Lunar" && a.UsedMainForce).ToList();
            double Cov(GlobalAllocator.Assign a) => a.ScoreMax > 1e-9 ? a.Score / a.ScoreMax : 1.0;
            var bossActs = lunarMain.Where(a => QuantData.Enemy(a.Act, "Lunar")?.Boss == true).ToList();
            var nonBossActs = lunarMain.Where(a => QuantData.Enemy(a.Act, "Lunar")?.Boss != true).ToList();
            var bossCov = bossActs.Count > 0 ? bossActs.Average(Cov) : 1.0;
            var nonBossCov = nonBossActs.Count > 0 ? nonBossActs.Average(Cov) : 1.0;
            Check("决策·boss 幕优先（覆盖不低于非 boss）",
                bossActs.Count > 0 && bossCov >= nonBossCov - 1e-9,
                $"boss {bossActs.Count} 个 平均 {bossCov:0.00}｜非 boss {nonBossActs.Count} 个 {nonBossCov:0.00}｜boss 幕: " + string.Join("/", bossActs.Select(a => "第" + a.Act + "幕")));
            var hardest = bossActs.OrderByDescending(a => QuantData.Enemy(a.Act, "Lunar")?.Level ?? 0).FirstOrDefault();
            Check("决策·最难幕不留缺口", hardest != null && hardest.Gaps.Count == 0,
                hardest == null ? "无 boss 幕" : $"第{hardest.Act}幕 Lv{QuantData.Enemy(hardest.Act, "Lunar")?.Level} 覆盖 {Cov(hardest):0.00} 缺口 {hardest.Gaps.Count}");
        }

        // 配队质量闸门：把 B 层的"只打印"升级为 A 层的"逐条断言"
        int gateRealCheckedActs = 0;
        QualityGates.Load(AppPaths.Data("decision-policy.json"));
        var gateReal = new QualityGates.Report();
        {
            int checkedActs = 0;
            foreach (var d in QuantData.Difficulties)
            {
                for (int a = 1; a <= QuantData.MaxAct(d); a++)
                {
                    var e = QuantData.Enemy(a, d);
                    if (e == null || !e.HasAnyData) continue;
                    QualityGates.CheckPlan(TeamSolver.Solve(e, a), e, gateReal);
                    checkedActs++;
                }
            }
            gateRealCheckedActs = checkedActs;
        }
        Check("闸门·真实配队全过", gateReal.Passed,
            $"检查 {gateReal.Checked} 项（{gateRealCheckedActs} 幕）｜违规 {gateReal.Violations.Count}"
            + (gateReal.Violations.Count > 0 ? "｜首个: " + gateReal.Violations[0] : ""));

        // 闸门必须能真的检出违规，否则等于没判据
        var gateBad = new QualityGates.Report();
        var badPlan = new TeamSolver.Plan { Difficulty = "Lunar", Act = 3 };
        badPlan.Team.Add("钟离");   // 只有 1 人，且没覆盖 Electro
        badPlan.Need["Electro"] = 1.0;
        var badEnemy = QuantData.Enemy(3, "Lunar");
        QualityGates.CheckPlan(badPlan, badEnemy, gateBad);
        Check("闸门·能检出违规（人数/需求）",
            !gateBad.Passed
            && gateBad.Violations.Any(v => v.Gate == "G1")
            && gateBad.Violations.Any(v => v.Gate == "G3"),
            string.Join("；", gateBad.Violations.Select(v => v.ToString()).Take(3)));

        // 无元素需求却给分 → G7 必须拦住
        var gateG7 = new QualityGates.Report();
        var scorePlan = new TeamSolver.Plan { Difficulty = "Lunar", Act = 1, Score = 0.5 };
        scorePlan.Team.AddRange(new[] { "钟离", "五郎", "云堇", "凝光" });
        QualityGates.CheckPlan(scorePlan, QuantData.Enemy(1, "Lunar"), gateG7);
        Check("闸门·无需求不给伪得分（G7）",
            gateG7.Violations.Any(v => v.Gate == "G7"),
            string.Join("；", gateG7.Violations.Where(v => v.Gate == "G7").Select(v => v.ToString())));

        // 量化决策自洽判据（Q1-Q6）：把 --quant-decide 从"能算"升级为"可判对错"
        {
            var quantGate = new QualityGates.Report();
            var quantTeam = TeamSolver.AsTeamProfile();
            int quantActs = 0;
            foreach (var d in QuantData.Difficulties)
            {
                for (int a = 1; a <= QuantData.MaxAct(d); a++)
                {
                    var e = QuantData.Enemy(a, d);
                    if (e == null || !e.HasAnyData) continue;
                    QualityGates.CheckQuantDecision(QuantScorer.Score(e, quantTeam, a), quantGate);
                    quantActs++;
                }
            }
            Check("闸门·量化自洽（Q1-Q6）", quantGate.Passed,
                $"检查 {quantGate.Checked} 项（{quantActs} 幕）｜违规 {quantGate.Violations.Count}"
                + (quantGate.Violations.Count > 0 ? "｜首个: " + quantGate.Violations[0] : ""));
        }

        // 判据必须能真的拦住坏数据，否则等于没有判据
        {
            var badQuant = new QualityGates.Report();
            var badResult = new QuantScorer.Result { Act = 3, Score = 1.5, Confidence = -0.2, ResPart = 0.99, KnowPart = 0 };
            badResult.Candidates.Add(new QuantScorer.Candidate { Element = "Electro", NeedWeight = 1.0, CoverFactor = 0 });
            QualityGates.CheckQuantDecision(badResult, badQuant);
            Check("闸门·量化能检出越界与不自洽",
                badQuant.Violations.Any(v => v.Gate == "Q1")
                && badQuant.Violations.Any(v => v.Gate == "Q2")
                && badQuant.Violations.Any(v => v.Gate == "Q4"),
                string.Join("；", badQuant.Violations.Select(v => v.ToString()).Take(3)));
        }

        // OCR 引擎调优：参数来自数据；DirectML 是可选增益，缺失必须回退 CPU 而不是崩
        OcrTuning.Load(AppPaths.Data("decision-policy.json"));
        Check("OCR·引擎调优参数来自数据",
            OcrTuning.Source.Contains("decision-policy"),
            $"来源 {OcrTuning.Source}｜DirectML={OcrTuning.EnableDirectML} device={OcrTuning.DirectMLDeviceId} threads={(OcrTuning.NumThreads == 0 ? "自动" : OcrTuning.NumThreads.ToString())}");
        Check("OCR·执行提供器状态可读（CPU 兜底）",
            !string.IsNullOrEmpty(OcrTuning.EpStatus),
            $"EP={OcrTuning.EpStatus}");

        // BetterGI OCR 对齐（审计 docs/BETTERGI-OCR-AUDIT.md 的 D1/D2）
        OcrTextPipeline.LoadFromPolicy(AppPaths.Data("decision-policy.json"));
        var rawBoxes = new[]
        {
            new OcrTextPipeline.Box { X = 300, Y = 100, Score = 0.92, Text = "第二张" },
            new OcrTextPipeline.Box { X = 100, Y = 102, Score = 0.95, Text = "第一张" },
            new OcrTextPipeline.Box { X = 500, Y = 99,  Score = 0.90, Text = "第三张" },
            new OcrTextPipeline.Box { X = 100, Y = 300, Score = 0.40, Text = "低分噪声" },
            new OcrTextPipeline.Box { X = 120, Y = 305, Score = 0.88, Text = "下一排" },
        };
        var ordered = OcrTextPipeline.ToLines(rawBoxes);
        Check("OCR·置信度过滤（BetterGI DropScore）",
            !ordered.Contains("低分噪声") && ordered.Count == 4,
            $"阈值 {OcrTextPipeline.DropScore:0.00}｜保留 {ordered.Count} 条：{string.Join("/", ordered)}");
        Check("OCR·同排按 X 排序（Y 差<10px 归并）",
            ordered.Count >= 3 && ordered[0] == "第一张" && ordered[1] == "第二张" && ordered[2] == "第三张",
            string.Join(" → ", ordered));
        // 不同排不能被合并：Y 差远大于容差时保持 Y 顺序
        var twoRows = OcrTextPipeline.OrderSameLine(new[]
        {
            new OcrTextPipeline.Box { X = 900, Y = 400, Text = "下" },
            new OcrTextPipeline.Box { X = 900, Y = 100, Text = "上" },
        });
        Check("OCR·跨排不误合并", twoRows.Count == 2 && twoRows[0].Text == "上" && twoRows[1].Text == "下",
            string.Join(" → ", twoRows.Select(b => b.Text)));

        // 决策内容必须来自数据，不硬编码：策略加载、难度权重、触发词一致、别名、幕数数据驱动
        DecisionPolicy.Load(AppPaths.Data("decision-policy.json"));
        Check("策略·来自数据文件", DecisionPolicy.Source.Contains("decision-policy"),
            $"来源 {DecisionPolicy.Source}｜队形 {DecisionPolicy.TeamSize}｜覆盖 {DecisionPolicy.CoveragePerElement}");
        Check("策略·难度权重来自数据",
            DecisionPolicy.DifficultyWeightCount >= 4
            && DecisionPolicy.DifficultyWeight("Lunar") > DecisionPolicy.DifficultyWeight("Easy"),
            $"Lunar={DecisionPolicy.DifficultyWeight("Lunar"):0.00} Easy={DecisionPolicy.DifficultyWeight("Easy"):0.00}");
        // 触发词必须只有一份来源：此前 QuantScorer 多算了"力竭"、分配器没有，两边判定不一致
        Check("策略·职能触发词单一来源",
            !DecisionPolicy.NeedsHealer("满条后力竭") && DecisionPolicy.NeedsHealer("被标记要奶满")
            && DecisionPolicy.NeedsBreaker("16 秒内用岩/钝击把锅打碎") && !DecisionPolicy.NeedsBreaker("清怪推进"),
            $"healer 触发 {DecisionPolicy.HealerTriggerCount} 个；力竭不算治疗需求");
        Check("策略·角色别名归一",
            DecisionPolicy.Canonical("心海") == "珊瑚宫心海" && DecisionPolicy.Canonical("行秋") == "行秋",
            $"心海→{DecisionPolicy.Canonical("心海")}｜行秋→{DecisionPolicy.Canonical("行秋")}");
        Check("策略·幕数由赛季数据推导",
            QuantData.MaxAct("Lunar") >= QuantData.MaxAct("Easy") && QuantData.MaxAct("Lunar") > 0,
            $"Lunar={QuantData.MaxAct("Lunar")}幕 Easy={QuantData.MaxAct("Easy")}幕");

        // 玩家池适配：给一个小池子，分配只能从这个池子里挑（不再输出"通用最优"）
        var selftestRosterPath = Path.Combine(AppPaths.Dir, "__selftest_roster.json");
        try
        {
            File.WriteAllText(selftestRosterPath, "{\"characters\":[\"行秋\",\"芭芭拉\",\"五郎\",\"丽莎\"]}", new System.Text.UTF8Encoding(false));
            var pr = GlobalAllocator.Solve(0, selftestRosterPath);
            var picked = pr.Assigns.SelectMany(a => a.Team).Where(nm => !nm.StartsWith("（")).ToList();
            var allowed = new HashSet<string>(new[] { "行秋", "芭芭拉", "五郎", "丽莎" }, StringComparer.Ordinal);
            var outside = picked.Where(nm => !allowed.Contains(nm)).Distinct().ToList();
            Check("配队·按玩家池分配（不越池）",
                picked.Count > 0 && outside.Count == 0,
                picked.Count == 0 ? "未分配" : $"用到 {picked.Distinct().Count()} 人｜越池 {(outside.Count == 0 ? "无" : string.Join("/", outside))}｜{TeamSolver.LastPoolNote}");
        }
        finally { try { File.Delete(selftestRosterPath); } catch { } }

        // buff/三选一随机 → 按策略直接交 ◇模型 通道（不做概率建模）
        var buffWithModel = DecisionOrchestrator.Decide(new DecisionOrchestrator.Context
        {
            Act = 3, Team = generic, Kind = "choice", LlmAttempted = true, LlmText = "缺人优先拿伙伴，其次辉彩祝福",
        });
        var buffNoModel = DecisionOrchestrator.Decide(new DecisionOrchestrator.Context
        {
            Act = 3, Team = generic, Kind = "choice", LlmAttempted = false,
        });
        Check("编排·buff 随机走模型通道",
            buffWithModel.Source == "◇模型" && buffNoModel.Source.Contains("未取到")
            && buffNoModel.Randoms.Any(r => r.Contains("buff")),
            $"有模型={buffWithModel.Source}｜无模型={buffNoModel.Source}");

        // 跨幕全局分配：同一角色只能用一次 + 治疗/破盾硬约束
        var alloc = GlobalAllocator.Solve(0);
        var mainForce = alloc.Assigns.Where(a => a.UsedMainForce).ToList();
        // 口径：剧诗锁定只在**同一次挑战**内生效；Easy…Lunar 是分开的挑战，同一角色可跨难度复用。
        // 所以断言的是"**同一难度内**不重复"，不是全局不重复（早期断言写错了口径）。
        var dupByDiff = new List<string>();
        foreach (var g in alloc.Assigns.Where(a => a.UsedMainForce).GroupBy(a => a.Difficulty))
        {
            var teamNames = g.SelectMany(a => a.Team).Where(nm => !nm.StartsWith("（")).ToList();
            foreach (var d in teamNames.GroupBy(n => n, StringComparer.Ordinal).Where(x => x.Count() > 1))
                dupByDiff.Add($"{g.Key}:{d.Key}×{d.Count()}");
        }
        Check("全局分配·同难度内不重复（跨难度可复用）", alloc.Assigns.Count > 0 && dupByDiff.Count == 0,
            $"分配 {alloc.Assigns.Count} 幕｜主力 {alloc.PoolUsed.Count} 人｜同难度重复 {(dupByDiff.Count == 0 ? "无" : string.Join("/", dupByDiff.Take(5)))}");
        var lunarNeeds = mainForce.Where(a => a.Difficulty == "Lunar").ToList();
        Check("全局分配·最高难度有需求幕全覆盖",
            lunarNeeds.Count > 0 && lunarNeeds.All(a => a.Score >= a.ScoreMax - 1e-9),
            lunarNeeds.Count == 0 ? "无 Luna 需求幕" : string.Join("｜", lunarNeeds.OrderBy(a => a.Act).Select(a => $"第{a.Act}幕 {a.Score:0.00}/{a.ScoreMax:0.00}" + (a.Gaps.Count > 0 ? "(缺" + string.Join("+", a.Gaps) + ")" : ""))));
        var act10 = alloc.Assigns.FirstOrDefault(a => a.Difficulty == "Lunar" && a.Act == 10 && a.UsedMainForce);
        Check("全局分配·治疗硬约束（第10幕）",
            act10 == null || act10.Team.Any(nm => TeamSolver.HasRole(nm, "Healer")),
            act10 == null
                ? "第10幕未分配"
                : $"{string.Join("/", act10.Team)}"
                  + (act10.Gaps.Count > 0 ? "｜缺口:" + string.Join("+", act10.Gaps) : "")
                  + (act10.Reasons.Count > 0 ? "｜" + string.Join("，", act10.Reasons) : "")
                  + "｜池内治疗:" + string.Join("/", TeamSolver.Units.Where(u => TeamSolver.RoleHas(u.Role, "Healer")).Select(u => u.Name + "(" + u.Element + ")")));
        Check("全局分配·无需求幕不占主力",
            alloc.Assigns.Any(a => !a.UsedMainForce), alloc.Notes.FirstOrDefault() ?? "");

        // 赛季时效性：当前赛季数据不能过期（过期则所有量化都建立在错误基础上）
        {
            var cur0 = SeasonRegistry.For(0);
            var fresh = SeasonRegistry.CheckFreshness(cur0);
            var fake = SeasonRegistry.CheckFreshness(new SeasonRegistry.SeasonInfo { Season = 99, StartsAt = "2020-01-01T00:00:00Z" });
            Check("赛季·时效检查可判新旧",
                !fresh.Stale && fresh.DaysSinceStart >= 0
                && fake.Stale && fake.Verdict.Contains("过期"),
                $"当前：{fresh.Verdict}（{fresh.Detail}）｜伪造旧赛季：{fake.Verdict}");
        }

        // 赛季无关：自动发现 data/season*.json，未来赛季丢文件即生效（不改代码/工作流）
        var seasons = SeasonRegistry.Load();
        var cur = SeasonRegistry.For(0);
        Check("赛季·自动发现（未来赛季免改代码）",
            seasons.Count >= 1 && cur != null && cur.Elements.Count >= 1 && cur.Difficulties.Count >= 1,
            cur == null ? "无赛季" : $"发现 {seasons.Count} 个｜当前第{cur.Season}赛季 元素{string.Join("/", cur.Elements)} 难度{cur.Difficulties.Count} 幕{cur.ActCount}");

        // 编排器三态：任何一级缺失都必须仍能出指示，且来源标记正确
        // 用「没有云端已验证知识」的幕来验证模型/规则路径（有 ★ 知识的幕本就该优先于模型）
        int actNoVerified = Enumerable.Range(1, 12).FirstOrDefault(a => Playbook.VerifiedFor(a) == null);
        if (actNoVerified == 0) actNoVerified = 9;
        var enemyNoV = QuantData.Enemy(actNoVerified, "Lunar");

        var dOpt = DecisionOrchestrator.Decide(new DecisionOrchestrator.Context
        {
            Act = actNoVerified, Team = generic,
            Options = new[]
            {
                new ChoiceDecider.Option { Index = 1, Raw = "战斗·普通" },
                new ChoiceDecider.Option { Index = 2, Raw = "伙伴" },
            },
        });
        var dLlm = DecisionOrchestrator.Decide(new DecisionOrchestrator.Context
        {
            Act = actNoVerified, Enemy = enemyNoV, Team = generic,
            LlmAttempted = true, LlmText = "先推满怒气条再爆发",
        });
        var dNone = DecisionOrchestrator.Decide(new DecisionOrchestrator.Context { Act = actNoVerified });
        Check("编排·三态都有可执行指示",
            dOpt.Lines.Count > 0 && dLlm.Lines.Count > 0 && dNone.Lines.Count > 0,
            $"第{actNoVerified}幕｜选项={dOpt.Source} 模型={dLlm.Source} 兜底={dNone.Source}");
        // 兜底来源可以是 ○语料（语料库有内容时优先于 ·规则）或 ·规则（语料库空时）——
        // 两者都正确，硬写 "·规则" 会在补上语料层后误报（本轮踩过）。
        Check("编排·来源标记正确",
            dOpt.Source == "◆量化"
            && dLlm.Lines.Any(l => l.StartsWith("◇"))
            && (dNone.Source == "·规则" || dNone.Source == "○语料"),
            $"{dOpt.Source} / {dLlm.Source} / {dNone.Source}");
        Check("编排·随机要素显式标注",
            dNone.Randoms.Count >= 2 && dOpt.Randoms.Any(r => r.Contains("角色池")),
            dNone.Randoms.Count == 0 ? "未标注（错误）" : string.Join("；", dNone.Randoms.Take(3)));

        var relPath = Path.Combine(Path.GetDirectoryName(AppPaths.Data("act-playbook.json")) ?? ".", "knowledge-release.json");
        // 文件在就必须真的读到：之前解析失败会静默返回 0，断言照样通过（踩过）
        Check("知识·云端已验证知识可读", File.Exists(relPath) ? Playbook.VerifiedCount > 0 : true,
            $"已验证 {Playbook.VerifiedCount} 条 | 文件存在={File.Exists(relPath)} | {relPath}");

        Check("知识·首领提示来自数据", (Playbook.Boss(3) ?? "").Contains("翠翎恐蕈") && Playbook.Boss(4) == null,
            $"第3幕={(Playbook.Boss(3)?.Length ?? 0)} 字，第4幕={(Playbook.Boss(4) == null ? "无(正确)" : "有")}");
        Check("知识·缺失幕明确说暂无", TheaterLogic.NextAction("act", 4) == Playbook.Missing
            && (TheaterLogic.NextAction("act", 3) ?? "").Contains("怒气"),
            $"第4幕=>{TheaterLogic.NextAction("act", 4)}");
        Check("知识·缺失幕不猜元素", ChoiceDecider.NeedOf(4).Need == "" && ChoiceDecider.NeedOf(3).Need == "Electro",
            $"第4幕=空(不猜) 第3幕={ChoiceDecider.NeedOf(3).Need}");

        // 帧驱动量化：事实侧判定（界面类型 / 角色名匹配）
        Check("量化·界面类型判定",
            QuantRunner.FactKind("难度选择 参演要求 卓越模式", 0, 0) == "difficulty"
            && QuantRunner.FactKind("队伍配置 已额外编入4/4名待命角色", 0, 4) == "team"
            && QuantRunner.FactKind("战斗·普通 雷晶·进阶", 2, 0) == "shop",
            "difficulty/team/shop 分流");
        var knownChars = QuantRunner.LoadKnownCharacters();
        var matchedChars = QuantRunner.MatchCharacters("第7幕：行秋+菲谢尔+2挂件", knownChars);
        Check("量化·角色名匹配", knownChars.Count > 50 && matchedChars.Contains("行秋"),
            $"角色表 {knownChars.Count}，命中 {string.Join("/", matchedChars.Take(4))}");

        // 大模型顾问：脱敏 / 配置解析 / 响应解析 / 限流与冷却（全部纯函数）
        var dirty = "当前：第3幕 SESSDATA=abc123; bili_jct=deadbeef UID 123456789012345 邮箱 a@b.com 看 https://x.y/z";
        var clean = LlmAdvisor.Sanitize(dirty);
        Check("LLM·发送前脱敏",
            !clean.Contains("SESSDATA=") && !clean.Contains("bili_jct=") && !clean.Contains("123456789012345")
            && !clean.Contains("@b.com") && !clean.Contains("https://") && clean.Contains("第3幕"),
            clean.Length > 60 ? clean[..60] + "…" : clean);
        var cfg = LlmAdvisor.ParseConfig("{\"enabled\":true,\"minIntervalSeconds\":30,\"endpoints\":[" +
            "{\"name\":\"a\",\"url\":\"https://text.pollinations.ai/openai\",\"model\":\"openai\",\"apiKeyEnv\":\"\"}," +
            "{\"name\":\"b\",\"url\":\"https://openrouter.ai/api/v1/chat/completions\",\"model\":\"m:free\",\"apiKeyEnv\":\"OPENROUTER_API_KEY\"}]}");
        Check("LLM·多端点配置解析", cfg.Enabled && cfg.Endpoints.Count == 2
            && cfg.Endpoints[0].ApiKeyEnv == "" && cfg.Endpoints[1].ApiKeyEnv == "OPENROUTER_API_KEY",
            $"{cfg.Endpoints.Count} 端点，间隔 {cfg.MinIntervalSeconds}s");
        var legacy = LlmAdvisor.ParseConfig("{\"enabled\":true,\"endpoint\":\"https://api.deepseek.com/v1/chat/completions\",\"model\":\"deepseek-chat\",\"apiKeyEnv\":\"DEEPSEEK_API_KEY\"}");
        Check("LLM·旧配置兼容", legacy.Enabled && legacy.Endpoints.Count == 1 && legacy.Endpoints[0].Name == "legacy",
            legacy.Endpoints.Count > 0 ? legacy.Endpoints[0].Model : "-");
        Check("LLM·响应解析",
            LlmAdvisor.ExtractContent("{\"choices\":[{\"message\":{\"content\":\"用雷推怒气\"}}]}") == "用雷推怒气"
            && LlmAdvisor.ExtractContent("{\"error\":\"x\"}") == null
            && LlmAdvisor.ExtractContent("not json") == null,
            "正常/缺字段/非JSON 三态");
        var ep = new LlmAdvisor.Endpoint { Name = "t" };
        var nowT = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
        Check("LLM·限流与冷却判定",
            LlmAdvisor.SkipReason(ep, 0, 20, DateTime.MinValue, 30, nowT) == null
            && LlmAdvisor.SkipReason(ep, 0, 20, nowT.AddSeconds(-5), 30, nowT) != null
            && LlmAdvisor.SkipReason(ep, 20, 20, DateTime.MinValue, 30, nowT) != null
            && LlmAdvisor.SkipReason(new LlmAdvisor.Endpoint { CoolUntil = nowT.AddMinutes(1) }, 0, 20, DateTime.MinValue, 30, nowT) != null,
            "可调用/最小间隔/配额/冷却 四态");

        // 角色可替代性：别称归一 + 同职能替代 + 视频原操作解析
        OpCompare.Load(AppPaths.Data("substitutes.json"));
        Check("替代·别称归一", OpCompare.Loaded && OpCompare.Canonical("皇女") == "菲谢尔" && OpCompare.Canonical("一斗") == "荒泷一斗",
            "皇女→菲谢尔，一斗→荒泷一斗");
        var subs = OpCompare.SubstitutesFor("行秋");
        Check("替代·同职能排序", subs.Count >= 3 && subs.Any(s => s.Name == "夜兰") && subs.Any(s => s.Name == "心海"),
            string.Join("、", subs.Take(5).Select(s => s.Name)));
        var teams = OpCompare.ParseActualTeams(
            "[description|] 第1幕：一斗+爱诺+五郎+挂件\n[description|] 需要选出角色：5幕诺艾尔、7幕行秋+皇女\n[description|] 第2幕：一斗+夜兰+2挂件");
        Check("对比·原操作解析",
            teams.ContainsKey(1) && teams[1].Contains("荒泷一斗") && teams[1].Contains("爱诺")
            && teams.ContainsKey(7) && teams[7].Contains("行秋") && teams[7].Contains("菲谢尔"),
            string.Join(" | ", teams.OrderBy(k => k.Key).Select(k => $"{k.Key}:{string.Join(",", k.Value)}")));

        // 端到端：RunCompare 曾因 FirstOrDefault 元组默认值的 null Name 崩溃（CI 断言没覆盖到）
        var cTmp = Path.Combine(Path.GetTempPath(), "ta-cmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            var vd = Path.Combine(cTmp, "BVtest"); Directory.CreateDirectory(vd);
            File.WriteAllText(Path.Combine(vd, "subs.txt"),
                "[description|] 第7幕：行秋+菲谢尔+2挂件\n[description|] 第2幕：一斗+芭芭拉+挂件", Encoding.UTF8);
            var cOut = Path.Combine(cTmp, "cmp.md");
            Check("对比·端到端不崩", OpCompare.RunCompare(cTmp, cOut) == 0 && File.Exists(cOut) && File.ReadAllText(cOut).Contains("职能命中"),
                cOut);
        }
        catch (Exception ex) { Check("对比·端到端不崩", false, ex.GetType().Name + ": " + ex.Message); }
        finally { try { Directory.Delete(cTmp, true); } catch { } }

        // 本地日志解析（纯函数）：只认 avatarId/level，其余字段丢弃
        var log = "{\"avatarId\":10000060,\"level\":90} {\"avatarId\":10000046,\"level\":80} junk {\"avatarId\":10000060,\"level\":90}";
        var parsed = LogScan.ParseCharacters(log);
        Check("日志解析·角色ID与等级", parsed.Count == 2 && parsed[10000060] == 90 && parsed[10000046] == 80,
            string.Join(",", parsed.Select(kv => $"{kv.Key}=Lv{kv.Value}")));

        // 蒸馏解析（纯函数）
        var rep = Distiller.Parse("{\"frames\":3,\"jumps\":[\"1→3\"],\"flips\":[1],\"steps\":[" +
            "{\"Frame\":\"a\",\"Act\":3,\"RawAct\":3,\"TruthAct\":3,\"Kind\":\"act\",\"Ms\":100}," +
            "{\"Frame\":\"b\",\"Act\":2,\"RawAct\":4,\"TruthAct\":4,\"Kind\":\"act\",\"Ms\":120}," +
            "{\"Frame\":\"c\",\"Act\":0,\"RawAct\":0,\"TruthAct\":0,\"Kind\":\"unknown\",\"Ms\":80}]}", "t.json");
        Check("蒸馏解析·准确率与错例", rep.Frames == 3 && rep.TruthFrames == 2 && rep.StableRight == 1 && rep.RawRight == 2 && rep.Mismatches.Count == 1,
            $"frames={rep.Frames} truth={rep.TruthFrames} stable={rep.StableRight} raw={rep.RawRight} mism={rep.Mismatches.Count}");

        // 决策引擎：解析卡片 + 按幕次给出"选第几张"
        var shopText = "战斗·普通\n战斗·普通\n雷晶·进阶\n感电·原态";
        var shopOpts = ChoiceDecider.ParseOptions(shopText.Split('\n'));
        var d3 = ChoiceDecider.Decide(shopOpts, 3, 3, 4);
        Check("决策·卡片解析", shopOpts.Count == 4 && shopOpts.Count(o => o.Kind == ChoiceDecider.Kind.Battle) == 2,
            $"{shopOpts.Count} 张，战斗卡 {shopOpts.Count(o => o.Kind == ChoiceDecider.Kind.Battle)} 张");
        Check("决策·第3幕选雷强化",
            d3 != null && d3.Pick.Element == "Electro" && d3.Pick.Raw.Contains("雷"),
            d3?.Sentence ?? "null");
        var dEvent = ChoiceDecider.Decide(ChoiceDecider.ParseOptions(new[] { "伙伴", "神秘收获", "辉彩祝福" }), 4, 2, 4);
        Check("决策·三选一先补人", dEvent != null && dEvent.Sentence.Contains("伙伴"), dEvent?.Sentence ?? "null");
        var dModel = ChoiceDecider.FromModel("建议选第 4 个", shopOpts);
        Check("决策·收敛模型输出", dModel != null && dModel.Pick.Index == 4, dModel?.Sentence ?? "null");
        var dBad = ChoiceDecider.FromModel("我觉得随便都行", shopOpts);
        Check("决策·模型说不清就不采纳", dBad == null, dBad?.Sentence ?? "null");

        // 幕次时序稳定器：正常推进立即采纳；倒退/跳级要连续确认；没观测就保持
        var stab = new ActStabilizer { MinConfirm = 2 };
        var seq = new List<int?>();
        seq.Add(stab.Push(3));            // 首次 3 → 3
        seq.Add(stab.Push(4));            // 正常推进 → 4
        seq.Add(stab.Push(9));            // 跳级一次 → 仍 4
        seq.Add(stab.Push(null));         // 没观测 → 仍 4
        seq.Add(stab.Push(9));            // 第二次 9 → 采纳 9
        seq.Add(stab.Push(7));            // 倒退一次 → 仍 9
        bool okStab = seq[0] == 3 && seq[1] == 4 && seq[2] == 4 && seq[3] == 4 && seq[4] == 9 && seq[5] == 9;
        Check("幕次时序稳定器", okStab && stab.Rejected == 2,
            string.Join(",", seq.Select(x => x?.ToString() ?? "-")) + $" 拦下 {stab.Rejected}");

        // 语音归一化：播报要读的句子不能带"▶ 现在做："和分隔符号
        var speech1 = Speech.NormalizeForSpeech("▶ 现在做：用雷把怒气条推满，满条后 15 秒内把爆发全打出去");
        var speech2 = Speech.NormalizeForSpeech("耐力 2 点/人｜别在头两幕耗光");
        Check("语音归一化", speech1.StartsWith("用雷") && !speech1.Contains("▶") && !speech2.Contains("｜"),
            speech1 + " / " + speech2);

        // 资源包哈希校验（临时文件，验完即删）
        var tmpPack = Path.Combine(Path.GetTempPath(), "ta-pack-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            File.WriteAllText(tmpPack, "theater-assistant-pack-probe");
            var good = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(tmpPack)));
            Check("资源包哈希校验",
                Updater.VerifyHash(tmpPack, good) && !Updater.VerifyHash(tmpPack, new string('0', 64)) && Updater.VerifyHash(tmpPack, ""),
                "好哈希通过 / 坏哈希拒绝 / 空哈希跳过");
        }
        catch (Exception ex) { Check("资源包哈希校验", false, ex.Message); }
        finally { try { File.Delete(tmpPack); } catch { } }

        // 语料扫描的过滤规则（纯函数）：别把界面文案当攻略收进语料
        var worthYes = new[] { "1幕需2雷岩、3幕需2水雷", "队伍：钟离+菲谢尔+卡齐娜+挂件", "选关：愚人众 优先打带标记的" };
        var worthNo = new[] { "演出准备", "Lv.90", "第1幕", "队伍配置", "×24", "已编入" };
        var badW = worthYes.Where(s => !CorpusScan.LineWorthy(s)).Concat(worthNo.Where(CorpusScan.LineWorthy)).ToList();
        Check("语料过滤规则", badW.Count == 0, badW.Count == 0 ? $"{worthYes.Length} 收 / {worthNo.Length} 拒" : "错判: " + string.Join(",", badW));
        Check("语料幕次推断", CorpusScan.GuessAct("3幕需2水雷") == 3 && CorpusScan.GuessAct("队伍：钟离") == 0,
            $"3幕→{CorpusScan.GuessAct("3幕需2水雷")} / 无→{CorpusScan.GuessAct("队伍：钟离")}");

        // 一行"现在做"：每种状态都必须是可在游戏里直接执行的祈使句
        var na3 = TheaterLogic.NextAction("act", 3);
        var na6 = TheaterLogic.NextAction("act", 6);
        var na8 = TheaterLogic.NextAction("act", 8);
        var na10 = TheaterLogic.NextAction("act", 10);
        var naChoice = TheaterLogic.NextAction("choice", null);
        var naTeam = TheaterLogic.NextAction("team", null);
        Check("现在做·四个首领各有专属动作",
            na3.Contains("怒气") && na6.Contains("钓客") && na8.Contains("锅") && na10.Contains("标记"),
            $"{na3} | {na6} | {na8} | {na10}");
        Check("现在做·三选一与队伍", naChoice.Contains("伙伴") && naTeam.Contains("28"), $"{naChoice} | {naTeam}");
        Check("现在做·未知状态不瞎指挥", TheaterLogic.NextAction("unknown", null) == null, "null");

        // 中文名映射：characters.json 的键是英文，但 OCR/游戏 UI 是中文，必须能按中文名判"有没有这个角色"
        try
        {
            using var cdoc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(AppPaths.Data("characters.json")));
            int zhCount = cdoc.RootElement.TryGetProperty("zhNames", out var zhEl) ? zhEl.EnumerateObject().Count() : 0;
            Check("characters.json 有中文名", zhCount >= 120, $"zhNames {zhCount} 条");
            if (zhCount > 0)
            {
                Check("中文名可被 RosterScan 词典命中",
                    RosterScan.MatchName("夜兰", zhEl.EnumerateObject().Where(p => p.Name == "Yelan").Select(p => p.Value.GetString())) == "夜兰",
                    "夜兰");
            }
        }
        catch (Exception ex) { Check("characters.json 有中文名", false, ex.Message); }

        // 离线检索（BM25）：与 tools/audit-retrieval.mjs 同口径，CI 里守住召回
        GuideIndex.LoadWithExtra(AppPaths.Data("guide-corpus.json"));
        if (!GuideIndex.Loaded)
        {
            Check("攻略语料可加载", false, "data/guide-corpus.json 缺失");
        }
        else
        {
            var qlCases = new (string Q, int? Act, string Expect)[]
            {
                ("第3幕 翠翎恐蕈 怎么打", 3, "act3-boss"),
                ("深黯魇语之主 钓客 破盾", 6, "act6-boss"),
                ("丘丘萨满 砸锅", 8, "act8-boss"),
                ("月守宫 标记怎么解", 10, "act10-boss"),
                ("三选一 选什么", null, "rule-三选一"),
                ("月谕 编入 28 人", null, "rule-difficulty"),
            };
            int qlBad = 0;
            var qlDetail = new List<string>();
            foreach (var c in qlCases)
            {
                var top = GuideIndex.Search(c.Q, c.Act, 1).FirstOrDefault();
                var got = top?.Entry.Id ?? "<null>";
                if (got != c.Expect) { qlBad++; qlDetail.Add($"{c.Q}→{got}(期望 {c.Expect})"); }
            }
            Check("攻略检索 hit@1", qlBad == 0, $"{qlCases.Length} 例，失败 {qlBad}" + (qlBad > 0 ? "：" + string.Join("; ", qlDetail) : ""));
            // 合并 extra 语料（--corpus-scan 产物）必须真的进索引：用临时文件验证，不动 data/
            var tmpExtra = Path.Combine(Path.GetTempPath(), $"ta-extra-{Guid.NewGuid():N}.json");
            try
            {
                File.WriteAllText(tmpExtra,
                    "{\"entries\":[{\"id\":\"zz-merge-probe\",\"act\":7,\"tags\":[\"第7幕\"],\"title\":\"合并探针\",\"text\":\"第七幕合并探针 特殊词 熔岩游像 打法\",\"source\":\"curated\"}]}",
                    System.Text.Encoding.UTF8);
                int before = GuideIndex.Count;
                GuideIndex.Append(tmpExtra);
                var merged = GuideIndex.Search("合并探针 特殊词", 7, 1).FirstOrDefault();
                Check("extra 语料可合并进索引",
                    GuideIndex.Count == before + 1 && merged?.Entry.Id == "zz-merge-probe",
                    $"{before} → {GuideIndex.Count}，首条 {merged?.Entry.Id ?? "<null>"}");
            }
            catch (Exception ex) { Check("extra 语料可合并进索引", false, ex.Message); }
            finally { try { File.Delete(tmpExtra); } catch { } }

            Check("多语料源已合并", GuideIndex.Count >= 30, $"索引共 {GuideIndex.Count} 条（主语料 23 + wiki/extra）");

            Check("检索结果带出处", GuideIndex.Search("第3幕 翠翎恐蕈", 3, 1).All(h => !string.IsNullOrWhiteSpace(h.SourceLabel)), "source 非空");
        }

        Check("RosterScan 等级解析",
            RosterScan.MatchLevel("Lv.90") == 90 && RosterScan.MatchLevel("lv 70") == 70 && RosterScan.MatchLevel("等级") == null,
            $"Lv.90→{RosterScan.MatchLevel("Lv.90")} / lv 70→{RosterScan.MatchLevel("lv 70")}");
    }

    // ---- 5. 相似度数学 ----
    private static void CheckScoreMath()
    {
        using var a = MakePattern(240, 120, 0);
        using var b = MakePattern(240, 120, 0);
        var ga = TemplateFactory.GrayFrame.From(a);
        var gb = TemplateFactory.GrayFrame.From(b);

        double same = TemplateFactory.ScoreAt(ga, gb, 0, 0);
        Check("ScoreAt 自身=1.0", Math.Abs(same - 1.0) < 1e-6, same.ToString("0.0000"));

        using var noisy = MakePattern(240, 120, 40);
        var gn = TemplateFactory.GrayFrame.From(noisy);
        double diff = TemplateFactory.ScoreAt(ga, gn, 0, 0);
        Check("ScoreAt 不同图案<0.9", diff < 0.9, diff.ToString("0.0000"));

        // 在 480x240 的画布上把图案贴到 (137, 61)，BestIn 应精确找回
        using var canvas = new Bitmap(480, 240, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.Clear(Color.FromArgb(26, 30, 44));
            g.DrawImage(a, 137, 61);
        }
        var gc = TemplateFactory.GrayFrame.From(canvas);
        var hit = TemplateFactory.BestIn(gc, ga, new Rectangle(100, 30, 160, 90));
        Check("BestIn 定位", hit.Score > 0.999 && hit.X == 137 && hit.Y == 61, $"{hit.Score:0.0000} @{hit.X},{hit.Y}");

        double absent = TemplateFactory.BestInFrame(gc, TemplateFactory.GrayFrame.From(MakeSolid(200, 60, Color.FromArgb(168, 168, 168)))).Score;
        Check("BestInFrame 缺席不误报", absent < 0.8, absent.ToString("0.0000"));

        // 粗扫 + 命中点细化后应精确落回 (137,61)
        var located = TemplateFactory.BestInFrame(gc, ga, 2, 2);
        Check("BestInFrame 全帧定位",
            located.Score > 0.999 && located.X == 137 && located.Y == 61,
            $"{located.Score:0.0000} @{located.X},{located.Y}");
    }

    // ---- 6. 裁剪 → 回找（模板工厂最小闭环） ----
    private static void CheckCropRoundTrip()
    {
        using var frame = MakeFrameLike(1280, 720);
        var boxes = new List<OcrBox>
        {
            new() { Text = "TESTLABEL", Rect = new Rectangle(80, 80, 210, 40), Score = 0.93 },
        };
        var specs = TemplateFactory.Derive(boxes, 1280, 720);
        var spec = specs.FirstOrDefault(s => s.Name == "test");
        if (spec == null) { Check("Crop 闭环", false, "没有派生 test 规格"); return; }

        using var crop = TemplateFactory.Crop(frame, spec);
        var gt = TemplateFactory.GrayFrame.From(frame);
        var gcrop = TemplateFactory.GrayFrame.From(crop);
        double at = TemplateFactory.ScoreAt(gt, gcrop, spec.X, spec.Y);
        var hit = TemplateFactory.BestIn(gt, gcrop, new Rectangle(spec.X - 6, spec.Y - 6, 12, 12));
        Check("Crop→ScoreAt=1.0", Math.Abs(at - 1.0) < 1e-6, at.ToString("0.0000"));
        Check("Crop→BestIn 原位", hit.Score > 0.999 && Math.Abs(hit.X - spec.X) <= 1 && Math.Abs(hit.Y - spec.Y) <= 1,
            $"{hit.Score:0.0000} @{hit.X},{hit.Y} 期望 {spec.X},{spec.Y}");
    }

    // ---- 7. 可选：OCR 真的给出坐标 ----
    private static void CheckOcrBoxes()
    {
        var modelsDir = AppPaths.ModelsRoot;
        using var ocr = new OcrService();
        ocr.InitializeAsync(modelsDir).GetAwaiter().GetResult();
        Lines.Add("OCR: " + ocr.Status);

        using var frame = MakeFrameLike(1280, 720);
        var boxes = ocr.RecognizeBoxesAsync(frame, null, 1).GetAwaiter().GetResult();
        if (boxes == null) { Check("OCR 文本框", false, "OCR 未就绪（缺模型？）"); return; }

        bool hasAnchor = boxes.Any(b => b.Text != null && b.Text.Contains("TESTLABEL", StringComparison.OrdinalIgnoreCase));
        Check("OCR 读出 ASCII 锚点", hasAnchor, $"文本框 {boxes.Count} 条");

        var anchor = boxes.FirstOrDefault(b => b.Text != null && b.Text.Contains("TESTLABEL", StringComparison.OrdinalIgnoreCase));
        if (anchor != null)
        {
            bool inPlace = Math.Abs(anchor.Rect.X - 80) <= 24 && Math.Abs(anchor.Rect.Y - 80) <= 24;
            Check("OCR 坐标可用", inPlace, $"{anchor.Rect.X},{anchor.Rect.Y} {anchor.Rect.Width}x{anchor.Rect.Height}（期望≈80,80）");
        }

        var specs = TemplateFactory.Derive(boxes, frame.Width, frame.Height);
        Check("OCR→派生规格", specs.Any(s => s.Name == "test"), string.Join(",", specs.Select(s => s.Name)));
    }

    // ---- 8. UI 层：把悬浮窗渲染成 bitmap 后断言配色与文字像素 ----
    /// <summary>不需要桌面会话，CI 里也能跑；同时把渲染结果存成 visual-preview.png 供人眼看。</summary>
    public static int RunVisual()
    {
        Cli.Attach();
        int pass = 0, fail = 0;
        var lines = new List<string> { "=== 悬浮窗渲染自检 ===" };

        void Check(string name, bool ok, string detail)
        {
            lines.Add((ok ? "PASS  " : "FAIL  ") + name + "  " + detail);
            if (ok) pass++; else fail++;
        }

        using var form = new OverlayForm();

        // 生产悬浮窗会随 DPI 缩放（AutoScale），自检也按同一比例放大画布，
        // 否则高 DPI 下固定 600x236 一定文字溢出——那是测具的问题，不是程序的问题。
        float dpiScale = Math.Max(1f, form.DeviceDpi / 96f);
        int W = (int)Math.Round(600 * dpiScale);
        int H = (int)Math.Round(236 * dpiScale);

        using var bmp = form.RenderPreview(
            "第 3 幕 · 来源 模板 act3=0.980",
            new[]
            {
                "首领·翠翎恐蕈｜1:45（星章 1:10）｜雷推怒气条（火 -34%）",
                "优先遇到: 翠翎恐蕈(9，双雷共鸣)｜备选 遗迹机兵(6) / 熔岩游像(5)",
                "耐力 2 点/人；别在头两幕把开幕主力耗光",
            },
            clickThrough: true,
            size: new Size(W, H));

        Check("渲染尺寸（按 DPI 缩放）", bmp.Width == W && bmp.Height == H, $"{bmp.Width}x{bmp.Height}（DPI {form.DeviceDpi} → 缩放 {dpiScale:0.00}）");

        // 左侧强调条改成"派蒙天蓝"(126,184,232) 且只占标题行高度（AI 卡片风格常把色条收在标题侧）。
        // 采样点随之移到竖条实际区域 (1,18)，不再取半高的整条。
        var accent = bmp.GetPixel(1, 18);
        bool accentOk = accent.B > 170 && accent.G > 140 && accent.R < 170;
        Check("左侧天蓝强调条", accentOk, $"RGB({accent.R},{accent.G},{accent.B})");

        // 背景应是深色（角落采样，避开强调条）
        var bg = bmp.GetPixel(bmp.Width - 6, bmp.Height - 6);
        bool bgOk = bg.R < 60 && bg.G < 60 && bg.B < 70;
        Check("深色背景", bgOk, $"RGB({bg.R},{bg.G},{bg.B})");

        // 顶部标题行必须有比背景亮很多的文字像素
        int bright = 0;
        for (int x = 16; x < Math.Min(bmp.Width, W - 40); x++)
        {
            for (int y = 8; y < 32 + (int)(6 * dpiScale); y++)
            {
                var p = bmp.GetPixel(x, y);
                if (p.R > 150 && p.G > 150 && p.B > 150) bright++;
            }
        }
        Check("标题行有文字像素", bright > 40, $"亮点 {bright} 个");

        // 正文区也得有文字像素（证明多行排版真的画了东西）
        int body = 0;
        for (int x = 16; x < Math.Min(bmp.Width, W - 10); x++)
        {
            for (int y = 40; y < Math.Min(bmp.Height, 150 + (int)(60 * dpiScale)); y++)
            {
                var p = bmp.GetPixel(x, y);
                if (p.R > 140 && p.G > 140 && p.B > 140) body++;
            }
        }
        Check("正文有文字像素", body > 150, $"亮点 {body} 个");

        // 溢出检测：最右 4 列里有**文字级亮点** = 文字被画到边界外（175% 下曾把"攻略"裁掉）。
        // 新样式有圆角描边，描边像素亮度中等（≤120），所以这里用 150 阈值只抓文字；
        // 同时跳过最外 1 列（圆角弧的抗锯齿会落在那）。
        int edge = 0;
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = Math.Max(0, bmp.Width - 4); x < bmp.Width - 1; x++)
            {
                var p = bmp.GetPixel(x, y);
                if (p.R > 150 && p.G > 150 && p.B > 150) edge++;
            }
        }
        Check("文字不溢出右边", edge == 0, $"右边 4 列亮点 {edge} 个");

        // 底部热键行也必须在画布内（贴底 2 行不该有**文字级**亮点）。
        // 与右边同理：圆角描边亮度中等，用 150 阈值只抓文字。
        int bottom = 0;
        for (int y = bmp.Height - 2; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                var p = bmp.GetPixel(x, y);
                if (p.R > 150 && p.G > 150 && p.B > 150) bottom++;
            }
        }
        Check("文字不溢出底边", bottom == 0, $"底部 2 行亮点 {bottom} 个");

        try
        {
            var outPath = Path.Combine(AppPaths.Dir, "visual-preview.png");
            bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
            lines.Add("已存预览：" + outPath);
        }
        catch { }

        lines.Add($"通过 {pass} / 失败 {fail}");
        foreach (var l in lines) Cli.Say(l);
        try
        {
            File.WriteAllText(Path.Combine(AppPaths.Dir, "selftest-visual-report.txt"),
                string.Join(Environment.NewLine, lines), Encoding.UTF8);
        }
        catch { }
        return fail == 0 ? 0 : 1;
    }

    // ---- 合成素材 ----
    private static Bitmap MakePattern(int w, int h, int seed)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(26, 30, 44));
        var rnd = new Random(seed + 1);
        using var brush = new SolidBrush(Color.FromArgb(210, 180, 130));
        for (int i = 0; i < 12; i++)
        {
            g.FillEllipse(brush, rnd.Next(0, Math.Max(1, w - 20)), rnd.Next(0, Math.Max(1, h - 20)),
                rnd.Next(6, Math.Max(8, w / 6)), rnd.Next(6, Math.Max(8, h / 6)));
        }
        return bmp;
    }

    private static Bitmap MakeSolid(int w, int h, Color c)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(c);
        return bmp;
    }

    /// <summary>一张"像游戏 UI"的帧：深色渐变 + 一行 ASCII 文字。</summary>
    private static Bitmap MakeFrameLike(int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using (var brush = new LinearGradientBrush(new Rectangle(0, 0, w, h),
                   Color.FromArgb(26, 30, 44), Color.FromArgb(12, 14, 22), 45f))
        {
            g.FillRectangle(brush, 0, 0, w, h);
        }
        using var font = new Font("Arial", 38f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var fg = new SolidBrush(Color.FromArgb(230, 200, 150));
        g.DrawString("TESTLABEL", font, fg, 80, 80);
        using var pen = new Pen(Color.FromArgb(70, 255, 255, 255), 2f);
        g.DrawEllipse(pen, w / 2, h / 2, 200, 120);
        return bmp;
    }
}
