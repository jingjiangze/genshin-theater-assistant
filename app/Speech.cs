using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace TheaterAssistant;

/// <summary>
/// Windows 内置语音（SAPI5，COM 调用，**零依赖零下载**）。
///
/// 审计结论（本机实测）：`SAPI.SpVoice` 可用，语音 2 个，其中
/// `Microsoft Huihui Desktop - Chinese (Simplified)` 是简体中文 → 完全离线可读中文。
/// OneCore 里还有 Huihui/Kangkang/Yaoyao（音质更好），但 SAPI COM 看不到，故不采用。
///
/// 设计要点：
///  · COM 对象有线程亲和性 → 用一条专用 STA 线程 + 队列，调用方永不阻塞、永不抛。
///  · 播报是可选的：没有语音、或被禁用时，全部静默降级（不报错、不影响识别）。
/// </summary>
internal static class Speech
{
    internal sealed class Config
    {
        public bool Enabled { get; set; } = true;
        /// <summary>语速，SAPI 范围 -10~10，0 为常速。</summary>
        public int Rate { get; set; } = 1;
        public int Volume { get; set; } = 100;
        /// <summary>两次播报的最小间隔（秒），避免刷屏。</summary>
        public int MinSecondsBetween { get; set; } = 6;
        /// <summary>只播报这些状态（空 = 全部）。</summary>
        public string[] OnlyKinds { get; set; } = Array.Empty<string>();
    }

    private static Config _cfg = new();
    private static BlockingCollection<string> _queue;
    private static Thread _worker;
    private static volatile bool _ready;
    private static DateTime _lastSpoken = DateTime.MinValue;
    private static string _lastText = "";

    public static bool Ready => _ready;

    /// <summary>去掉给眼睛看的符号，只留能读的字：▶ / ｜ / 括号里的分数等。</summary>
    internal static string NormalizeForSpeech(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = text.Trim();
        s = s.TrimStart('▶', ' ', '　');
        if (s.StartsWith("现在做：", StringComparison.Ordinal)) s = s["现在做：".Length..];
        s = Regex.Replace(s, @"[▶◀|｜·•\u2500-\u257F]", " ");
        s = Regex.Replace(s, @"\s+", " ");
        return s.Trim();
    }

    /// <summary>读 data/speech.json；文件不存在时用默认（开启、常速）。</summary>
    public static void LoadAndConfigure(string path)
    {
        var cfg = new Config();
        try
        {
            if (File.Exists(path))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                var r = doc.RootElement;
                if (r.TryGetProperty("enabled", out var e) && e.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                    cfg.Enabled = e.GetBoolean();
                if (r.TryGetProperty("rate", out var rate) && rate.ValueKind == System.Text.Json.JsonValueKind.Number) cfg.Rate = rate.GetInt32();
                if (r.TryGetProperty("volume", out var vol) && vol.ValueKind == System.Text.Json.JsonValueKind.Number) cfg.Volume = vol.GetInt32();
                if (r.TryGetProperty("minSecondsBetween", out var ms) && ms.ValueKind == System.Text.Json.JsonValueKind.Number) cfg.MinSecondsBetween = ms.GetInt32();
                if (r.TryGetProperty("onlyKinds", out var ok) && ok.ValueKind == System.Text.Json.JsonValueKind.Array)
                    cfg.OnlyKinds = ok.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray();
            }
        }
        catch { }
        Configure(cfg);
    }

    public static void Configure(Config cfg)
    {
        _cfg = cfg ?? new Config();
        if (!_cfg.Enabled) return;
        Start();
    }

    private static void Start()
    {
        if (_ready) return;
        try
        {
            _queue = new BlockingCollection<string>(new ConcurrentQueue<string>());
            _worker = new Thread(() =>
            {
                dynamic voice = null;
                try
                {
                    var t = Type.GetTypeFromProgID("SAPI.SpVoice");
                    if (t == null) return;
                    voice = Activator.CreateInstance(t);
                    TrySelectChineseVoice(voice);
                    try { voice.Rate = Math.Clamp(_cfg.Rate, -10, 10); } catch { }
                    try { voice.Volume = Math.Clamp(_cfg.Volume, 0, 100); } catch { }
                    _ready = true;

                    foreach (var text in _queue.GetConsumingEnumerable())
                    {
                        try
                        {
                            // 1 = SVSFlagsAsync：不阻塞队列；2 = SVSFPurgeBeforeSpeak：新话打断旧话
                            voice.Speak(text, 1 | 2);
                        }
                        catch { }
                    }
                }
                catch { }
                finally { _ready = false; }
            })
            { IsBackground = true, Name = "ta-speech" };
            _worker.SetApartmentState(ApartmentState.STA);
            _worker.Start();
        }
        catch { _ready = false; }
    }

    private static void TrySelectChineseVoice(dynamic voice)
    {
        try
        {
            var voices = voice.GetVoices();
            int n = voices.Count;
            for (int i = 0; i < n; i++)
            {
                var v = voices.Item(i);
                string desc = "";
                try { desc = v.GetDescription(); } catch { }
                if (desc.Contains("Chinese", StringComparison.OrdinalIgnoreCase)
                    || desc.Contains("中文") || desc.Contains("Huihui") || desc.Contains("Yaoyao")
                    || desc.Contains("Kangkang"))
                {
                    voice.Voice = v;
                    return;
                }
            }
        }
        catch { }
    }

    /// <summary>排队播报（异步、丢弃式）：调用方无需关心失败。</summary>
    public static void Say(string text)
    {
        if (!_cfg.Enabled || !_ready || _queue == null) return;
        var s = NormalizeForSpeech(text);
        if (s.Length < 2) return;

        // 同一条内容 + 时间窗内不重复播
        if (s == _lastText && (DateTime.UtcNow - _lastSpoken).TotalSeconds < Math.Max(2, _cfg.MinSecondsBetween)) return;
        _lastText = s;
        _lastSpoken = DateTime.UtcNow;

        try { _queue.Add(s); } catch { }
    }

    /// <summary>按状态+幕次播报（内部按 OnlyKinds 过滤）。</summary>
    public static void Announce(string kind, int? act, string nextActionLine)
    {
        if (!_cfg.Enabled) return;
        if (_cfg.OnlyKinds is { Length: > 0 } && !_cfg.OnlyKinds.Contains(kind ?? "")) return;
        var text = NormalizeForSpeech(nextActionLine);
        if (text.Length == 0 && act.HasValue) text = $"第{act}幕";
        Say(text);
    }

    // ---------------- CLI ----------------

    public static int RunCli(string text, bool listVoices)
    {
        Cli.Attach();
        Configure(new Config { Enabled = true, Rate = 0 });

        // 等 COM 线程就绪（最多 2 秒）
        for (int i = 0; i < 20 && !_ready; i++) Thread.Sleep(100);

        if (_ready)
        {
            Cli.Say("SAPI 可用：将用系统内置语音播报（离线）");
            if (listVoices)
            {
                try
                {
                    var t = Type.GetTypeFromProgID("SAPI.SpVoice");
                    dynamic v = Activator.CreateInstance(t);
                    var vs = v.GetVoices();
                    for (int i = 0; i < vs.Count; i++)
                        Cli.Say($"  [{i}] {vs.Item(i).GetDescription()}");
                }
                catch (Exception ex) { Cli.Say("列语音失败: " + ex.Message); }
            }
        }
        else
        {
            Cli.Say("SKIP 本机没有可用的 SAPI 语音（或初始化失败）");
            return 4;
        }

        if (string.IsNullOrWhiteSpace(text)) text = "剧诗助手语音自检，第一行永远是你现在该做的事。";
        Cli.Say("播报: " + Text(text));
        Say(text);
        Thread.Sleep(1800);        // 给一点时间让它读完开头
        return 0;

        static string Text(string s) => NormalizeForSpeech(s);
    }
}
