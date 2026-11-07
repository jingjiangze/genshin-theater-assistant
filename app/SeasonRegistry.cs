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

    /// <summary>扫描 data 目录里所有 season*.json（文件名里的数字即赛季号）。</summary>
    public static IReadOnlyList<SeasonInfo> Load()
    {
        _all = new List<SeasonInfo>();
        try
        {
            var dataDir = Path.GetDirectoryName(AppPaths.Data("season.json")) ?? AppPaths.Dir;
            if (!Directory.Exists(dataDir)) return _all;
            foreach (var f in Directory.GetFiles(dataDir, "season*.json"))
            {
                var name = Path.GetFileNameWithoutExtension(f);
                var m = Regex.Match(name, @"season\s*(\d+)", RegexOptions.IgnoreCase);
                if (!m.Success) continue;               // 跳过 season 无关文件
                var info = new SeasonInfo { Path = f };
                int.TryParse(m.Groups[1].Value, out info.Season);
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(f));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("season", out var sn) && sn.ValueKind == JsonValueKind.Number)
                        info.Season = sn.GetInt32();
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
                }
                catch { continue; }
                _all.Add(info);
            }
            // 当前赛季 = 赛季号最大的（赛季号单调递增；startsAt 仅作展示）
            var current = _all.OrderByDescending(s => s.Season).FirstOrDefault();
            if (current != null) current.IsCurrent = true;
            _all = _all.OrderByDescending(s => s.Season).ToList();
            return _all;
        }
        catch { return _all; }
    }

    /// <summary>取指定赛季；0 表示当前赛季。</summary>
    public static SeasonInfo For(int season) =>
        season <= 0 ? _all.FirstOrDefault(s => s.IsCurrent) ?? _all.FirstOrDefault()
                    : _all.FirstOrDefault(s => s.Season == season);

    public static IReadOnlyList<SeasonInfo> All => _all;

    /// <summary>把「当前赛季」的元素写回运行期（substitutes.json 若无 seasonElements 时兜底）。</summary>
    public static IReadOnlyList<string> CurrentElements() =>
        (For(0)?.Elements ?? new List<string>());

    public static int RunCli()
    {
        Cli.Attach();
        var list = Load();
        if (list.Count == 0) { Cli.Say("没有发现 data/season*.json"); return 2; }
        Cli.Say($"发现 {list.Count} 个赛季（自动扫描 data/season*.json，新增文件即生效）");
        foreach (var s in list)
            Cli.Say($"  第{s.Season}赛季{(s.IsCurrent ? "（当前）" : "")} 版本{s.Version} 元素{string.Join("/", s.Elements)} " +
                    $"难度{s.Difficulties.Count}个 最多{s.ActCount}幕 起{s.StartsAt} ← {Path.GetFileName(s.Path)}");
        return 0;
    }
}
