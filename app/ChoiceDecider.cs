using System.Text.RegularExpressions;

namespace TheaterAssistant;

/// <summary>
/// 决策引擎：把「现在屏幕上有哪些选项」+「第几幕/元素限制」压成一条**可直接照做**的指令：
///   「选第 2 张：雷晶·进阶（雷系强化，第 3 幕要用雷推怒气条）」
///
/// 设计原则（为什么不是让模型自由发挥）：
///  · 选项必须来自 OCR 实读，编号必须能在界面上对上（第 N 张从 1 开始，按从左到右/从上到下）；
///  · 规则先行：幕次 → 需要的元素/动作（来自机制事实，不是猜测）；
///  · 池子/角色名只在"有数据"时才参与，缺数据就退回纯规则（season29.json 的 rec/chars 目前是空的）；
///  · 大模型只允许在**给定选项里挑一个**，输出必须能被解析成"第N"，否则整条丢弃回落规则。
/// </summary>
internal static class ChoiceDecider
{
    internal enum Kind { Battle, Buff, Partner, Mystery, Blessing, Unknown }

    internal sealed class Option
    {
        public int Index;              // 1-based，界面顺序
        public string Raw = "";        // OCR 原文
        public Kind Kind = Kind.Unknown;
        public string Element = "";    // Hydro / Electro / Geo / ""（未知）
    }

    internal sealed class Decision
    {
        public Option Pick;
        public string Sentence = "";   // 可直接照做的一句话
        public string Reason = "";     // 依据（含来源）
        public bool FromModel;
    }

    private static readonly (string Word, string Element)[] ElementWords =
    {
        ("水", "Hydro"), ("雷", "Electro"), ("岩", "Geo"), ("火", "Pyro"),
        ("冰", "Cryo"), ("风", "Anemo"), ("草", "Dendro"), ("感电", "Electro"),
        ("超载", "Pyro"), ("扩散", "Anemo"), ("结晶", "Geo"), ("绽放", "Dendro"),
    };

    // 商店/选卡界面的关键词（实测：战斗·普通 / 雷晶·进阶 / 感电·原态）
    private static readonly Regex ShopLine = new(@"([\u4e00-\u9fff]{2,6})[·・\.]([\u4e00-\u9fff]{2,6})", RegexOptions.Compiled);
    private static readonly Regex EventWords = new(@"(伙伴|神秘收获|辉彩祝福)", RegexOptions.Compiled);

    internal static string ElementOf(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        foreach (var (w, e) in ElementWords)
            if (text.Contains(w, StringComparison.Ordinal)) return e;
        return "";
    }

    private static readonly Regex TeamSizeRe = new(@"已(?:额外)?编入\s*(\d{1,2})\s*/\s*(\d{1,2})", RegexOptions.Compiled);
    private static readonly Regex TeamSizeAlt = new(@"编入的角色数量[^\d]{0,6}(\d{1,2})", RegexOptions.Compiled);

    /// <summary>
    /// 从界面文字里读"已编入人数/上限"（实测文案：`已额外编入4/4名待命角色`、`编入的角色数量 26`）。
    /// 读不到返回 (0,0)，调用方保持原值。
    /// </summary>
    internal static (int Size, int Target) ParseTeamSize(string text)
    {
        if (string.IsNullOrEmpty(text)) return (0, 0);
        var m = TeamSizeRe.Match(text);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var s) && int.TryParse(m.Groups[2].Value, out var tg))
            return (s, tg);
        var a = TeamSizeAlt.Match(text);
        if (a.Success && int.TryParse(a.Groups[1].Value, out var one)) return (one, 0);
        return (0, 0);
    }

    /// <summary>从 OCR 文本里解析「可选项」；只认带分隔符的卡片行，避免把界面文案当卡片。</summary>
    internal static List<Option> ParseOptions(IEnumerable<string> lines)
    {
        var list = new List<Option>();
        if (lines == null) return list;

        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var line = raw.Trim();

            var ev = EventWords.Match(line);
            if (ev.Success && line.Length <= 12)
            {
                var k = ev.Groups[1].Value switch
                {
                    "伙伴" => Kind.Partner,
                    "神秘收获" => Kind.Mystery,
                    "辉彩祝福" => Kind.Blessing,
                    _ => Kind.Unknown,
                };
                if (list.All(o => o.Raw != line))
                    list.Add(new Option { Index = list.Count + 1, Raw = line, Kind = k, Element = ElementOf(line) });
                continue;
            }

            if (!ShopLine.IsMatch(line)) continue;
            if (line.Length > 20) continue;

            var kind = line.Contains("战斗", StringComparison.Ordinal) ? Kind.Battle : Kind.Buff;
            // 注意：**不去重**。商店里经常出现两张同名卡（实测「战斗·普通」×2），
            // 去重会让"第N张"与界面对不上，玩家照做就选错。
            list.Add(new Option { Index = list.Count + 1, Raw = line, Kind = kind, Element = ElementOf(line) });
        }
        return list;
    }

    /// <summary>本幕"该干什么"的机制事实（与 TheaterLogic.BossAdvice / 语料一致）。</summary>
    /// <summary>
    /// 本幕需要的元素来自 data/act-playbook.json（内容源），代码不再按幕硬编码。
    /// 没有该幕资料时返回 ("","")，调用方**不加元素分**——不猜。
    /// </summary>
    internal static (string Need, string Why) NeedOf(int act)
    {
        var (element, why) = Playbook.Need(act);
        if (!string.IsNullOrEmpty(element)) return (element, why);
        return ("", why);
    }

    /// <summary>
    /// 在给定选项里做决定。没有选项返回 null（调用方保持原建议）。
    /// teamSize：当前已编入人数（未知传 0）；targetSize：本幕想达到的人数。
    /// </summary>
    internal static Decision Decide(IReadOnlyList<Option> options, int act, int teamSize = 0, int targetSize = 4)
    {
        if (options == null || options.Count == 0) return null;

        var (need, why) = NeedOf(act);
        bool bossAct = act is 3 or 6 or 8 or 10;

        // 1) 三选一事件：缺人先拿伙伴，否则拿祝福
        var partner = options.FirstOrDefault(o => o.Kind == Kind.Partner);
        var blessing = options.FirstOrDefault(o => o.Kind == Kind.Blessing);
        if (partner != null && teamSize < targetSize)
            return new Decision { Pick = partner, Sentence = $"选「伙伴」（补人：当前 {teamSize}/{targetSize}）", Reason = "人数不足先补人，避免后续幕凑不齐" };
        if (blessing != null && (teamSize >= targetSize || partner == null))
            return new Decision { Pick = blessing, Sentence = "选「辉彩祝福」（提上限，人数已够）", Reason = "人数够时祝福收益最高" };
        var mystery = options.FirstOrDefault(o => o.Kind == Kind.Mystery);
        if (mystery != null && options.Count == 1)
            return new Decision { Pick = mystery, Sentence = "选「神秘收获」", Reason = "没有更好的选项时拿随机收益" };

        // 2) 买卡界面：首领关优先本幕需要的元素强化
        if (bossAct && need.Length > 0)
        {
            var match = options.FirstOrDefault(o => o.Kind == Kind.Buff && string.Equals(o.Element, need, StringComparison.Ordinal));
            if (match != null)
                return new Decision
                {
                    Pick = match,
                    Sentence = $"选第 {match.Index} 张：{match.Raw}（{match.Element} 强化）",
                    Reason = why,
                };
            var anyBuff = options.FirstOrDefault(o => o.Kind == Kind.Buff);
            if (anyBuff != null)
                return new Decision
                {
                    Pick = anyBuff,
                    Sentence = $"选第 {anyBuff.Index} 张：{anyBuff.Raw}（本幕没有 {need} 强化，拿通用强化）",
                    Reason = why,
                };
        }

        // 3) 非首领关 / 首领关没有强化卡：优先战斗卡补人
        var battle = options.FirstOrDefault(o => o.Kind == Kind.Battle);
        if (battle != null)
            return new Decision
            {
                Pick = battle,
                Sentence = $"选第 {battle.Index} 张：{battle.Raw}（补人/推进）",
                Reason = teamSize > 0 ? $"当前 {teamSize}/{targetSize} 人，先补人" : "非首领关优先铺开人数",
            };

        // 4) 兜底：第一张
        var first = options[0];
        return new Decision
        {
            Pick = first,
            Sentence = $"选第 {first.Index} 张：{first.Raw}",
            Reason = "没有更具体的匹配，按最左优先",
        };
    }

    /// <summary>把大模型的自由文本收敛成选项之一：只认「第N」或直接包含某选项原文。</summary>
    internal static Decision FromModel(string answer, IReadOnlyList<Option> options)
    {
        if (string.IsNullOrWhiteSpace(answer) || options == null || options.Count == 0) return null;
        var m = Regex.Match(answer, @"第\s*(\d{1,2})\s*(?:张|个|项)?");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n >= 1 && n <= options.Count)
            return new Decision { Pick = options[n - 1], Sentence = $"选第 {n} 张：{options[n - 1].Raw}", Reason = "大模型选择", FromModel = true };

        foreach (var o in options)
            if (answer.Contains(o.Raw, StringComparison.Ordinal))
                return new Decision { Pick = o, Sentence = $"选第 {o.Index} 张：{o.Raw}", Reason = "大模型选择", FromModel = true };

        return null;   // 说不清就不采纳
    }

    // ---------------- CLI ----------------

    public static int RunCli(string text, int act, int teamSize, int target)
    {
        Cli.Attach();
        var lines = (text ?? "").Split('\n', '；', ';', '|').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        var opts = ParseOptions(lines);
        Cli.Say($"解析到选项 {opts.Count} 个：");
        foreach (var o in opts) Cli.Say($"  [{o.Index}] {o.Raw}  kind={o.Kind} element={(o.Element == "" ? "-" : o.Element)}");
        var d = Decide(opts, act, teamSize, target);
        if (d == null) { Cli.Say("没有可决策的选项"); return 3; }
        Cli.Say("");
        Cli.Say("▶ " + d.Sentence);
        Cli.Say("  依据：" + d.Reason);
        return 0;
    }
}
