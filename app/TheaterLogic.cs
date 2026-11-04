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
                                Rec = Str(e, "rec")
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

        var charPath = Path.Combine(AppContext.BaseDirectory, "data", "characters.json");
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

internal sealed record Advice(string Header, List<string> Lines, string Diagnostics);

/// <summary>剧本决策：只看识别结果 + 第 29 期数据；不含任何游戏内操作。</summary>
internal static class TheaterLogic
{
    private static readonly Regex ActRegex = new(@"第\s*([一二三四五六七八九十0-9]{1,3})\s*幕", RegexOptions.Compiled);

    private static readonly Dictionary<int, string> BossAdvice = new()
    {
        [3] = "首领·翠翎恐蕈｜1:45（星章 1:10）｜雷推怒气条（火 -34%）；满条后约 15 秒力竭 + 全抗 -25%，输出全压这个窗口",
        [6] = "首领·深黯魇语之主｜1:50（星章 1:15）｜45% 血量的护盾只吃元素/反应伤害；杀 4 个钓客各削 25%，破盾后瘫痪 10 秒 + 全抗 -70%",
        [8] = "首领·强化丘丘萨满｜1:50（星章 1:15）｜别只灭火：16 秒内用岩/钝击打碎锅（30U）；破锅 = 真伤 + 瘫痪 10 秒；失败会狂暴",
        [10] = "首领·蕴光月守宫｜1:45（星章 1:10）｜被标记时伤害 -50%、暴击 -100%，无敌帧躲开或把输出奶满解除；解除给暴击 +15%、增伤 +50%"
    };

    private const string EventAdvice = "三选一：缺人拿「伙伴」→ 输出缺口拿「辉彩祝福」→ 都不缺才拿「神秘收获」";

    public static Advice Evaluate(List<Matcher.Hit> hits, string text, TheaterData data, RosterData roster)
    {
        string src = "—";
        int? act = null;
        string kind = "unknown";
        string note = "";

        foreach (var h in hits)
        {
            var m = Regex.Match(h.Name, @"^act(\d{1,2})$");
            if (m.Success) { kind = "act"; act = int.Parse(m.Groups[1].Value); src = $"模板 {h.Score}"; break; }
            if (h.Name == "choice") { kind = "choice"; src = $"模板 {h.Score}"; break; }
            if (h.Name == "result") { kind = "result"; src = $"模板 {h.Score}"; break; }
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
                var e = a.Encounters[0];
                if (!string.IsNullOrEmpty(e.Req)) lines.Add("限时/星章: " + e.Req);
            }
            lines.Add("耐力 2 点/人；别在头两幕把开幕主力耗光");
            return Finish(header, lines, roster, data, src, act);
        }

        if (kind == "choice")
        {
            lines.Add(EventAdvice);
            return Finish("三选一 · 来源 " + src, lines, roster, data, src, null);
        }

        if (kind == "theater")
        {
            lines.Add("还没进幕：先确认人数够档，再排前四幕");
            return Finish("剧诗界面 · 来源 " + src, lines, roster, data, src, null);
        }

        if (kind == "result")
        {
            lines.Add("要重来就走回溯：回到上一个首领关结束时的状态");
            return Finish("结算/回溯 · 来源 " + src, lines, roster, data, src, null);
        }

        var hitNames = hits.Count > 0 ? string.Join(", ", hits.Select(h => $"{h.Name}={h.Score}")) : "无";
        lines.Add("没认出幕次（模板命中: " + hitName(hitNames) + "）");
        lines.Add("Ctrl+Alt+D 存一帧，用来做锚点模板");
        return new Advice("剧诗助手 · 等待识别", lines, "act=?, src=" + src);
    }

    private static string hitName(string s) => s.Length > 60 ? s[..60] + "…" : s;

    private static Advice Finish(string header, List<string> lines, RosterData roster, TheaterData data, string src, int? act)
    {
        var elig = roster.Eligibility(data);
        if (elig.HasValue)
        {
            var (count, best, detail) = elig.Value;
            lines.Add($"池子 {count} 人 → 最高可进【{best}】（{detail}）");
        }

        return new Advice(header, lines, $"act={act?.ToString() ?? "?"}, src={src}");
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
