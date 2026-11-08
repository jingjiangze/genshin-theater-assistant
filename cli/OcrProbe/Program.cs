// 云端 OCR 探针：验证"针对云端环境的模型"。
//
// 为什么单独一个程序（而不是复用 Windows 的 OcrService）：
//   OcrService 依赖 System.Drawing + WinForms，Linux 上跑不了；
//   云端验证需要的是**裸 RapidOcrNet + SkiaSharp**，这样才能在 headless Linux 上测出
//   真实的云端推理速度与模型差异。
//
// 模型选择规则（来自 RapidOcrNet 源码注释，必须遵守）：
//   v5 → RapidOcrOptions.Default（Padding=50 + ImgResize=1024，为 v5 调优）
//   v6 → RapidOcrOptions.PPOCRv6（无 padding + 短边自适应 736；用 Default 会让 v6 漏检）
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RapidOcrNet;
using SkiaSharp;

internal static class Program
{
    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        string Opt(string name, string fallback = null)
        {
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
            return fallback;
        }
        bool Flag(string name) => Array.IndexOf(args, name) >= 0;

        var modelsRoot = Opt("--models", "models");
        var dir = Opt("--dir");
        var modelArg = Opt("--model", "v5");          // v5 | v6 | both
        var threads = int.TryParse(Opt("--threads", "0"), out var th) ? th : 0;
        var limit = int.TryParse(Opt("--limit", "0"), out var lm) ? lm : 0;
        var outPath = Opt("--out");

        if (Flag("--help") || (dir == null && !Flag("--version")))
        {
            Console.WriteLine(@"OcrProbe — 云端 OCR 验证（v5-mobile / v6-small，CPU）

用法:
  OcrProbe --dir <帧目录> [--model v5|v6|both] [--threads N] [--limit N] [--out r.json]
  OcrProbe --version           # 打印运行环境与可用模型

参数:
  --models <dir>   模型根目录（默认 models，内含 v5/ 与 v6/ 子目录）
  --threads N      ONNX 线程数（0=交给运行时；云端 4 核建议 2，留余量）
  --limit N        只跑前 N 张（快速冒烟）
  --out <file>     结果写 JSON
");
            return dir == null ? 0 : 2;
        }

        var sets = new List<(string Name, string Det, string Cls, string Rec, string Keys, RapidOcrOptions Opt)>();
        void TryAdd(string name)
        {
            if (name == "v5")
            {
                var d = "v5";
                var det = Path.Combine(modelsRoot, d, "ch_PP-OCRv5_mobile_det.onnx");
                var cls = Path.Combine(modelsRoot, d, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");
                var rec = Path.Combine(modelsRoot, d, "ch_PP-OCRv5_rec_mobile_infer.onnx");
                var key = Path.Combine(modelsRoot, d, "ppocrv5_dict.txt");
                if (File.Exists(det) && File.Exists(rec) && File.Exists(key))
                    sets.Add(("v5-mobile", det, File.Exists(cls) ? cls : null, rec, key, RapidOcrOptions.Default));
            }
            else if (name == "v6")
            {
                var d = "v6";
                var det = Path.Combine(modelsRoot, d, "PP-OCRv6_det_small.onnx");
                var rec = Path.Combine(modelsRoot, d, "PP-OCRv6_rec_small.onnx");
                var key = Path.Combine(modelsRoot, d, "ppocrv6_small_dict.txt");
                // RapidOcrNet 的 InitModels(det,cls,rec,keys,op) 不做 null 检查：
                // clsPath 为 null 会走到 TextClassifier.InitModel("") 抛 FileNotFoundException（云端实测）。
                // v6 官方也用 PP-OCRv5 的同一个方向分类器，因此回退到 v5 的 cls。
                var cls = Path.Combine(modelsRoot, d, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");
                if (!File.Exists(cls)) cls = Path.Combine(modelsRoot, "v5", "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");
                if (File.Exists(det) && File.Exists(rec) && File.Exists(key) && File.Exists(cls))
                    sets.Add(("v6-small", det, cls, rec, key, RapidOcrOptions.PPOCRv6));
            }
        }
        if (modelArg is "v5" or "both") TryAdd("v5");
        if (modelArg is "v6" or "both") TryAdd("v6");

        Console.WriteLine($"环境: {Environment.OSVersion} / {Environment.ProcessorCount} 核 / dotnet {Environment.Version}");
        Console.WriteLine($"模型根: {Path.GetFullPath(modelsRoot)}｜请求 {modelArg}｜可用 {(sets.Count == 0 ? "无" : string.Join(",", sets.Select(s => s.Name)))}");

        if (sets.Count == 0)
        {
            Console.WriteLine("没有可用模型：请先把 v5/ 或 v6/ 模型放到 models/ 下（见 models/README.md）");
            // 明确区分"环境缺模型"与"失败"，便于云端脚本判定
            return 4;
        }

        var engines = new List<(string Name, RapidOcr Ocr)>();
        foreach (var s in sets)
        {
            var op = RapidOcr.GetDefaultSessionOptions(threads);
            if (threads > 0) { op.IntraOpNumThreads = threads; op.InterOpNumThreads = Math.Max(1, threads / 2); }
            var ocr = new RapidOcr();
            ocr.InitModels(s.Det, s.Cls, s.Rec, s.Keys, op);
            engines.Add((s.Name, ocr));
            Console.WriteLine($"  已加载 {s.Name}: det={Path.GetFileName(s.Det)} rec={Path.GetFileName(s.Rec)}" +
                              $"｜opts={(ReferenceEquals(s.Opt, RapidOcrOptions.PPOCRv6) ? "PPOCRv6" : "Default")}");
        }

        var files = Directory.GetFiles(Path.GetFullPath(dir), "*.png")
            .Concat(Directory.GetFiles(Path.GetFullPath(dir), "*.jpg"))
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        if (limit > 0 && files.Count > limit) files = files.Take(limit).ToList();
        if (files.Count == 0) { Console.WriteLine("目录里没有图"); return 2; }
        Console.WriteLine($"图片 {files.Count} 张");

        var results = new List<Dictionary<string, object>>();
        var perEngine = new Dictionary<string, List<(string Text, double Ms)>>();

        foreach (var f in files)
        {
            using var bmp = SKBitmap.Decode(f);
            if (bmp == null) continue;
            var row = new Dictionary<string, object> { ["file"] = Path.GetFileName(f), ["w"] = bmp.Width, ["h"] = bmp.Height };
            foreach (var (name, ocr) in engines)
            {
                var sw = Stopwatch.StartNew();
                var r = ocr.Detect(bmp, ReferenceEquals(name, "v6-small") ? RapidOcrOptions.PPOCRv6 : RapidOcrOptions.Default);
                sw.Stop();
                var text = string.Join("\n", r.TextBlocks.Select(b => b.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
                row[name] = text;
                row[name + "_ms"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
                row[name + "_lines"] = r.TextBlocks.Length;
                if (!perEngine.TryGetValue(name, out var acc)) perEngine[name] = acc = new();
                acc.Add((text, sw.Elapsed.TotalMilliseconds));
            }
            results.Add(row);
        }

        Console.WriteLine();
        foreach (var (name, acc) in perEngine)
        {
            var avg = acc.Count == 0 ? 0 : acc.Average(x => x.Ms);
            var p95 = acc.Count == 0 ? 0 : acc.Select(x => x.Ms).OrderBy(x => x).ElementAt((int)(acc.Count * 0.95) is var i && i >= acc.Count ? acc.Count - 1 : i);
            var nonEmpty = acc.Count(x => x.Text.Length > 0);
            Console.WriteLine($"[{name}] 平均 {avg:0.0} ms/帧｜P95 {p95:0.0} ms｜非空 {nonEmpty}/{acc.Count}（{100.0 * nonEmpty / Math.Max(1, acc.Count):0.0}%）");
        }

        // 交叉验证：两个引擎文本一致率（"跑其他模型"的核心产出）
        if (engines.Count == 2)
        {
            int same = 0, compare = 0;
            foreach (var r in results)
            {
                var a = r[engines[0].Name] as string;
                var b = r[engines[1].Name] as string;
                if (a == null || b == null) continue;
                compare++;
                if (Norm(a) == Norm(b)) same++;
            }
            var rate = compare == 0 ? 0 : 100.0 * same / compare;
            Console.WriteLine($"[交叉验证] {engines[0].Name} vs {engines[1].Name} 一致 {same}/{compare}（{rate:0.0}%）");
            Console.WriteLine(rate >= 80
                ? "  → 两模型一致性良好（差异 <20%），云端可用轻量模型承担回归"
                : "  → ⚠ 两模型差异较大，云端结论不能直接套用到桌面端，需人工核对差异帧");
        }

        if (!string.IsNullOrWhiteSpace(outPath))
        {
            var doc = new
            {
                generatedAt = DateTime.UtcNow.ToString("o"),
                os = Environment.OSVersion.ToString(),
                cores = Environment.ProcessorCount,
                modelArg,
                images = files.Count,
                engines = perEngine.Select(kv => new
                {
                    name = kv.Key,
                    avgMs = Math.Round(kv.Value.Average(x => x.Ms), 1),
                    nonEmpty = kv.Value.Count(x => x.Text.Length > 0),
                    total = kv.Value.Count,
                }),
                results,
            };
            File.WriteAllText(Path.GetFullPath(outPath),
                JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
                new UTF8Encoding(false));
            Console.WriteLine("已写: " + Path.GetFullPath(outPath));
        }
        return 0;
    }

    private static string Norm(string s) =>
        new string((s ?? "").Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c)).ToArray()).ToLowerInvariant();
}
