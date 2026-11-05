using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TheaterAssistant;

/// <summary>
/// 从**本机游戏日志**里读角色数据（仅角色，不碰其他账号信息）。
///
/// 审计结论（本机实测）：
///   · 游戏装在注册表 `InstallLocation`（本机：C:\DDDD\game\Genshin Impact bilibili，b服 7.1）；
///   · 日志候选两个位置：`%LOCALAPPDATA%Low\miHoYo\YuanShen\Player.log`、
///     `<安装目录>\...\YuanShen_Data\output_log.txt`；
///   · **本机当前没有任何日志**（游戏未被启动过/日志被清理）→ 只能先把链路做通，
///     等用户启动一次游戏后再验证（PENDING）。
///
/// 红线：
///   · 只读日志文件，不注入、不读进程内存、不联网；
///   · 只提取"角色 ID + 等级"这类字段，其余内容一律丢弃、不入库、不上传；
///   · 输出写在本机 roster.json，带 source=local-log 便于追溯。
/// </summary>
internal static class LogScan
{
    private static readonly string[] Candidates =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Low", "miHoYo", "YuanShen", "Player.log"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Low", "miHoYo", "原神", "Player.log"),
    };

    private static readonly Regex AvatarIdRe = new(@"""avatarId""\s*:\s*(\d{6,12})", RegexOptions.Compiled);
    private static readonly Regex LevelPairRe = new(@"""avatarId""\s*:\s*(\d{6,12})[^}]{0,400}?""level""\s*:\s*(\d{1,2})", RegexOptions.Compiled);

    /// <summary>从注册表找安装目录（只读）。</summary>
    internal static string FindInstallDir()
    {
        try
        {
            using var hklm = Microsoft.Win32.Registry.LocalMachine;
            foreach (var sub in new[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            })
            {
                using var root = hklm.OpenSubKey(sub);
                if (root == null) continue;
                foreach (var name in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(name);
                    var dn = k?.GetValue("DisplayName") as string;
                    if (string.IsNullOrEmpty(dn) || !(dn.Contains("原神") || dn.Contains("Genshin", StringComparison.OrdinalIgnoreCase))) continue;
                    var loc = k.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrWhiteSpace(loc) && Directory.Exists(loc)) return loc;
                }
            }
        }
        catch { }
        return null;
    }

    internal static List<string> LogCandidates()
    {
        var list = new List<string>(Candidates.Where(File.Exists));
        var inst = FindInstallDir();
        if (!string.IsNullOrEmpty(inst))
        {
            foreach (var rel in new[] { @"games\Genshin Impact Game\YuanShen_Data\output_log.txt", @"Genshin Impact Game\YuanShen_Data\output_log.txt", @"YuanShen_Data\output_log.txt" })
            {
                var p = Path.Combine(inst, rel);
                if (File.Exists(p)) list.Add(p);
            }
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>纯函数：从日志文本里抽 (角色ID, 等级) 对；同名取最大值。可断言。</summary>
    internal static Dictionary<long, int> ParseCharacters(string logText)
    {
        var map = new Dictionary<long, int>();
        if (string.IsNullOrEmpty(logText)) return map;

        foreach (Match m in LevelPairRe.Matches(logText))
        {
            if (!long.TryParse(m.Groups[1].Value, out var id)) continue;
            if (!int.TryParse(m.Groups[2].Value, out var lv)) continue;
            if (lv < 1 || lv > 100) continue;
            map[id] = map.TryGetValue(id, out var old) ? Math.Max(old, lv) : lv;
        }

        // 只有 ID 没有等级的情况：保留（等级 0 表示未知）
        foreach (Match m in AvatarIdRe.Matches(logText))
        {
            if (!long.TryParse(m.Groups[1].Value, out var id)) continue;
            if (!map.ContainsKey(id)) map[id] = 0;
        }
        return map;
    }

    public static int Run(string explicitLog, string outPath)
    {
        Cli.Attach();
        var logs = string.IsNullOrWhiteSpace(explicitLog) ? LogCandidates() : new List<string> { explicitLog };

        var inst = FindInstallDir();
        Cli.Say("安装目录: " + (inst ?? "<未从注册表读到>"));

        if (logs.Count == 0)
        {
            Cli.Say("没找到游戏日志。候选位置：");
            foreach (var c in Candidates) Cli.Say("  " + c);
            if (!string.IsNullOrEmpty(inst)) Cli.Say("  " + Path.Combine(inst, @"games\Genshin Impact Game\YuanShen_Data\output_log.txt"));
            Cli.Say("→ 启动一次游戏（进到主界面即可）后再跑本命令；本工具只读取该文件，不注入、不联网。");
            return 4;   // 4 = 环境不满足（可跳过）
        }

        outPath = string.IsNullOrWhiteSpace(outPath) ? Path.Combine(AppPaths.Dir, "roster.json") : Path.GetFullPath(outPath);
        var idMap = LoadIdTable();

        var all = new Dictionary<long, int>();
        foreach (var log in logs)
        {
            try
            {
                var text = File.ReadAllText(log);
                Cli.Say($"读取 {log}（{text.Length / 1024} KB）");
                foreach (var kv in ParseCharacters(text))
                    all[kv.Key] = all.TryGetValue(kv.Key, out var old) ? Math.Max(old, kv.Value) : kv.Value;
            }
            catch (Exception ex) { Cli.Say("读取失败: " + ex.Message); }
        }

        if (all.Count == 0)
        {
            Cli.Say("日志里没有角色字段（avatarId）。可能原因：本次启动没拉取角色数据（进到角色界面/祈愿记录再看一次）。");
            return 3;
        }

        var rows = all.OrderBy(kv => kv.Key).Select(kv => new
        {
            id = kv.Key,
            name = idMap.TryGetValue(kv.Key, out var n) ? n : "",
            level = kv.Value,
        }).ToList();

        Cli.Say($"识别到角色 {rows.Count} 个（ID 表命中 {rows.Count(r => r.name.Length > 0)} 个）");
        foreach (var r in rows.Take(10)) Cli.Say($"  {r.id}  {(r.name == "" ? "<无ID表>" : r.name)}  Lv.{r.level}");

        var payload = new
        {
            source = "local-log",
            note = "从本机游戏日志提取的角色 ID/等级；只读日志、只保留角色字段、不上传。ID→名字依赖 data/characters-id.json。",
            generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            characters = rows.Select(r => new { key = r.name == "" ? r.id.ToString() : r.name, name = r.name, id = r.id, level = r.level }).ToList(),
        };
        try
        {
            File.WriteAllText(outPath, JsonSerializer.Serialize(payload, TemplateFactory.JsonOpts), Encoding.UTF8);
            Cli.Say("已写: " + outPath);
        }
        catch (Exception ex) { Cli.Say("写 roster 失败: " + ex.Message); return 1; }

        return 0;
    }

    private static Dictionary<long, string> LoadIdTable()
    {
        var map = new Dictionary<long, string>();
        try
        {
            var p = AppPaths.Data("characters-id.json");
            if (!File.Exists(p)) return map;
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            if (!doc.RootElement.TryGetProperty("characters", out var chars)) return map;
            foreach (var e in chars.EnumerateObject())
            {
                if (!long.TryParse(e.Name, out var id)) continue;
                var n = e.Value.TryGetProperty("name", out var nv) ? nv.GetString() : null;
                if (!string.IsNullOrWhiteSpace(n)) map[id] = n;
            }
        }
        catch { }
        return map;
    }
}
