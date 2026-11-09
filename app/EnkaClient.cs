using System.Net.Http;
using System.Text.Json;

namespace TheaterAssistant;

/// <summary>
/// Enka.Network 客户端：用 UID 查角色展柜，得到命座/天赋/武器/圣遗物。
///
/// 为什么是它（docs/FINAL-ARCHITECTURE.md 五方案审计）：
///   · 原神客户端**不落地**玩家角色数据，纯本地只能读内存 —— 那是封号红线，禁止；
///   · 游戏日志只有 UID/成就/抽卡，没有角色池；
///   · 所以「自动获取角色池」的唯一合规路径是公开 API。胡桃工具箱的角色详情同样走 Enka。
///
/// 零配置设计：UID 不需要玩家手填 —— 我们已经在悬浮窗截图里 OCR 到了（界面右下角 `UID:xxxxxxxxx`），
/// 直接复用即可。玩家连"打开角色展柜"都不必（不开时 Enka 会返回空列表，我们如实降级）。
///
/// 合规声明：只做一次 HTTPS GET 到公开接口，不接触游戏进程、不读内存、不模拟输入。
/// </summary>
internal static class EnkaClient
{
    private const string BaseUrl = "https://enka.network/api/uid/";

    /// <summary>缓存有效期：避免每次 tick 都请求（Enka 有速率限制，且角色池不常变）。</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

    internal sealed class Outcome
    {
        public bool Ok;
        public string Uid = "";
        public string Detail = "";
        public int AvatarCount;
        public bool FromCache;
        /// <summary>转成 GOOD 形态（供 CharacterStrength / TeamSolver 直接复用）。</summary>
        public string GoodJson = "";
        public List<string> Names = new();
    }

    /// <summary>从任意文本里提取 UID（我们 OCR 到的界面文本里就有）。</summary>
    public static string ExtractUid(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var m = System.Text.RegularExpressions.Regex.Match(text, @"UID\s*[:：]?\s*(\d{9})");
        return m.Success ? m.Groups[1].Value : "";
    }

    private static string CachePath(string uid) => Path.Combine(AppPaths.Dir, "cache", $"enka-{uid}.json");

    /// <summary>查询并转换为 GOOD。force=true 忽略缓存。</summary>
    public static Outcome Fetch(string uid, bool force = false)
    {
        var o = new Outcome { Uid = uid ?? "" };
        if (!System.Text.RegularExpressions.Regex.IsMatch(o.Uid, @"^\d{9}$"))
        {
            o.Detail = "UID 格式不对（应为 9 位数字）";
            return o;
        }

        // 缓存命中
        try
        {
            var cp = CachePath(o.Uid);
            if (!force && File.Exists(cp) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cp) < CacheTtl)
            {
                var cached = File.ReadAllText(cp);
                o = Parse(cached, o.Uid);
                o.FromCache = true;
                if (o.Ok) { o.Detail += "（缓存）"; return o; }
            }
        }
        catch { }

        // 网络请求（同步等待，调用方负责放到后台线程）
        try
        {
            // 超时放宽到 30s：Enka 首次响应可能 5–15s，10s 会抛 TaskCanceledException（实测踩过）。
            // 显式 UseProxy=false：系统里可能残留失效代理（本机 git 曾配过 127.0.0.1:7897），
            // HttpClient 默认走系统代理 → 连不上还以为是网络问题。
            // 关键：.NET 默认走 "Happy Eyeballs"，IPv6 不通时会先挂很久才回落 IPv4，
            // 表现为"curl 25s 能拿到、HttpClient 60s 超时"（实测踩过）。
            // 这里用 SocketsHttpHandler 的 ConnectCallback 强制 IPv4，绕过该问题。
            var handler = new System.Net.Http.SocketsHttpHandler
            {
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(25),
                ConnectCallback = async (ctx, ct) =>
                {
                    var addrs = await System.Net.Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
                    var v4 = System.Linq.Enumerable.FirstOrDefault(addrs, a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                    var target = v4 ?? System.Linq.Enumerable.First(addrs);
                    var sock = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
                                                             System.Net.Sockets.SocketType.Stream,
                                                             System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
                    await sock.ConnectAsync(new System.Net.IPEndPoint(target, ctx.DnsEndPoint.Port), ct);
                    return new System.Net.Sockets.NetworkStream(sock, ownsSocket: true);
                },
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            // Enka 要求带 User-Agent，否则可能 403/429
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "theater-assistant/0.53 (read-only; contact: local)");
            http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
            var resp = http.GetAsync(BaseUrl + o.Uid).GetAwaiter().GetResult();
            var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode)
            {
                o.Detail = $"HTTP {(int)resp.StatusCode}：{Trim(body)}";
                return o;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath(o.Uid))!);
                File.WriteAllText(CachePath(o.Uid), body);
            }
            catch { }
            var parsed = Parse(body, o.Uid);
            return parsed;
        }
        catch (Exception ex)
        {
            o.Detail = "请求失败：" + ex.GetType().Name + "（离线/被墙时属预期，应回退到 GOOD 或 OCR）";
            return o;
        }
    }

    /// <summary>把 Enka 响应转成 GOOD v1 形态（我们内部统一用 GOOD）。</summary>
    private static Outcome Parse(string body, string uid)
    {
        var o = new Outcome { Uid = uid };
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("avatarInfoList", out var avatars) || avatars.ValueKind != JsonValueKind.Array)
            {
                o.Detail = "无角色列表（玩家未开启「角色展柜」，或 UID 不存在）";
                return o;
            }

            var idMap = AvatarIdMap.Load();     // avatarId → 中文名
            var chars = new List<object>();
            var weapons = new List<object>();
            var arts = new List<object>();
            var names = new List<string>();

            foreach (var a in avatars.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object) continue;
                var id = a.TryGetProperty("avatarId", out var aid) && aid.ValueKind == JsonValueKind.Number ? aid.GetInt32() : 0;
                if (id <= 0) continue;
                var key = idMap.Resolve(id);
                var cn = idMap.ChineseName(key);
                if (!string.IsNullOrEmpty(cn)) names.Add(cn);

                // 等级：propMap["4001"].ival
                int level = 0;
                // 注意：Enka 的 ival 是**字符串**（"90"）而不是数字，只判 Number 会得到 level=0（实测踩过）。
                if (a.TryGetProperty("propMap", out var pm) && pm.ValueKind == JsonValueKind.Object
                    && pm.TryGetProperty("4001", out var lv) && lv.ValueKind == JsonValueKind.Object)
                {
                    if (lv.TryGetProperty("ival", out var iv))
                        level = iv.ValueKind == JsonValueKind.Number ? iv.GetInt32()
                              : int.TryParse(iv.ValueKind == JsonValueKind.String ? iv.GetString() : null, out var pl) ? pl : 0;
                }

                // 命座：talentIdList 长度 - 1（0 命时该字段可能缺失）
                int cons = 0;
                if (a.TryGetProperty("talentIdList", out var tl) && tl.ValueKind == JsonValueKind.Array)
                    cons = Math.Max(0, tl.GetArrayLength() - 1);

                // 天赋：skillLevelMap 里取三个（具体 id 因角色而异，这里取前三个值作近似）
                int t1 = 0, t2 = 0, t3 = 0;
                if (a.TryGetProperty("skillLevelMap", out var sk) && sk.ValueKind == JsonValueKind.Object)
                {
                    var vals = sk.EnumerateObject()
                        .Select(p => p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetInt32() : 0)
                        .Where(v => v > 0).OrderByDescending(v => v).ToList();
                    if (vals.Count > 0) t1 = vals[0];
                    if (vals.Count > 1) t2 = vals[1];
                    if (vals.Count > 2) t3 = vals[2];
                }

                chars.Add(new
                {
                    key,
                    level,
                    constellation = cons,
                    talent = new { auto = t1, skill = t2, burst = t3 },
                });

                // 装备：武器 + 圣遗物
                if (a.TryGetProperty("equipList", out var eq) && eq.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in eq.EnumerateArray())
                    {
                        if (e.ValueKind != JsonValueKind.Object) continue;
                        if (e.TryGetProperty("weapon", out var w) && w.ValueKind == JsonValueKind.Object)
                        {
                            int wl = w.TryGetProperty("level", out var wlv) && wlv.ValueKind == JsonValueKind.Number ? wlv.GetInt32() : 0;
                            int refi = 1;
                            if (w.TryGetProperty("affixMap", out var af) && af.ValueKind == JsonValueKind.Object)
                                refi = Math.Min(5, af.EnumerateObject().Count() == 0 ? 1 : af.EnumerateObject().Max(p => p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetInt32() : 0) + 1);
                            weapons.Add(new { key = $"weapon-{id}", level = wl, refinement = refi, location = key });
                        }
                        else if (e.TryGetProperty("reliquary", out var rq) && rq.ValueKind == JsonValueKind.Object)
                        {
                            int rl = rq.TryGetProperty("level", out var rlv) && rlv.ValueKind == JsonValueKind.Number ? rlv.GetInt32() : 0;
                            var subs = new List<object>();
                            if (e.TryGetProperty("flat", out var flat) && flat.ValueKind == JsonValueKind.Object
                                && flat.TryGetProperty("reliquarySubstats", out var ss) && ss.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var s in ss.EnumerateArray())
                                {
                                    if (s.ValueKind != JsonValueKind.Object) continue;
                                    var mk = s.TryGetProperty("appendPropId", out var ap) && ap.ValueKind == JsonValueKind.String ? ap.GetString() ?? "" : "";
                                    var mv = s.TryGetProperty("statValue", out var sv) && sv.ValueKind == JsonValueKind.Number ? sv.GetDouble() : 0;
                                    if (mk.Length > 0 && mv > 0) subs.Add(new { key = MapProp(mk), value = mv });
                                }
                            }
                            arts.Add(new { setKey = "enka", slotKey = "unknown", level = rl, rarity = 5, location = key, substats = subs });
                        }
                    }
                }
            }

            if (chars.Count == 0)
            {
                o.Detail = "角色列表为空（展柜未开启或全为隐藏角色）";
                return o;
            }

            o.Ok = true;
            o.AvatarCount = chars.Count;
            o.Names = names.Distinct().ToList();
            o.Detail = $"Enka {chars.Count} 个角色（含命座/天赋/武器/圣遗物）";
            o.GoodJson = JsonSerializer.Serialize(new
            {
                format = "GOOD",
                version = 1,
                source = "enka.network",
                uid,
                fetchedAt = DateTime.UtcNow.ToString("o"),
                characters = chars,
                weapons,
                artifacts = arts,
            }, new JsonSerializerOptions { WriteIndented = true });
            return o;
        }
        catch (Exception ex)
        {
            o.Detail = "解析失败：" + ex.GetType().Name;
            return o;
        }
    }

    /// <summary>Enka 的属性 id → GOOD 的 key（CharacterStrength 依赖这些前缀识别暴击/词条）。</summary>
    private static string MapProp(string enkaProp) => enkaProp switch
    {
        "FIGHT_PROP_CRITICAL" => "critRate_",
        "FIGHT_PROP_CRITICAL_HURT" => "critDMG_",
        "FIGHT_PROP_ATTACK_PERCENT" => "atk_",
        "FIGHT_PROP_HP_PERCENT" => "hp_",
        "FIGHT_PROP_DEFENSE_PERCENT" => "def_",
        "FIGHT_PROP_ELEMENT_MASTER_EM" or "FIGHT_PROP_ELEMENT_MASTERY" => "eleMas",
        "FIGHT_PROP_CHARGE_EFFICIENCY" => "enerRech_",
        _ => enkaProp.ToLowerInvariant(),
    };

    private static string Trim(string s) => (s ?? "").Length > 120 ? s[..120] + "…" : (s ?? "");

    public static int RunCli(string uid)
    {
        Cli.Attach();
        Cli.Say($"查询 UID {uid} …");
        var o = Fetch(uid, force: true);
        Cli.Say($"成功={o.Ok}｜角色 {o.AvatarCount}｜{o.Detail}");
        if (o.Names.Count > 0) Cli.Say("角色: " + string.Join("、", o.Names.Take(20)));
        if (o.Ok && o.GoodJson.Length > 0)
        {
            var p = Path.Combine(AppPaths.Dir, "roster-enka.json");
            try { File.WriteAllText(p, o.GoodJson); Cli.Say("已写 " + p); } catch { }
        }
        return o.Ok ? 0 : 3;
    }
}

/// <summary>
/// avatarId → 角色英文名 → 中文名 的映射。
/// 数据来自 data/avatar-ids.json（由 tools/ci/gen-avatar-ids.mjs 从公开角色表生成）。
/// 缺失时如实返回空，不猜。
/// </summary>
internal static class AvatarIdMap
{
    private static Dictionary<int, string> _idToKey = new();
    private static Dictionary<string, string> _keyToCn = new();
    private static bool _loaded;

    public static AvatarIdMapHandle Load()
    {
        if (!_loaded)
        {
            _loaded = true;
            try
            {
                var p = AppPaths.Data("avatar-ids.json");
                if (File.Exists(p))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(p));
                    if (doc.RootElement.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Object)
                        foreach (var e in ids.EnumerateObject())
                            if (int.TryParse(e.Name, out var id) && e.Value.ValueKind == JsonValueKind.String)
                                _idToKey[id] = e.Value.GetString() ?? "";
                }
                var cp = AppPaths.Data("characters.json");
                if (File.Exists(cp))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(cp));
                    if (doc.RootElement.TryGetProperty("zhNames", out var zh) && zh.ValueKind == JsonValueKind.Object)
                        foreach (var e in zh.EnumerateObject())
                            if (e.Value.ValueKind == JsonValueKind.String && e.Value.GetString() is { Length: > 0 } cn)
                            {
                                // 两种形态都要注册：characters.json 里既有 "Hu Tao" 也有 "KamisatoAyaka"，
                                // Enka 的英文名来自官方（带空格）。只认一种会漏掉一半（实测 60%→92%）。
                                _keyToCn[e.Name] = cn;
                                _keyToCn[AvatarIdMapHandle.NormKey(e.Name)] = cn;
                            }
                }
            }
            catch { }
        }
        return new AvatarIdMapHandle(_idToKey, _keyToCn);
    }
}

internal readonly struct AvatarIdMapHandle
{
    private readonly Dictionary<int, string> _ids;
    private readonly Dictionary<string, string> _cn;
    public AvatarIdMapHandle(Dictionary<int, string> ids, Dictionary<string, string> cn) { _ids = ids; _cn = cn; }
    public string Resolve(int id) => _ids.TryGetValue(id, out var k) ? k : $"avatar-{id}";

    /// <summary>归一化：只保留字母与汉字，忽略空格/撇号/连字符（"Hu Tao"↔"HuTao"、"Kamisato Ayaka"↔"KamisatoAyaka"）。</summary>
    internal static string NormKey(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c) || (c >= 0x4E00 && c <= 0x9FFF)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    public string ChineseName(string key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        if (_cn.TryGetValue(key, out var v)) return v;
        var nk = NormKey(key);
        return _cn.TryGetValue(nk, out var v2) ? v2 : key;
    }
}
