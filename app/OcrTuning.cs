// ─────────────────────────────────────────────────────────────────────────────
// 本文件包含对 BetterGI（https://github.com/babalae/better-genshin-impact）实现的
// 参考或衍生。原版权：Copyright (c) babalae 及 BetterGI contributors（GPL-3.0）。
// 本项目在 WinForms + .NET 8 下重写/改写，修改时间 2026-11。
// 本文件以 GPL-3.0 分发，全文见仓库根 LICENSE，来源说明见 NOTICE.md。
// ─────────────────────────────────────────────────────────────────────────────
using System.Text.Json;

namespace TheaterAssistant;

/// <summary>
/// OCR 运行时调优：参数全部来自 data/decision-policy.json 的 <c>ocr</c> 段，不硬编码。
///
/// 审计依据（docs/BETTERGI-OCR-AUDIT.md + RapidOcrNet 源码）：
///   · RapidOcrNet 4.2.0 公开 <c>InitModels(..., SessionOptions)</c>，可注入执行提供器 → DirectML 可零架构改动接入；
///   · 抓屏与游戏同机，OCR 线程必须受限，否则拖慢游戏本身；
///   · DirectML 用反射探测：包内无 EP 就回退 CPU，绝不因加速缺失而不可用。
///
/// 与 <see cref="OcrTextPipeline"/> 的分工：
///   本类管"引擎怎么跑"（线程/EP），OcrTextPipeline 管"文本怎么排"（置信度/同排）。
/// </summary>
internal static class OcrTuning
{
    public static bool EnableDirectML { get; private set; } = true;
    public static int DirectMLDeviceId { get; private set; } = 0;
    /// <summary>0 = 交给 ONNX Runtime 自己决定（BetterGI 的 numThread 语义）。</summary>
    /// <summary>推理线程数。0 = 按硬件画像自适应（HardwareInfo.SuggestThreads）。</summary>
    public static int NumThreads { get; private set; } = 0;
    public static string Source { get; private set; } = "(未加载，用内置默认)";

    /// <summary>
    /// 实际生效的执行提供器（CPU / DirectML），由 OcrService 初始化时写回。
    /// 放在纯逻辑层是为了让**跨平台 CLI 也能读**（OcrService 依赖 System.Drawing/Skia，不进 CLI）。
    /// </summary>
    public static string EpStatus { get; internal set; } = "未知（OCR 引擎尚未初始化）";

    /// <summary>
    /// 模型选择（设置窗写入 ocr.model）：v6 / v5 / auto。
    /// 此前这个字段**只写不读**（审计发现），设置页切了也不生效。
    /// </summary>
    public static string ModelPreference { get; private set; } = "auto";

    /// <summary>参考画布长边（OCR 与模板匹配共用）。低配机调小（如 800）能省算力，代价是小字漏检。</summary>
    public static int ReferenceCanvas { get; private set; } = 960;

    /// <summary>幕次稳定后的重扫间隔（tick 数，1 tick = 扫描间隔）。低配机调大（如 12）。</summary>
    public static int HeavyEveryStable { get; private set; } = 8;

    public static void Load(string policyPath)
    {
        Source = "(缺 decision-policy.json 的 ocr 段，用内置默认)";
        try
        {
            if (!File.Exists(policyPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(policyPath));
            if (!doc.RootElement.TryGetProperty("ocr", out var ocr) || ocr.ValueKind != JsonValueKind.Object)
                return;

            if (ocr.TryGetProperty("enableDirectML", out var dml) &&
                (dml.ValueKind == JsonValueKind.True || dml.ValueKind == JsonValueKind.False))
                EnableDirectML = dml.GetBoolean();
            if (ocr.TryGetProperty("directMLDeviceId", out var dev) && dev.ValueKind == JsonValueKind.Number)
                DirectMLDeviceId = dev.GetInt32();
            if (ocr.TryGetProperty("numThreads", out var nt) && nt.ValueKind == JsonValueKind.Number)
                NumThreads = nt.GetInt32();

            // 环境变量优先于配置文件（换机型/压测时不必改仓库文件）：TA_OCR_THREADS=4
            if (int.TryParse(Environment.GetEnvironmentVariable("TA_OCR_THREADS"), out var envThreads) && envThreads > 0)
                NumThreads = envThreads;

            // 0 = 自适应；上限按可用核夹住，避免 4 核机上配 6 反而过订阅
            if (ocr.TryGetProperty("referenceCanvas", out var rc) && rc.ValueKind == JsonValueKind.Number)
                ReferenceCanvas = Math.Clamp(rc.GetInt32(), 640, 1280);
            if (ocr.TryGetProperty("heavyEveryStable", out var he) && he.ValueKind == JsonValueKind.Number)
                HeavyEveryStable = Math.Clamp(he.GetInt32(), 2, 60);

            if (NumThreads <= 0) NumThreads = HardwareInfo.SuggestThreads();
            NumThreads = Math.Clamp(NumThreads, 1, HardwareInfo.Cores);
            // 环境变量覆盖（排查/AB 用，不改仓库配置）：TA_OCR_MODEL=v5|v6|auto
            var envModel = (Environment.GetEnvironmentVariable("TA_OCR_MODEL") ?? "").Trim().ToLowerInvariant();
            if (envModel is "v5" or "v6" or "auto") ModelPreference = envModel;
            if (ocr.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String)
            {
                var v = (m.GetString() ?? "").Trim().ToLowerInvariant();
                if (v is "v6" or "v5" or "auto") ModelPreference = v;
            }

            Source = Path.GetFileName(policyPath) + " → ocr";
        }
        catch { }
    }

    public static int RunCli()
    {
        Cli.Attach();
        Load(AppPaths.Data("decision-policy.json"));
        Cli.Say($"OCR 调优来源: {Source}");
        Cli.Say($"  enableDirectML={EnableDirectML} deviceId={DirectMLDeviceId} numThreads={NumThreads} model={ModelPreference}");
        Cli.Say($"  硬件画像: {HardwareInfo.Describe()}");
        Cli.Say($"  实际执行提供器: {EpStatus}");
        return 0;
    }
}
