using System.Text.Json;

namespace TheaterAssistant;

/// <summary>
/// P2 量化评分器：把「敌人需求向量」和「我方覆盖」乘起来，产出**可解释的分数**。
///
/// 公式（权重在 data/quant-model.json，可调可回归）：
///   score = w_res × 抗性利用 + w_role × 职能覆盖 + w_know × 云端已验证命中 − w_stam × 代价
///   confidence = 分差归一（只有一个候选时给低分，明确"证据不足"）
///
/// 边界：
///   · 纯函数，不联网、不读环境；
///   · 缺数据的分项按 0 计并写进 `gaps`，**不用默认值假装有**；
///   · 池子不是真实池子（Full=false）时，结论必须标"通用池"。
/// </summary>
internal static class QuantScorer
{
    internal sealed class Weights
    {
        public double Res = 0.55;
        public double Role = 0.25;
        public double Know = 0.20;
        public double Stam = 0.10;
    }

    internal sealed class Candidate
    {
        public string Label = "";            // 例如 "Electro 位：菲谢尔"
        public string Element = "";
        public List<string> Members = new();
        public double NeedWeight;            // 该需求在需求向量里的权重
        public double CoverFactor;           // 0..1，覆盖程度
        public double Contribution;          // NeedWeight × CoverFactor
    }

    internal sealed class Result
    {
        public int Act;
        public string Difficulty = "";
        public double Score;                 // 0..1
        public double Confidence;            // 0..1
        public double ResPart, RolePart, KnowPart, StamPart;
        public List<Candidate> Candidates = new();
        public List<string> Gaps = new();    // 未满足的需求（明确列出）
        public List<string> Sources = new();
        public bool PoolFull;
        public string Decision = "";
    }

    private static Weights _w = new();

    public static void LoadWeights(string path)
    {
        _w = new Weights();
        try
        {
            if (!File.Exists(path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            _w.Res = Num(r, "res", _w.Res);
            _w.Role = Num(r, "role", _w.Role);
            _w.Know = Num(r, "know", _w.Know);
            _w.Stam = Num(r, "stam", _w.Stam);
        }
        catch { }
    }

    /// <summary>对给定敌人画像与池子打分。enemy/team 为 null 时返回 null（调用方须说明"无数据"）。</summary>
    public static Result Score(QuantData.EnemyProfile enemy, QuantData.TeamProfile team, int act)
    {
        if (enemy == null || team == null) return null;

        var res = new Result { Act = act, Difficulty = enemy.Difficulty, PoolFull = team.Full };
        res.Sources.AddRange(enemy.Sources);
        if (team.Source.Length > 0) res.Sources.Add(team.Source);

        // ---- 抗性/需求利用 ----
        double needTotal = enemy.Need.Values.Sum();
        double covered = 0;
        foreach (var kv in enemy.Need.OrderByDescending(k => k.Value))
        {
            var element = kv.Key;
            var need = kv.Value;
            var members = team.Members
                .Where(m => string.Equals(m.Element, element, StringComparison.OrdinalIgnoreCase))
                .Select(m => m.Name)
                .ToList();
            int cover = team.Cover.TryGetValue(element, out var c) ? c : members.Count;
            double factor = Math.Min(1.0, cover / 2.0);   // 2 人视为充分覆盖
            covered += need * factor;
            res.Candidates.Add(new Candidate
            {
                Element = element,
                NeedWeight = need,
                CoverFactor = factor,
                Members = members,
                Label = members.Count == 0
                    ? $"缺 {element} 位（本幕需要，池子没有）"
                    : $"{element} 位：{string.Join("/", members.Take(3))}",
                Contribution = need * factor,
            });
            if (members.Count == 0) res.Gaps.Add($"{element}(权重{need:0.0})未覆盖");
        }
        res.ResPart = needTotal > 0 ? covered / needTotal : 0;

        // ---- 职能覆盖（治疗/破盾类需求，来自 playbook 的 why 文本线索）----
        var why = string.Join(" ", enemy.MechanicEffect, Playbook.Need(act).Why);
        bool needHeal = why.Contains("奶") || why.Contains("治疗") || why.Contains("力竭");
        bool needBreak = why.Contains("破锅") || why.Contains("钝击") || why.Contains("破盾") || why.Contains("护盾");
        int roleNeeded = 0, roleHave = 0;
        if (needHeal) { roleNeeded++; if (team.Roles.TryGetValue("Healer", out var h) && h > 0) roleHave++; }
        if (needBreak) { roleNeeded++; if (team.Roles.TryGetValue("GeoBreak", out var g) && g > 0) roleHave++; }
        res.RolePart = roleNeeded == 0 ? 0.5 : (double)roleHave / roleNeeded;   // 无职能需求时给中性 0.5

        // ---- 云端已验证知识命中 ----
        res.KnowPart = Playbook.VerifiedFor(act) != null ? 1.0 : 0.0;
        if (res.KnowPart > 0) res.Sources.Add("knowledge-release(VERIFIED)");

        // ---- 代价（未知耐力 → 用"池子是否完整"作代理，避免编数值）----
        res.StamPart = team.Full ? 0.0 : 0.5;

        res.Score = Clamp01(_w.Res * res.ResPart + _w.Role * res.RolePart + _w.Know * res.KnowPart - _w.Stam * res.StamPart);

        // ---- 置信度：候选需求之间的分差（只有 1 个需求时视为证据不足）----
        var contribs = res.Candidates.Select(c => c.Contribution).OrderByDescending(x => x).ToList();
        if (contribs.Count < 2) res.Confidence = 0.3;
        else
        {
            double best = contribs[0], second = contribs[1];
            res.Confidence = best <= 0 ? 0 : Clamp01((best - second) / Math.Max(best, 1e-6));
        }
        if (!team.Full) res.Confidence = Math.Min(res.Confidence, 0.4);   // 通用池不给高置信

        res.Decision = BuildDecision(res, enemy);
        return res;
    }

    private static string BuildDecision(Result r, QuantData.EnemyProfile enemy)
    {
        var top = r.Candidates.Where(c => c.Members.Count > 0).OrderByDescending(c => c.Contribution).FirstOrDefault();
        var pool = r.PoolFull ? "" : "（未读到你的角色池，按通用池）";
        var window = enemy.WindowSeconds > 0 ? $"，输出压在 {enemy.WindowSeconds}s 窗口" : "";
        if (top == null)
            return r.Gaps.Count > 0
                ? $"本幕需要 {string.Join("/", r.Gaps)}{pool}：先补人再打{window}"
                : $"本幕无明确元素需求{pool}：按手法推进{window}";
        return $"优先上 {top.Members[0]}（{top.Element}，覆盖本幕需求 {top.Contribution:0.00}）{pool}{window}";
    }

    private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);

    private static double Num(JsonElement e, string name, double fallback) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : fallback;

    public static int RunCli(int act, string difficulty, string outPath)
    {
        Cli.Attach();
        QuantData.LoadEnemies(AppPaths.Data("season29.json"));
        QuantData.LoadCharacters(AppPaths.Data("characters.json"), AppPaths.Data("substitutes.json"));
        Playbook.Load(AppPaths.Data("act-playbook.json"));
        LoadWeights(AppPaths.Data("quant-model.json"));

        var real = QuantData.Team(Path.Combine(AppPaths.Dir, "roster.json"));
        var team = real.HasData ? real : QuantData.GenericTeam();

        var acts = act > 0 ? new[] { act } : Enumerable.Range(1, 12).ToArray();
        var lines = new List<string>();
        int ok = 0;
        foreach (var a in acts)
        {
            var enemy = QuantData.Enemy(a, difficulty);
            if (enemy == null || !enemy.HasAnyData) { lines.Add($"第{a}幕: 无敌人数据（不外推）"); continue; }
            var r = Score(enemy, team, a);
            if (r == null) { lines.Add($"第{a}幕: 无法评分"); continue; }
            ok++;
            lines.Add($"第{a}幕 score={r.Score:0.00} conf={r.Confidence:0.00} " +
                      $"[抗性{r.ResPart:0.00} 职能{r.RolePart:0.00} 知识{r.KnowPart:0.00} 代价{r.StamPart:0.00}] " +
                      $"{(r.Gaps.Count > 0 ? "缺口:" + string.Join("+", r.Gaps) : "需求全覆盖")}");
            lines.Add($"      → {r.Decision}");
        }
        foreach (var l in lines) Cli.Say(l);
        Cli.Say($"已评分 {ok}/{acts.Length} 幕｜池子 Full={team.Full}｜来源 {(team.Source)}");

        if (!string.IsNullOrWhiteSpace(outPath))
        {
            try
            {
                var doc = new
                {
                    generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    poolFull = team.Full,
                    weights = new { _w.Res, _w.Role, _w.Know, _w.Stam },
                    lines,
                };
                File.WriteAllText(Path.GetFullPath(outPath),
                    JsonSerializer.Serialize(doc, TemplateFactory.JsonOpts), new System.Text.UTF8Encoding(false));
                Cli.Say("已写: " + Path.GetFullPath(outPath));
            }
            catch (Exception ex) { Cli.Say("写文件失败: " + ex.Message); return 1; }
        }
        return ok > 0 ? 0 : 2;
    }
}
