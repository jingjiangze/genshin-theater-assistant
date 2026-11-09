using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TheaterAssistant;

/// <summary>
/// 一个玩法（game mode）的档案：识别区域、时间轴解析、数据命名空间。
///
/// 为什么要有这层：本项目现在是「幻想真境剧诗」专用，但同一套链路
/// （抓屏 → 模板 → OCR → 时序稳定 → 决策 → 遮罩）对**深渊/其他高难**是通用的，
/// 差别只在「在哪读、读出来的东西怎么编号、用哪份数据」。
/// 把这些差异收进档案，识别与决策代码就不用为每个玩法分枝。
///
/// 全部数值来自 data/modes.json（不硬编码），加载失败时回落到内置的剧诗默认值。
/// </summary>
public sealed class GameModeProfile
{
    public string Id { get; set; } = "theater";
    public string DisplayName { get; set; } = "幻想真境剧诗";
    public string ShortName { get; set; } = "剧诗";
    public bool Enabled { get; set; } = true;
    public string Note { get; set; } = "";

    /// <summary>幕次条带 ROI（帧比例）。Width/Height 为 0 表示该玩法没有条带。</summary>
    public double BandLeft { get; set; } = 0.28;
    public double BandTop { get; set; } = 0.828;
    public double BandWidth { get; set; } = 0.46;
    public double BandHeight { get; set; } = 0.05;

    /// <summary>时间轴编号的正则（剧诗=第N幕；深渊=第N层）。第 1 个捕获组必须是编号。</summary>
    public string ActRegex { get; set; } = @"第\s*(\d{1,2})\s*幕";

    /// <summary>该玩法专属的数据文件（相对 data/），用于提示缺什么，不做强制。</summary>
    public string[] DataFiles { get; set; } = Array.Empty<string>();

    public bool HasBand => BandWidth > 0 && BandHeight > 0;

    private Regex _actRegexCache;

    /// <summary>从一段文字里取时间轴编号（幕/层）。认阿拉伯数字与「一二三…十二」。</summary>
    public int? ParseAct(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        _actRegexCache ??= new Regex(ActRegex, RegexOptions.CultureInvariant);
        var m = _actRegexCache.Match(text);
        if (!m.Success || m.Groups.Count < 2) return null;

        var raw = m.Groups[1].Value.Trim();
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n;
        return ParseChineseNumber(raw);
    }

    /// <summary>「一…十、十一、十二」→ 数字（剧诗上限 12 幕，不做到百）。</summary>
    internal static int? ParseChineseNumber(string t)
    {
        if (string.IsNullOrEmpty(t)) return null;

        var map = new Dictionary<char, int>
        {
            ['一'] = 1, ['二'] = 2, ['三'] = 3, ['四'] = 4, ['五'] = 5,
            ['六'] = 6, ['七'] = 7, ['八'] = 8, ['九'] = 9, ['十'] = 10,
        };

        if (t.Length == 1) return map.TryGetValue(t[0], out var v) ? v : null;
        if (t[0] == '十') return map.TryGetValue(t[1], out var u) ? 10 + u : null;
        if (t.Length == 2 && t[1] == '十') return map.TryGetValue(t[0], out var w) ? w * 10 : null;
        return null;
    }
}

/// <summary>玩法档案仓库：从 data/modes.json 读，失败回落内置剧诗。</summary>
public static class GameModes
{
    public const string FileName = "modes.json";

    public static List<GameModeProfile> All { get; } = new();
    public static GameModeProfile Default { get; private set; } = BuiltinTheater();
    public static string Status { get; private set; } = "内置默认（未读 data/modes.json）";

    private static int _loaded;

    /// <summary>幂等；App 启动时调一次即可，其余地方直接用 Default/Get。</summary>
    public static void Init(string dataDir = null)
    {
        if (_loaded > 0) return;
        _loaded = 1;

        try
        {
            var path = dataDir != null ? Path.Combine(dataDir, FileName) : AppPaths.Data(FileName);
            if (!File.Exists(path))
            {
                Status = $"缺 {FileName}，用内置剧诗档案";
                All.Add(Default);
                return;
            }

            LoadFromFile(path);
        }
        catch (Exception ex)
        {
            Status = "加载失败：" + ex.GetType().Name;
            All.Clear();
            All.Add(Default);
        }
    }

    /// <summary>供自检/测试直接喂一个文件。</summary>
    public static void LoadFromFile(string path)
    {
        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);

        All.Clear();
        var wantedDefault = doc.RootElement.TryGetProperty("default", out var d) ? d.GetString() : null;

        if (doc.RootElement.TryGetProperty("modes", out var modes) && modes.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in modes.EnumerateArray())
            {
                var p = Parse(e);
                if (p != null) All.Add(p);
            }
        }

        if (All.Count == 0)
        {
            All.Add(BuiltinTheater());
            Status = "modes.json 无有效条目，用内置剧诗档案";
            return;
        }

        Default = (!string.IsNullOrEmpty(wantedDefault) ? Get(wantedDefault) : null) ?? All[0];
        if (!Default.Enabled && All.Exists(x => x.Enabled)) Default = All.Find(x => x.Enabled);
        Status = $"已加载 {All.Count} 个玩法，默认 {Default.Id}";
    }

    private static GameModeProfile Parse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;

        string Str(string name, string fallback)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : fallback;

        double Num(string name, double fallback)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;

        bool Bool(string name, bool fallback)
            => e.TryGetProperty(name, out var v) && (v.ValueKind is JsonValueKind.True or JsonValueKind.False) ? v.GetBoolean() : fallback;

        var id = Str("id", "");
        if (string.IsNullOrWhiteSpace(id)) return null;

        var band = new double[4];
        if (e.TryGetProperty("band", out var b) && b.ValueKind == JsonValueKind.Array && b.GetArrayLength() == 4)
        {
            var i = 0;
            foreach (var n in b.EnumerateArray()) band[i++] = n.GetDouble();
        }
        else
        {
            band = new[] { 0.28, 0.828, 0.46, 0.05 };
        }

        var files = Array.Empty<string>();
        if (e.TryGetProperty("dataFiles", out var df) && df.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var s in df.EnumerateArray())
                if (s.ValueKind == JsonValueKind.String) list.Add(s.GetString());
            files = list.ToArray();
        }

        return new GameModeProfile
        {
            Id = id,
            DisplayName = Str("displayName", id),
            ShortName = Str("shortName", id),
            Enabled = Bool("enabled", true),
            Note = Str("note", ""),
            BandLeft = band[0],
            BandTop = band[1],
            BandWidth = band[2],
            BandHeight = band[3],
            ActRegex = Str("actRegex", @"第\s*(\d{1,2})\s*幕"),
            DataFiles = files,
        };
    }

    public static GameModeProfile Get(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var m in All)
            if (string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)) return m;
        return null;
    }

    /// <summary>内置剧诗档案：数值与改造前 MainLoop 里的常量逐项一致（回归基准）。</summary>
    public static GameModeProfile BuiltinTheater() => new()
    {
        Id = "theater",
        DisplayName = "幻想真境剧诗",
        ShortName = "剧诗",
        Enabled = true,
        BandLeft = 0.28,
        BandTop = 0.828,
        BandWidth = 0.46,
        BandHeight = 0.05,
        ActRegex = @"第\s*(\d{1,2})\s*幕",
        DataFiles = new[] { "act-playbook.json", "act-teams.json", "season29.json" },
    };
}
