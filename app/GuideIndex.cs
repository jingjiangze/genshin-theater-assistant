using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TheaterAssistant;

/// <summary>
/// 离线攻略检索（零依赖、可解释）：BM25 over 中文字符 bigram。
///
/// 为什么不用向量模型：在 data/guide-corpus.json（23 条中文攻略）上实测
/// BM25 + 幕次加权 hit@1 = 17/17、hit@3 = 17/17（tools/audit-retrieval.mjs 可复跑）；
/// 而 bge-small-zh ONNX 约 95MB，会把 100MB 的 exe 体积直接翻倍，收益为零。
/// 语料涨到几百条以后再考虑加检索重排。
///
/// 每条结果都带出处（BV 号 / 游戏内 / BWIKI / 本项目整理），UI 必须一起显示，避免"看起来像攻略"。
/// </summary>
internal static class GuideIndex
{
    internal sealed class Entry
    {
        public string Id = "";
        public int Act;
        public string Title = "";
        public string Text = "";
        public string Source = "";
        public string[] Tags = Array.Empty<string>();
    }

    internal sealed class Hit
    {
        public Entry Entry;
        public double Score;
        public string SourceLabel = "";
        public override string ToString() => $"{Entry?.Title}（{Score:0.00}，{SourceLabel}）";
    }

    private static readonly Regex AsciiWord = new(@"[a-z0-9]+", RegexOptions.Compiled);
    private static readonly Regex CjkSeg = new(@"[\u4e00-\u9fff]+", RegexOptions.Compiled);
    private static readonly Regex ActNo = new(@"第\s*(\d{1,2})\s*幕", RegexOptions.Compiled);

    private static List<Entry> _docs = new();
    private static Dictionary<string, string> _sources = new(StringComparer.Ordinal);
    private static List<List<string>> _tokens = new();
    private static Dictionary<string, int> _df = new(StringComparer.Ordinal);
    private static double _avgLen = 1;
    private static bool _loaded;

    public static bool Loaded => _loaded;
    public static int Count => _docs.Count;

    /// <summary>BM25 参数与实验脚本一致（k1=1.5 / b=0.75）。</summary>
    private const double K1 = 1.5, B = 0.75;

    /// <summary>加载主语料；若同目录存在 guide-corpus-extra.json（--corpus-scan 产物）则一并合并。</summary>
    public static void LoadWithExtra(string path)
    {
        Load(path);
        var extra = Path.Combine(Path.GetDirectoryName(path) ?? ".", "guide-corpus-extra.json");
        if (File.Exists(extra)) Append(extra);
    }

    /// <summary>把另一个同格式语料追加进索引（按 id 去重）。</summary>
    public static void Append(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("entries", out var arr)) return;

            var have = new HashSet<string>(_docs.Select(d => d.Id), StringComparer.Ordinal);
            int added = 0;
            foreach (var e in arr.EnumerateArray())
            {
                var entry = new Entry
                {
                    Id = Str(e, "id"), Title = Str(e, "title"), Text = Str(e, "text"), Source = Str(e, "source"),
                    Act = e.TryGetProperty("act", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt32() : 0,
                    Tags = e.TryGetProperty("tags", out var tg) && tg.ValueKind == JsonValueKind.Array
                        ? tg.EnumerateArray().Select(t => t.GetString() ?? "").ToArray() : Array.Empty<string>(),
                };
                if (string.IsNullOrWhiteSpace(entry.Id) || !have.Add(entry.Id)) continue;
                _docs.Add(entry);
                _tokens.Add(Tokenize($"{entry.Title} {entry.Text} {string.Join(" ", entry.Tags)} 第{entry.Act}幕"));
                added++;
            }
            if (added == 0) return;
            Reindex();
            _loaded = _docs.Count > 0;
        }
        catch { }
    }

    private static void Reindex()
    {
        _df = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var toks in _tokens)
            foreach (var t in toks.Distinct())
                _df[t] = _df.TryGetValue(t, out var c) ? c + 1 : 1;
        _avgLen = _tokens.Count == 0 ? 1 : _tokens.Average(t => (double)t.Count);
    }

    public static void Load(string path)
    {
        _docs = new List<Entry>();
        _sources = new Dictionary<string, string>(StringComparer.Ordinal);
        _tokens = new List<List<string>>();
        _df = new Dictionary<string, int>(StringComparer.Ordinal);
        _avgLen = 1;
        _loaded = false;

        try
        {
            if (!File.Exists(path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            if (root.TryGetProperty("sources", out var srcs))
                foreach (var p in srcs.EnumerateObject())
                    _sources[p.Name] = p.Value.GetString() ?? p.Name;

            if (!root.TryGetProperty("entries", out var arr)) return;
            foreach (var e in arr.EnumerateArray())
            {
                var entry = new Entry
                {
                    Id = Str(e, "id"),
                    Title = Str(e, "title"),
                    Text = Str(e, "text"),
                    Source = Str(e, "source"),
                    Act = e.TryGetProperty("act", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt32() : 0,
                    Tags = e.TryGetProperty("tags", out var tg) && tg.ValueKind == JsonValueKind.Array
                        ? tg.EnumerateArray().Select(t => t.GetString() ?? "").ToArray()
                        : Array.Empty<string>(),
                };
                _docs.Add(entry);
                _tokens.Add(Tokenize($"{entry.Title} {entry.Text} {string.Join(" ", entry.Tags)} 第{entry.Act}幕"));
            }

            foreach (var toks in _tokens)
                foreach (var t in toks.Distinct())
                    _df[t] = _df.TryGetValue(t, out var c) ? c + 1 : 1;

            _avgLen = _tokens.Count == 0 ? 1 : _tokens.Average(t => (double)t.Count);
            _loaded = _docs.Count > 0;
        }
        catch { _loaded = false; }
    }

    /// <summary>分词：ASCII 词 + 中文单字 + 中文 bigram（中文短查询靠 bigram 才稳）。</summary>
    internal static List<string> Tokenize(string s)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(s)) return list;
        var text = s.ToLowerInvariant();
        foreach (Match m in AsciiWord.Matches(text)) list.Add(m.Value);
        foreach (Match seg in CjkSeg.Matches(text))
        {
            var v = seg.Value;
            for (int i = 0; i < v.Length; i++)
            {
                list.Add(v[i].ToString());
                if (i + 1 < v.Length) list.Add(v.Substring(i, 2));
            }
        }
        return list;
    }

    /// <summary>
    /// 检索。act 非空时对同幕条目加权（运行时我们本来就知道当前第几幕）。
    /// 查询里写"第N幕"也会自动解析成 act。
    /// </summary>
    public static List<Hit> Search(string query, int? act = null, int top = 3, double minScore = 0.01)
    {
        var result = new List<Hit>();
        if (!_loaded || _docs.Count == 0 || string.IsNullOrWhiteSpace(query)) return result;

        int? actCtx = act;
        if (!actCtx.HasValue)
        {
            var m = ActNo.Match(query);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var an)) actCtx = an;
        }

        var qTokens = Tokenize(query).Distinct().ToList();
        int n = _docs.Count;

        for (int i = 0; i < _docs.Count; i++)
        {
            var toks = _tokens[i];
            if (toks.Count == 0) continue;

            var tf = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var t in toks) tf[t] = tf.TryGetValue(t, out var c) ? c + 1 : 1;

            double score = 0;
            foreach (var t in qTokens)
            {
                if (!tf.TryGetValue(t, out var f) || f == 0) continue;
                int dft = _df.TryGetValue(t, out var d) ? d : 0;
                double idf = Math.Log(1 + (n - dft + 0.5) / (dft + 0.5));
                score += idf * (f * (K1 + 1)) / (f + K1 * (1 - B + B * toks.Count / _avgLen));
            }
            if (score <= 0) continue;

            if (actCtx.HasValue && _docs[i].Act == actCtx.Value) score *= 2.2;

            result.Add(new Hit
            {
                Entry = _docs[i],
                Score = Math.Round(score, 4),
                SourceLabel = _sources.TryGetValue(_docs[i].Source, out var lab) ? lab : _docs[i].Source,
            });
        }

        return result.Where(h => h.Score >= minScore)
                     .OrderByDescending(h => h.Score)
                     .Take(Math.Max(1, top))
                     .ToList();
    }

    /// <summary>把检索结果压成悬浮窗能显示的一两行（带出处）。</summary>
    public static List<string> ToLines(IEnumerable<Hit> hits, int max = 2, int textLen = 46)
    {
        var lines = new List<string>();
        foreach (var h in hits.Take(max))
        {
            var t = h.Entry.Text.Replace("\n", " ").Trim();
            if (t.Length > textLen) t = t[..textLen] + "…";
            lines.Add($"攻略·{h.Entry.Title}：{t}［{h.Entry.Source}］");
        }
        return lines;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // ---------------- CLI：--advise ----------------

    public static int RunCli(string query, int? act, int top)
    {
        Cli.Attach();
        Load(AppPaths.Data("guide-corpus.json"));
        if (!_loaded) { Cli.Say("语料没加载成功：data/guide-corpus.json"); return 2; }
        if (string.IsNullOrWhiteSpace(query) && !act.HasValue) { Cli.Say("用法：--advise \"问题\" [--act N] [--top 3]"); return 2; }
        if (string.IsNullOrWhiteSpace(query)) query = $"第{act}幕 打法";

        var hits = Search(query, act, top);
        Cli.Say($"查询：{query}（act={act?.ToString() ?? "自动"}）｜语料 {_docs.Count} 条 → 命中 {hits.Count}");
        if (hits.Count == 0) { Cli.Say("没有相关条目"); return 3; }
        foreach (var h in hits)
        {
            Cli.Say("");
            Cli.Say($"[{h.Score:0.00}] {h.Entry.Title}  （act={h.Entry.Act}）");
            Cli.Say("    " + h.Entry.Text);
            Cli.Say("    出处：" + h.SourceLabel);
        }
        return 0;
    }
}
