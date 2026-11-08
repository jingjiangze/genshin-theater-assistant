using System.Text;
using System.Text.Json;

namespace TheaterAssistant;

/// <summary>
/// 手法提示（read-only 能力）：根据当前幕次输出"手该怎么按"的步骤，**不模拟任何输入**。
///
/// 审计结论：BetterGI / MAA 那类"自动战斗"依赖注入、内存读取或模拟键鼠，
/// 与本项目的红线（只读屏幕像素）冲突，因此**不实现自动战斗**；
/// 但它内部的"技能编排"思路可以借过来，转成给玩家看的文本/语音指令。
///
/// 数据：data/rotations.json（每条都带 source）
///   --routine --act 6        打印第 6 幕手法
///   --routine               列出全部
/// </summary>
internal static class Routine
{
    internal sealed class Step
    {
        public string At = "";
        public string Do = "";
        public string Why = "";
    }

    internal sealed class ActRoutine
    {
        public string Title = "";
        public List<Step> Steps = new();
        public string Source = "";
        public string Status = "BASELINE";
        /// <summary>历史来源中的推荐角色；除非显式 VERIFIED，否则只作候选提示。</summary>
        public List<string> Recommended = new();
    }

    private static readonly Dictionary<int, ActRoutine> _acts = new();
    private static Dictionary<string, string> _sources = new(StringComparer.Ordinal);
    private static bool _loaded;

    public static bool Loaded => _loaded;
    public static int Count => _acts.Count;

    public static void Load(string path)
    {
        _acts.Clear();
        _sources = new Dictionary<string, string>(StringComparer.Ordinal);
        _loaded = false;
        try
        {
            if (!File.Exists(path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            if (root.TryGetProperty("sources", out var src))
                foreach (var p in src.EnumerateObject()) _sources[p.Name] = p.Value.GetString() ?? p.Name;

            if (!root.TryGetProperty("acts", out var acts)) return;
            foreach (var p in acts.EnumerateObject())
            {
                if (!int.TryParse(p.Name, out var act)) continue;
                var r = new ActRoutine
                {
                    Title = p.Value.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                    Source = p.Value.TryGetProperty("source", out var s) ? s.GetString() ?? "" : "",
                };
                r.Status = p.Value.TryGetProperty("status", out var statusElement)
                    ? statusElement.GetString() ?? "CANDIDATE"
                    : r.Source.Contains("inGame", StringComparison.OrdinalIgnoreCase)
                        ? "BASELINE" : "LEGACY_UNVERIFIED";
                if (p.Value.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
                    foreach (var st in steps.EnumerateArray())
                        r.Steps.Add(new Step
                        {
                            At = st.TryGetProperty("at", out var a) ? a.GetString() ?? "" : "",
                            Do = st.TryGetProperty("do", out var d) ? d.GetString() ?? "" : "",
                            Why = st.TryGetProperty("why", out var w) ? w.GetString() ?? "" : "",
                        });
                if (p.Value.TryGetProperty("recommended", out var recs) && recs.ValueKind == JsonValueKind.Array)
                    foreach (var rc in recs.EnumerateArray())
                        if (rc.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(rc.GetString()))
                            r.Recommended.Add(rc.GetString());
                _acts[act] = r;
            }
            _loaded = _acts.Count > 0;
        }
        catch { _loaded = false; }
    }

    /// <summary>取某幕的手法；没有专属就给"通用(0)"；都没有返回空列表。</summary>
    internal static List<string> LinesFor(int? act, int max = 3)
    {
        if (!_loaded) return new List<string>();
        ActRoutine r = null;
        if (act.HasValue && _acts.TryGetValue(act.Value, out var exact)) r = exact;
        else if (_acts.TryGetValue(0, out var generic)) r = generic;
        if (r == null || r.Steps.Count == 0) return new List<string>();

        var outLines = new List<string>();

        // 旧版社区条目按视频数计数，未验证作者独立性；只显示为历史候选。
        if (r.Recommended.Count > 0)
        {
            var label = r.Status == "VERIFIED" ? "已验证推荐" : "历史候选（未独立验证）";
            outLines.Add($"选人·第 {act} 幕参考：{string.Join("、", r.Recommended.Take(3))}（{label}）");
        }

        foreach (var s in r.Steps.Take(max))
        {
            var at = string.IsNullOrEmpty(s.At) ? "" : s.At + " ";
            var prefix = r.Status == "LEGACY_UNVERIFIED" ? "历史候选手法·" : "手法·";
            var line = $"{prefix}{at}{s.Do}";
            if (!string.IsNullOrEmpty(s.Why)) line += $"（{s.Why}）";
            outLines.Add(line);
        }
        return outLines;
    }

    internal static string SourceLabel(string key) =>
        _sources.TryGetValue(key ?? "", out var v) ? v : (key ?? "");

    // ---------------- CLI ----------------

    public static int RunCli(int? act)
    {
        Cli.Attach();
        Load(AppPaths.Data("rotations.json"));
        if (!_loaded) { Cli.Say("手法数据没加载成功：data/rotations.json"); return 2; }

        if (act.HasValue)
        {
            var lines = LinesFor(act);
            if (lines.Count == 0) { Cli.Say($"第 {act} 幕没有专属手法，也没通用条目"); return 3; }
            var r = _acts.TryGetValue(act.Value, out var exact) ? exact : _acts[0];
            Cli.Say($"▶ {r.Title}");
            foreach (var l in lines) Cli.Say("  " + l);
            Cli.Say("  出处：" + SourceLabel(r.Source));
            return 0;
        }

        Cli.Say($"手法数据 {_acts.Count} 条：");
        foreach (var kv in _acts.OrderBy(k => k.Key))
        {
            Cli.Say($"  第 {kv.Key} 幕：{kv.Value.Title}（{kv.Value.Steps.Count} 步，出处 {SourceLabel(kv.Value.Source)}）");
            foreach (var s in kv.Value.Steps) Cli.Say($"      {s.At} {s.Do}");
        }
        return 0;
    }
}
