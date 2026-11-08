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
        // --by-kind：按界面类型选模型（30 帧实验发现 v6 强于角色名、v5 强于数字）
        var byKind = Flag("--by-kind");
        var charsPath = Opt("--chars", "data/characters.json");

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

        // ── 按界面类型选模型：用"关键字段命中"判断哪种策略更好 ──
        if (byKind && engines.Count == 2)
        {
            var dic = LoadCharacterNames(charsPath);
            Console.WriteLine();
            Console.WriteLine($"[按界面选模型] 角色名词典 {dic.Count} 个（来自 {charsPath}）");

            var rows = new List<(string File, string Kind, int V5Name, int V6Name, int PickName,
                                 int V5Num, int V6Num, int PickNum, double PickMs, string Picked)>();
            foreach (var f in files)
            {
                using var bmp = SKBitmap.Decode(f);
                if (bmp == null) continue;

                var r5 = engines[0].Ocr.Detect(bmp, RapidOcrOptions.Default);
                var r6 = engines[1].Ocr.Detect(bmp, RapidOcrOptions.PPOCRv6);
                var t5 = string.Join("\n", r5.TextBlocks.Select(b => b.Text).Where(x => !string.IsNullOrWhiteSpace(x)));
                var t6 = string.Join("\n", r6.TextBlocks.Select(b => b.Text).Where(x => !string.IsNullOrWhiteSpace(x)));

                // 界面类型用**两模型并集**判定，避免偏向任一模型
                var kind = GuessKind(t5 + "\n" + t6);
                // 选模型：角色相关界面要角色名（v6 强）→ v6；战斗 HUD 要数字（v5 强）→ v5
                var pickV6 = kind is "team" or "select" or "unknown";
                var picked = pickV6 ? "v6" : "v5";
                var pickedText = pickV6 ? t6 : t5;
                var pickedMs = pickV6 ? r6.DetectTime : r5.DetectTime;
                if (pickedMs <= 0) pickedMs = -1;

                rows.Add((Path.GetFileName(f), kind,
                    CountNames(t5, dic), CountNames(t6, dic), CountNames(pickedText, dic),
                    CountNumbers(t5), CountNumbers(t6), CountNumbers(pickedText),
                    pickedMs, picked));
            }

            if (rows.Count > 0)
            {
                double Sum(Func<(string File, string Kind, int V5Name, int V6Name, int PickName, int V5Num, int V6Num, int PickNum, double PickMs, string Picked), int> sel)
                    => rows.Sum(sel);
                Console.WriteLine();
                Console.WriteLine("| 策略 | 角色名命中 | 数字命中 |");
                Console.WriteLine("|---|---:|---:|");
                Console.WriteLine($"| 全 v5-mobile | {Sum(r => r.V5Name)} | {Sum(r => r.V5Num)} |");
                Console.WriteLine($"| 全 v6-small | {Sum(r => r.V6Name)} | {Sum(r => r.V6Num)} |");
                Console.WriteLine($"| **按界面选** | **{Sum(r => r.PickName)}** | **{Sum(r => r.PickNum)}** |");

                var bestName = Math.Max(rows.Sum(r => r.V5Name), rows.Sum(r => r.V6Name));
                var bestNum = Math.Max(rows.Sum(r => r.V5Num), rows.Sum(r => r.V6Num));
                var pickName = rows.Sum(r => r.PickName);
                var pickNum = rows.Sum(r => r.PickNum);
                Console.WriteLine();
                Console.WriteLine($"角色名最优单模型 {bestName} → 按界面 {pickName}（{(pickName >= bestName ? "不劣 ✅" : "更差 ❌")}）");
                Console.WriteLine($"数字最优单模型 {bestNum} → 按界面 {pickNum}（{(pickNum >= bestNum ? "不劣 ✅" : "更差 ❌")}）");

                foreach (var g in rows.GroupBy(r => r.Kind))
                    Console.WriteLine($"  {g.Key}: {g.Count()} 帧（选 {(g.Count(x => x.Picked == "v6") > g.Count() / 2 ? "v6" : "v5")}）");
            }
        }

        // 交叉验证：两个引擎的**字符级相似度**（"跑其他模型"的核心产出）
        //
        // 注意：不能用"整段文本完全相等"当一致率——三张图的输出都是几十行，
        // 只要一个标点不同就判整帧不一致，实测得出 0/3 的假象（本轮踩过）。
        // 改用字符 bigram Jaccard，能反映"实质内容是否相同"。
        if (engines.Count == 2)
        {
            var sims = new List<(string File, double Sim)>();
            foreach (var r in results)
            {
                var a = r[engines[0].Name] as string;
                var b = r[engines[1].Name] as string;
                if (a == null || b == null) continue;
                sims.Add((r["file"] as string ?? "?", BigramJaccard(a, b)));
            }
            if (sims.Count > 0)
            {
                var avg = sims.Average(x => x.Sim) * 100;
                var min = sims.MinBy(x => x.Sim);
                Console.WriteLine($"[交叉验证] {engines[0].Name} vs {engines[1].Name} 字符相似度 平均 {avg:0.0}%（最低 {min.Sim * 100:0.0}% @ {min.File}）");
                foreach (var s in sims) Console.WriteLine($"    {s.File}: {s.Sim * 100:0.0}%");
                // 阈值来自 data/decision-policy.cloud.json（minAgreementPct），缺省 80
                var threshold = ReadAgreementThreshold();
                Console.WriteLine(avg >= threshold
                    ? $"  → 两模型一致性 ≥{threshold:0}%，云端可用轻量模型承担回归，结论可外推"
                    : $"  → ⚠ 相似度 <{threshold:0}%，云端结论不能直接套用桌面端，需人工核对差异帧");
            }
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

    /// <summary>字符 bigram 的 Jaccard 相似度（0..1）。忽略空白与标点，避免格式差异放大。</summary>
    private static double BigramJaccard(string a, string b)
    {
        static HashSet<string> Grams(string s)
        {
            var t = new string((s ?? "").Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c)).ToArray());
            var set = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i + 1 < t.Length; i++) set.Add(t.Substring(i, 2));
            if (t.Length == 1) set.Add(t);
            return set;
        }
        var A = Grams(a); var B = Grams(b);
        if (A.Count == 0 && B.Count == 0) return 1.0;
        if (A.Count == 0 || B.Count == 0) return 0.0;
        var inter = A.Count(x => B.Contains(x));
        var union = A.Count + B.Count - inter;
        return union == 0 ? 1.0 : (double)inter / union;
    }

    /// <summary>从 data/decision-policy.cloud.json 读一致率阈值；读不到用 80。</summary>
    private static double ReadAgreementThreshold()
    {
        try
        {
            var p = Path.Combine("data", "decision-policy.cloud.json");
            if (!File.Exists(p)) return 80;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(p));
            if (doc.RootElement.TryGetProperty("verify", out var v)
                && v.TryGetProperty("minAgreementPct", out var m)
                && m.ValueKind == System.Text.Json.JsonValueKind.Number)
                return m.GetDouble();
        }
        catch { }
        return 80;
    }

    /// <summary>从 characters.json 的 zhNames 取角色中文名词典。</summary>
    private static HashSet<string> LoadCharacterNames(string path)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(path)) return set;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("zhNames", out var zh) && zh.ValueKind == System.Text.Json.JsonValueKind.Object)
                foreach (var e in zh.EnumerateObject())
                    if (e.Value.ValueKind == System.Text.Json.JsonValueKind.String && e.Value.GetString() is { Length: > 1 } s)
                        set.Add(s);
        }
        catch { }
        return set;
    }

    /// <summary>界面类型判定：只用文本特征，不依赖 Windows 侧代码。</summary>
    private static string GuessKind(string text)
    {
        if (string.IsNullOrEmpty(text)) return "unknown";
        if (text.Contains("队伍配置") || text.Contains("待命角色") || text.Contains("已选开幕")
            || text.Contains("编入") || text.Contains("试用角色")) return "team";
        if (text.Contains("可选角色") || text.Contains("邀请助演")) return "select";
        if (text.Contains("挑战限时") || text.Contains("怒气") || text.Contains("秒") && text.Contains("/"))
            return "battle";
        if (text.Contains("幻剧之花") || text.Contains("祝福等级") || text.Contains("辉彩祝福")) return "buff";
        return "unknown";
    }

    private static int CountNames(string text, HashSet<string> dic)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int n = 0;
        foreach (var name in dic) if (text.Contains(name, StringComparison.Ordinal)) n++;
        return n;
    }

    private static int CountNumbers(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return System.Text.RegularExpressions.Regex.Matches(text, @"\d+\s*/\s*\d+|\d+\.\d+|\d{2,}").Count;
    }

    private static string Norm(string s) =>
        new string((s ?? "").Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c)).ToArray()).ToLowerInvariant();
}
