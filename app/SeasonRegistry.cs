using System.Text.Json;
using System.Text.RegularExpressions;

namespace TheaterAssistant;

/// <summary>
/// 赛季注册表：**自动发现** data/season*.json，不硬编码赛季号。
///
/// 为什么要有：用户要求「之前的赛季和下一赛季都能用」。
/// 只要把新的 `data/seasonNN.json` 丢进仓库，求解器/量化/CI 全部自动认到，
/// 不需要改代码、不需要改工作流。
/// </summary>
internal static class SeasonRegistry
{
    internal sealed class SeasonInfo
    {
        public int Season;
        public string Path = "";
        public string Version = "";
        public List<string> Elements = new();
        public string StartsAt = "";
        public int ActCount;              // 各难度里最多的幕数
        public List<string> Difficulties = new();
        public bool IsCurrent;
    }

    private static List<SeasonInfo> _all = new();

    /// <summary>扫描 data 目录里所有 season*.json（文件名或内容里的数字即赛季号）。</summary>
    public static IReadOnlyList<SeasonInfo> Load()
    {
        _all = new List<SeasonInfo>();
        try
        {
            // 单文件 exe 的 data/ 在解包目录（AppPaths.Base），用户也可能把赛季文件放在 exe 同级；
            // 两处都要扫，否则 CI 里永远"无赛季"（断言「赛季·自动发现」抓到过）。
            var dirs = new[] { Path.Combine(AppPaths.Dir, "data"), Path.Combine(AppPaths.Base, "data") }
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in dirs)
            {
                foreach (var path in Directory.GetFiles(dir, "season*.json"))
                {
                    if (!seen.Add(Path.GetFileName(path))) continue;   // 同名只取一次
                    var info = TryParse(path);
                    if (info != null) _all.Add(info);
                }
            }

            var current = _all.OrderByDescending(s => s.Season).FirstOrDefault();
            if (current != null) current.IsCurrent = true;
            _all = _all.OrderByDescending(s => s.Season).ToList();
            return _all;
        }
        catch { return _all; }
    }

    private static SeasonInfo TryParse(string path)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var m = Regex.Match(name, @"season\s*(\d+)", RegexOptions.IgnoreCase);
            if (!m.Success) return null;

            var info = new SeasonInfo { Path = path };
            if (!int.TryParse(m.Groups[1].Value, out info.Season)) info.Season = 0;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("season", out var sn) && sn.ValueKind == JsonValueKind.Number)
                info.Season = sn.GetInt32();
            if (root.TryGetProperty("gameVersion", out var gv) && gv.ValueKind == JsonValueKind.String)
                info.Version = gv.GetString() ?? "";
            if (root.TryGetProperty("elements", out var el) && el.ValueKind == JsonValueKind.Array)
                foreach (var e in el.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s)
                        info.Elements.Add(s);
            if (root.TryGetProperty("startsAt", out var st) && st.ValueKind == JsonValueKind.String)
                info.StartsAt = st.GetString() ?? "";
            if (root.TryGetProperty("acts", out var acts) && acts.ValueKind == JsonValueKind.Object)
                foreach (var d in acts.EnumerateObject())
                {
                    info.Difficulties.Add(d.Name);
                    if (d.Value.ValueKind == JsonValueKind.Object)
                        info.ActCount = Math.Max(info.ActCount, d.Value.EnumerateObject().Count());
                }
            return info;
        }
        catch { return null; }
    }

    /// <summary>
    /// 赛季新鲜度：用 startsAt 与今天比较，判断这份数据是否还可能是"当前赛季"。
    ///
    /// 为什么需要：所有量化都建立在赛季数据上（元素/敌人/难度/幕数），
    /// 一旦数据过期，配队与需求向量全部失效，但表面上看不出任何错误。
    /// 这里只做**告警**，不自动改数据（改数据要靠外部抓取，不能猜）。
    /// </summary>
    internal sealed class Freshness
    {
        public int DaysSinceStart = -1;
        public string Verdict = "未知";
        public bool Stale;
        public string Detail = "";
    }

    /// <summary>35 天视为一个赛季周期上限（含缓冲）；超过即提示可能已过期。</summary>
    public const int MaxSeasonDays = 35;

    public static Freshness CheckFreshness(SeasonInfo s, DateTime? nowUtc = null)
    {
        var f = new Freshness();
        if (s == null) { f.Verdict = "无赛季数据"; f.Stale = true; return f; }
        if (string.IsNullOrWhiteSpace(s.StartsAt)) { f.Verdict = "缺 startsAt，无法判断时效"; return f; }

        if (!DateTimeOffset.TryParse(s.StartsAt, out var start))
        {
            f.Verdict = "startsAt 无法解析：" + s.StartsAt;
            return f;
        }

        var now = nowUtc.HasValue
            ? new DateTimeOffset(nowUtc.Value, TimeSpan.Zero)
            : DateTimeOffset.UtcNow;
        f.DaysSinceStart = (int)(now - start).TotalDays;
        f.Detail = $"startsAt={start:yyyy-MM-dd} 距今 {f.DaysSinceStart} 天（阈值 {MaxSeasonDays} 天）";

        if (f.DaysSinceStart < -1) { f.Verdict = "startsAt 在未来，数据可能有误"; f.Stale = true; }
        else if (f.DaysSinceStart <= MaxSeasonDays) { f.Verdict = "当前赛季（新）"; }
        else if (f.DaysSinceStart <= MaxSeasonDays * 2) { f.Verdict = "可能已过期（跨赛季？）"; f.Stale = true; }
        else { f.Verdict = "已过期（需更新赛季数据）"; f.Stale = true; }
        return f;
    }

    /// <summary>取指定赛季；0 表示当前赛季。</summary>
    public static SeasonInfo For(int season) =>
        season <= 0 ? _all.FirstOrDefault(s => s.IsCurrent) ?? _all.FirstOrDefault()
                    : _all.FirstOrDefault(s => s.Season == season);

    public static IReadOnlyList<SeasonInfo> All => _all;

    /// <summary>当前赛季的开放元素（substitutes.json 没写 seasonElements 时兜底）。</summary>
    public static IReadOnlyList<string> CurrentElements() =>
        (For(0)?.Elements ?? new List<string>());

    public static int RunCli()
    {
        Cli.Attach();
        var list = Load();
        if (list.Count == 0) { Cli.Say("没有发现 data/season*.json（赛季无关设计：丢文件即生效）"); return 2; }
        Cli.Say($"发现 {list.Count} 个赛季（自动扫描 data/season*.json，新增文件即生效，无需改代码）");
        var staleAny = false;
        foreach (var s in list)
        {
            var f = CheckFreshness(s);
            if (f.Stale) staleAny = true;
            var mark = f.Stale ? "⚠" : "✓";
            Cli.Say($"{mark} 第{s.Season}赛季{(s.IsCurrent ? "（当前）" : "")} 版本{s.Version} " +
                    $"元素{string.Join("/", s.Elements)} 难度{s.Difficulties.Count}个 最多{s.ActCount}幕 " +
                    $"起{s.StartsAt} ← {Path.GetFileName(s.Path)}");
            Cli.Say($"    时效：{f.Verdict}｜{f.Detail}");
        }
        if (staleAny)
            Cli.Say("⚠ 存在过期/可疑赛季：请更新 data/seasonNN.json（数据必须外部抓取，不可猜）");
        return 0;
    }
}
