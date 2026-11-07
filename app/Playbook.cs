using System.Text.Json;
using System.Text.RegularExpressions;

namespace TheaterAssistant;

/// <summary>
/// 幕内知识的内容源（**代码不再硬编码"每一幕该干什么"**）。
///
/// 职责边界：
///   · 识别层（模板/OCR/幕次/界面）在 Recognition / TheaterLogic，属于确定性代码；
///   · **幕内内容**（首领机制、现在做什么、本幕需要什么元素、推荐职能）全部来自
///     `data/act-playbook.json`，由语料采集 + 跨 UP 主共识 + 游戏内机制整理产出；
///   · 查不到就返回 null，调用方必须**明确说"暂无资料"**或交给大模型/语料补充，**不许编**。
///
/// 这样后续把新赛季/新视频的语料灌进来时，只需要更新数据文件，不用改代码。
/// </summary>
internal static class Playbook
{
    private sealed class ActEntry
    {
        public string Boss = "";
        public string NextAction = "";
        public string NeedElement = "";
        public string NeedWhy = "";
        public string Source = "";
    }

    private static readonly Dictionary<int, ActEntry> _acts = new();
    private static readonly Dictionary<string, string> _kinds = new(StringComparer.Ordinal);
    /// <summary>云端已验证知识：act → "推荐角色 · 作者数 · 周期"。来源 data/knowledge-release.json。</summary>
    private static readonly Dictionary<int, string> _verified = new();
    private static bool _loaded;

    public static bool Loaded => _loaded;
    public static int ActCount => _acts.Count;
    public static string Missing { get; private set; } = "暂无该幕资料（待语料或模型补充）";
    public static string DefaultNext { get; private set; } = "现在做：清怪推进第 {act} 幕；耐力优先留给 3/6/8/10 幕";

    public static void Load(string path)
    {
        _acts.Clear();
        _kinds.Clear();
        _loaded = false;
        try
        {
            if (!File.Exists(path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            if (root.TryGetProperty("missing", out var m) && m.ValueKind == JsonValueKind.String)
                Missing = m.GetString() ?? Missing;
            if (root.TryGetProperty("default_next", out var dn) && dn.ValueKind == JsonValueKind.String)
                DefaultNext = dn.GetString() ?? DefaultNext;

            if (root.TryGetProperty("kinds", out var kinds) && kinds.ValueKind == JsonValueKind.Object)
                foreach (var p in kinds.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } s)
                        _kinds[p.Name] = s;

            if (root.TryGetProperty("acts", out var acts) && acts.ValueKind == JsonValueKind.Object)
                foreach (var p in acts.EnumerateObject())
                {
                    if (!int.TryParse(p.Name, out var act)) continue;
                    var e = new ActEntry
                    {
                        Boss = Str(p.Value, "boss"),
                        NextAction = Str(p.Value, "next_action"),
                        NeedElement = Str(p.Value, "need_element"),
                        NeedWhy = Str(p.Value, "need_why"),
                        Source = Str(p.Value, "source"),
                    };
                    _acts[act] = e;
                }
            _loaded = _acts.Count > 0;
            LoadVerified(Path.Combine(Path.GetDirectoryName(path) ?? ".", "knowledge-release.json"));
        }
        catch { _loaded = false; }
    }

    /// <summary>读云端发布的知识（每幕一条「第N幕 推荐 X」），只认 status=VERIFIED 的条目。</summary>
    private static void LoadVerified(string releasePath)
    {
        _verified.Clear();
        try
        {
            if (!File.Exists(releasePath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(releasePath));
            if (!doc.RootElement.TryGetProperty("candidates", out var arr) || arr.ValueKind != JsonValueKind.Array) return;
            foreach (var c in arr.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.Object) continue;
                var status = Str(c, "status");
                if (status is { Length: > 0 } && status != "VERIFIED") continue;
                var text = Str(c, "text");
                var m = Regex.Match(text, @"第s*(d{1,2})s*幕s*推荐s*([一-鿿]{2,8})");
                if (!m.Success) continue;
                if (!int.TryParse(m.Groups[1].Value, out var act) || act < 1 || act > 12) continue;
                var creators = c.TryGetProperty("independent_creator_count", out var ic) && ic.ValueKind == JsonValueKind.Number
                    ? ic.GetInt32() : 0;
                var period = Str(c, "period");
                _verified[act] = $"{m.Groups[2].Value}（{creators} 位作者共识{(period.Length > 0 ? " · " + period : "")}）";
            }
        }
        catch { }
    }

    public static int VerifiedCount => _verified.Count;

    /// <summary>云端已验证的该幕推荐；没有返回 null。</summary>
    public static string VerifiedFor(int? act)
    {
        if (!act.HasValue || !_verified.TryGetValue(act.Value, out var v)) return null;
        return $"云端已验证：第{act}幕 推荐 {v}";
    }

    /// <summary>首领机制提示；没有返回 null（不是空串，调用方能区分"没有"与"空内容"）。</summary>
    public static string Boss(int? act)
    {
        if (!act.HasValue || !_acts.TryGetValue(act.Value, out var e)) return null;
        return string.IsNullOrWhiteSpace(e.Boss) ? null : e.Boss;
    }

    /// <summary>「现在做」：先按界面类型查，act 类型再按幕次查；都没有返回 null。</summary>
    public static string Next(string kind, int? act)
    {
        if (string.IsNullOrEmpty(kind)) return null;
        if (kind == "act")
        {
            if (act.HasValue && _acts.TryGetValue(act.Value, out var e) && !string.IsNullOrWhiteSpace(e.NextAction))
                return e.NextAction;
            // 没有该幕资料就返回 null（调用方会说"暂无该幕资料"），
            // 不用代码兜一句通用话术——那等于代码又在决定"每一幕干什么"。
            return null;
        }
        return _kinds.TryGetValue(kind, out var line) ? line : null;
    }

    /// <summary>本幕需要的元素与原因；没有返回 ("","")，调用方据此不做元素加分（不猜）。</summary>
    public static (string Element, string Why) Need(int act)
    {
        if (_acts.TryGetValue(act, out var e))
            return (e.NeedElement ?? "", e.NeedWhy ?? "");
        return ("", "");
    }

    /// <summary>该幕是否有任何资料（用于"暂无资料"提示与弃权判定）。</summary>
    public static bool HasAct(int act) => _acts.ContainsKey(act);

    public static int RunCli(string outPath)
    {
        Cli.Attach();
        Load(AppPaths.Data("act-playbook.json"));
        if (!_loaded) { Cli.Say("act-playbook.json 未加载"); return 2; }
        Cli.Say($"幕内知识 {_acts.Count} 幕 / 界面类型 {_kinds.Count} 类（来源：data/act-playbook.json）");
        foreach (var kv in _acts.OrderBy(k => k.Key))
            Cli.Say($"  第{kv.Key}幕 [{kv.Value.Source}] boss={(kv.Value.Boss.Length > 40 ? kv.Value.Boss[..40] + "…" : kv.Value.Boss)} need={kv.Value.NeedElement}");
        foreach (var kv in _kinds.OrderBy(k => k.Key, StringComparer.Ordinal))
            Cli.Say($"  {kv.Key}: {kv.Value}");
        return 0;
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";
}
