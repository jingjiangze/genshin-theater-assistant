// ─────────────────────────────────────────────────────────────────────────────
// 从 app/OverlayForm.cs 抽出：TemplateEntry 是模板的通用载体（Recognition/Matcher 依赖），
// 而 OverlayForm 是 WinForms 窗体。WPF 版需要 TemplateEntry 但不引用 WinForms 窗体。
// ─────────────────────────────────────────────────────────────────────────────
using System;
using System.Drawing;

namespace TheaterAssistant;

internal sealed record TemplateEntry(string Name, Bitmap Bitmap, double Threshold)
{
    /// <summary>
    /// 模板像素的采集基准宽/高（来自 templates\&lt;宽x高&gt;\ 桶）。
    /// ScanCv 用它把模板缩放到当前帧：模板缩放 = k × (帧宽 / SourceWidth)；
    /// 从 templates 根目录读到的模板没有桶信息，保持 0 表示"源宽未知"（按旧行为按帧宽算）。
    /// </summary>
    public int SourceWidth { get; init; }

    /// <summary>见 <see cref="SourceWidth"/>；目前只用于诊断/后续按高度选桶。</summary>
    public int SourceHeight { get; init; }

    /// <summary>
    /// 模板级阈值：`actN` 这类"外形几乎一样、只有数字不同"的模板要更严（实测跨幕相似度 0.80–0.81，
    /// 若继续用 0.86 会擦边误命中）；其余界面锚点用 0.86。
    /// </summary>
    public static double ThresholdFor(string name) =>
        name != null && name.StartsWith("act", StringComparison.Ordinal) && name.Length <= 5 ? 0.90 : 0.86;

}
