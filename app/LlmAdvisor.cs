using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TheaterAssistant;

/// <summary>
/// 可选的大模型顾问：把「当前状态 + 本幕规则 + 你的池子」发给你自己配置的 OpenAI 兼容端点，
/// 让模型把建议压缩成 2–3 行「下一步做什么」。
///
/// 设计红线：
///   - 默认关闭（data/llm.json 不存在或 enabled=false 时完全不联网）
///   - 规则引擎永远先算，模型只做「改写/排序说明」，失败即回退规则文本
///   - 3 秒超时；不发账号、不发截图、不发 cookie；只发幕次、首领名、池子统计数字
/// </summary>
internal sealed class LlmAdvisor : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly string _endpoint;
    private readonly string _model;
    private readonly string _apiKey;

    public bool Enabled { get; }

    /// <summary>最近一次调用的健康状态（给悬浮窗诊断用）：ok / 冷却中 / 未探测。</summary>
    public string Health { get; private set; } = "未探测";
    private DateTime _coolUntil = DateTime.MinValue;

    private LlmAdvisor(string endpoint, string model, string apiKey)
    {
        _endpoint = endpoint;
        _model = model;
        _apiKey = apiKey;
        Enabled = true;
    }

    public static LlmAdvisor TryLoad(string configPath)
    {
        try
        {
            if (!File.Exists(configPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            var r = doc.RootElement;
            if (!r.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.True) return null;

            var endpoint = r.TryGetProperty("endpoint", out var ep) ? ep.GetString() : null;
            var model = r.TryGetProperty("model", out var mo) ? mo.GetString() : null;
            var keyEnv = r.TryGetProperty("apiKeyEnv", out var ke) ? ke.GetString() : null;

            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(model)) return null;
            var key = string.IsNullOrWhiteSpace(keyEnv) ? "" : Environment.GetEnvironmentVariable(keyEnv) ?? "";
            return new LlmAdvisor(endpoint, model, key);
        }
        catch { return null; }
    }

    /// <summary>返回 null 表示"不用模型 / 调用失败"，调用方应继续用规则文本。</summary>
    public async Task<string> RefineAsync(string stateLine, IReadOnlyList<string> ruleLines, string rosterLine)
    {
        if (!Enabled) return null;

        // 端点健康检查：失败后冷却 5 分钟，避免每次幕次变化都等 3 秒超时
        if (DateTime.UtcNow < _coolUntil) { Health = $"冷却中(至 {_coolUntil:HH:mm:ss})"; return null; }

        try
        {
            var system = "你是《原神》幻想真境剧诗的战斗顾问。只输出 3 条以内的短句，每条不超过 28 个汉字，" +
                         "必须是玩家下一步能立刻执行的动作（带元素/技能/时间窗口）。不要寒暄，不要复述规则。";
            var user = $"当前：{stateLine}\n规则与数据：{string.Join("；", ruleLines)}\n账号：{rosterLine}";

            var payload = JsonSerializer.Serialize(new
            {
                model = _model,
                temperature = 0.2,
                max_tokens = 220,
                messages = new object[]
                {
                    new { role = "system", content = system },
                    new { role = "user", content = user }
                }
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            if (!string.IsNullOrEmpty(_apiKey))
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);

            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Health = $"HTTP {(int)resp.StatusCode} → 冷却 5 分钟";
                _coolUntil = DateTime.UtcNow.AddMinutes(5);
                return null;
            }
            Health = "ok";

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            var text = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (Exception ex)
        {
            Health = "不可用(" + ex.GetType().Name + ") → 冷却 5 分钟";
            _coolUntil = DateTime.UtcNow.AddMinutes(5);
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
