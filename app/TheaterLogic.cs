using System.Text.Json;
using System.Text.RegularExpressions;

namespace TheaterAssistant;

/// <summary>第 29 期数据（由 data\season29.json 载入；五档难度全部幕次）。</summary>
internal sealed class TheaterData
{
    public int Season { get; private set; }
    public string[] Elements { get; private set; } = Array.Empty<string>();
    public string[] Opening { get; private set; } = Array.Empty<string>();
    public string[] Guests { get; private set; } = Array.Empty<string>();

    private readonly Dictionary<string, Dictionary<int, ActData>> _acts = new(StringComparer.OrdinalIgnoreCase);

    public sealed class ActData
    {
        public int Act;
        public int Level;
        public bool Boss;
        public List<Encounter> Encounters = new();
    }

    public sealed class Encounter
    {
        public string Name;
        public string Res;
        public string Req;
        public string Mech;
        public string Rec;
        public string Chars;
    }

    public static TheaterData Load(string path)
    {
        var d = new TheaterData();
        if (!File.Exists(path)) return d;

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        d.Season = root.TryGetProperty("season", out var s) ? s.GetInt32() : 0;
        d.Elements = ReadStrings(root, "elements");
        d.Opening = ReadStrings(root, "opening");
        d.Guests = ReadStrings(root, "guests");

        if (root.TryGetProperty("acts", out var acts))
        {
            foreach (var diff in acts.EnumerateObject())
            {
                var map = new Dictionary<int, ActData>();
                foreach (var actProp in diff.Value.EnumerateObject())
                {
                    if (!int.TryParse(actProp.Name, out int actNo)) continue;
                    var a = new ActData { Act = actNo };
                    var v = actProp.Value;
                    if (v.TryGetProperty("level", out var lv) && lv.ValueKind == JsonValueKind.Number) a.Level = lv.GetInt32();
                    if (v.TryGetProperty("boss", out var bo) && bo.ValueKind is JsonValueKind.True or JsonValueKind.False) a.Boss = bo.GetBoolean();
                    if (v.TryGetProperty("encounters", out var ens))
                    {
                        foreach (var e in ens.EnumerateArray())
                        {
                            a.Encounters.Add(new Encounter
                            {
                                Name = Str(e, "name"),
                                Res = Str(e, "res"),
                                Req = Str(e, "req"),
                                Mech = Str(e, "mech"),
                                Rec = Str(e, "rec"),
                                Chars = Str(e, "chars")
                            });
                        }
                    }
                    map[actNo] = a;
                }
                d._acts[diff.Name] = map;
            }
        }

        return d;
    }

    public ActData Get(string difficulty, int act)
    {
        if (_acts.TryGetValue(difficulty, out var m) && m.TryGetValue(act, out var a)) return a;
        // 回退：卓越 → 困难 → 普通
        foreach (var fallback in new[] { "Visionary", "Hard", "Normal", "Easy", "Lunar" })
        {
            if (_acts.TryGetValue(fallback, out var mm) && mm.TryGetValue(act, out var aa)) return aa;
        }
        return null;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string[] ReadStrings(JsonElement root, string name) =>
        root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()
            : Array.Empty<string>();
}

/// <summary>角色池（GOOD 导出）+ 资格判定，与 PowerShell 版同一套公式。</summary>
internal sealed class RosterData
{
    private readonly Dictionary<string, int> _owned = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _element = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> Alias = new()
    {
        ["kamisatoayaka"] = "ayaka",
        ["aratakiitto"] = "itto",
        ["sangonomiyakokomi"] = "kokomi",
        ["kujousara"] = "sara",
        ["shikanoinheizou"] = "heizou",
        ["kaedeharakazuha"] = "kazuha",
        ["raidenshogun"] = "raidenshogun"
    };

    private static readonly (string Name, int Min, int Level)[] Tiers =
    {
        ("轻简", 8, 60), ("普通", 12, 60), ("困难", 16, 70), ("卓越", 22, 70), ("月谕", 28, 70)
    };

    public bool Loaded { get; private set; }

    public static RosterData TryLoad(string goodPath)
    {
        var r = new RosterData();

        var charPath = Path.Combine(AppPaths.Dir, "data", "characters.json");   // 单文件 exe 下 BaseDirectory 可能是解包临时目录
        if (File.Exists(charPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(charPath));
            if (doc.RootElement.TryGetProperty("characters", out var chars))
            {
                foreach (var p in chars.EnumerateObject())
                {
                    r._element[Norm(p.Name)] = p.Value.GetString() ?? "";
                }
            }
        }

        if (File.Exists(goodPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(goodPath));
            if (doc.RootElement.TryGetProperty("characters", out var arr))
            {
                foreach (var c in arr.EnumerateArray())
                {
                    var key = Norm(c.TryGetProperty("key", out var k) ? k.GetString() : "");
                    int level = c.TryGetProperty("level", out var lv) && lv.ValueKind == JsonValueKind.Number ? lv.GetInt32() : 0;
                    if (key.Length > 0) r._owned[key] = level;
                }
                r.Loaded = true;
            }
        }

        return r;
    }

    private static string Norm(string n)
    {
        if (string.IsNullOrEmpty(n)) return "";
        var k = new string(n.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return Alias.TryGetValue(k, out var alias) ? alias : k;
    }

    public bool Owns(string name) => _owned.ContainsKey(Norm(name));

    public int CountElement(TheaterData t, string element, int minLevel)
    {
        int n = 0;
        foreach (var kv in _owned)
        {
            if (kv.Key == "traveler" || kv.Value < minLevel) continue;
            if (_element.TryGetValue(kv.Key, out var el) && string.Equals(el, element, StringComparison.OrdinalIgnoreCase)) n++;
        }
        return n;
    }

    /// <summary>返回 (可出场人数, 最高可进档位, 明细)。未加载角色池时返回 null。</summary>
    public (int Count, string Best, string Detail)? Eligibility(TheaterData t)
    {
        if (!Loaded) return null;

        int count = 0;
        int element = 0, guest = 0, openingTrial = 0;

        foreach (var kv in _owned)
        {
            if (kv.Key == "traveler") continue;
            if (kv.Value < 70) continue; // 统一按最高门槛统计"可出场"，档位差在 Good 判定里单独看

            var el = _element.TryGetValue(kv.Key, out var e) ? e : null;
            bool isGuest = t.Guests.Any(g => Norm(g) == kv.Key);
            if (isGuest) { guest++; count++; continue; }
            if (el == null) continue;
            if (!t.Elements.Contains(el, StringComparer.OrdinalIgnoreCase)) continue;
            element++; count++;
        }

        foreach (var o in t.Opening)
        {
            if (!_owned.ContainsKey(Norm(o))) { openingTrial++; count++; }
        }

        count += 1; // 旅行者
        string best = Tiers.LastOrDefault(x => count >= x.Min).Name ?? "不足轻简";
        string detail = $"元素{element}+开幕试用{openingTrial}+特邀{guest}+旅行者1";
        return (count, best, detail);
    }
}

internal sealed record Advice(string Header, List<string> Lines, string Diagnostics, int? Act = null, string Kind = null);

/// <summary>剧本决策：只看识别结果 + 第 29 期数据；不含任何游戏内操作。</summary>
internal static class TheaterLogic
{
    private static readonly Regex ActRegex = new(@"第\s*([一二三四五六七八九十0-9]{1,3})\s*幕", RegexOptions.Compiled);
    // 宽松版：只用于「底部条带」这种只剩幕次文字的区域（小字常把"幕"识别丢）
    private static readonly Regex ActLoose = new(@"第\s*([一二三四五六七八九十0-9]{1,2})", RegexOptions.Compiled);
    // 大堂/演出准备界面的专属锚点：命中即说明"这不是某一幕的进行中画面"
    private static readonly Regex HallRegex = new(@"演出准备|演出回顾|助演嘉宾|奖励一览|助演阵容", RegexOptions.Compiled);
    // 菜单/面板类界面锚点。这些界面里会出现"第十幕演出"之类的规则文案，
    // 绝不能据此判成当前第 10 幕 —— 真实截图（难度选择界面）就踩过这个坑。
    private static readonly Regex DifficultyRegex = new(@"难度选择|参演要求|角色元素类型限制|卓越模式|月谕模式", RegexOptions.Compiled);
    private static readonly Regex TeamRegex = new(@"队伍配置|开幕角色|待命角色|可选角色|特邀角色|邀请助演|编入的角色", RegexOptions.Compiled);

    private static readonly Dictionary<int, string> BossAdvice = new()
    {
        [3] = "首领·翠翎恐蕈｜1:45（星章 1:10）｜雷推怒气条（火 -34%）；满条后约 15 秒力竭 + 全抗 -25%，输出全压这个窗口",
        [6] = "首领·深黯魇语之主｜1:50（星章 1:15）｜45% 血量的护盾只吃元素/反应伤害；杀 4 个钓客各削 25%，破盾后瘫痪 10 秒 + 全抗 -70%",
        [8] = "首领·强化丘丘萨满｜1:50（星章 1:15）｜别只灭火：16 秒内用岩/钝击打碎锅（30U）；破锅 = 真伤 + 瘫痪 10 秒；失败会狂暴",
        [10] = "首领·蕴光月守宫｜1:45（星章 1:10）｜被标记时伤害 -50%、暴击 -100%，无敌帧躲开或把输出奶满解除；解除给暴击 +15%、增伤 +50%"
    };

    private const string EventAdvice = "三选一：缺人拿「伙伴」→ 输出缺口拿「辉彩祝福」→ 都不缺才拿「神秘收获」";

    public static Advice Evaluate(List<Matcher.Hit> hits, string text, TheaterData data, RosterData roster, string bandText = null)
    {
        string src = "—";
        int? act = null;
        string kind = "unknown";
        string note = "";

        foreach (var h in hits)
        {
            var m = Regex.Match(h.Name, @"^act(\d{1,2})$");
            if (m.Success) { kind = "act"; act = int.Parse(m.Groups[1].Value); src = $"模板 {h.Name}={h.Score}"; break; }
            if (h.Name == "choice") { kind = "choice"; src = $"模板 {h.Name}={h.Score}"; break; }
            if (h.Name == "team") { kind = "team"; src = $"模板 {h.Name}={h.Score}"; break; }
            if (h.Name is "diff") { kind = "difficulty"; src = $"模板 {h.Name}={h.Score}"; break; }
            if (h.Name == "result") { kind = "result"; src = $"模板 {h.Name}={h.Score}"; break; }
            // --batch 派生出来的界面锚点：都表示"在剧诗界面里，但不是某一幕的战斗画面"
            if (h.Name is "title" or "hall" or "begin" or "diff")
            {
                kind = "theater"; src = $"模板 {h.Name}={h.Score}"; break;
            }
        }

        // 先认菜单/面板界面：难度选择与队伍配置都带"参演要求/编入角色"这类文案，
        // 里面可能出现"第十幕演出"（规则说明），若不先拦会被判成当前第 10 幕（真实截图复现过）。
        if (kind == "unknown" && !string.IsNullOrEmpty(text))
        {
            if (DifficultyRegex.IsMatch(text)) { kind = "difficulty"; src = "OCR(难度面板)"; }
            else if (TeamRegex.IsMatch(text)) { kind = "team"; src = "OCR(队伍配置面板)"; }
        }

        // 大堂界面（演出准备/助演嘉宾/奖励一览…）的底部是 第一幕…第十幕 的列表，
        // 不是"当前幕次"。所以：条带里没有明确"幕"字时，只要整帧出现大堂锚点就不采信条带。
        // 注意不能反过来用大堂锚点直接压掉幕次——视频源常被 UP 贴字，幕内画面也可能出现这些词。
        bool looksHall = !string.IsNullOrEmpty(text) && HallRegex.IsMatch(text);

        // 底部条带：小字常丢"幕"字，这里用宽松规则兜底
        if (kind == "unknown" && !string.IsNullOrEmpty(bandText))
        {
            var bt = Regex.Replace(bandText, @"\s+", "");
            var strict = ActRegex.Match(bt);                 // 明确带"幕"：信
            var nums = new List<int>();
            foreach (Match bm in ActLoose.Matches(bt))
            {
                if (TryCn(bm.Groups[1].Value, out int bn) && !nums.Contains(bn)) nums.Add(bn);
            }

            if (strict.Success && TryCn(strict.Groups[1].Value, out int sn))
            {
                kind = "act"; act = sn; src = "OCR(条带)";
            }
            else if (nums.Count == 1 && !looksHall)
            {
                kind = "act"; act = nums[0]; src = "OCR(条带)";
            }
            else if (nums.Count == 1)
            {
                note = $"条带读到「{bt}」但整帧是大堂界面，判为幕次列表，不采信";
            }
            else if (nums.Count > 1)
            {
                note = $"条带里出现多个幕次({string.Join("/", nums)})，判断为幕次列表，不采信";
            }
        }

        if (kind == "unknown" && !string.IsNullOrEmpty(text))
        {
            var t = Regex.Replace(text, @"\s+", "");
            var m = ActRegex.Match(t);
            if (m.Success && TryCn(m.Groups[1].Value, out int n))
            {
                kind = "act"; act = n; src = "OCR";
            }
            else if (t.Contains("伙伴·") || t.Contains("神秘收获") || t.Contains("辉彩祝福") || t.Contains("圣牌挑战") || t.Contains("战斗·"))
            {
                kind = "choice"; src = "OCR";
            }
            else if (t.Contains("剧诗") || t.Contains("幻剧") || t.Contains("演出"))
            {
                kind = "theater"; src = "OCR";
            }
            else if (t.Contains("巡演") || t.Contains("重新演出"))
            {
                kind = "result"; src = "OCR";
            }
        }

        var lines = new List<string>();

        if (kind == "act" && act.HasValue)
        {
            string header = $"第 {act} 幕 · 来源 {src}";
            var a = data.Get("Visionary", act.Value);
            if (BossAdvice.TryGetValue(act.Value, out var bossLine)) lines.Add(bossLine);
            else lines.Add("普通关｜先保通关再争星章；主力耐力留给 3/6/8/10 幕");

            if (a != null && a.Encounters.Count > 0)
            {
                var ranked = PlanRanker.Rank(a.Encounters, roster, data);
                var top = ranked[0];
                if (!string.IsNullOrEmpty(top.Encounter.Req)) lines.Add("限时/星章: " + top.Encounter.Req);
                if (ranked.Count > 1)
                {
                    var alt = string.Join(" / ", ranked.Skip(1).Take(2).Select(r => $"{Short(r.Encounter.Name)}({r.Score})"));
                    lines.Add($"优先遇到: {Short(top.Encounter.Name)}({top.Score}，{top.Reason})｜备选 {alt}");
                }
                else
                {
                    lines.Add($"本次遭遇: {Short(top.Encounter.Name)}（{top.Reason}）");
                }
            }
            lines.Add("耐力 2 点/人；别在头两幕把开幕主力耗光");
            if (!string.IsNullOrEmpty(note)) lines.Add(note);
            return Finish(header, lines, roster, data, src, act, "act");
        }

        if (kind == "difficulty")
        {
            lines.Add("难度选择：卓越模式 = 12 场战斗演出；月谕模式 = 圣牌挑战 + 第十幕，且需 ≥28 名 70 级角色（可邀 1 名助演）");
            lines.Add("角色池不够就先打卓越模式；月谕的额外编入名额会给开幕增益，别空着");
            if (!string.IsNullOrEmpty(note)) lines.Add(note);
            return Finish("难度选择 · 来源 " + src, lines, roster, data, src, null, "difficulty");
        }

        if (kind == "team")
        {
            lines.Add("队伍配置：开幕角色先塞试用 + 自己高练度主 C；待命角色凑够 28 人才能开月谕模式");
            lines.Add("额外编入每多 1 名待命角色，开幕演出多给一份增益（4/4 时是满额）");
            if (!string.IsNullOrEmpty(note)) lines.Add(note);
            return Finish("队伍配置 · 来源 " + src, lines, roster, data, src, null, "team");
        }

        if (kind == "choice")
        {
            lines.Add(EventAdvice);
            if (!string.IsNullOrEmpty(note)) lines.Add(note);
            return Finish("三选一 · 来源 " + src, lines, roster, data, src, null, "choice");
        }

        if (kind == "theater")
        {
            lines.Add("还没进幕：先确认人数够档，再排前四幕");
            if (!string.IsNullOrEmpty(note)) lines.Add(note);
            return Finish("剧诗界面 · 来源 " + src, lines, roster, data, src, null, "theater");
        }

        if (kind == "result")
        {
            lines.Add("要重来就走回溯：回到上一个首领关结束时的状态");
            if (!string.IsNullOrEmpty(note)) lines.Add(note);
            return Finish("结算/回溯 · 来源 " + src, lines, roster, data, src, null, "result");
        }

        var hitNames = hits.Count > 0 ? string.Join(", ", hits.Select(h => $"{h.Name}={h.Score}")) : "无";
        lines.Add("没认出幕次（模板命中: " + hitName(hitNames) + "）");
        if (!string.IsNullOrEmpty(note)) lines.Add(note);
        lines.Add("Ctrl+Alt+D 存一帧，用来做锚点模板");
        return new Advice("剧诗助手 · 等待识别", lines, "act=?, src=" + src, null, "unknown");
    }

    private static string hitName(string s) => s.Length > 60 ? s[..60] + "…" : s;

    private static string Short(string name)
    {
        var i = name.LastIndexOf('·');
        return (i >= 0 ? name[(i + 1)..] : name).Trim();
    }

    private static Advice Finish(string header, List<string> lines, RosterData roster, TheaterData data, string src, int? act, string kind)
    {
        var elig = roster.Eligibility(data);
        if (elig.HasValue)
        {
            var (count, best, detail) = elig.Value;
            lines.Add($"池子 {count} 人 → 最高可进【{best}】（{detail}）");
        }

        return new Advice(header, lines, $"act={act?.ToString() ?? "?"}, src={src}", act, kind);
    }

    private static bool TryCn(string s, out int value)
    {
        value = 0;
        var map = new Dictionary<string, int>
        {
            ["一"] = 1, ["二"] = 2, ["三"] = 3, ["四"] = 4, ["五"] = 5,
            ["六"] = 6, ["七"] = 7, ["八"] = 8, ["九"] = 9, ["十"] = 10,
            ["十一"] = 11, ["十二"] = 12
        };
        if (map.TryGetValue(s, out value)) return true;
        return int.TryParse(s, out value);
    }
}
