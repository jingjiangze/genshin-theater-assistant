using System.Drawing;
using System.Text;
using System.Text.Json;

namespace TheaterAssistant;

/// <summary>
/// 视频模拟运行测试：把一段游玩录像抽出的帧，**按时间顺序**逐帧喂进完整链路
/// （模板匹配 → OCR → 底部条带 → 状态判定 → 攻略检索 → 会播报什么），
/// 产出一条时间线报告，用来回答"真机上连续跑会不会乱跳/漏判"。
///
///   TheaterAssistant.exe --replay frames\ --out replay.md
///   TheaterAssistant.exe --replay frames\ --json replay.json --min-stable 2
///
/// 与 --frame 的区别：--frame 是单帧诊断；--replay 有**时序**概念，会统计
/// 幕次跳变（3→5 中间没有 4）、状态抖动（同一幕在两种状态间来回）、
/// 每幕驻留帧数与检索命中率 —— 这些才是"模拟真实运行"要暴露的问题。
/// </summary>
internal static class ReplayRunner
{
    internal sealed class Step
    {
        public string Frame = "";
        public int Act;
        public int StableAct;
        public int RawAct;
        public int TruthAct;   // 同帧底部条带 OCR 代理标签；与输入图像耦合，非独立真值
        public string BandText = "";
        public string Kind = "";
        public string Header = "";
        public string NextAction = "";
        public string Source = "";
        public string TopHit = "";
        public string WouldSpeak = "";
        public string Llm = "";        // ◇模型层输出（--llm 且端点可用时非空；"ERR:*" = 调用失败）
        public int Boxes;
        public long Ms;
    }

    public static int Run(string frameDir, string outMd, string outJson, int minStable, bool useLlm = false)
    {
        Cli.Attach();
        frameDir = Path.GetFullPath(frameDir);
        if (!Directory.Exists(frameDir)) { Cli.Say("帧目录不存在: " + frameDir); return 2; }

        var files = Directory.GetFiles(frameDir)
            .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        if (files.Count == 0) { Cli.Say("没有帧"); return 2; }

        var theater = TheaterData.Load(AppPaths.Data("season29.json"));
        var roster = RosterData.TryLoad(Path.Combine(AppPaths.Dir, "roster.json"));
        GuideIndex.LoadWithExtra(AppPaths.Data("guide-corpus.json"));
        Playbook.Load(AppPaths.Data("act-playbook.json"));

        // ◇模型层在环（--llm）：优先进程内 GGUF（内嵌模型，实测 ~150ms/答），
        // 没有再走 data/llm.json 的 HTTP 端点（tools/ml/serve.py）。
        // 两者都不可用 = Step.Llm 记为空，报告如实显示"未在环"。
        var brain = useLlm ? LocalBrain.TryLoad(Path.Combine(AppPaths.ModelsRoot, "llm", "minimind-26m-q8.gguf")) : null;
        var llm = useLlm && brain == null ? LlmAdvisor.TryLoad(AppPaths.Data("llm.json")) : null;
        var llmCalled = 0;
        var llmOk = 0;
        if (useLlm) Cli.Say("◇模型: " + (brain != null ? "内嵌 " + brain.Status : llm != null ? "HTTP " + llm.Health : "未启用"));

        using var ocr = new OcrService();
        ocr.InitializeAsync(AppPaths.ModelsRoot).GetAwaiter().GetResult();
        Cli.Say("OCR: " + ocr.Status + "｜语料 " + GuideIndex.Count + " 条｜帧 " + files.Count
                + (brain != null ? "｜◇模型: 内嵌 llama-server" : llm != null ? "｜◇模型: HTTP " + llm.Health : "｜◇模型: 未启用"));

        var steps = new List<Step>();
        var stab = new ActStabilizer();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        foreach (var f in files)
        {
            var one = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var img = new Bitmap(f);
                var templates = LoadTemplates(img.Width);
                var hits = Matcher.Scan(img, templates);

                // 参考画布：与生产路径一致（MainLoop 用 OcrService.ReferenceCanvas 限到 ~1100 长边）
                using var canvas = OcrService.ReferenceCanvas(img, out var canvasScaled);
                var ocrImg = canvasScaled ? canvas : img;

                // 与生产一致：只 OCR 选项区（0.10–0.95 x / 0.22–0.82 y），不全帧扫
                var boxes = ocr.RecognizeBoxesAsync(ocrImg,
                    new RectangleF(0.10f, 0.22f, 0.85f, 0.60f), ocrImg.Width < 900 ? 2 : 1).GetAwaiter().GetResult();
                var text = string.Join("\n", (boxes ?? new List<OcrBox>()).Select(b => b.Text));

                bool actFromTemplate = hits.Any(h => System.Text.RegularExpressions.Regex.IsMatch(h.Name, @"^act\d{1,2}$"));
                string band = actFromTemplate ? null
                    : ocr.RecognizeRegionAsync(ocrImg, new RectangleF(0.28f, 0.828f, 0.46f, 0.05f), 2).GetAwaiter().GetResult();

                var advice = TheaterLogic.Evaluate(hits, text, theater, roster, band);
                // 视觉代理标签：从同一输入帧的底部条带 OCR 提取，只用于自洽性诊断，不是独立真值
                int truthAct = 0;
                if (!string.IsNullOrEmpty(band))
                {
                    // 真值同样要求"条带里恰好一个幕次"：多个=幕次列表，不能当标准答案
                    var tms = System.Text.RegularExpressions.Regex.Matches(band, @"第\s*([0-9]{1,2})\s*幕");
                    var nums = tms.Select(m => int.TryParse(m.Groups[1].Value, out var v) ? v : -1).Where(v => v > 0).Distinct().ToList();
                    if (nums.Count == 1) truthAct = nums[0];
                }

                var rawAct = advice.Act;
                var stableAct = stab.Push(rawAct);          // 与运行时同一套时序稳定
                var query = (stableAct.HasValue ? $"第{stableAct}幕 " : "") + (advice.Lines.FirstOrDefault() ?? "");
                var top = GuideIndex.Search(query, advice.Act, 1).FirstOrDefault();

                var st = new Step
                {
                    Frame = Path.GetFileNameWithoutExtension(f),
                    Act = stableAct ?? 0,
                    RawAct = rawAct ?? 0,
                    TruthAct = truthAct,
                    BandText = (band ?? "").Replace("\n", " ").Trim(),
                    StableAct = stableAct ?? 0,
                    Kind = advice.Kind ?? "unknown",
                    Header = advice.Header,
                    NextAction = TheaterLogic.NextAction(advice.Kind ?? "unknown", advice.Act) ?? "",
                    Source = advice.Diagnostics ?? "",
                    TopHit = top == null ? "" : $"{top.Entry.Title}({top.Score:0.00})",
                    WouldSpeak = Speech.NormalizeForSpeech(advice.Lines.FirstOrDefault() ?? ""),
                    Boxes = boxes?.Count ?? 0,
                    Ms = one.ElapsedMilliseconds,
                };

                // ◇模型层在环：优先内嵌 GGUF（~150ms），否则 HTTP 端点（~1s）。
                // 只在决策有意义的帧上调（幕次已判出 / 三选一 / 剧诗界面），unknown 帧不打扰模型。
                // ◇只补「幕次已知」与「三选一」（模型训练集只有幕次语境与随机要素）；
                // hall/difficulty/team 这类界面答案来自 playbook 确定性文本，交规则/检索更准。
                if ((brain != null || llm != null) && st.Kind is "act" or "choice")
                {
                    llmCalled++;
                    try
                    {
                        if (brain != null)
                        {
                            var ask = (st.Act > 0 ? $"我在剧诗第{st.Act}幕。" : "我在剧诗界面，幕次还没认出来。")
                                      + "一句话概括该干嘛。";
                            st.Llm = (brain.Ask(ask, 64, 1500) ?? "").Replace("\r", " ").Trim();
                        }
                        else
                        {
                            var stateLine = (st.Act > 0 ? $"第{st.Act}幕 " : "") + (st.Kind ?? "");
                            var t = llm == null ? null : llm.RefineAsync(stateLine,
                                advice.Lines.Take(3).ToList(),
                                roster?.Loaded == true ? "已导入角色池" : "未导入角色池").GetAwaiter().GetResult();
                            st.Llm = (t ?? "").Replace("\r", " ").Trim();
                        }
                        if (st.Llm.Length > 0) llmOk++;
                    }
                    catch (Exception ex2)
                    {
                        st.Llm = "ERR:" + ex2.GetType().Name;
                    }
                }

                steps.Add(st);
            }
            catch (Exception ex)
            {
                steps.Add(new Step { Frame = Path.GetFileNameWithoutExtension(f), Kind = "error", Header = ex.Message, Ms = one.ElapsedMilliseconds });
            }
        }
        sw.Stop();

        // ---- 时序分析 ----
        static List<string> Jumps(IEnumerable<Step> src, Func<Step, int> pick)
        {
            var list = new List<string>();
            int prev = 0;
            foreach (var s in src)
            {
                var a = pick(s);
                if (a <= 0) continue;
                if (prev > 0 && a != prev && a != prev + 1 && a != prev - 1)
                    list.Add($"{prev} → {a}（{s.Frame}）");
                prev = a;
            }
            return list;
        }

        var acted = steps.Where(s => s.Act > 0).ToList();
        var jumps = Jumps(steps, s => s.Act);                    // 稳定后
        var rawJumps = Jumps(steps, s => s.RawAct);               // 原始（未稳定）
        var stableSeq = acted.Select(s => s.Act).Distinct().ToList();
        var rawSeq = steps.Where(s => s.RawAct > 0).Select(s => s.RawAct).Distinct().ToList();

        var flips = new List<string>();
        for (int i = 2; i < steps.Count; i++)
        {
            if (steps[i].Kind == steps[i - 2].Kind && steps[i].Kind != steps[i - 1].Kind)
                flips.Add($"{steps[i - 2].Kind}/{steps[i - 1].Kind} @ {steps[i].Frame}");
        }

        // ---- 视觉条带代理自洽性：标签与预测来自同一帧，不得当作独立准确率 ----
        var truthFrames = steps.Where(s => s.TruthAct > 0).ToList();
        var wrong = truthFrames.Where(s => s.Act != s.TruthAct).ToList();
        int missed = truthFrames.Count(s => s.Act == 0);
        double acc = truthFrames.Count == 0 ? 0 : 100.0 * (truthFrames.Count - wrong.Count) / truthFrames.Count;
        // 原始识别另算：稳定器会拦下"倒退"，若拿剪辑过的视频当真值，会把稳定器判成错
        var rawWrong = truthFrames.Where(s => s.RawAct != s.TruthAct).ToList();
        double rawAcc = truthFrames.Count == 0 ? 0 : 100.0 * (truthFrames.Count - rawWrong.Count) / truthFrames.Count;

        var perKind = steps.GroupBy(s => s.Kind).OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key}={g.Count()}").ToList();
        int retrieved = steps.Count(s => s.TopHit.Length > 0);
        int withNext = steps.Count(s => s.NextAction.Length > 0);

        var lines = new List<string>
        {
            "# 视频模拟运行报告",
            "",
            "- 标签来源：同一输入帧底部条带 OCR；与预测输入耦合，不用于知识晋级。",
            $"- 帧数：{steps.Count}（按文件名排序模拟时间顺序）",
            $"- 总耗时：{sw.ElapsedMilliseconds} ms（平均 {sw.ElapsedMilliseconds / Math.Max(1, steps.Count)} ms/帧）",
            $"- 本帧识别到幕次：{steps.Count(s => s.RawAct > 0)} 帧；沿用上一帧：{steps.Count(s => s.RawAct == 0 && s.Act > 0)} 帧",
            $"- **视觉条带代理·稳定后**：有代理标签 {truthFrames.Count} 帧，自洽率 **{acc:0.0}%**（漏判 {missed}）",
            $"- **视觉条带代理·原始识别**：自洽率 **{rawAcc:0.0}%**（一致 {truthFrames.Count - rawWrong.Count}）",
            rawWrong.Count > 0
              ? $"- 原始错例（前 8 条）：{string.Join("；", rawWrong.Take(8).Select(s => $"{s.Frame}: 原始={s.RawAct}/真值={s.TruthAct}/条带『{s.BandText}』"))}"
              : "- 原始错例：无",
            wrong.Count > 0
              ? $"- 错例（前 8 条）：{string.Join("；", wrong.Take(8).Select(s => $"{s.Frame}: 我们={s.Act}/真值={s.TruthAct}/条带『{s.BandText}』"))}"
              : "- 错例：无",
            $"- 状态分布：{string.Join("，", perKind)}",
            $"- 给出「现在做」：{withNext}/{steps.Count}；检索命中：{retrieved}/{steps.Count}",
            llmCalled > 0
                ? $"- ◇模型在环：{llmOk}/{llmCalled} 帧有输出（来源: {(brain != null ? "内嵌 llama-server" : llm?.Health ?? "?")}）"
                : "- ◇模型在环：未启用（--llm）",
            $"- 幕次序列（原始）：{string.Join(" → ", rawSeq)}；跳变 {rawJumps.Count} 次{(rawJumps.Count > 0 ? "：" + string.Join("；", rawJumps.Take(4)) : "")}",
            $"- 幕次序列（稳定后）：{string.Join(" → ", stableSeq)}；跳变 {jumps.Count} 次{(jumps.Count > 0 ? "：" + string.Join("；", jumps.Take(4)) : "")}",
            $"- 稳定器拦下的候选：{stab.Rejected} 次",
            $"- 状态抖动（AB A 形态）：{flips.Count} 次{(flips.Count > 0 ? "：" + string.Join("；", flips.Take(6)) : "")}",
            "",
            "| 帧 | 幕 | 状态 | 现在做 | 检索首条 | 会播报 | ◇模型 | ms |",
            "|---|---|---|---|---|---|---|---|",
        };
        foreach (var s in steps)
        {
            lines.Add($"| {s.Frame} | {(s.Act > 0 ? s.Act.ToString() : "-")} | {s.Kind} | {Cut(s.NextAction, 26)} | {Cut(s.TopHit, 26)} | {Cut(s.WouldSpeak, 24)} | {Cut(s.Llm, 24)} | {s.Ms} |");
        }

        foreach (var l in lines.Take(12)) Cli.Say(l);

        try
        {
            if (!string.IsNullOrWhiteSpace(outMd))
            {
                File.WriteAllText(Path.GetFullPath(outMd), string.Join(Environment.NewLine, lines), Encoding.UTF8);
                Cli.Say("报告: " + Path.GetFullPath(outMd));
            }
            if (!string.IsNullOrWhiteSpace(outJson))
            {
                File.WriteAllText(Path.GetFullPath(outJson),
                    JsonSerializer.Serialize(new
                    {
                        frames = steps.Count,
                        jumps,
                        flips,
                        llm = new { ok = llmOk, total = llmCalled, health = llm?.Health ?? "" },
                        steps,
                    }, TemplateFactory.JsonOpts), Encoding.UTF8);
                Cli.Say("JSON: " + Path.GetFullPath(outJson));
            }
        }
        catch (Exception ex) { Cli.Say("写报告失败: " + ex.Message); }

        // 稳定性判定：连续稳定（同幕至少 minStable 帧）与无跳变，才算这条录像"能跑"
        bool stable = jumps.Count == 0 && acted.Count >= Math.Min(minStable, steps.Count);
        Cli.Say(stable ? "PASS 时序稳定（无跨幕跳变）" : "WARN 时序有问题（见报告）");
        return stable ? 0 : 1;
    }

    private static string Cut(string s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "-";
        s = s.Replace("|", "/").Replace("\n", " ");
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
        var m = System.Text.RegularExpressions.Regex.Match(name, @"^(\d{3,5})x\d{3,5}$");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }
}
