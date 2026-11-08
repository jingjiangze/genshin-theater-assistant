using System.Text.Json;

namespace TheaterAssistant;

/// <summary>
/// 角色强度量化：按多项指标打分，供配队选人排序（解决"最优=覆盖最优而非通关最优"）。
///
/// 数据来源：**GOOD v1 标准格式**（Genshin Open Object Description，社区圣遗物扫描器通用）。
/// 审计确认：本仓库的 `roster.json` 本来就是 GOOD 格式（`"format":"GOOD","version":1`），
/// 此前只解析了 `key` / `level` 两个字段，把已存在的强度信息全丢了。
///
/// GOOD 里可用的强度指标：
///   characters[].constellation      命座 0–6
///   characters[].talent { auto, skill, burst }   天赋等级 1–10（+命座最多 13）
///   artifacts[]  { slotKey, level, rarity, substats[{key,value}] }   圣遗物
///   weapons[]    { key, level, refinement }      武器等级 1–90 / 精炼 1–5
///
/// 评分（0–100，权重全部来自 data/strength-model.json，不硬编码）：
///   圣遗物分 = 0.6×CV归一 + 0.4×有效词条归一   （CV = 暴击率×2 + 暴击伤害，社区通用口径）
///   天赋分   = 三个天赋等级均值归一
///   命座分   = 命座/6
///   武器分   = 0.7×等级归一 + 0.3×精炼归一
///   总分     = w_artifact×圣遗物 + w_talent×天赋 + w_cons×命座 + w_weapon×武器
///
/// 边界：**没有 GOOD 强度字段时返回 Unknown**，绝不假装算出了强度（缺失即跳过加权）。
/// </summary>
internal static class CharacterStrength
{
    internal sealed class Profile
    {
        public string Key = "";
        public string Name = "";        // 中文名（由 zhNames 反查）
        public int Level;
        public int Constellation;       // 0–6
        public int TalentAuto, TalentSkill, TalentBurst;
        public int WeaponLevel;
        public int WeaponRefinement;    // 1–5
        public int ArtifactCount;
        public double ArtifactCV;       // 暴击率×2 + 暴击伤害 之和
        public double ArtifactRolls;    // 有效词条数（按角色主属性需求）
        public double Score;            // 0–100
        public bool Unknown;            // true = 缺强度字段，未评分
        public string Note = "";
    }

    private static readonly Dictionary<string, Profile> _byName = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Profile> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;

    public static string Source { get; private set; } = "(未加载)";
    public static int Count => _byName.Count;
    public static int ScoredCount => _byName.Values.Count(p => !p.Unknown);
    public static int UnknownCount => _byName.Values.Count(p => p.Unknown);

    // 权重（来自 strength-model.json）
    private static double _wArtifact = 0.40, _wTalent = 0.25, _wCons = 0.20, _wWeapon = 0.15;
    private static double _cvcvWeight = 0.6, _rollWeight = 0.4;
    private static double _cvFull = 180;      // CV 达到该值视为满分（约 60/120 双爆）
    private static double _rollFull = 24;     // 有效词条达到该数视为满分

    public static void LoadModel(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            double N(string n, double f) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : f;
            _wArtifact = N("weightArtifact", _wArtifact);
            _wTalent = N("weightTalent", _wTalent);
            _wCons = N("weightConstellation", _wCons);
            _wWeapon = N("weightWeapon", _wWeapon);
            _cvcvWeight = N("artifactCvWeight", _cvcvWeight);
            _rollWeight = N("artifactRollWeight", _rollWeight);
            _cvFull = N("artifactCvFull", _cvFull);
            _rollFull = N("artifactRollFull", _rollFull);
        }
        catch { }
    }

    /// <summary>读 GOOD 格式（或本项目简化的 roster）。没有强度字段的角色标 Unknown。</summary>
    public static void Load(string rosterPath, string charactersPath)
    {
        _byName.Clear();
        _byKey.Clear();
        _loaded = false;
        if (!File.Exists(rosterPath)) { Source = "(无 roster)"; return; }

        var zhByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (File.Exists(charactersPath))
            {
                using var cdoc = JsonDocument.Parse(File.ReadAllText(charactersPath));
                if (cdoc.RootElement.TryGetProperty("zhNames", out var zh) && zh.ValueKind == JsonValueKind.Object)
                    foreach (var e in zh.EnumerateObject())
                        if (e.Value.ValueKind == JsonValueKind.String && e.Value.GetString() is { Length: > 0 } cn)
                            zhByKey[e.Name] = cn;
            }
        }
        catch { }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(rosterPath));
            var root = doc.RootElement;

            // 1) 角色：命座 + 天赋
            if (root.TryGetProperty("characters", out var chars) && chars.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in chars.EnumerateArray())
                {
                    var key = Str(c, "key");
                    if (key.Length == 0) continue;
                    var p = new Profile
                    {
                        Key = key,
                        Name = zhByKey.TryGetValue(key, out var cn) ? cn : key,
                        Level = Int(c, "level", 0),
                        Constellation = Int(c, "constellation", 0),
                        Unknown = true,   // 先假设未知，有任一天赋/命座字段才转已评分
                    };
                    if (c.TryGetProperty("talent", out var t) && t.ValueKind == JsonValueKind.Object)
                    {
                        p.TalentAuto = Int(t, "auto", 0);
                        p.TalentSkill = Int(t, "skill", 0);
                        p.TalentBurst = Int(t, "burst", 0);
                        if (p.TalentAuto + p.TalentSkill + p.TalentBurst > 0) p.Unknown = false;
                    }
                    if (p.Constellation > 0) p.Unknown = false;
                    Put(p);
                }
            }

            // 2) 武器：等级 + 精炼（按角色 key 关联，GOOD 里每次出现即该角色佩戴）
            if (root.TryGetProperty("weapons", out var weapons) && weapons.ValueKind == JsonValueKind.Array)
            {
                foreach (var w in weapons.EnumerateArray())
                {
                    var owner = Str(w, "location");
                    if (owner.Length == 0) continue;
                    if (_byKey.TryGetValue(owner, out var p))
                    {
                        p.WeaponLevel = Int(w, "level", 0);
                        p.WeaponRefinement = Int(w, "refinement", 1);
                        if (p.WeaponLevel > 0) p.Unknown = false;
                    }
                }
            }

            // 3) 圣遗物：CV 与有效词条（按角色聚合）
            if (root.TryGetProperty("artifacts", out var arts) && arts.ValueKind == JsonValueKind.Array)
            {
                var acc = new Dictionary<string, (double cv, double rolls, int n)>(StringComparer.OrdinalIgnoreCase);
                foreach (var a in arts.EnumerateArray())
                {
                    var owner = Str(a, "location");
                    if (owner.Length == 0) continue;
                    if (!_byKey.TryGetValue(owner, out var p)) continue;
                    double cv = 0, rolls = 0;
                    if (a.TryGetProperty("substats", out var subs) && subs.ValueKind == JsonValueKind.Array)
                        foreach (var s in subs.EnumerateArray())
                        {
                            var k = Str(s, "key");
                            double v = s.TryGetProperty("value", out var vv) && vv.ValueKind == JsonValueKind.Number ? vv.GetDouble() : 0;
                            if (v <= 0) continue;
                            if (k.StartsWith("critRate", StringComparison.OrdinalIgnoreCase)) { cv += v * 2; rolls += v / 3.3; }
                            else if (k.StartsWith("critDMG", StringComparison.OrdinalIgnoreCase)) { cv += v; rolls += v / 6.6; }
                            else if (k.StartsWith("hp_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("atk_", StringComparison.OrdinalIgnoreCase)
                                  || k.StartsWith("def_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("eleMas", StringComparison.OrdinalIgnoreCase)
                                  || k.StartsWith("enerRech", StringComparison.OrdinalIgnoreCase))
                                rolls += v / 5.0;
                        }
                    if (!acc.TryGetValue(owner, out var cur)) cur = (0, 0, 0);
                    acc[owner] = (cur.cv + cv, cur.rolls + rolls, cur.n + 1);
                }
                foreach (var kv in acc)
                    if (_byKey.TryGetValue(kv.Key, out var p))
                    {
                        p.ArtifactCount = kv.Value.n;
                        p.ArtifactCV = kv.Value.cv;
                        p.ArtifactRolls = kv.Value.rolls;
                        if (p.ArtifactCount > 0) p.Unknown = false;
                    }
            }
        }
        catch { Source = "(roster 解析失败)"; return; }

        foreach (var p in _byName.Values) Score(p);

        _loaded = true;
        Source = $"{Path.GetFileName(rosterPath)}｜角色 {Count}（已评分 {ScoredCount}，缺强度字段 {UnknownCount}）";
    }

    private static void Score(Profile p)
    {
        if (p.Unknown) { p.Score = 0; p.Note = "缺 GOOD 强度字段（命座/天赋/武器/圣遗物），不参与加权"; return; }

        double artCv = Math.Clamp(p.ArtifactCV / Math.Max(1, _cvFull), 0, 1);
        double artRoll = Math.Clamp(p.ArtifactRolls / Math.Max(1, _rollFull), 0, 1);
        double art = _cvcvWeight * artCv + _rollWeight * artRoll;

        double talentAvg = (p.TalentAuto + p.TalentSkill + p.TalentBurst) / 3.0;
        double talent = Math.Clamp(talentAvg / 10.0, 0, 1);

        double cons = Math.Clamp(p.Constellation / 6.0, 0, 1);

        double wLv = Math.Clamp(p.WeaponLevel / 90.0, 0, 1);
        double wRef = Math.Clamp((p.WeaponRefinement - 1) / 4.0, 0, 1);
        double weapon = 0.7 * wLv + 0.3 * wRef;

        p.Score = 100.0 * (_wArtifact * art + _wTalent * talent + _wCons * cons + _wWeapon * weapon);
        p.Note = $"圣遗物 CV {p.ArtifactCV:0}（{p.ArtifactCount} 件）/ 词条 {p.ArtifactRolls:0.0}｜天赋 {p.TalentAuto}/{p.TalentSkill}/{p.TalentBurst}｜命座 {p.Constellation}｜武器 Lv{p.WeaponLevel} R{p.WeaponRefinement}";
    }

    private static void Put(Profile p)
    {
        if (p.Name.Length > 0) _byName[p.Name] = p;
        _byKey[p.Key] = p;
    }

    /// <summary>按角色名取强度（0–100）；未知返回 -1（调用方据此**不加权**）。</summary>
    public static double ScoreOf(string name)
    {
        if (!_loaded || string.IsNullOrEmpty(name)) return -1;
        if (_byName.TryGetValue(name, out var p) && !p.Unknown) return p.Score;
        var canon = DecisionPolicy.Canonical(name);
        if (_byName.TryGetValue(canon, out var p2) && !p2.Unknown) return p2.Score;
        return -1;
    }

    public static Profile ProfileOf(string name)
    {
        if (_byName.TryGetValue(name, out var p)) return p;
        var canon = DecisionPolicy.Canonical(name);
        return _byName.TryGetValue(canon, out var p2) ? p2 : null;
    }

    public static int RunCli(string rosterPath = null)
    {
        Cli.Attach();
        DecisionPolicy.Load(AppPaths.Data("decision-policy.json"));
        LoadModel(AppPaths.Data("strength-model.json"));
        rosterPath ??= Path.Combine(AppPaths.Dir, "roster.json");
        Load(rosterPath, AppPaths.Data("characters.json"));
        Cli.Say($"强度数据: {Source}");
        if (Count == 0) { Cli.Say("没有可用的 roster（需要 GOOD 格式，含 constellation/talent/artifacts/weapons）"); return 3; }
        Cli.Say($"权重: 圣遗物 {_wArtifact:0.00} / 天赋 {_wTalent:0.00} / 命座 {_wCons:0.00} / 武器 {_wWeapon:0.00}");
        foreach (var p in _byName.Values.OrderByDescending(x => x.Score).Take(15))
            Cli.Say($"  {p.Name,-8} {p.Score,6:0.0} 分  {(p.Unknown ? "（未评分：" + p.Note + "）" : p.Note)}");
        return 0;
    }

    private static string Str(JsonElement e, string n) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static int Int(JsonElement e, string n, int f) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : f;
}
