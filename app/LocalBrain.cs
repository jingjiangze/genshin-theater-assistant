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
// 复用实例的归属：端口上已有健康实例时**收养**它 —— 读模型同目录的 sidecar
// `.server-<port>.pid`（pid + 模型路径 + 启动时间），校验模型路径与进程名后记下 pid，
// Dispose 时按 pid 杀掉。不收养的话，上一轮残留的实例永远关不掉，越积越多的孤儿进程
// 会一直吃内存（本进程退出 → 下轮"复用" → 又杀不掉，恶性循环）。
//
// 超时：AskAsync 的 timeoutMs 是**生成阶段的真超时**（CancellationTokenSource → SendAsync 的 token），
// 不再是"只卡进程门"的装饰参数；冷启动等 /health 另有独立下限（见 HealthWaitMs）。
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
using System.Threading.Tasks;

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
    /// <summary>端口上那个 llama-server 的 pid（自己起的或收养的；0 = 未知，Dispose 时无法回收）。</summary>
    private int _serverPid;
    /// <summary>本实例是否负责删 sidecar pid 文件（自己起的、或收养来的都算）。</summary>
    private bool _ownsPidFile;
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
        // 全局 8s 只作兜底：真正的超时判据是每次请求自己的 CancellationTokenSource（见 AskCoreAsync）。
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
    /// 问一句（同步版，保留给既有调用方/CLI/自检）。返回 null = 超时/失败
    /// （调用方必须能处理 null，自动落回 ○语料/·规则）。内部与 <see cref="AskAsync"/> 同一套逻辑。
    /// timeoutMs 默认 2000ms：热身态实测 147ms，留足冷启动余量。
    /// </summary>
    public string Ask(string user, int maxTokens = 72, int timeoutMs = 2000)
    {
        try
        {
            return AskCoreAsync(user, maxTokens, timeoutMs, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // 与 AskAsync 同样的承诺：永不抛，失败只落 Status
            Status = "本地模型失败：" + ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    /// <summary>
    /// 问一句（异步版）。主循环跑在 UI 线程的 DispatcherTimer 上，冷启动要等 /health，
    /// 同步 Ask 会把界面卡住数秒 —— 新调用方一律用这个。
    /// 返回 null = 超时 / 取消 / 失败，**永不抛异常**；最近一次失败原因留在 <see cref="Status"/>（供日志）。
    /// timeoutMs 默认 1200ms，是**生成阶段的真超时**（CancelAfter → SendAsync 的 token），
    /// 不是"只卡住进程门"的装饰参数。冷启动的 /health 等待另有独立下限，见 HealthWaitMs。
    /// </summary>
    public async Task<string> AskAsync(string user, int maxTokens = 72, int timeoutMs = 1200, CancellationToken ct = default)
    {
        try
        {
            return await AskCoreAsync(user, maxTokens, timeoutMs, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // AskCoreAsync 内部已捕获；这里是最后一道保险，绝不让异常冒到 UI 线程
            Status = "本地模型：已取消（或超出 " + Math.Max(1, timeoutMs) + "ms 预算）";
            return null;
        }
        catch (Exception ex)
        {
            Status = "本地模型失败：" + ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    /// <summary>
    /// 同步/异步共用的实现。任一环节失败都只写 <see cref="Status"/> 并返回 null，不抛。
    /// 三层预算各自独立（都是上限，不是叠加的承诺）：
    ///   ① 进程门排队 ≤ max(100, timeoutMs)  ② 冷启动 /health ≤ max(1500, timeoutMs)  ③ 生成 = timeoutMs。
    /// </summary>
    private async Task<string> AskCoreAsync(string user, int maxTokens, int timeoutMs, CancellationToken ct)
    {
        if (!Ready || string.IsNullOrWhiteSpace(user)) return null;

        var budget = Math.Max(1, timeoutMs);
        var entered = false;
        var sw = Stopwatch.StartNew();
        try
        {
            // ① 进程门：同一时刻只放一个生成进去。原实现只在这里用了 timeoutMs，
            //    于是"1.2s 超时"对 HTTP 请求毫无约束（实际最长走 HttpClient.Timeout=8s）。
            entered = await _gate.WaitAsync(TimeSpan.FromMilliseconds(Math.Max(100, budget)), ct)
                               .ConfigureAwait(false);
            if (!entered)
            {
                Status = $"本地模型忙：排队超过 {Math.Max(100, budget)}ms";
                return null;
            }

            // ② 服务就绪（可能是冷启动：拉进程 + 等 /health）
            var warm = await EnsureServerAsync(budget, ct).ConfigureAwait(false);
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

            // ③ 生成：真正的超时在这里。HttpClient.Timeout（8s）只是兜底，
            //    主判据是这个 cts —— 到点 SendAsync 抛 OperationCanceledException，下面转成 null。
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(budget));
            using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{_port}/completion")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token)
                                       .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Status = $"llama-server 返回 HTTP {(int)resp.StatusCode}";
                return null;
            }

            var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var text = doc.RootElement.TryGetProperty("content", out var c) ? c.GetString() : null;
            text = (text ?? "").Trim();

            LastLatencyMs = sw.ElapsedMilliseconds;
            Status = $"llama-server {(warm ? "复用" : "冷启动")} :{_port}"
                     + (_serverPid > 0 ? $" pid={_serverPid}" : "")
                     + $"｜{LastLatencyMs}ms";
            return text.Length == 0 ? null : text;
        }
        catch (OperationCanceledException)
        {
            Status = ct.IsCancellationRequested
                ? "本地模型：调用方已取消"
                : $"本地模型超时：{budget}ms 内未返回";
            return null;
        }
        catch (Exception ex)
        {
            Status = "本地模型失败：" + ex.GetType().Name + ": " + ex.Message;
            return null;
        }
        finally
        {
            if (entered) { try { _gate.Release(); } catch { } }
        }
    }

    /// <summary>
    /// 冷启动等 /health 的上限。26MB GGUF 实测 &lt;1s，但冷盘 / 首次释放原生库 / 杀软扫描
    /// 会拖到 2s+，所以保留 1500ms 下限；timeoutMs 更大时按 timeoutMs。
    /// **独立于生成预算**：冷启动不该被 1.2s 的生成超时判死（否则每次首问都必然 null）。
    /// </summary>
    private static int HealthWaitMs(int timeoutMs) => Math.Max(1500, timeoutMs);

    /// <summary>
    /// 确保 server 在跑；返回 true=复用已有进程，false=本次冷启动。
    /// 复用来的实例会被"收养"（记下 pid，Dispose 时回收），否则就是孤儿进程。
    /// </summary>
    private async Task<bool> EnsureServerAsync(int timeoutMs, CancellationToken ct)
    {
        if (_port != 0 && await HealthAsync(_port, ct).ConfigureAwait(false)) return true;

        // 已有健康实例（例如上一轮应用残留）：复用 + 收养
        for (var p = BasePort; p < BasePort + 8; p++)
        {
            if (!await HealthAsync(p, ct).ConfigureAwait(false)) continue;
            _port = p;
            AdoptExisting(p);
            return true;
        }

        var port = BasePort;
        for (var p = BasePort; p < BasePort + 8; p++)
        {
            if (!await PortBusyAsync(p, ct).ConfigureAwait(false)) { port = p; break; }
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
        if (_proc == null) { Status = "llama-server 启动失败（Process.Start 返回 null）"; return false; }

        _serverPid = SafePid(_proc);
        // 自己起的实例也写 sidecar：本进程被强杀时，下一轮启动靠它收养并回收
        _ownsPidFile = WritePidFile(port, _serverPid);

        string errText = "";
        try
        {
            _proc.ErrorDataReceived += (_, e) => { if (e.Data != null) errText = (errText + " " + e.Data).Trim(); if (errText.Length > 400) errText = errText[^400..]; };
            _proc.BeginErrorReadLine();
            _proc.BeginOutputReadLine();
        }
        catch { }

        // 等 /health（模型 26MB，实测 <1s；上限见 HealthWaitMs）
        var waitMs = HealthWaitMs(timeoutMs);
        var deadline = Environment.TickCount64 + waitMs;
        while (Environment.TickCount64 < deadline)
        {
            if (await HealthAsync(port, ct).ConfigureAwait(false)) { _port = port; return false; }
            if (_proc.HasExited)
            {
                Status = $"llama-server 退出(code={(int)_proc.ExitCode})：{errText}";
                DeletePidFile(port, _serverPid);
                return false;
            }
            await Task.Delay(120, ct).ConfigureAwait(false);
        }
        Status = $"llama-server 未在 {waitMs}ms 内就绪：" + errText;
        DeletePidFile(port, _serverPid);   // 没就绪就别留 pid 记录（进程仍活着的由 Dispose 兜底杀）
        return false;
    }

    /// <summary>
    /// 复用端口上已存在的实例：读 sidecar 认领 pid，Dispose 才能回收它。
    /// 没有可用 pid 记录时仍然复用（不影响本轮可用性），但退出时回收不了 —— 记进 Status 供日志。
    /// </summary>
    private void AdoptExisting(int port)
    {
        var pid = TryReadPidFile(port);
        if (pid <= 0)
        {
            Status = $"复用 :{port} 的既有 llama-server（无 pid 记录，退出时不回收）";
            return;
        }
        _serverPid = pid;
        _ownsPidFile = true;
        Status = $"复用 :{port} 的既有 llama-server（pid={pid}，已收养）";
    }

    // ---------------- sidecar pid 文件（收养 / 回收复用实例） ----------------

    /// <summary>
    /// 路径规则：**模型同目录** `.server-&lt;port&gt;.pid`（不写进 data/，避免随包分发带上别人的 pid）。
    /// 内容 JSON：{ pid, model(完整路径), startedUtc }。
    /// </summary>
    private string PidFilePath(int port)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_gguf));
        if (string.IsNullOrEmpty(dir)) dir = AppContext.BaseDirectory;
        return Path.Combine(dir, $".server-{port}.pid");
    }

    private bool WritePidFile(int port, int pid)
    {
        if (pid <= 0) return false;
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                pid,
                model = Path.GetFullPath(_gguf),
                startedUtc = DateTime.UtcNow.ToString("O"),
            });
            File.WriteAllText(PidFilePath(port), json);
            return true;
        }
        catch (Exception ex)
        {
            Status = "写 pid 文件失败：" + ex.GetType().Name + "（退出时可能回收不了 llama-server）";
            return false;
        }
    }

    private void DeletePidFile(int port, int pid)
    {
        try
        {
            var file = PidFilePath(port);
            if (!File.Exists(file)) return;
            // 只删指向我们这台的记录：同端口换了实例时不要误删别人的
            if (pid > 0)
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("pid", out var p)
                    && p.ValueKind == JsonValueKind.Number
                    && p.GetInt32() != pid) return;
            }
            File.Delete(file);
        }
        catch { }
    }

    /// <summary>
    /// 读 sidecar，返回可回收的 pid（0 = 没有 / 过期 / 不是同一模型 / 不是 llama-server）。
    /// 三重校验是为了**不误杀**：模型路径一致 + 进程仍活着 + 进程名与 _serverExe 同名。
    /// </summary>
    private int TryReadPidFile(int port)
    {
        try
        {
            var file = PidFilePath(port);
            if (!File.Exists(file)) return 0;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var pid = doc.RootElement.TryGetProperty("pid", out var p) && p.ValueKind == JsonValueKind.Number
                ? p.GetInt32() : 0;
            var model = doc.RootElement.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() ?? "" : "";
            if (pid <= 0) return 0;
            if (!SamePath(model, _gguf)) return 0;
            using var proc = Process.GetProcessById(pid);
            if (proc.HasExited) return 0;
            var expect = Path.GetFileNameWithoutExtension(_serverExe);
            if (!string.Equals(proc.ProcessName, expect, StringComparison.OrdinalIgnoreCase)) return 0;
            return pid;
        }
        catch { return 0; }
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static int SafePid(Process proc)
    {
        try { return proc?.Id ?? 0; } catch { return 0; }
    }

    private async Task<bool> HealthAsync(int port, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(300);
            using var resp = await _http.GetAsync($"http://127.0.0.1:{port}/health", cts.Token)
                                       .ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>端口是否已被监听。200ms 内没连上按"空闲"处理（与原同步实现同语义）。</summary>
    private static async Task<bool> PortBusyAsync(int port, CancellationToken ct)
    {
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(200);
            await tcp.ConnectAsync("127.0.0.1", port, cts.Token).ConfigureAwait(false);
            return true;   // 连上了 = 被监听
        }
        catch (OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();   // 调用方主动取消要冒出去，别当成"端口空闲"
            return false;
        }
        catch { return false; }   // 拒绝连接 / 超时 = 空闲
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _http.Dispose(); } catch { }

        var pid = _serverPid;
        var port = _port;

        try
        {
            if (_proc is { HasExited: false })
            {
                _proc.Kill(entireProcessTree: true);
                _proc.WaitForExit(1500);
            }
        }
        catch { }

        // 复用/收养来的实例没有 _proc 句柄，必须按 pid 回收：否则上一轮残留的 server
        // 会一直被"复用"、永远关不掉（孤儿进程，越积越多）。
        if (pid > 0 && (_proc == null || SafePid(_proc) != pid))
        {
            try
            {
                using var other = Process.GetProcessById(pid);
                if (!other.HasExited)
                {
                    other.Kill(entireProcessTree: true);
                    other.WaitForExit(1500);
                }
            }
            catch { }   // 已退出 / pid 被复用 / 权限不足：只当已回收，绝不冒到上层
        }

        try { _proc?.Dispose(); } catch { }
        if (_ownsPidFile && port != 0) DeletePidFile(port, pid);
        try { _gate.Dispose(); } catch { }
    }
}
