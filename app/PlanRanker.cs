using System.Text.RegularExpressions;

namespace TheaterAssistant;

/// <summary>
/// 「同一幕的三个遭遇变体，哪个最适合当前账号」——本地打分，不依赖模型。
/// 依据：推荐元素命中你池子里的元素数量、推荐角色命中数量、你的角色等级。
/// </summary>
internal static class PlanRanker
{
    public sealed record Ranked(TheaterData.Encounter Encounter, int Score, string Reason);

    public static List<Ranked> Rank(IEnumerable<TheaterData.Encounter> encounters, RosterData roster, TheaterData data)
    {
        var list = new List<Ranked>();
        foreach (var e in encounters)
        {
            int score = 0;
            var reasons = new List<string>();

            foreach (var el in Elements(e.Rec))
            {
                int n = roster.CountElement(data, el, minLevel: 60);
                if (n > 0) { score += Math.Min(n, 3) * 2; reasons.Add($"{el}×{n}"); }
            }

            foreach (var name in Characters(e))
            {
                if (roster.Owns(name)) { score += 3; reasons.Add(name); }
            }

            if (e.Name.Contains("Climactic") || e.Name.Contains("Showdown")) { score -= 1; }

            list.Add(new Ranked(e, score, reasons.Count > 0 ? string.Join("、", reasons) : "无匹配"));
        }

        return list.OrderByDescending(x => x.Score).ToList();
    }

    private static IEnumerable<string> Elements(string rec)
    {
        if (string.IsNullOrWhiteSpace(rec)) yield break;
        foreach (var part in rec.Split(new[] { '/', ',', ' ', '|' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = part.Trim();
            if (p is "Physical" or "Anemo" or "Geo" or "Dendro" or "Cryo" or "Hydro" or "Pyro" or "Electro")
                yield return p;
        }
    }

    private static IEnumerable<string> Characters(TheaterData.Encounter e)
    {
        if (string.IsNullOrWhiteSpace(e.Chars)) yield break;
        foreach (var c in e.Chars.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = c.Trim();
            if (t.Length > 0) yield return t;
        }
    }
}
