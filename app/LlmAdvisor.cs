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
///   1. 默认关闭（**注释与实现一致**）：只有 `data/llm.json` 存在且 `enabled: true` 才联网。
///      `data/llm.example.json` 只是"怎么配"的样例（自身 enabled=false），
///      仅在显式设置环境变量 `TA_LLM_ALLOW_EXAMPLE=1` 时才允许它作为回退配置（此时按"允许启用"
///      处理；`LLM_ENABLED=0` 仍可一键关闭）。
///      另一种合法路径是纯环境变量（云端/CI 的 `LLM_ENDPOINT`，凭据不落盘）—— 那是人手动设的，
///      属于显式同意联网；`LLM_ENABLED=0` 可一键关掉。
///      旧实现注释写"不存在就完全不联网"、代码却回退到 enabled=true 的 example（4 个第三方端点，
///      其中 pollinations 的响应还会被第三方缓存），等于默认偷偷联网 —— 已按上面的口径收紧；
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

    /// <summary>
    /// 环境变量覆盖（云端/CI 用，避免把凭据写进文件）：
    /// LLM_ENDPOINT（逗号分隔多个）、LLM_MODEL（默认 deepseek-v4）、LLM_API_KEY、LLM_ENABLED=0/1。
    /// 只覆盖，不新增语义：仍然默认开启、失败降级、脱敏发送。
    /// </summary>
    internal static void ApplyEnvOverrides(Config cfg, string envPrefix = "LLM_")
    {
        // envPrefix 允许并存两套通道：
        //   LLM_*        主通道（玩家实际使用，省配额）
        //   LLM_VERIFY_* 验证/模拟通道（云端 CI 与模拟测试专用，可指向另一个模型，交叉对照）
        var enabled = Environment.GetEnvironmentVariable(envPrefix + "ENABLED");
        if (enabled == "0") { cfg.Enabled = false; return; }
        if (enabled == "1") cfg.Enabled = true;

        var endpoints = Environment.GetEnvironmentVariable(envPrefix + "ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoints)) return;

        var model = Environment.GetEnvironmentVariable(envPrefix + "MODEL");
        if (string.IsNullOrWhiteSpace(model)) model = "deepseek-v4";
        var keyEnv = Environment.GetEnvironmentVariable(envPrefix + "API_KEY_ENV");
        if (string.IsNullOrWhiteSpace(keyEnv)) keyEnv = envPrefix + "API_KEY";

        cfg.Endpoints = endpoints.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(u => u.Trim())
            .Where(u => u.Length > 0)
            .Select(u => new Endpoint
            {
                Name = "env:" + (new Uri(u).Host),
                Url = u,
                Model = model,
                ApiKeyEnv = keyEnv,
                Privacy = "standard",
                TimeoutSeconds = 45,
            })
            .ToList();
    }

    public static LlmAdvisor TryLoad(string configPath)
    {
        try
        {
            var cfg = ResolveConfig(configPath, out var path, out _);
            if (cfg == null) return null;
            LastConfigPath = path ?? "(环境变量)";
            return new LlmAdvisor(cfg);
        }
        catch { return null; }
    }

    /// <summary>
    /// 配置发现（运行时唯一口径）。返回 null = 不联网，来源只有三种：
    ///   ① data/llm.json 存在且 enabled=true（正常路径）；
    ///   ② 纯环境变量：{prefix}ENDPOINT 有值（云端/CI，凭据不落盘）+ 未被 {prefix}ENABLED=0 关掉；
    ///   ③ data/llm.example.json —— 仅当 TA_LLM_ALLOW_EXAMPLE=1（或 allowExampleOverride，
    ///      只给 RunProbe 这类显式命令用）时作为回退；此时视为已允许启用，{prefix}ENABLED=0 仍可关闭。
    /// 必须走 AppPaths.Data：单文件 exe 的 data/ 在解包目录里，只有 Resolve 会两处都找。
    /// </summary>
    internal static Config ResolveConfig(string configPath, out string path, out bool fromExample,
        bool allowExampleOverride = false, string envPrefix = "LLM_")
    {
        path = null;
        fromExample = false;
        try
        {
            var allowExample = allowExampleOverride
                || Environment.GetEnvironmentVariable("TA_LLM_ALLOW_EXAMPLE") == "1";
            if (File.Exists(configPath))
            {
                path = configPath;
            }
            else if (allowExample && File.Exists(AppPaths.Data("llm.example.json")))
            {
                path = AppPaths.Data("llm.example.json");
                fromExample = true;
            }
            if (path == null)
            {
                // 纯环境变量配置（云端/CI：凭据不落盘，见 docs/FLOW.md 的 LLM_ENDPOINT=... 用法）。
                // 显式给了端点就算显式同意联网 —— 这是人手动设的环境变量，不是随包附带的默认值；
                // LLM_ENABLED=0 仍然能一键关掉（ApplyEnvOverrides 会直接 return）。
                var endpointEnv = Environment.GetEnvironmentVariable(envPrefix + "ENDPOINT");
                if (string.IsNullOrWhiteSpace(endpointEnv)) return null;
                var envCfg = new Config { Enabled = true };
                ApplyEnvOverrides(envCfg, envPrefix);
                return envCfg.Enabled && envCfg.Endpoints.Count > 0 ? envCfg : null;
            }

            var cfg = ParseConfig(File.ReadAllText(path));
            ApplyEnvOverrides(cfg, envPrefix);   // 云端只设环境变量，凭据不落盘
            // example 自身 enabled=false（防止随包默认联网）。既然操作者已用 TA_LLM_ALLOW_EXAMPLE=1
            // 显式允许它当回退配置，就按"允许启用"处理；{prefix}ENABLED=0 仍然优先（不覆盖关闭）。
            if (fromExample && Environment.GetEnvironmentVariable(envPrefix + "ENABLED") != "0")
                cfg.Enabled = true;
            if (!cfg.Enabled || cfg.Endpoints.Count == 0) return null;
            return cfg;
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
            max_tokens = 400,   // 推理型模型会先用预算写 reasoning_content，给足避免 content 为空
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
            var text = msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
                ? (content.GetString() ?? "").Trim() : "";
            // 推理型模型（deepseek-v4.1-flash 实测）在 max_tokens 偏小时 content 为空、
            // 答案落在 reasoning_content 里；不认这一段就等于把可用输出判成失败。
            if (text.Length == 0 && msg.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
                text = (rc.GetString() ?? "").Trim();
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

    public static int RunProbe(string configPath, bool dry, bool verifyChannel = false)
    {
        Cli.Attach();
        // 与 TryLoad 同一套发现逻辑；区别只有一点：**显式探针命令**允许把 example 当回退配置
        // （运行时默认不读它），免得"想试一下还得先复制文件"。
        // 验证/模拟通道独立：读 LLM_VERIFY_*，可与主通道指向不同模型，互不占配额
        var prefix = verifyChannel ? "LLM_VERIFY_" : "LLM_";
        var cfg = ResolveConfig(configPath, out var path, out var fromExample,
            allowExampleOverride: true, envPrefix: prefix);
        if (cfg == null)
        {
            if (path == null)
                Cli.Say($"没有任何 llm 配置（data/llm.json 不存在；也没有 {prefix}ENDPOINT 环境变量；example 未显式允许）");
            else
                Cli.Say($"{path} 不可用（enabled=false 或无端点）"
                    + (fromExample ? "；example 只是样例，复制成 data/llm.json 并改 enabled=true 才会启用"
                                   : "；要联网请把 enabled 改成 true"));
            return 4;
        }
        Cli.Say("配置文件: " + (path ?? $"(环境变量 {prefix}ENDPOINT)"));
        if (fromExample) Cli.Say("注意：用的是 data/llm.example.json（样例文件，不是本机实际配置）");
        Cli.Say($"通道: {prefix}*（{(verifyChannel ? "验证/模拟专用" : "主通道")}）");
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
