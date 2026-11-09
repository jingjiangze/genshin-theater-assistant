using System.Text.Json;

namespace TheaterAssistant;

/// <summary>
/// 角色池解析器：统一三个来源，解决「玩家不会手填 GOOD 文件」导致池永远是空的。
///
/// 设计依据（docs/FINAL-ARCHITECTURE.md）：
///   用户明确「没有什么最优解，是需要玩家当前的角色池来判定」。
///   没有自动化的角色池，一切"个性化最优"都停留在纸面。
///
/// 三层来源（按优先级，失败自动回退下一层）：
///   ① GOOD 文件（胡桃/圣遗物扫描器导出）——最精确，含圣遗物/命座/天赋/武器
///   ② OCR 扫角色界面——离线可用，只有角色名（可配合等级）
///   ③ Enka.Network（UID 查角色展柜）——完整但需联网 + 玩家开角色展柜
///
/// 铁律：
///   · 绝不读游戏进程内存（封号红线）；
///   · 来源必须写进 Source 供上层标注，不假装数据完整；
///   · 任何来源失败都回退，不阻塞主链路。
/// </summary>
internal static class RosterResolver
{
    internal sealed class Result
    {
        public bool Ok;
        public string Source = "(无)";      // good / ocr / enka / none
        public string Detail = "";
        public bool Complete;               // 是否含强度字段（命座/天赋/武器/圣遗物）
        public string RosterPath = "";      // GOOD 落盘路径（供 CharacterStrength 复用）
        public List<string> Names = new();  // 已知角色名（中文）
    }

    /// <summary>按优先级解析角色池。goodPath 为空时尝试默认位置。</summary>
    public static Result Resolve(string goodPath = null, string ocrText = null, string uid = null)
    {
        // ① GOOD 文件（最精确）
        var p = goodPath ?? Path.Combine(AppPaths.Dir, "roster.json");
        if (File.Exists(p))
        {
            var names = ReadGoodNames(p, out var complete);
            if (names.Count > 0)
                return new Result
                {
                    Ok = true, Source = "good", RosterPath = p, Complete = complete,
                    Names = names,
                    Detail = $"GOOD {names.Count} 个角色{(complete ? "（含强度字段）" : "（仅 key/level）")}",
                };
        }

        // ② OCR 文本（离线兜底）：从已识别的界面文本里抽角色名
        if (!string.IsNullOrWhiteSpace(ocrText))
        {
            var names = MatchKnownNames(ocrText);
            if (names.Count > 0)
                return new Result
                {
                    Ok = true, Source = "ocr", Complete = false, Names = names,
                    Detail = $"OCR 命中 {names.Count} 个角色（无强度字段，置信度应降级）",
                };
        }

        // ③ Enka：需要 UID + 联网 + 玩家开角色展柜（无需登录，公开接口）。
        //    UID 不需要玩家填——悬浮窗截图里就有（界面右下角 `UID:xxxxxxxxx`），由调用方传入。
        if (!string.IsNullOrWhiteSpace(uid))
        {
            var e = EnkaClient.Fetch(uid);
            if (e.Ok)
            {
                // 落盘成 GOOD，供 CharacterStrength / TeamSolver 直接复用
                var outp = Path.Combine(AppPaths.Dir, "roster-enka.json");
                try { File.WriteAllText(outp, e.GoodJson); } catch { }
                return new Result
                {
                    Ok = true, Source = "enka", Complete = true, RosterPath = outp,
                    Names = e.Names,
                    Detail = e.Detail + (e.FromCache ? "｜缓存" : ""),
                };
            }
            return new Result { Ok = false, Source = "enka", Complete = false, Detail = e.Detail };
        }

        return new Result { Ok = false, Source = "none", Detail = "未找到任何角色池来源（GOOD/OCR/Enka 均不可用）" };
    }

    /// <summary>从 GOOD 文件读出角色中文名；同时判断是否含强度字段。</summary>
    private static List<string> ReadGoodNames(string path, out bool complete)
    {
        complete = false;
        var names = new List<string>();
        try
        {
            var zh = LoadZhNames();
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (!root.TryGetProperty("characters", out var chars) || chars.ValueKind != JsonValueKind.Array)
                return names;
            foreach (var c in chars.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.Object) continue;
                var key = c.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
                if (string.IsNullOrEmpty(key)) continue;
                // 有命座或天赋字段 → 视为含强度
                if ((c.TryGetProperty("constellation", out _)) || (c.TryGetProperty("talent", out _))) complete = true;
                names.Add(zh.TryGetValue(key, out var cn) ? cn : key);
            }
        }
        catch { }
        return names;
    }

    private static Dictionary<string, string> LoadZhNames()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var p = AppPaths.Data("characters.json");
            if (!File.Exists(p)) return map;
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            if (doc.RootElement.TryGetProperty("zhNames", out var zh) && zh.ValueKind == JsonValueKind.Object)
                foreach (var e in zh.EnumerateObject())
                    if (e.Value.ValueKind == JsonValueKind.String && e.Value.GetString() is { Length: > 0 } cn)
                        map[e.Name] = cn;
        }
        catch { }
        return map;
    }

    /// <summary>在任意文本里匹配已知角色中文名（OCR 兜底用）。</summary>
    private static List<string> MatchKnownNames(string text)
    {
        var hit = new List<string>();
        var zh = LoadZhNames().Values.Distinct().ToList();
        foreach (var cn in zh)
            if (cn.Length > 1 && text.Contains(cn, StringComparison.Ordinal) && !hit.Contains(cn))
                hit.Add(cn);
        return hit;
    }

    public static int RunCli()
    {
        Cli.Attach();
        var goodPath = GetArg("--good");
        var ocrText = GetArg("--ocr-text");
        var uid = GetArg("--uid");
        var r = Resolve(goodPath, ocrText, uid);
        Cli.Say($"来源: {r.Source}｜成功={r.Ok}｜完整={r.Complete}");
        Cli.Say($"详情: {r.Detail}");
        Cli.Say($"角色 {r.Names.Count} 个: " + string.Join("、", r.Names.Take(20)));
        return r.Ok ? 0 : 3;
    }

    private static string GetArg(string flag)
    {
        var a = Environment.GetCommandLineArgs();
        for (int i = 0; i < a.Length - 1; i++) if (a[i] == flag) return a[i + 1];
        return null;
    }
}
