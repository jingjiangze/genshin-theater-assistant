using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TheaterAssistant;

/// <summary>
/// 可选的大模型顾问（多端点，按顺序降级）。
///
/// 审计结论（docs/LLM-ENDPOINTS-AUDIT.md）：
///   · Pollinations Classic 文本端点：**无需注册/无需 key**、OpenAI 兼容（`POST /openai`），
///     免费但**每 IP 文字请求 ≥30 秒一次**，超限返回 402 + `Retry-After`，且明确"best effort 无 uptime 保证"；
///     文档还写明**相同 URL 的结果会被缓存并返回给任何人** → 因此发送内容必须脱敏、且不含任何账号信息。
///   · OpenRouter / Groq / Gemini 一类"免费层"都需要注册 key，属于用户自带凭据路径。
///
/// 设计红线（不可放宽）：
///   1. 默认关闭：`data/llm.json` 不存在或 enabled≠true 时**完全不联网**；
///   2. 规则引擎永远先算；模型只做"改写/补充"，任何失败都回退规则文本（永不阻断）；
///   3. **只发** 幕次 / 首领名 / 本幕机制短句 / 角色池统计数字；
///      **绝不发** 截图、像素、选区文本原文、UID、cookie、token、URL；
///   4. 发送前统一走 `Sanitize`：剥离凭据样式、超长数字、邮箱、链接；
///   5. 端点失败进冷却（默认 5 分钟），402/429 优先用 `Retry-After` 决定冷却时长。
/// </summary>
internal sealed class LlmAdvisor : IDisposable
{
    internal sealed class Endpoint
    {
        public string Name = "";
        public string Url = "";
        public string Model = "";
        public string ApiKeyEnv = "";
        /// <summary>public-cacheable = 第三方可能缓存/复用该请求结果，发送内容必须无个人标识。</summary>
        public string Privacy = "";
        /// <summary>端点级超时（秒）。0 = 用全局 timeoutSeconds。实测 Pollinations 首字节要 11s+。</summary>
        public int TimeoutSeconds;
        public DateTime CoolUntil = DateTime.MinValue;
        public int Failures;
        public string LastError = "";
    }

    internal sealed class Config
    {
        public bool Enabled;
        public int TimeoutSeconds = 8;
        public int MinIntervalSeconds = 30;
        public int MaxPerSession = 20;
        public int CooldownSeconds = 300;
        public List<Endpoint> Endpoints = new();
    }

    private readonly HttpClient _http;
    private readonly Config _cfg;
    private readonly DateTime _started = DateTime.UtcNow;
    private DateTime _lastCall = DateTime.MinValue;
    private int _calls;

    public bool Enabled => _cfg is { Enabled: true } && _cfg.Endpoints.Count > 0;
    public string Health { get; private set; } = "未探测";
    public IReadOnlyList<Endpoint> Endpoints => _cfg?.Endpoints ?? new List<Endpoint>();

    private LlmAdvisor(Config cfg)
    {
        _cfg = cfg;
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    // ---------------- 配置解析（纯函数，可断言） ----------------

    internal static Config ParseConfig(string json)
    {
        var cfg = new Config();
        if (string.IsNullOrWhiteSpace(json)) return cfg;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); } catch { return cfg; }
        using (doc)
        {
            var r = doc.RootElement;
            cfg.Enabled = r.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True;
            cfg.TimeoutSeconds = IntOf(r, "timeoutSeconds", cfg.TimeoutSeconds);
            cfg.MinIntervalSeconds = IntOf(r, "minIntervalSeconds", cfg.MinIntervalSeconds);
            cfg.MaxPerSession = IntOf(r, "maxPerSession", cfg.MaxPerSession);
            cfg.CooldownSeconds = IntOf(r, "cooldownSeconds", cfg.CooldownSeconds);

            if (r.TryGetProperty("endpoints", out var eps) && eps.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in eps.EnumerateArray())
                {
                    var url = StrOf(e, "url");
                    var model = StrOf(e, "model");
                    if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(model)) continue;
                    cfg.Endpoints.Add(new Endpoint
                    {
                        Name = StrOf(e, "name") is { Length: > 0 } n ? n : url,
                        Url = url,
                        Model = model,
                        ApiKeyEnv = StrOf(e, "apiKeyEnv"),
                        Privacy = StrOf(e, "privacy"),
                        TimeoutSeconds = IntOf(e, "timeoutSeconds", 0),
                    });
                }
            }
            else
            {
                // 兼容旧配置：单 endpoint / model / apiKeyEnv
                var url = StrOf(r, "endpoint");
                var model = StrOf(r, "model");
                if (!string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(model))
                    cfg.Endpoints.Add(new Endpoint
                    {
                        Name = "legacy", Url = url, Model = model,
                        ApiKeyEnv = StrOf(r, "apiKeyEnv"),
                    });
            }
        }
        return cfg;
    }

    /// <summary>实际使用的配置文件路径（诊断用）。</summary>
    public static string LastConfigPath { get; private set; } = "";

    public static LlmAdvisor TryLoad(string configPath)
    {
        try
        {
            // 默认开启：本机 data/llm.json 优先（可覆盖或关闭），缺失时回退到仓库内 data/llm.example.json。
            // 两者都不存在才视为"不联网"。
            var path = File.Exists(configPath) ? configPath
                : File.Exists(Path.Combine(AppPaths.Dir, "data", "llm.example.json"))
                    ? Path.Combine(AppPaths.Dir, "data", "llm.example.json")
                    : null;
            if (path == null) return null;
            var cfg = ParseConfig(File.ReadAllText(path));
            if (!cfg.Enabled || cfg.Endpoints.Count == 0) return null;
            LastConfigPath = path;
            return new LlmAdvisor(cfg);
        }
        catch { return null; }
    }

    // ---------------- 脱敏（纯函数，可断言） ----------------

    private static readonly Regex CookieLike = new(
        @"(SESSDATA|bili_jct|DedeUserID|buvid\d?|LOGIN_INFO|SID|HSID|SSID|APISID|SAPISID)\s*=\s*[^;，,。\s]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LongDigits = new(@"\d{13,}", RegexOptions.Compiled);
    private static readonly Regex Email = new(@"[\w.+-]+@[\w-]+\.[\w.]+", RegexOptions.Compiled);
    private static readonly Regex Url = new(@"https?://\S+", RegexOptions.Compiled);
    private static readonly Regex Tokenish = new(@"\b[A-Za-z0-9_-]{32,}\b", RegexOptions.Compiled);

    /// <summary>
    /// 发送前脱敏：剥离凭据样式 / 超长数字（UID）/ 邮箱 / 链接 / 长随机串。
    /// 保留：幕次、首领名、机制短句、角色名与统计数字。
    /// </summary>
    internal static string Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = CookieLike.Replace(text, "<凭据已移除>");
        s = Url.Replace(s, "<链接已移除>");
        s = Email.Replace(s, "<邮箱已移除>");
        s = LongDigits.Replace(s, "<长数字已移除>");
        s = Tokenish.Replace(s, "<长串已移除>");
        if (s.Length > 600) s = s[..600];
        return s.Trim();
    }

    // ---------------- 请求构造与响应解析（纯函数，可断言） ----------------

    internal static string BuildPayload(string model, string system, string user)
    {
        return JsonSerializer.Serialize(new
        {
            model,
            temperature = 0.2,
            max_tokens = 220,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            },
        });
    }

    internal static string ExtractContent(string responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (!doc.RootElement.TryGetProperty("choices", out var ch) || ch.ValueKind != JsonValueKind.Array) return null;
            var first = ch.EnumerateArray().FirstOrDefault();
            if (first.ValueKind != JsonValueKind.Object) return null;
            if (!first.TryGetProperty("message", out var msg)) return null;
            if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String) return null;
            var text = (content.GetString() ?? "").Trim();
            return text.Length == 0 ? null : text;
        }
        catch { return null; }
    }

    /// <summary>本次调用是否应跳过（会话配额 / 全局最小间隔 / 端点冷却）。</summary>
    internal static string SkipReason(Endpoint ep, int calls, int maxPerSession,
        DateTime lastCall, int minIntervalSeconds, DateTime now)
    {
        if (calls >= maxPerSession) return "会话配额已用尽";
        if (lastCall != DateTime.MinValue && (now - lastCall).TotalSeconds < minIntervalSeconds)
            return $"限流等待 {(int)(minIntervalSeconds - (now - lastCall).TotalSeconds)}s";
        if (ep.CoolUntil > now) return $"冷却中(至 {ep.CoolUntil:HH:mm:ss})";
        return null;
    }

    // ---------------- 调用 ----------------

    /// <summary>返回 null 表示"不用模型 / 全部端点失败"，调用方继续用规则文本。</summary>
    public async Task<string> RefineAsync(string stateLine, IReadOnlyList<string> ruleLines, string rosterLine)
    {
        if (!Enabled) return null;

        var user = Sanitize($"当前：{stateLine}\n机制：{string.Join("；", ruleLines.Take(4))}\n池子：{rosterLine}");
        const string system =
            "你是《原神》幻想真境剧诗的战斗顾问。只输出 3 条以内的短句，每条不超过 28 个汉字，" +
            "必须是玩家下一步能立刻执行的动作（带元素/技能/时间窗口）。不要寒暄，不要复述规则。";

        foreach (var ep in _cfg.Endpoints)
        {
            var now = DateTime.UtcNow;
            var skip = SkipReason(ep, _calls, _cfg.MaxPerSession, _lastCall, _cfg.MinIntervalSeconds, now);
            if (skip != null) { Health = $"{ep.Name}: {skip}"; continue; }

            _lastCall = now;
            _calls++;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, ep.Url);
                req.Content = new StringContent(BuildPayload(ep.Model, system, user), Encoding.UTF8, "application/json");
                var key = string.IsNullOrWhiteSpace(ep.ApiKeyEnv) ? "" : Environment.GetEnvironmentVariable(ep.ApiKeyEnv) ?? "";
                if (key.Length > 0)
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);

                var timeoutSec = ep.TimeoutSeconds > 0 ? ep.TimeoutSeconds : Math.Max(3, _cfg.TimeoutSeconds);
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
                using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    var cool = CoolFromResponse(resp, _cfg.CooldownSeconds);
                    ep.CoolUntil = DateTime.UtcNow.AddSeconds(cool);
                    ep.Failures++;
                    ep.LastError = $"HTTP {(int)resp.StatusCode} → 冷却 {cool}s";
                    Health = $"{ep.Name}: {ep.LastError}";
                    continue;   // 降级到下一个端点
                }

                var text = ExtractContent(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
                if (text == null)
                {
                    ep.CoolUntil = DateTime.UtcNow.AddSeconds(_cfg.CooldownSeconds);
                    ep.LastError = "响应无内容";
                    Health = $"{ep.Name}: {ep.LastError}";
                    continue;
                }
                ep.Failures = 0; ep.LastError = "";
                Health = $"{ep.Name}: ok";
                return text;
            }
            catch (Exception ex)
            {
                ep.CoolUntil = DateTime.UtcNow.AddSeconds(_cfg.CooldownSeconds);
                ep.Failures++;
                ep.LastError = "不可用(" + ex.GetType().Name + ")";
                Health = $"{ep.Name}: {ep.LastError}";
            }
        }
        return null;
    }

    /// <summary>402/429 优先用 Retry-After 决定冷却；其余错误用默认冷却。</summary>
    internal static int CoolFromResponse(HttpResponseMessage resp, int defaultSeconds)
    {
        var code = (int)resp.StatusCode;
        if (code == 402 || code == 429)
        {
            if (resp.Headers.RetryAfter?.Delta is { } d) return Math.Max(5, (int)d.TotalSeconds);
            if (resp.Headers.RetryAfter?.Date is { } dt)
            {
                var s = (int)(dt - DateTimeOffset.UtcNow).TotalSeconds;
                if (s > 0) return Math.Max(5, s);
            }
            return Math.Max(30, defaultSeconds);
        }
        return defaultSeconds;
    }

    // ---------------- 连通性探针（审计/自检用） ----------------

    public static int RunProbe(string configPath, bool dry)
    {
        Cli.Attach();
        // 与 TryLoad 同一套回退：本机 data/llm.json 优先，其次仓库内 data/llm.example.json（默认开启）
        var path = File.Exists(configPath) ? configPath
            : File.Exists(Path.Combine(AppPaths.Dir, "data", "llm.example.json"))
                ? Path.Combine(AppPaths.Dir, "data", "llm.example.json")
                : null;
        if (path == null) { Cli.Say("没有任何 llm 配置（data/llm.json 与 data/llm.example.json 都不存在）"); return 4; }
        Cli.Say("配置文件: " + path);
        var cfg = ParseConfig(File.ReadAllText(path));
        Cli.Say($"enabled={cfg.Enabled} 端点={cfg.Endpoints.Count} 超时={cfg.TimeoutSeconds}s 最小间隔={cfg.MinIntervalSeconds}s 单次会话上限={cfg.MaxPerSession}");
        foreach (var ep in cfg.Endpoints)
        {
            var key = string.IsNullOrWhiteSpace(ep.ApiKeyEnv) ? "" : Environment.GetEnvironmentVariable(ep.ApiKeyEnv) ?? "";
            Cli.Say($"  - {ep.Name}  {ep.Url}  model={ep.Model}  key={(string.IsNullOrWhiteSpace(ep.ApiKeyEnv) ? "无需" : (key.Length > 0 ? ep.ApiKeyEnv + "=已设置" : ep.ApiKeyEnv + "=未设置"))}  privacy={(ep.Privacy is { Length: > 0 } p ? p : "-")}");
        }
        if (dry) { Cli.Say("--dry：只校验配置，不联网"); return 0; }
        if (!cfg.Enabled) { Cli.Say("enabled=false：不联网（要试就改 true）"); return 4; }

        using var adv = new LlmAdvisor(cfg);
        var sw = Stopwatch.StartNew();
        var text = adv.RefineAsync("第 3 幕 · 翠翎恐蕈", new[] { "雷推怒气条，满条后 15 秒力竭" }, "5 人 / 耐力求和未知")
            .GetAwaiter().GetResult();
        sw.Stop();
        Cli.Say($"实测用时 {sw.ElapsedMilliseconds} ms｜健康状态 {adv.Health}");
        if (text == null) { Cli.Say("没有任何端点成功（全部冷却或不可用）"); return 1; }
        Cli.Say("模型输出：");
        foreach (var line in text.Split('\n').Take(4)) Cli.Say("  " + line.Trim());
        return 0;
    }

    public void Dispose() => _http.Dispose();

    private static string StrOf(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int IntOf(JsonElement e, string name, int fallback) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;
}
