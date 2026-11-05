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
        GuideIndex.Load(AppPaths.Data("guide-corpus.json"));
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

        // 左侧 4px 强调条必须是金色（198,168,96）
        var accent = bmp.GetPixel(1, bmp.Height / 2);
        bool accentOk = accent.R > 150 && accent.G > 120 && accent.B < 130;
        Check("左侧金色强调条", accentOk, $"RGB({accent.R},{accent.G},{accent.B})");

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

        // 溢出检测：最后 4 列里有亮点 = 文字被画到边界外（175% 下曾把"攻略"裁掉）
        int edge = 0;
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = bmp.Width - 4; x < bmp.Width; x++)
            {
                var p = bmp.GetPixel(x, y);
                if (p.R > 120 && p.G > 120 && p.B > 120) edge++;
            }
        }
        Check("文字不溢出右边", edge == 0, $"右边 4 列亮点 {edge} 个");

        // 底部热键行也必须在画布内（贴底 2 行不该有亮点）
        int bottom = 0;
        for (int y = bmp.Height - 2; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                var p = bmp.GetPixel(x, y);
                if (p.R > 120 && p.G > 120 && p.B > 120) bottom++;
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
