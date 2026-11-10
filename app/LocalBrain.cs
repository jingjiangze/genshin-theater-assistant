// ─────────────────────────────────────────────────────────────────────────────
// 内嵌小模型推理（llama.cpp / LLamaSharp，GGUF）。
//
// 为什么内嵌而不是 HTTP：
//   实测（2026-11-10，MiniMind-26M Q8_0，同一台 i5-13400F）：
//     · 本地 HTTP（Python + torch）  985–1487ms/答
//     · llama.cpp 进程内 Q8          加载 35ms ＋ 生成 63 token 49ms ≈ **147ms/答**
//   差距来自两处：torch eager 对 26M 模型是逐 op 开销；HTTP 还有进程/序列化开销。
//   llama.cpp 的 C 实现 + Q8 内核在这个尺度上快一个量级，且不吃 Python 环境。
//
// 设计约束：
//   · 只读红线不变：模型只吃文本、只产文本，不碰游戏进程。
//   · 单飞：同一时刻只允许一次推理（避免主循环与语音/详情面板并发出两个答案）。
//   · 超时可配：默认 1200ms；超时就返回 null，调用方自动落回 ○语料/·规则。
//   · 提示词手搓：GGUF 里没有 chat_template（tokenizer_config 未带），
//     用 Qwen 式 <|im_start|> 模板；注意 **首 token 由 llama 自动补 BOS**（BOS 即 <|im_start|>），
//     所以提示字符串里第一个 <|im_start|> 不写，否则会重复。
// ─────────────────────────────────────────────────────────────────────────────
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TheaterAssistant;

internal sealed class LocalBrain : IDisposable
{
    /// <summary>与模型训练时的 system 保持一致（改这里要同步重训）。</summary>
    public const string DefaultSystem = "你是原神「幻想真境剧诗」的随身助手。只给一句可照做的中文指令；不确定就说「不确定」。";

    private readonly LLama.LLamaWeights _weights;
    private readonly LLama.StatelessExecutor _exec;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public string ModelPath { get; }
    public string Status { get; private set; } = "";
    public bool Ready => _weights != null && !_disposed;

    /// <summary>上一次 TryLoad 失败的原因（诊断/自检用，不吞异常信息）。</summary>
    public static string LastLoadError { get; private set; } = "";

    /// <summary>最近一次应答耗时（诊断/自检用）。</summary>
    public long LastLatencyMs { get; private set; }

    private LocalBrain(LLama.LLamaWeights weights, LLama.StatelessExecutor exec, string path, string status)
    {
        _weights = weights;
        _exec = exec;
        ModelPath = path;
        Status = status;
    }

    /// <summary>
    /// 加载 GGUF；文件不存在/加载失败返回 null（调用方据此回退）。
    /// threads=0 交给 llama 自己定（物理核数），实测 6 线程在 13400F 上最快。
    /// </summary>
    public static LocalBrain TryLoad(string ggufPath, int threads = 6, uint contextSize = 1024)
    {
        if (string.IsNullOrWhiteSpace(ggufPath) || !File.Exists(ggufPath)) return null;
        try
        {
            var mp = new LLama.Common.ModelParams(ggufPath)
            {
                ContextSize = contextSize,
                GpuLayerCount = 0,          // CPU 后端；将来接 Vulkan 后端再放开
                BatchSize = 256,
            };
            if (threads > 0) mp.Threads = threads;

            var sw = Stopwatch.StartNew();
            var weights = LLama.LLamaWeights.LoadFromFile(mp);
            var exec = new LLama.StatelessExecutor(weights, mp);
            sw.Stop();
            LastLoadError = "";
            return new LocalBrain(weights, exec, ggufPath, $"GGUF 已加载（{sw.ElapsedMilliseconds}ms，{new FileInfo(ggufPath).Length / 1048576.0:0.0}MB）");
        }
        catch (Exception ex)
        {
            // 带上完整原因（含内层），否则单文件发布下"加载失败"无从定位
            LastLoadError = ex.GetType().Name + ": " + ex.Message
                            + (ex.InnerException != null ? " ← " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message : "");
            return null;
        }
    }

    /// <summary>
    /// 问一句。返回 null = 超时/失败/未就绪（调用方必须能处理 null）。
    /// timeoutMs 默认 1200ms：实测 147ms，留 8 倍余量给冷启动/GC。
    /// </summary>
    public string Ask(string user, int maxTokens = 72, int timeoutMs = 1200)
    {
        if (!Ready || string.IsNullOrWhiteSpace(user)) return null;
        if (!_gate.Wait(Math.Max(50, timeoutMs))) return null;

        var sw = Stopwatch.StartNew();
        try
        {
            var prompt =
                "system\n" + DefaultSystem + "<|im_end|>\n" +
                "<|im_start|>user\n" + user.Trim() + "<|im_end|>\n" +
                "<|im_start|>assistant\n";

            var ip = new LLama.Common.InferenceParams
            {
                MaxTokens = maxTokens,
                AntiPrompts = new List<string> { "<|im_end|>" },
                SamplingPipeline = new LLama.Sampling.DefaultSamplingPipeline { Temperature = 0.0f },
            };

            var sb = new StringBuilder();
            using var cts = new CancellationTokenSource(timeoutMs);
            var task = Task.Run(async () =>
            {
                await foreach (var tok in _exec.InferAsync(prompt, ip, cts.Token).ConfigureAwait(false))
                    sb.Append(tok);
            }, cts.Token);

            // 允许超时后放弃等待（推理线程由 llama 内部收尾，不阻塞主循环）
            if (!task.Wait(Math.Max(50, timeoutMs)))
            {
                try { cts.Cancel(); } catch { }
                return null;
            }

            var text = sb.ToString().Trim();
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _exec?.Context?.Dispose(); } catch { }
        try { _weights?.Dispose(); } catch { }
        try { _gate.Dispose(); } catch { }
    }
}
