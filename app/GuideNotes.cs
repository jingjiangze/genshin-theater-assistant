using System.Text.Json;

namespace TheaterAssistant;

/// <summary>
/// 从 B站攻略视频转录的「每幕操作节奏」（data/guide-notes.json）。
/// 只是参考文本，不参与识别；缺失文件时静默跳过。
/// </summary>
internal sealed class GuideNotes
{
    private readonly Dictionary<int, string> _acts = new();
    private readonly List<string> _prep = new();

    public static GuideNotes Load(string path)
    {
        var n = new GuideNotes();
        if (!File.Exists(path)) return n;

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        if (root.TryGetProperty("acts", out var acts))
        {
            foreach (var p in acts.EnumerateObject())
            {
                if (int.TryParse(p.Name, out int act) && p.Value.ValueKind == JsonValueKind.String)
                    n._acts[act] = p.Value.GetString() ?? "";
            }
        }

        if (root.TryGetProperty("preparation", out var prep))
        {
            foreach (var p in prep.EnumerateObject())
            {
                var v = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
                if (!string.IsNullOrWhiteSpace(v)) n._prep.Add(v);
            }
        }

        return n;
    }

    public string ActNote(int act) => _acts.TryGetValue(act, out var s) ? s : null;

    public IReadOnlyList<string> Preparation => _prep;
}
