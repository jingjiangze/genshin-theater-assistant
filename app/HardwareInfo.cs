// ─────────────────────────────────────────────────────────────────────────────
// 硬件画像：把「写死 6 线程」改成按机器自适应。
//
// 背景（2026-11-10 实测，i5-13400F 6P+4E）：
//   ORT 自动档会吃满 16 逻辑核（跨 P/E），5.0s/帧；钉 6（P 核数）=1.56s/帧；
//   钉 8（6P+2E）=3.8s/帧 —— 所以不是"线程越多越快"，而是"别跨到 E 核、别和游戏抢核"。
// 但 6 是那台机器的 P 核数，写死会在 4 核/2 核机器上过订阅，反而更慢。
//
// 这里只做**保守的自适应**：逻辑核的一半，夹在 [2, 6]。
//   16 逻辑 → 8 → 夹到 6（与实测最优一致）
//    8 逻辑 → 4
//    4 逻辑 → 2
// 物理/混合架构（P/E）在 .NET 里拿不到，所以这只是起步值；用户仍可用
// data/decision-policy.json 的 ocr.numThreads 或环境变量 TA_OCR_THREADS 覆盖。
// ─────────────────────────────────────────────────────────────────────────────
using System;

namespace TheaterAssistant;

internal static class HardwareInfo
{
    /// <summary>逻辑核数（Environment.ProcessorCount 受进程亲和性影响，这里就是可用核）。</summary>
    public static int Cores => Math.Max(1, Environment.ProcessorCount);

    /// <summary>是否支持 AVX2（内嵌 llama-server 是 avx2 构建；不支持时模型层应禁用而不是崩）。</summary>
    public static bool HasAvx2 => System.Runtime.Intrinsics.X86.Avx2.IsSupported;

    /// <summary>建议的推理线程数：逻辑核的一半，夹在 [2, 6]（理由见文件头）。</summary>
    public static int SuggestThreads() => Math.Clamp(Cores / 2, 2, Math.Min(6, Cores));

    /// <summary>一行画像，写进启动日志/诊断页。</summary>
    public static string Describe() =>
        $"核 {Cores} · AVX2 {(HasAvx2 ? "有" : "无")} · 建议线程 {SuggestThreads()}";
}
