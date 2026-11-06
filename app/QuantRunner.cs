using System.Drawing;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TheaterAssistant;

/// <summary>
/// 视频帧驱动的「模拟 → 对照 → 量化」评测：
///
///   1. 用真实视频帧跑一遍软件链路（模板 + OCR + 判定 + 决策），得到**软件指示**；
///   2. 从**同一帧**里独立提取"这一帧客观发生了什么"（幕次 / 界面类型 / 可选项数 / 编入人数 / 出现的角色名）；
///   3. 两者逐帧对照，量化三类指标：人物选择、决策、战斗。
///
///   帧 → 软件指示 ─┐
///                  ├→ 逐帧对照 → 分类量化 + 错例清单
///   帧 → 客观事实 ─┘
///
/// 重要边界（不虚报）：
///   · 幕次"事实"仍来自底部条带 OCR（与判定同源），因此 combat 指标是**自洽性/覆盖率**，
///     不是独立准确率；报告里明确标注。
///   · 决策指标衡量的是"指示是否与界面实际选项数一致"（编号有效性、该弃权时是否弃权），
///     不判断玩家选择对错。
///   · 人物指标衡量"软件推荐角色是否出现在该帧识别到的编队里"，命中率低不等于错（可能是别的可行解）。
/// </summary>
internal static class QuantRunner
{
    private sealed class FrameResult
    {
        public string Frame = "";
        public int ActFact;              // 条带里恰好一个「第N幕」（与判定同源，仅自洽性）
        public int ActTemplate;          // 模板 actN 命中（另一条独立通道）
        public string KindFact = "";     // team / difficulty / shop / battle / unknown
        public int OptionsFact;          // 可解析选项数
        public int TeamSize, TeamTarget;
        public List<string> CharsFact = new();
        public int SimActDetected;       // 本帧真正检测到的幕次（0=没检测到）
        public int SimAct;               // 经时序稳定后的幕次（可能沿用上一帧）
        public bool Carried;             // SimAct 是否为沿用
        public string SimKind = "";
        public string SimLine = "";
        public int DecisionIndex;        // 0 = 未给决策
        public int DecisionCount;        // 决策时可用选项数
        public List<string> Recommended = new();
        public bool HasInstruction;
        public long Ms;
    }

    public static int Run(string frameDir, string outMd, string outJson)
    {
        Cli.Attach();
        frameDir = Path.GetFullPath(frameDir);
        if (!Directory.Exists(frameDir)) { Cli.Say("帧目录不存在: " + frameDir); return 2; }

        var files = Directory.GetFiles(frameDir)
            .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        if (files.Count == 0) { Cli.Say("没有帧"); return 2; }

        var theater = TheaterData.Load(AppPaths.Data("season29.json"));
        var roster = RosterData.TryLoad(Path.Combine(AppPaths.Dir, "roster.json"));
        GuideIndex.LoadWithExtra(AppPaths.Data("guide-corpus.json"));
        Routine.Load(AppPaths.Data("rotations.json"));
        Playbook.Load(AppPaths.Data("act-playbook.json"));
        var knownChars = LoadKnownCharacters();

        using var ocr = new OcrService();
        ocr.InitializeAsync(AppPaths.ModelsRoot).GetAwaiter().GetResult();
        Cli.Say($"OCR: {ocr.Status}｜角色表 {knownChars.Count} 个｜帧 {files.Count}");

        var rows = new List<FrameResult>();
        var stab = new ActStabilizer();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        List<TemplateEntry> cacheTemplates = null;
        int cacheWidth = 0;

        foreach (var f in files)
        {
            var one = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var img = new Bitmap(f);
                if (cacheTemplates == null || cacheWidth != img.Width)
                {
                    cacheTemplates = LoadTemplates(img.Width);
                    cacheWidth = img.Width;
                }
                var hits = Matcher.Scan(img, cacheTemplates);
                var boxes = ocr.RecognizeBoxesAsync(img, null, img.Width < 900 ? 2 : 1).GetAwaiter().GetResult();
                var lines = (boxes ?? new List<OcrBox>()).Select(b => (b.Text ?? "").Trim()).Where(s => s.Length > 0).ToList();
                var text = string.Join("\n", lines);

                bool actFromTemplate = hits.Any(h => Regex.IsMatch(h.Name, @"^act\d{1,2}$"));
                // 对照用途：条带**总是**读，这样模板与条带两条通道能同时存在做交叉验证。
                // 运行时语义不变：只有模板未命中时才把条带结果交给判定（与线上逻辑一致）。
                var bandAlways = ocr.RecognizeRegionAsync(img, new RectangleF(0.28f, 0.828f, 0.46f, 0.05f), 3)
                    .GetAwaiter().GetResult();
                string band = actFromTemplate ? null : bandAlways;

                // ---------- 事实侧（这一帧客观发生了什么）----------
                int actFact = 0;
                if (!string.IsNullOrEmpty(bandAlways))
                {
                    var nums = Regex.Matches(bandAlways, @"第\s*([0-9]{1,2})\s*幕")
                        .Select(m => int.TryParse(m.Groups[1].Value, out var v) ? v : -1)
                        .Where(v => v > 0).Distinct().ToList();
                    if (nums.Count == 1) actFact = nums[0];   // 多个幕次=列表行，不当事实
                }
                var options = ChoiceDecider.ParseOptions(lines);
                var (tSize, tTarget) = ChoiceDecider.ParseTeamSize(text);
                var kindFact = FactKind(text, options.Count, tSize);
                var charsFact = MatchCharacters(text, knownChars);
                int actTemplate = hits.Where(h => Regex.IsMatch(h.Name, @"^act(\d{1,2})$"))
                    .Select(h => int.TryParse(Regex.Match(h.Name, @"^act(\d{1,2})$").Groups[1].Value, out var v) ? v : 0)
                    .Where(v => v > 0).DefaultIfEmpty(0).Max();

                // ---------- 软件侧（我们给玩家什么指示）----------
                var advice = TheaterLogic.Evaluate(hits, text, theater, roster, band);
                int simDetected = advice.Act ?? 0;      // 本帧真正检测到的
                var stable = stab.Push(advice.Act);
                int simAct = stable ?? 0;               // 时序稳定后（可能沿用）
                var dec = options.Count >= 2
                    ? ChoiceDecider.Decide(options, simAct, tSize, tTarget > 0 ? tTarget : 4)
                    : null;
                // 只有本帧真正识别到幕次才算"有上下文"；沿用旧幕次属于盲推荐，不计入人物指标
                var recAct = simDetected;
                var recommended = Routine.LinesFor(recAct > 0 ? recAct : (int?)null, 1)
                    .SelectMany(l => Regex.Matches(l, @"[\u4e00-\u9fff]{2,6}").Select(m => m.Value))
                    .Where(w => knownChars.Contains(w)).Distinct().ToList();

                rows.Add(new FrameResult
                {
                    Frame = Path.GetFileNameWithoutExtension(f),
                    ActFact = actFact, ActTemplate = actTemplate, KindFact = kindFact, OptionsFact = options.Count,
                    TeamSize = tSize, TeamTarget = tTarget, CharsFact = charsFact,
                    SimActDetected = simDetected, SimAct = simAct, Carried = simDetected == 0 && simAct > 0,
                    SimKind = advice.Kind ?? "", SimLine = advice.Lines.FirstOrDefault() ?? "",
                    DecisionIndex = dec?.Pick?.Index ?? 0,
                    DecisionCount = options.Count,
                    Recommended = recommended,
                    HasInstruction = advice.Lines.Count > 0,
                    Ms = one.ElapsedMilliseconds,
                });
            }
            catch (Exception ex)
            {
                rows.Add(new FrameResult { Frame = Path.GetFileNameWithoutExtension(f), KindFact = "error", SimLine = ex.Message, Ms = one.ElapsedMilliseconds });
            }
        }
        sw.Stop();

        // ---------- 量化 ----------
        // 战斗 A：双通道交叉（模板命中 vs 条带 OCR）——两条独立识别通道是否一致
        var crossFrames = rows.Where(r => r.ActTemplate > 0 && r.ActFact > 0).ToList();
        int crossHit = crossFrames.Count(r => r.ActTemplate == r.ActFact);
        // 战斗 B：本帧检测到的幕次 vs 条带事实（同源，仅自洽性）
        var combatFrames = rows.Where(r => r.ActFact > 0).ToList();
        int combatHit = combatFrames.Count(r => r.SimActDetected == r.ActFact);

        var withOptions = rows.Where(r => r.OptionsFact >= 2).ToList();
        int decValid = withOptions.Count(r => r.DecisionIndex >= 1 && r.DecisionIndex <= r.OptionsFact);
        int decMissing = withOptions.Count(r => r.DecisionIndex == 0);
        var noOptions = rows.Where(r => r.OptionsFact < 2).ToList();
        int correctAbstain = noOptions.Count(r => r.DecisionIndex == 0);

        // 人物：分母改为「该帧识别到编队角色的帧」，不再要求同时有幕次
        var charFrames = rows.Where(r => r.CharsFact.Count > 0 && r.Recommended.Count > 0).ToList();
        int charHit = charFrames.Count(r => r.Recommended.Any(x => r.CharsFact.Contains(x)));
        int carriedFrames = rows.Count(r => r.Carried);
        int detectedFrames = rows.Count(r => r.SimActDetected > 0);

        double Pct(int a, int b) => b == 0 ? 0 : 100.0 * a / b;

        var md = new StringBuilder();
        md.AppendLine("# 视频帧 · 模拟对照量化报告");
        md.AppendLine();
        md.AppendLine($"> 帧数 {rows.Count}｜总耗时 {sw.ElapsedMilliseconds} ms（平均 {sw.ElapsedMilliseconds / Math.Max(1, rows.Count)} ms/帧）");
        md.AppendLine("> 口径：从同一帧提取界面事实，与软件指示逐帧对照。分母为 0 表示该批帧没有可判定的样本（不是 0 分）。");
        md.AppendLine();
        md.AppendLine("## 三类量化指标");
        md.AppendLine();
        md.AppendLine("| 维度 | 指标 | 命中 | 分母 | 比例 | 说明 |");
        md.AppendLine("|---|---|---|---|---|---|");
        md.AppendLine($"| 战斗 | 双通道交叉一致（模板 vs 条带） | {crossHit} | {crossFrames.Count} | **{Pct(crossHit, crossFrames.Count):0.0}%** | 两条独立识别通道 |");
        md.AppendLine($"| 战斗 | 本帧检测幕次 = 条带事实 | {combatHit} | {combatFrames.Count} | {Pct(combatHit, combatFrames.Count):0.0}% | 同源，仅自洽性 |");
        md.AppendLine($"| 决策 | 编号有效（第K张 ∈ 1..N） | {decValid} | {withOptions.Count} | **{Pct(decValid, withOptions.Count):0.0}%** | 有 ≥2 选项的帧 |");
        md.AppendLine($"| 决策 | 该弃权时弃权 | {correctAbstain} | {noOptions.Count} | **{Pct(correctAbstain, noOptions.Count):0.0}%** | 选项 <2 的帧 |");
        md.AppendLine($"| 决策 | 有选项却未给决策 | {decMissing} | {withOptions.Count} | {Pct(decMissing, withOptions.Count):0.0}% | 越低越好 |");
        md.AppendLine($"| 人物 | 推荐角色出现在该帧编队 | {charHit} | {charFrames.Count} | **{Pct(charHit, charFrames.Count):0.0}%** | 有编队文本的帧 |");
        md.AppendLine($"| 总体 | 每帧都有可执行指示 | {rows.Count(r => r.HasInstruction)} | {rows.Count} | {Pct(rows.Count(r => r.HasInstruction), rows.Count):0.0}% | 弃权也算指示 |");
        md.AppendLine();
        md.AppendLine("## 幕次识别状态（诚实口径）");
        md.AppendLine();
        md.AppendLine($"- 本帧真正检测到幕次：**{detectedFrames}/{rows.Count}**");
        md.AppendLine($"- 沿用上一帧（未检测）：**{carriedFrames}/{rows.Count}** ← 这部分时长里指示依赖历史，需注意");
        md.AppendLine($"- 模板通道命中：{rows.Count(r => r.ActTemplate > 0)}｜条带通道命中：{combatFrames.Count}");
        md.AppendLine();
        md.AppendLine("## 界面类型分布（客观侧）");
        md.AppendLine();
        md.AppendLine("| 界面 | 帧数 | 平均选项数 | 给了决策 |");
        md.AppendLine("|---|---|---|---|");
        foreach (var g in rows.GroupBy(r => r.KindFact).OrderByDescending(g => g.Count()))
            md.AppendLine($"| {g.Key} | {g.Count()} | {g.Average(r => r.OptionsFact):0.0} | {g.Count(r => r.DecisionIndex > 0)} |");
        md.AppendLine();
        md.AppendLine("## 逐帧对照（前 40 帧）");
        md.AppendLine();
        md.AppendLine("| 帧 | 事实:条带幕/模板幕/界面/选项/人数 | 软件:幕(沿用?)/决策/指示 | 对照 |");
        md.AppendLine("|---|---|---|---|");
        foreach (var r in rows.Take(40))
        {
            var fact = $"{(r.ActFact > 0 ? r.ActFact.ToString() : "-")}/{(r.ActTemplate > 0 ? r.ActTemplate.ToString() : "-")}/{r.KindFact}/{r.OptionsFact}/{(r.TeamSize > 0 ? r.TeamSize + (r.TeamTarget > 0 ? "/" + r.TeamTarget : "") : "-")}";
            var simActText = r.SimAct > 0 ? r.SimAct + (r.Carried ? "(沿用)" : "") : "-";
            var sim = $"{simActText}/{(r.DecisionIndex > 0 ? "第" + r.DecisionIndex + "张" : "无")}/{Cut(r.SimLine, 30)}";
            var verdict = Verdict(r);
            md.AppendLine($"| {r.Frame} | {fact} | {sim} | {verdict} |");
        }

        outMd = string.IsNullOrWhiteSpace(outMd) ? Path.Combine(AppPaths.Dir, "quant-report.md") : Path.GetFullPath(outMd);
        outJson = string.IsNullOrWhiteSpace(outJson) ? Path.ChangeExtension(outMd, ".json") : Path.GetFullPath(outJson);
        try
        {
            File.WriteAllText(outMd, md.ToString(), Encoding.UTF8);
            File.WriteAllText(outJson, JsonSerializer.Serialize(new
            {
                frames = rows.Count,
                avgMs = sw.ElapsedMilliseconds / Math.Max(1, rows.Count),
                combatCross = new { hit = crossHit, total = crossFrames.Count, pct = Pct(crossHit, crossFrames.Count) },
                combat = new { hit = combatHit, total = combatFrames.Count, pct = Pct(combatHit, combatFrames.Count) },
                detectedFrames, carriedFrames,
                decision = new { valid = decValid, total = withOptions.Count, pct = Pct(decValid, withOptions.Count), missing = decMissing },
                abstain = new { correct = correctAbstain, total = noOptions.Count, pct = Pct(correctAbstain, noOptions.Count) },
                character = new { hit = charHit, total = charFrames.Count, pct = Pct(charHit, charFrames.Count) },
                instruction = new { frames = rows.Count(r => r.HasInstruction), total = rows.Count },
                kinds = rows.GroupBy(r => r.KindFact).ToDictionary(g => g.Key, g => g.Count()),
                rows,
            }, TemplateFactory.JsonOpts), Encoding.UTF8);
        }
        catch (Exception ex) { Cli.Say("写报告失败: " + ex.Message); return 1; }

        Cli.Say($"战斗·双通道交叉一致 {crossHit}/{crossFrames.Count}（{Pct(crossHit, crossFrames.Count):0.0}%）｜本帧检测={detectedFrames} 沿用={carriedFrames}");
        Cli.Say($"决策·编号有效 {decValid}/{withOptions.Count}（{Pct(decValid, withOptions.Count):0.0}%）｜该弃权时弃权 {correctAbstain}/{noOptions.Count}");
        Cli.Say($"人物·推荐出现在编队 {charHit}/{charFrames.Count}（{Pct(charHit, charFrames.Count):0.0}%）");
        Cli.Say("已写: " + outMd);
        Cli.Say("已写: " + outJson);
        return 0;
    }

    private static string Verdict(FrameResult r)
    {
        if (r.KindFact == "error") return "错误";
        var parts = new List<string>();
        if (r.ActFact > 0) parts.Add(r.SimActDetected == r.ActFact ? "幕次✓" : $"幕次✗(检测{r.SimActDetected}≠{r.ActFact})");
        if (r.ActTemplate > 0 && r.ActFact > 0 && r.ActTemplate != r.ActFact)
            parts.Add($"通道冲突(模板{r.ActTemplate}≠条带{r.ActFact})");
        if (r.OptionsFact >= 2)
            parts.Add(r.DecisionIndex >= 1 && r.DecisionIndex <= r.OptionsFact ? "编号✓" : "编号✗");
        if (r.KindFact == "team" && r.TeamSize > 0)
            parts.Add(r.TeamSize < (r.TeamTarget > 0 ? r.TeamTarget : 4) ? "该补人" : "人数已满");
        return parts.Count == 0 ? "-" : string.Join("｜", parts);
    }

    internal static string FactKind(string text, int optionCount, int teamSize)
    {
        if (string.IsNullOrEmpty(text)) return "blank";
        if (Regex.IsMatch(text, @"难度选择|参演要求|卓越|月谕")) return "difficulty";
        if (Regex.IsMatch(text, @"队伍配置|待命角色|已选开幕|已额外编入|编入的角色数量")) return "team";
        if (optionCount >= 2) return "shop";
        if (Regex.IsMatch(text, @"辉彩祝福|三选一|伙伴|神秘收获")) return "choice";
        return "battle";
    }

    internal static HashSet<string> LoadKnownCharacters()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in new[] { "substitutes.json", "characters.json" })
        {
            try
            {
                var p = AppPaths.Data(name);
                if (!File.Exists(p)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(p));
                var root = doc.RootElement;
                if (root.TryGetProperty("characters", out var chars) && chars.ValueKind == JsonValueKind.Object)
                    foreach (var e in chars.EnumerateObject())
                    {
                        if (name == "substitutes.json") set.Add(e.Name);
                        else if (e.Value.ValueKind == JsonValueKind.String) set.Add(e.Name);
                    }
                if (root.TryGetProperty("zhNames", out var zh) && zh.ValueKind == JsonValueKind.Object)
                    foreach (var e in zh.EnumerateObject())
                        if (e.Value.ValueKind == JsonValueKind.String && e.Value.GetString() is { Length: > 1 } s) set.Add(s);
            }
            catch { }
        }
        set.Remove("角色");
        return set;
    }

    internal static List<string> MatchCharacters(string text, HashSet<string> known)
    {
        var found = new List<string>();
        if (string.IsNullOrEmpty(text)) return found;
        foreach (var name in known)
            if (name.Length >= 2 && text.Contains(name, StringComparison.Ordinal) && !found.Contains(name))
                found.Add(name);
        return found;
    }

    private static string Cut(string s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "-";
        s = s.Replace("|", "/").Replace("\n", " ").Replace("▶", "").Trim();
        return s.Length <= n ? s : s[..n] + "…";
    }

    private static List<TemplateEntry> LoadTemplates(int width)
    {
        var list = new List<TemplateEntry>();
        var root = AppPaths.TemplatesRoot;
        if (!Directory.Exists(root)) return list;
        var dir = root;
        var best = Directory.GetDirectories(root)
            .Select(d => new { Dir = d, W = ParseWidth(Path.GetFileName(d)) })
            .Where(x => x.W > 0 && Math.Abs(x.W - width) <= Math.Max(32, (int)(width * 0.12)))
            .OrderBy(x => Math.Abs(x.W - width)).FirstOrDefault();
        if (best != null) dir = best.Dir;
        foreach (var f in Directory.GetFiles(dir, "*.png"))
        {
            var name = Path.GetFileNameWithoutExtension(f);
            if (name.StartsWith('_')) continue;
            try { list.Add(new TemplateEntry(name, new Bitmap(f), TemplateEntry.ThresholdFor(name))); } catch { }
        }
        return list;
    }

    private static int ParseWidth(string name)
    {
        var m = Regex.Match(name, @"^(\d{3,5})x\d{3,5}$");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }
}
