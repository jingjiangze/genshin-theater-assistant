using System;
using System.Drawing;
using System.Drawing.Imaging;
using OpenCvSharp;
using OpenCvSharp.Extensions;

namespace TheaterAssistant;

/// <summary>
/// OCR 预处理（对应 BetterGI 的 D3–D5 中的 **D5：白字掩膜**）。
///
/// 为什么只做 D5：
///   审计过 RapidOcrNet 源码（OcrUtils.GetRotateCropImage）——
///   **D3（竖排 高/宽≥1.5 旋转 90°）和 D4（四点透视裁剪）它内部已经实现**，
///   我们再写一遍等于重复造轮子。真正缺的只有 D5。
///
/// D5 解决什么：
///   游戏 UI 里大量"白字 + 深色描边"压在彩色/亮背景上（剧诗幕次条、商店价格、角色名）。
///   直接灰度化后，背景与文字的亮度差可能不够，检测器会漏框。
///   白字掩膜把接近白色的像素单独强化，让检测器更容易框住文字行。
///
/// 模式（配置 data/decision-policy.json 的 ocr.preprocess.mode）：
///   off    —— 不处理（默认，保持与之前行为一致）
///   boost  —— 温和：白字提纯 + 背景压暗，保留彩色信息（推荐）
///   mask   —— 激进：只留白字，其余全黑（极端低对比度时用）
/// </summary>
internal static class OcrPreprocess
{
    public enum Mode { Off, Boost, Mask }

    public static Mode CurrentMode { get; private set; } = Mode.Off;
    /// <summary>白字判定下限（0–255）。越高越严格，默认 210（比 BetterGI 的 230 宽一点，兼顾浅灰字）。</summary>
    public static int WhiteLow { get; private set; } = 210;
    public static string Source { get; private set; } = "(未加载，用默认 off)";

    /// <summary>从 decision-policy.json 读取预处理配置。</summary>
    public static void Load(string policyPath)
    {
        try
        {
            if (!System.IO.File.Exists(policyPath)) { Source = "(无策略文件，用默认 off)"; return; }
            using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(policyPath));
            if (!doc.RootElement.TryGetProperty("ocr", out var ocr) || ocr.ValueKind != System.Text.Json.JsonValueKind.Object)
            { Source = "(策略无 ocr 段，用默认 off)"; return; }
            if (!ocr.TryGetProperty("preprocess", out var pp) || pp.ValueKind != System.Text.Json.JsonValueKind.Object)
            { Source = "(策略无 preprocess 段，用默认 off)"; return; }

            if (pp.TryGetProperty("mode", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                CurrentMode = (m.GetString() ?? "off").ToLowerInvariant() switch
                {
                    "boost" => Mode.Boost,
                    "mask" => Mode.Mask,
                    _ => Mode.Off,
                };
            }
            if (pp.TryGetProperty("whiteLow", out var wl) && wl.ValueKind == System.Text.Json.JsonValueKind.Number)
                WhiteLow = Math.Clamp(wl.GetInt32(), 150, 250);

            Source = $"mode={CurrentMode} whiteLow={WhiteLow}";
        }
        catch (Exception ex)
        {
            Source = "(读取失败：" + ex.GetType().Name + "，用默认 off)";
        }
    }

    /// <summary>按当前模式处理一帧。返回新 Bitmap（调用方负责 Dispose）；off 时返回 null 表示不处理。</summary>
    public static Bitmap Apply(Bitmap src)
    {
        if (src == null || CurrentMode == Mode.Off) return null;
        try
        {
            using var mat = BitmapConverter.ToMat(src);
            if (mat.Empty()) return null;

            using var gray = new Mat();
            Cv2.CvtColor(mat, gray, ColorConversionCodes.BGR2GRAY);

            // 白字掩膜：接近白色的像素（文字主体）
            using var mask = new Mat();
            Cv2.InRange(gray, new Scalar(WhiteLow), new Scalar(255), mask);

            // 形态学闭运算：把被描边切断的笔画连起来（3×3 核，一次即可）
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3));
            Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);

            Mat outp;
            if (CurrentMode == Mode.Mask)
            {
                // 激进：只保留白字区域（其余全黑）
                outp = new Mat(mat.Size(), mat.Type(), Scalar.Black);
                mat.CopyTo(outp, mask);
            }
            else
            {
                // 温和：白字提纯到接近纯白 + 整体背景压暗 35%
                outp = new Mat();
                mat.ConvertTo(outp, -1, 0.65, 0);          // 先整体压暗，拉开与白字的对比
                using var white = new Mat();
                mat.ConvertTo(white, -1, 1.0, 0);
                // 把掩膜区域覆盖成偏白
                using var bright = new Mat(mat.Size(), mat.Type(), new Scalar(245, 245, 245));
                bright.CopyTo(outp, mask);
            }

            using (outp)
                return BitmapConverter.ToBitmap(outp);
        }
        catch (Exception ex)
        {
            // 预处理失败绝不能拖垮主循环——回退到原图
            App.LogCrash(ex, "OcrPreprocess.Apply");
            return null;
        }
    }

    /// <summary>CLI：报告配置并可对一张图做前后对比（便于排查"开了没效果"）。</summary>
    public static int RunCli(string imagePath)
    {
        Cli.Attach();
        Load(AppPaths.Data("decision-policy.json"));
        Cli.Say($"预处理配置：{Source}");

        if (string.IsNullOrWhiteSpace(imagePath) || !System.IO.File.Exists(imagePath))
        {
            Cli.Say("（未指定 --image，只报告配置）");
            return 0;
        }

        using var src = new Bitmap(imagePath);
        Cli.Say($"输入：{System.IO.Path.GetFileName(imagePath)} {src.Width}x{src.Height}");
        foreach (var mode in new[] { Mode.Boost, Mode.Mask })
        {
            CurrentMode = mode;
            using var outp = Apply(src);
            if (outp == null) { Cli.Say($"  {mode}: 无输出"); continue; }
            var name = $"preprocess-{mode.ToString().ToLowerInvariant()}.png";
            outp.Save(System.IO.Path.Combine(AppPaths.Dir, name), ImageFormat.Png);
            Cli.Say($"  {mode}: 已写 {name}（{outp.Width}x{outp.Height}）");
        }
        return 0;
    }
}
