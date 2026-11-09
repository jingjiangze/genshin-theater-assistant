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
    /// 模板级阈值：`actN` 这类"外形几乎一样、只有数字不同"的模板要更严（实测跨幕相似度 0.80–0.81，
    /// 若继续用 0.86 会擦边误命中）；其余界面锚点用 0.86。
    /// </summary>
    public static double ThresholdFor(string name) =>
        name != null && name.StartsWith("act", StringComparison.Ordinal) && name.Length <= 5 ? 0.90 : 0.86;

}
