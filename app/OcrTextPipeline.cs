using System.Text.Json;

namespace TheaterAssistant;

/// <summary>
/// OCR 文本后处理：把 BetterGI 的两个关键做法搬到我们的识别层（审计见 docs/BETTERGI-OCR-AUDIT.md）。
///
/// 只搬**纯逻辑**部分（不引 OpenCV）：
///   D1 置信度过滤：BetterGI `DropScore = 0.5f`，`Score >= DropScore` 才保留；
///   D2 同排归并排序：先按 Y 再按 X；相邻两框 **Y 差 &lt; 10px 视为同一排**，排内按 X 升序。
///
/// 为什么必须搬 D2：商店/三选一的「第N张」完全依赖阅读顺序；
/// 我们此前直接用 OCR 引擎输出顺序，同一行多卡片时顺序可能错 → 玩家照做会选错。
/// 阈值全部来自 data/decision-policy.json，不硬编码。
/// </summary>
internal static class OcrTextPipeline
{
    internal sealed class Box
    {
        public double X;
        public double Y;
        public double Score = 1.0;
        public string Text = "";
    }

    public static double DropScore { get; private set; } = 0.5;
    public static double SameLineTolerance { get; private set; } = 10.0;

    public static void Configure(double dropScore, double sameLineTolerance)
    {
        if (dropScore is > 0 and <= 1) DropScore = dropScore;
        if (sameLineTolerance is > 0 and <= 200) SameLineTolerance = sameLineTolerance;
    }

    /// <summary>D1：丢置信度低于阈值的框（BetterGI 的 DropScore）。</summary>
    public static List<Box> FilterByScore(IEnumerable<Box> boxes)
    {
        var list = new List<Box>();
        if (boxes == null) return list;
        foreach (var b in boxes)
            if (b != null && b.Score >= DropScore) list.Add(b);
        return list;
    }

    /// <summary>
    /// D2：同排归并排序。先按 Y 升序（容忍 <c>SameLineTolerance</c> 的抖动），
    /// 同一排内按 X 升序；不同排之间保持 Y 相对顺序。
    /// </summary>
    public static List<Box> OrderSameLine(IEnumerable<Box> boxes)
    {
        var list = boxes?.Where(b => b != null).ToList() ?? new List<Box>();
        if (list.Count <= 1) return list;

        // 先按（Y, X）粗排
        list.Sort((a, b) =>
        {
            var dy = a.Y - b.Y;
            if (Math.Abs(dy) > SameLineTolerance) return dy < 0 ? -1 : 1;
            return a.X.CompareTo(b.X);
        });

        // 再把「与上一排 Y 差 < 容差」的连续段当作同一排，段内按 X 排序（稳定）
        int start = 0;
        for (int i = 1; i <= list.Count; i++)
        {
            bool sameRow = i < list.Count && Math.Abs(list[i].Y - list[start].Y) < SameLineTolerance;
            if (sameRow) continue;
            if (i - start > 1)
                list.Sort(start, i - start, Comparer<Box>.Create((a, b) => a.X.CompareTo(b.X)));
            start = i;
        }
        return list;
    }

    /// <summary>一步到位：过滤 + 排序，返回最终阅读顺序的文本行。</summary>
    public static List<string> ToLines(IEnumerable<Box> boxes) =>
        OrderSameLine(FilterByScore(boxes)).Select(b => b.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();

    /// <summary>从 data/decision-policy.json 的 ocr 段读阈值；缺省用 BetterGI 默认值。</summary>
    public static void LoadFromPolicy(string policyPath)
    {
        try
        {
            if (!File.Exists(policyPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(policyPath));
            if (!doc.RootElement.TryGetProperty("ocr", out var ocr) || ocr.ValueKind != JsonValueKind.Object) return;
            if (ocr.TryGetProperty("dropScore", out var ds) && ds.ValueKind == JsonValueKind.Number)
                DropScore = ds.GetDouble();
            if (ocr.TryGetProperty("sameLineTolerance", out var sl) && sl.ValueKind == JsonValueKind.Number)
                SameLineTolerance = sl.GetDouble();
        }
        catch { }
    }

    public static int RunCli()
    {
        Cli.Attach();
        LoadFromPolicy(AppPaths.Data("decision-policy.json"));
        Cli.Say($"OCR 后处理参数（来自 decision-policy.json）: dropScore={DropScore:0.00} sameLineTolerance={SameLineTolerance:0}px");

        var sample = new[]
        {
            new Box { X = 300, Y = 100, Score = 0.92, Text = "第二张" },
            new Box { X = 100, Y = 102, Score = 0.95, Text = "第一张" },
            new Box { X = 500, Y = 99,  Score = 0.90, Text = "第三张" },
            new Box { X = 100, Y = 300, Score = 0.40, Text = "低分噪声" },
            new Box { X = 120, Y = 305, Score = 0.88, Text = "下一排" },
        };
        Cli.Say("输入（乱序，含 1 条低分）: " + string.Join(" | ", sample.Select(b => $"{b.Text}@({b.X},{b.Y})s{b.Score:0.00}")));
        var lines = ToLines(sample);
        Cli.Say("输出（过滤+同排排序）: " + string.Join(" | ", lines));
        return 0;
    }
}
