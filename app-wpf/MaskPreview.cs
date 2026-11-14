// ─────────────────────────────────────────────────────────────────────────────
// 忠实预览：把**真实一帧**喂进生产同一套识别 + 决策，再把结果画成遮罩 PNG。
//
// 为什么需要它：此前的预览图是"样式样张"——遮罩上的文字是自检里手写的，
// 和底下的游戏画面没有关系（比如给选卡画面配了首领战的手法）。这种图只能看排版，
// 不能回答"这一屏到底会显示什么"。这里改成：
//   帧文件 →（与 MainLoop 相同的抓帧后处理：参考画布 → 模板 → 条带 OCR → 全帧 OCR）
//          →（同一个 DecisionOrchestrator + Routine）→ 遮罩真实状态 → 渲染
// 同时把渲染用的每个字段写进同名 .json，便于逐条核对 PNG 与决策是否一致。
//
// 用法：TheaterAssistantWpf.exe --mask-preview <帧目录> [--out <产物目录>]
// ─────────────────────────────────────────────────────────────────────────────
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TheaterAssistant;

internal static class MaskPreview
{
    /// <summary>渲染宽度：与 1080p 帧同尺寸，保证 1 DIP = 1 px，叠上去不失真。</summary>
    private const int RefWidth = 1920, RefHeight = 1080;

    public static int Run(string frameDir, string outDir)
    {
        if (string.IsNullOrEmpty(frameDir) || !Directory.Exists(frameDir))
        {
            Cli.Say("--mask-preview 需要帧目录");
            return 2;
        }
        Directory.CreateDirectory(outDir);

        // 生产启动链同款初始化（缺了 Playbook/Season 会让 ★/◆队伍 层成空壳）
        try { DecisionBridge.Init(); } catch (Exception ex) { Cli.Say("决策层初始化失败: " + ex.GetType().Name); }
        try { GameModes.Init(); } catch { }
        try { OcrTextPipeline.LoadFromPolicy(AppPaths.Data("decision-policy.json")); } catch { }
        try { Routine.Load(AppPaths.Data("rotations.json")); } catch { }

        var mode = GameModes.Default;
        using var ocr = new OcrService();
        ocr.InitializeAsync(AppPaths.ModelsRoot).GetAwaiter().GetResult();
        Cli.Say("OCR: " + ocr.Status);

        var templates = Matcher.LoadTemplatesNearest(AppPaths.TemplatesRoot, RefWidth);
        Cli.Say($"模板: {templates.Count} 个（最近桶）");

        var stab = new ActStabilizer();
        var frames = Directory.GetFiles(frameDir, "*.png").OrderBy(x => x, StringComparer.Ordinal).ToList();
        Cli.Say($"帧数: {frames.Count}");

        // 预览是**逐帧独立判定**：每帧都从干净的稳定器开始，回答「这一屏会显示什么」。
        // 生产是连续时间轴（上一帧的幕次会沿用，跨幕需要连续 2 帧确认），所以同一帧在
        // 生产里可能仍显示旧幕次 —— 这是设计，不是预览失真。
        foreach (var f in frames)
        {
            stab.Reset();
            try { AnalyzeOne(f, outDir, ocr, templates, mode, stab); }
            catch (Exception ex) { Cli.Say($"跳过 {Path.GetFileName(f)}: {ex.GetType().Name} {ex.Message}"); }
        }
        return 0;
    }

    private static void AnalyzeOne(string path, string outDir, OcrService ocr,
                                   List<TemplateEntry> templates, GameModeProfile mode, ActStabilizer stab)
    {
        using var bmp = new Bitmap(path);

        // ① 参考画布（与 MainLoop 同一条：长边限 960）
        using var canvas = OcrService.ReferenceCanvas(bmp, out var scaled);
        var ocrFrame = scaled ? canvas : bmp;

        // ② 模板（生产在同分辨率帧上扫）
        var hits = Matcher.Scan(bmp, templates);

        // ③ 条带 OCR（幕次，scale=2）→ ④ 全帧 OCR（选项/文本）
        var band = mode.HasBand
            ? ocr.RecognizeRegionAsync(ocrFrame, new RectangleF(
                (float)mode.BandLeft, (float)mode.BandTop,
                (float)mode.BandWidth, (float)mode.BandHeight), 2).GetAwaiter().GetResult()
            : "";
        var boxes = ocr.RecognizeBoxesAsync(ocrFrame).GetAwaiter().GetResult();
        var lines = boxes == null
            ? new List<string>()
            : OcrTextPipeline.ToLines(boxes.Select(b => new OcrTextPipeline.Box
            {
                X = b.Rect.X,
                Y = b.Rect.Y,
                Score = b.Score,
                Text = b.Text,
            }));
        var text = string.Join("\n", lines);

        // ⑤ 幕次 → 稳定器（生产同一套）
        var act = mode.ParseAct(band) ?? mode.ParseActLines(lines);
        var stable = stab.Push(act);

        // ⑥ 决策（同一个 orchestrator；◇ 模型这次不参与，预览只验确定性层）
        var options = ChoiceDecider.ParseOptions(lines);
        var diff = QuantData.Difficulties.LastOrDefault() ?? "";
        var enemy = stable.HasValue ? QuantData.Enemy(stable.Value, diff) : null;
        var d = DecisionOrchestrator.Decide(new DecisionOrchestrator.Context
        {
            Act = stable,
            Difficulty = diff,
            Enemy = enemy,
            Team = TeamSolver.AsTeamProfile(),
            Options = options,
            Kind = stable.HasValue ? "act"
                 : (hits.Any(h => string.Equals(h.Name, "hall", StringComparison.OrdinalIgnoreCase)) ? "theater"
                 : (options.Count >= 2 ? "choice" : (hits.Count > 0 ? "ui" : ""))),
        });

        var advice = d.Lines.FirstOrDefault() ?? "";
        // 与 MainLoop 同条件：只有「幕内且没读到选项」才追加手法行
        var guide = stable.HasValue && options.Count == 0
            ? (Routine.LinesFor(stable, 3).FirstOrDefault(l => l.StartsWith("手法·")) ?? "")
            : "";
        var source = string.IsNullOrEmpty(d.Source) ? "·" : d.Source;

        // ⑦ 渲染遮罩（与生产同样的 Set* 调用顺序）
        var boxesForMask = new List<MaskBox>();
        foreach (var h in hits)
        {
            var size = templates.FirstOrDefault(t => t.Name == h.Name)?.Bitmap;
            if (size == null) continue;
            boxesForMask.Add(new MaskBox
            {
                Bounds = new Rect(h.X, h.Y, size.Width, size.Height),
                Color = System.Windows.Media.Color.FromRgb(0x7E, 0xD8, 0xA0),
                Thickness = 2,
                Label = $"{h.Name} {h.Score:0.00}",
            });
        }

        var name = Path.GetFileNameWithoutExtension(path);
        var pngPath = Path.Combine(outDir, name + "-mask.png");
        var jsonPath = Path.Combine(outDir, name + "-mask.json");

        var w = new MaskWindow();
        w.SetMode(mode.DisplayName);
        w.SetStatus(stable.HasValue ? $"第{stable}幕" : "幕次未识别", source);
        if (string.IsNullOrEmpty(advice))
            w.SetAdvice("（本帧没有可执行指示）", warn: true);
        else
            w.SetAdvice(advice, warn: source is "·" or "◇模型(未取到)");
        w.SetGuide(string.IsNullOrEmpty(guide) ? "" : Shorten(guide));
        w.SetFrame(boxesForMask, bmp.Width, bmp.Height);

        // Window 没 Show 过时 RenderTargetBitmap 渲染出来是全空（实测 0 个非空像素）——必须先 Show。
        // 预览是命令行一次性工具，闪一下可以接受；渲染完立刻 Close。
        w.Show();
        var bmpOut = Render(w, bmp.Width, bmp.Height);
        SavePng(bmpOut, pngPath);
        w.Close();

        // ⑧ 落一份"渲染时用的字段"清单，供逐条核对
        var json = new Dictionary<string, object>
        {
            ["frame"] = Path.GetFileName(path),
            ["frameSize"] = $"{bmp.Width}x{bmp.Height}",
            ["bandText"] = band ?? "",
            ["rawAct"] = act ?? 0,
            ["stableAct"] = stable ?? 0,
            ["kind"] = stable.HasValue ? "act"
                     : (hits.Any(h => string.Equals(h.Name, "hall", StringComparison.OrdinalIgnoreCase)) ? "theater"
                     : (options.Count >= 2 ? "choice" : (hits.Count > 0 ? "ui" : ""))),
            ["optionCount"] = options.Count,
            ["options"] = options.Select(o => o.Raw).ToList(),
            ["source"] = source,
            ["advice"] = advice,
            ["guideRendered"] = string.IsNullOrEmpty(guide) ? "" : Shorten(guide),
            ["lines"] = d.Lines,
            ["provenance"] = d.Provenance,
            ["confidence"] = d.Confidence,
            ["hits"] = hits.Select(h => new { name = h.Name, score = Math.Round(h.Score, 4), x = h.X, y = h.Y }).ToList(),
            ["ocrText"] = text.Length > 600 ? text[..600] + "…" : text,
            ["ocrLineCount"] = lines.Count,
        };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(json,
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

        Cli.Say($"{name}: act={stable?.ToString() ?? "-"} src={source} 选项={options.Count} 命中={hits.Count} → {pngPath}");
    }

    /// <summary>与 App.ShortenGuide 同口径（遇「（」且位置 &gt; 8 截断，否则 26 字）。</summary>
    private static string Shorten(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var i = s.IndexOf('（');
        if (i > 8) return s[..i].TrimEnd();
        return s.Length <= 26 ? s : s[..26] + "…";
    }

    private static BitmapSource Render(Window w, int width, int height)
    {
        try
        {
            w.BeginAnimation(Window.OpacityProperty, null);
            w.Opacity = 1.0;
        }
        catch { }

        w.Width = width;
        w.Height = height;
        w.Measure(new System.Windows.Size(width, height));
        w.Arrange(new Rect(0, 0, width, height));
        w.UpdateLayout();
        if (w is MaskWindow maskWin) maskWin.ScrollLogToEnd();
        Pump(60);

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(w);
        rtb.Freeze();
        return rtb;
    }

    private static void SavePng(BitmapSource bmp, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(ms),
        };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
