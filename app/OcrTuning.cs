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
    public static int NumThreads { get; private set; } = 0;
    public static string Source { get; private set; } = "(未加载，用内置默认)";

    /// <summary>
    /// 实际生效的执行提供器（CPU / DirectML），由 OcrService 初始化时写回。
    /// 放在纯逻辑层是为了让**跨平台 CLI 也能读**（OcrService 依赖 System.Drawing/Skia，不进 CLI）。
    /// </summary>
    public static string EpStatus { get; internal set; } = "未知（OCR 引擎尚未初始化）";

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

            Source = Path.GetFileName(policyPath) + " → ocr";
        }
        catch { }
    }

    public static int RunCli()
    {
        Cli.Attach();
        Load(AppPaths.Data("decision-policy.json"));
        Cli.Say($"OCR 调优来源: {Source}");
        Cli.Say($"  enableDirectML={EnableDirectML} deviceId={DirectMLDeviceId} numThreads={(NumThreads == 0 ? "自动" : NumThreads.ToString())}");
        Cli.Say($"  实际执行提供器: {EpStatus}");
        return 0;
    }
}
