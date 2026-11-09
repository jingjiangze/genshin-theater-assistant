using System.Text.Json;
using System.Text.Json.Nodes;

namespace TheaterAssistant;

/// <summary>
/// 角色池合并：把「本地 GOOD（可能很全但没有强度字段）」与「Enka（只有展柜角色但强度字段齐全）」合并。
///
/// 用户要求「本地 GOOD 导出 + 后台异步补全的双轨」，这里就是双轨的汇合点。
/// 不能简单替换：Enka 只返回玩家**放进角色展柜**的角色（实测 12 个），
/// 而本地 GOOD 可能是全角色（50+）。用 Enka 覆盖 GOOD 会让池**变小**，属于倒退。
///
/// 合并规则：
///   1. 角色集合取**并集**（GOOD 有的都保留）；
///   2. 同一角色：强度字段（命座/天赋/武器/圣遗物）优先取**有值的一方**（Enka 通常更准，因为实时）；
///   3. 输出单一 GOOD 文件，供 CharacterStrength / TeamSolver 直接消费；
///   4. 记录来源，供悬浮窗标注（玩家要能看出"这个池是怎么来的"）。
/// </summary>
internal static class RosterMerge
{
    internal sealed class Outcome
    {
        public bool Ok;
        public string Path = "";
        public int GoodCount, EnkaCount, MergedCount;
        public string Detail = "";
    }

    /// <summary>合并 GOOD 与 Enka 结果；任一方缺失时直接返回另一方。</summary>
    public static Outcome Merge(string goodPath, string enkaPath, string outPath)
    {
        var o = new Outcome();
        bool hasGood = !string.IsNullOrEmpty(goodPath) && File.Exists(goodPath);
        bool hasEnka = !string.IsNullOrEmpty(enkaPath) && File.Exists(enkaPath);

        if (!hasGood && !hasEnka) { o.Detail = "两方都不存在"; return o; }
        if (hasGood && !hasEnka)
        {
            o.Ok = true; o.Path = goodPath; o.GoodCount = CountChars(goodPath);
            o.MergedCount = o.GoodCount; o.Detail = "仅 GOOD（无 Enka 数据）";
            return o;
        }
        if (!hasGood && hasEnka)
        {
            o.Ok = true; o.Path = enkaPath; o.EnkaCount = CountChars(enkaPath);
            o.MergedCount = o.EnkaCount; o.Detail = "仅 Enka（无本地 GOOD）";
            return o;
        }

        try
        {
            var good = JsonNode.Parse(File.ReadAllText(goodPath)) as JsonObject;
            var enka = JsonNode.Parse(File.ReadAllText(enkaPath)) as JsonObject;
            if (good == null || enka == null) { o.Detail = "解析失败"; return o; }

            var merged = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);

            // ① GOOD 的角色先入场
            foreach (var c in Arr(good, "characters"))
            {
                var key = Str(c, "key");
                if (key.Length == 0) continue;
                ((JsonObject)c).Remove("_src");
                ((JsonObject)c)["_src"] = "good";
                merged[key] = (JsonObject)c;
            }
            o.GoodCount = merged.Count;

            // ② Enka 的角色：并集 + 用有值的强度字段补/覆盖
            int enriched = 0, added = 0;
            foreach (var c in Arr(enka, "characters"))
            {
                var key = Str(c, "key");
                if (key.Length == 0) continue;
                if (merged.TryGetValue(key, out var exist))
                {
                    bool changed = false;
                    foreach (var field in new[] { "constellation", "talent", "level" })
                    {
                        var nv = (c as JsonObject)?[field];
                        var ov = exist[field];
                        bool nHas = nv != null && !(nv is JsonValue jv0 && jv0.TryGetValue<double>(out var d0) && d0 <= 0);
                        bool oHas = ov != null && !(ov is JsonValue jv1 && jv1.TryGetValue<double>(out var d1) && d1 <= 0);
                        if (nHas && !oHas) { exist[field] = nv?.DeepClone(); changed = true; }
                    }
                    if (changed) { exist["_src"] = "good+enka"; enriched++; }
                }
                else
                {
                    ((JsonObject)c).Remove("_src");
                    ((JsonObject)c)["_src"] = "enka";
                    merged[key] = (JsonObject)c;
                    added++;
                }
            }
            o.EnkaCount = Arr(enka, "characters").Count;

            // ③ 武器/圣遗物：以 key+location 去重合并（Enka 的按角色覆盖 GOOD 的同角色条目）
            var weapons = MergeByOwner(Arr(good, "weapons"), Arr(enka, "weapons"));
            var arts = MergeByOwner(Arr(good, "artifacts"), Arr(enka, "artifacts"));

            var outObj = new JsonObject
            {
                ["format"] = "GOOD",
                ["version"] = 1,
                ["source"] = "merged(good+enka)",
                ["_mergedAt"] = DateTime.UtcNow.ToString("o"),
                ["_note"] = "角色集合取并集；强度字段优先取有值的一方（Enka 实时更准）；武器/圣遗物按角色去重。",
                // 必须 DeepClone：JsonNode 已有父节点时再挂载会抛
                // InvalidOperationException（实测踩过）。这里 nodes 来自已解析的旧树。
                ["characters"] = new JsonArray(merged.Values.Select(v => (JsonNode)v.DeepClone()).ToArray()),
                ["weapons"] = new JsonArray(weapons.Select(v => (JsonNode)v.DeepClone()).ToArray()),
                ["artifacts"] = new JsonArray(arts.Select(v => (JsonNode)v.DeepClone()).ToArray()),
            };
            File.WriteAllText(outPath, outObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            o.Ok = true; o.Path = outPath; o.MergedCount = merged.Count;
            o.Detail = $"GOOD {o.GoodCount} + Enka {o.EnkaCount} → 合并 {o.MergedCount}（补强 {enriched}、新增 {added}）";
            return o;
        }
        catch (Exception ex)
        {
            o.Detail = "合并失败：" + ex.GetType().Name;
            return o;
        }
    }

    private static List<JsonNode> MergeByOwner(List<JsonNode> good, List<JsonNode> enka)
    {
        var map = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var x in good)
        {
            var owner = Str(x, "location");
            var k = Str(x, "key");
            map[$"{owner}|{k}|{Str(x, "slotKey")}"] = x;
        }
        // Enka 的同角色装备优先（实时）
        foreach (var x in enka)
        {
            var owner = Str(x, "location");
            var k = Str(x, "key");
            map[$"{owner}|{k}|{Str(x, "slotKey")}"] = x;
        }
        return map.Values.ToList();
    }

    private static List<JsonNode> Arr(JsonObject o, string name)
    {
        if (o.TryGetPropertyValue(name, out var v) && v is JsonArray a)
            return a.Where(x => x is JsonObject).Select(x => (JsonNode)x!).ToList();
        return new List<JsonNode>();
    }

    private static string Str(JsonNode n, string field)
    {
        if (n is JsonObject o && o.TryGetPropertyValue(field, out var v) && v is JsonValue jv && jv.TryGetValue<string>(out var s))
            return s ?? "";
        return "";
    }

    private static int CountChars(string path)
    {
        try
        {
            var o = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            return o == null ? 0 : Arr(o, "characters").Count;
        }
        catch { return 0; }
    }

    public static int RunCli(string goodPath, string enkaPath, string outPath)
    {
        Cli.Attach();
        var r = Merge(goodPath, enkaPath, outPath);
        Cli.Say($"合并: 成功={r.Ok}｜{r.Detail}");
        if (r.Ok) Cli.Say("输出: " + r.Path);
        return r.Ok ? 0 : 3;
    }
}
