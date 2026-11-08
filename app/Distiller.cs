using System.Text;
using System.Text.Json;

namespace TheaterAssistant;

/// <summary>
/// 把一批 --replay 产物"蒸馏"成可复用的经验：
///   · 数字面：视觉代理标签帧数、自洽率（原始/稳定）、跳变、状态分布、每帧耗时、检索命中率；
///   · 规则面：写出 docs/experience/SUMMARY.md 与 rules.json，供人和程序共同引用。
///
/// 目的：每跑一批真实视频，就把结论沉淀一次；后续判断可以直接引用"经验"而不是重新试错。
/// 注意：这只是**统计与规则沉淀**；条带 OCR 标签与预测输入耦合，只能用于自洽性诊断，不能作为独立准确率或知识晋级依据。
/// </summary>
internal static class Distiller
{
    internal sealed class Report
    {
        public string File = "";
        public int Frames, TruthFrames, StableRight, RawRight, Jumps, Flips;
        public long TotalMs;
        public Dictionary<string, int> Kinds = new(StringComparer.Ordinal);
        public List<string> JumpSamples = new();
        public List<string> Mismatches = new();
    }

    /// <summary>解析一个 replay JSON（纯函数，方便断言）。</summary>
    internal static Report Parse(string json, string name = "")
    {
        var r = new Report { File = name };
        if (string.IsNullOrWhiteSpace(json)) return r;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("frames", out var f) && f.ValueKind == JsonValueKind.Number) r.Frames = f.GetInt32();
            if (root.TryGetProperty("jumps", out var j) && j.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in j.EnumerateArray()) r.JumpSamples.Add(x.GetString() ?? "");
                r.Jumps = r.JumpSamples.Count;
            }
            if (root.TryGetProperty("flips", out var fl) && fl.ValueKind == JsonValueKind.Array)
                r.Flips = fl.GetArrayLength();

            if (!root.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array) return r;

            foreach (var s in steps.EnumerateArray())
            {
                int act = Num(s, "Act"), raw = Num(s, "RawAct"), truth = Num(s, "TruthAct");
                r.TotalMs += Num(s, "Ms");
                var kind = Str(s, "Kind");
                if (kind.Length > 0) r.Kinds[kind] = r.Kinds.TryGetValue(kind, out var c) ? c + 1 : 1;
                if (truth <= 0) continue;
                r.TruthFrames++;
                if (raw == truth) r.RawRight++;
                if (act == truth) r.StableRight++;
                else r.Mismatches.Add($"{Str(s, "Frame")}: 我们={act}/真值={truth}");
            }
        }
        catch { }
        return r;
    }

    public static int Run(IEnumerable<string> inputs, string outDir)
    {
        Cli.Attach();
        var files = Expand(inputs).ToList();
        if (files.Count == 0) { Cli.Say("没有找到 replay JSON（先跑 --replay --json）"); return 2; }

        outDir = string.IsNullOrWhiteSpace(outDir) ? Path.Combine(AppPaths.Dir, "docs", "experience") : Path.GetFullPath(outDir);
        Directory.CreateDirectory(outDir);

        var reports = new List<Report>();
        foreach (var f in files)
        {
            try
            {
                var rep = Parse(File.ReadAllText(f), Path.GetFileName(f));
                if (rep.Frames > 0) reports.Add(rep);
            }
            catch (Exception ex) { Cli.Say("解析失败 " + f + ": " + ex.Message); }
        }

        int frames = reports.Sum(r => r.Frames);
        int truth = reports.Sum(r => r.TruthFrames);
        int stableRight = reports.Sum(r => r.StableRight);
        int rawRight = reports.Sum(r => r.RawRight);
        int jumps = reports.Sum(r => r.Jumps);
        int flips = reports.Sum(r => r.Flips);
        long ms = reports.Sum(r => r.TotalMs);
        var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in reports)
            foreach (var kv in r.Kinds) kinds[kv.Key] = kinds.TryGetValue(kv.Key, out var c) ? c + kv.Value : kv.Value;

        double accStable = truth == 0 ? 0 : 100.0 * stableRight / truth;
        double accRaw = truth == 0 ? 0 : 100.0 * rawRight / truth;

        var md = new StringBuilder();
        md.AppendLine("# 经验汇总（自动蒸馏）");
        md.AppendLine();
        md.AppendLine($"> 由 `--distill` 从 {reports.Count} 份 **replay** 报告生成（已排除 `*-ops.json` 候选文件）；条带 OCR 代理标签与预测输入耦合，不能视为独立真值。");
        md.AppendLine();
        md.AppendLine("| 指标 | 数值 |");
        md.AppendLine("|---|---|");
        md.AppendLine($"| 视频/报告数 | {reports.Count} |");
        md.AppendLine($"| 帧数合计 | {frames} |");
        md.AppendLine($"| 有视觉代理标签帧 | {truth}（覆盖率 {(frames == 0 ? 0 : 100.0 * truth / frames):0.0}%） |");
        md.AppendLine($"| 视觉条带代理自洽率（稳定后 / 原始） | **{accStable:0.0}% / {accRaw:0.0}%** |");
        md.AppendLine($"| 跨幕跳变 | {jumps} 次 |");
        md.AppendLine($"| 状态抖动 | {flips} 次 |");
        md.AppendLine($"| 平均每帧耗时 | {(frames == 0 ? 0 : ms / frames)} ms |");
        md.AppendLine($"| 状态分布 | {string.Join("，", kinds.OrderByDescending(k => k.Value).Select(k => $"{k.Key}={k.Value}"))} |");
        md.AppendLine();
        md.AppendLine("## 逐视频");
        md.AppendLine();
        md.AppendLine("| 报告 | 帧 | 代理标签帧 | 自洽率(稳/原) | 跳变 | 抖动 |");
        md.AppendLine("|---|---|---|---|---|---|");
        foreach (var r in reports)
        {
            var a1 = r.TruthFrames == 0 ? 0 : 100.0 * r.StableRight / r.TruthFrames;
            var a2 = r.TruthFrames == 0 ? 0 : 100.0 * r.RawRight / r.TruthFrames;
            md.AppendLine($"| {r.File} | {r.Frames} | {r.TruthFrames} | {a1:0.0}% / {a2:0.0}% | {r.Jumps} | {r.Flips} |");
        }

        var mism = reports.SelectMany(r => r.Mismatches).Take(20).ToList();
        md.AppendLine();
        md.AppendLine("## 错例（最多 20 条）");
        md.AppendLine();
        if (mism.Count == 0) md.AppendLine("无");
        else foreach (var m in mism) md.AppendLine("- " + m);

        md.AppendLine();
        md.AppendLine("## 沉淀下来的规则（供程序与文档引用）");
        md.AppendLine();
        md.AppendLine("- 底部幕次标签的可靠读取窗口：纵向 `0.828–0.878h`（列表行在 0.89–0.94h，必须排除），横向 `0.28–0.74`。");
        md.AppendLine("- 条带里出现 ≥2 个「第N幕」→ 判为幕次列表，不采信。");
        md.AppendLine("- 幕次判定优先级：模板 > 底部条带 > 整帧文字。");
        md.AppendLine("- 时序稳定：倒退/跳级需连续 2 帧确认；未观测到幕次时保持上一次。");
        md.AppendLine("- `actN` 模板运行时阈值 0.90（跨幕相似度实测 0.80–0.82），其余 0.86。");

        var mdPath = Path.Combine(outDir, "SUMMARY.md");
        var jsonPath = Path.Combine(outDir, "rules.json");
        File.WriteAllText(mdPath, md.ToString(), Encoding.UTF8);
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(new
        {
            generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            reports = reports.Count, frames, truth, accStable, accRaw, jumps, flips,
            avgMs = frames == 0 ? 0 : ms / frames, kinds,
            perReport = reports.Select(r => new { r.File, r.Frames, r.TruthFrames, r.StableRight, r.RawRight, r.Jumps, r.Flips }),
        }, TemplateFactory.JsonOpts), Encoding.UTF8);

        Cli.Say($"蒸馏 {reports.Count} 份报告 → 帧 {frames}，代理标签帧 {truth}，自洽率 {accStable:0.0}%（稳定）/ {accRaw:0.0}%（原始），跳变 {jumps}，抖动 {flips}");
        Cli.Say("已写: " + mdPath);
        Cli.Say("已写: " + jsonPath);
        return 0;
    }

    internal static IEnumerable<string> Expand(IEnumerable<string> inputs)
    {
        foreach (var i in inputs ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(i)) continue;
            if (Directory.Exists(i))
                foreach (var f in Directory.GetFiles(i, "*-replay.json", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
                    yield return f;
            else if (File.Exists(i) && i.EndsWith("-replay.json", StringComparison.OrdinalIgnoreCase)) yield return i;
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
}
