// ─────────────────────────────────────────────────────────────────────────────
// 设置窗保存的 ui.json —— **此前只写不读**（审计发现：所有选项都是空转）。
// 这里集中读取一次，由 App 在启动时与设置窗关闭后各应用一次。
//
// 字段与 SettingsWindow.SaveAll 写出的键一一对应；缺失/越界一律回中性默认，
// 绝不因为一个坏字段让程序起不来。
// ─────────────────────────────────────────────────────────────────────────────
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TheaterAssistant;

internal sealed class UiPrefs
{
    public int ScanIntervalMs = 1000;
    public int OpacityPct = 96;
    public int BodyFontPt = 12;
    public bool ShowSourceMark = true;
    public bool HideWhenGameMissing = true;
    public bool HotkeyEnable = true;

    public static UiPrefs Load()
    {
        var p = new UiPrefs();
        try
        {
            var path = Path.Combine(AppPaths.Dir, "ui.json");
            if (!File.Exists(path)) return p;
            using var doc = JsonDocument.Parse(File.ReadAllText(path).Replace("\uFEFF", ""));
            var r = doc.RootElement;

            if (TryInt(r, "scanIntervalMs", out var a)) p.ScanIntervalMs = Math.Clamp(a, 200, 10000);
            if (TryInt(r, "opacityPct", out var b)) p.OpacityPct = Math.Clamp(b, 30, 100);
            if (TryInt(r, "bodyFontPt", out var c)) p.BodyFontPt = Math.Clamp(c, 9, 24);
            if (TryBool(r, "showSourceMark", out var d)) p.ShowSourceMark = d;
            if (TryBool(r, "hideWhenGameMissing", out var e)) p.HideWhenGameMissing = e;
            if (TryBool(r, "hotkeyEnable", out var f)) p.HotkeyEnable = f;
        }
        catch { }
        return p;
    }

    private static bool TryInt(JsonElement r, string name, out int v)
    {
        v = 0;
        return r.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out v);
    }

    private static bool TryBool(JsonElement r, string name, out bool v)
    {
        v = false;
        if (!r.TryGetProperty(name, out var e)) return false;
        if (e.ValueKind == JsonValueKind.True) { v = true; return true; }
        if (e.ValueKind == JsonValueKind.False) { v = false; return true; }
        return false;
    }

    /// <summary>只用于写回「热键开关」这类设置窗还没落盘的字段时保持其它值不变。</summary>
    public JsonObject ToJson() => new()
    {
        ["scanIntervalMs"] = ScanIntervalMs,
        ["opacityPct"] = OpacityPct,
        ["bodyFontPt"] = BodyFontPt,
        ["showSourceMark"] = ShowSourceMark,
        ["hideWhenGameMissing"] = HideWhenGameMissing,
        ["hotkeyEnable"] = HotkeyEnable,
    };
}
