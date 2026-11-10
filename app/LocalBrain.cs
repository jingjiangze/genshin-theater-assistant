// ─────────────────────────────────────────────────────────────────────────────
// 内嵌小模型推理：随包自带 llama.cpp 的 llama-server 子进程 + 本机 HTTP。
//
// 为什么不是进程内 P/Invoke / LLamaSharp：
//   · LLamaSharp 0.30 的 NuGet 包公开 API 与其源码 tag 不一致（WithSearchDirectories /
//     WithLibrary 都编译不过，实测 CS1061），其默认原生探测又要求
//     LLamaSharpRuntimes/win-x64/native/<avx>/ 结构，单文件/裸 publish 均拿不到（CI 四轮实测）。
//   · 直接用 llama.cpp C API 需要 P/Invoke 十几个版本相关签名，风险同级。
//   · llama-server 是官方二进制，行为稳定：实测 59 token prompt + 64 token 生成 = **147ms**。
//
// 形态：首次用模型时拉起 llama-server（读应用目录下的 GGUF），退出时杀掉；
// 端口从 18923 起（被占则顺延），已有健康实例则复用。用户机器上不需要装任何东西。
//
// 红线不变：只吃文本、只产文本，不碰游戏进程。
// ─────────────────────────────────────────────────────────────────────────────
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace TheaterAssistant;

internal sealed class LocalBrain : IDisposable
{
    /// <summary>与训练时的 system 保持一致（改这里要同步重训）。</summary>
    public const string DefaultSystem = "你是原神「幻想真境剧诗」的随身助手。只给一句可照做的中文指令；不确定就说「不确定」。";

    private const int BasePort = 18923;

    private readonly string _gguf;
    private readonly string _serverExe;
    private readonly int _threads;
    private readonly int _ctxSize;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Process _proc;
    private int _port;
    private bool _disposed;

    /// <summary>上一次 TryLoad 失败的原因（诊断/自检用）。</summary>
    public static string LastLoadError { get; private set; } = "";

    public string ModelPath => _gguf;
    public string Status { get; private set; } = "";
    public bool Ready => !_disposed && _gguf != null && File.Exists(_gguf) && _serverExe != null && File.Exists(_serverExe);

    /// <summary>最近一次应答耗时（诊断/自检用）。</summary>
    public long LastLatencyMs { get; private set; }

    /// <summary>最近一次是否复用了已在跑的进程（false = 本次冷启动）。</summary>
    public bool LastWasWarm { get; private set; }

    private LocalBrain(string gguf, string serverExe, int threads, int ctxSize)
    {
        _gguf = gguf;
        _serverExe = serverExe;
        _threads = threads;
        _ctxSize = ctxSize;
        _http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(8000) };
        Status = $"llama-server 就绪（{new FileInfo(gguf).Length / 1048576.0:0.0}MB 模型）";
    }

    /// <summary>
    /// 只做「文件在不在」的检查，不启动进程（避免启动即吃内存）。
    /// serverExe 缺省时在 exe 同级 llama-server\ 下找。
    /// </summary>
    public static LocalBrain TryLoad(string ggufPath, string serverExe = null, int threads = 6, int ctxSize = 1024)
    {
        try
        {
            LastLoadError = "";
            if (string.IsNullOrWhiteSpace(ggufPath) || !File.Exists(ggufPath))
            {
                LastLoadError = "GGUF 不存在：" + ggufPath;
                return null;
            }

            var exe = serverExe;
            if (string.IsNullOrWhiteSpace(exe))
            {
                var cand = Path.Combine(AppContext.BaseDirectory, "llama-server", "llama-server.exe");
                if (File.Exists(cand)) exe = cand;
            }
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            {
                LastLoadError = "llama-server.exe 不存在（应随包在 llama-server\\ 下）";
                return null;
            }

            return new LocalBrain(ggufPath, exe, threads, ctxSize);
        }
        catch (Exception ex)
        {
            LastLoadError = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    /// <summary>
    /// 问一句。返回 null = 超时/失败（调用方必须能处理 null，自动落回 ○语料/·规则）。
    /// timeoutMs 默认 2000ms：热身态实测 147ms，留足冷启动余量。
    /// </summary>
    public string Ask(string user, int maxTokens = 72, int timeoutMs = 2000)
    {
        if (!Ready || string.IsNullOrWhiteSpace(user)) return null;
        if (!_gate.Wait(Math.Max(100, timeoutMs))) return null;

        var sw = Stopwatch.StartNew();
        try
        {
            var warm = EnsureServer(timeoutMs);
            if (_port == 0) return null;
            LastWasWarm = warm;

            var prompt =
                "<|im_start|>system\n" + DefaultSystem + "<|im_end|>\n" +
                "<|im_start|>user\n" + user.Trim() + "<|im_end|>\n" +
                "<|im_start|>assistant\n";

            var payload = JsonSerializer.Serialize(new
            {
                prompt,
                n_predict = maxTokens,
                temperature = 0.0,
                stop = new[] { "<|im_end|>" },
                cache_prompt = true,
                stream = false,
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{_port}/completion")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            using var resp = _http.Send(req, HttpCompletionOption.ResponseContentRead);
            if (!resp.IsSuccessStatusCode) return null;

            var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(body);
            var text = doc.RootElement.TryGetProperty("content", out var c) ? c.GetString() : null;
            text = (text ?? "").Trim();

            LastLatencyMs = sw.ElapsedMilliseconds;
            return text.Length == 0 ? null : text;
        }
        catch
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>确保 server 在跑；返回 true=复用已有进程，false=本次冷启动。</summary>
    private bool EnsureServer(int timeoutMs)
    {
        if (_port != 0 && Health(_port)) return true;

        // 已有健康实例（例如上一轮应用残留）：直接复用
        for (var p = BasePort; p < BasePort + 8; p++)
        {
            if (Health(p)) { _port = p; return true; }
        }

        var port = BasePort;
        for (var p = BasePort; p < BasePort + 8; p++)
        {
            if (!PortBusy(p)) { port = p; break; }
        }

        var psi = new ProcessStartInfo
        {
            FileName = _serverExe,
            Arguments = $"-m \"{_gguf}\" --port {port} -t {_threads} -c {_ctxSize} --no-webui --no-warmup",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(_serverExe) ?? AppContext.BaseDirectory,
        };
        _proc = Process.Start(psi);
        if (_proc == null) return false;

        // 等 /health（模型 26MB，实测 <1s；给足 6s 上限）
        var deadline = Environment.TickCount64 + Math.Max(3000, timeoutMs + 4000);
        while (Environment.TickCount64 < deadline)
        {
            if (Health(port)) { _port = port; return false; }
            if (_proc.HasExited) return false;
            Thread.Sleep(120);
        }
        return false;
    }

    private bool Health(int port)
    {
        try
        {
            using var cts = new CancellationTokenSource(300);
            using var resp = _http.GetAsync($"http://127.0.0.1:{port}/health", cts.Token).GetAwaiter().GetResult();
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static bool PortBusy(int port)
    {
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            var task = tcp.ConnectAsync("127.0.0.1", port);
            return task.Wait(200);
        }
        catch { return false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _http.Dispose(); } catch { }
        try
        {
            if (_proc is { HasExited: false })
            {
                _proc.Kill(entireProcessTree: true);
                _proc.WaitForExit(1500);
            }
        }
        catch { }
        try { _proc?.Dispose(); } catch { }
        try { _gate.Dispose(); } catch { }
    }
}
