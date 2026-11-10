// ─────────────────────────────────────────────────────────────────────────────
// 模板搜索区域（ROI）：BetterGI 的识别对象都自带区域，只在区域内匹配——
// 成本按面积线性下降，避免「全帧 × 多模板 × 多尺度」的组合爆炸。
//
// 数据：data/template-rois.json
//   { "act": [x, y, w, h], "hall": [...], ... }   归一化到画布 0..1
// 匹配规则：按名字做「最长前缀」匹配（act3 → act），没有配置就返回 null = 全画布。
// ─────────────────────────────────────────────────────────────────────────────
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TheaterAssistant;

internal static class Roimap
{
    private static readonly Dictionary<string, double[]> Map = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;

    /// <summary>最近一次加载状态（诊断用）。</summary>
    public static string Status { get; private set; } = "未加载";

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            var path = AppPaths.Data("template-rois.json");
            if (!File.Exists(path)) { Status = "无配置（全画布匹配）"; return; }

            using var doc = JsonDocument.Parse(File.ReadAllText(path).Replace("\uFEFF", ""));
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Array || p.Value.GetArrayLength() != 4) continue;
                var v = new double[4];
                var i = 0;
                foreach (var n in p.Value.EnumerateArray()) v[i++] = n.GetDouble();
                Map[p.Name] = v;
            }
            Status = $"已加载 {Map.Count} 条 ROI";
        }
        catch (Exception ex)
        {
            Status = "加载失败: " + ex.GetType().Name;
        }
    }

    /// <summary>按最长前缀取 ROI（归一化 x,y,w,h）；未命中返回 null。</summary>
    public static double[] Lookup(string name)
    {
        EnsureLoaded();
        if (string.IsNullOrEmpty(name) || Map.Count == 0) return null;

        double[] best = null;
        var bestLen = -1;
        foreach (var kv in Map)
        {
            if (name.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase) && kv.Key.Length > bestLen)
            {
                best = kv.Value;
                bestLen = kv.Key.Length;
            }
        }
        return best;
    }
}
