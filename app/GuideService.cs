using System.Text.Json;

namespace TheaterAssistant;

/// <summary>一条攻略入口：站内搜索深链 / 直达 URL / 官方嵌入播放器。</summary>
internal sealed record GuideItem(string Title, string Kind, string Url, bool Embed);

/// <summary>
/// 攻略聚合：只做 URL 拼装与本地清单读取。
/// 不抓取页面、不调用非公开接口、不带 cookie、不登录。
/// </summary>
internal static class GuideService
{
    private const string BilibiliSearch = "https://search.bilibili.com/all?keyword={0}&order=click";
    private const string BilibiliPlayer = "https://player.bilibili.com/player.html?bvid={0}&page=1&high_quality=1&danmaku=0&autoplay=0";

    public static List<GuideItem> Load(string path, int? act)
    {
        var list = new List<GuideItem>();
        if (!File.Exists(path)) return list;

        using var doc = JsonDocument.Parse(File.ReadAllText(path));

        // 幕专属放前面
        if (act.HasValue
            && doc.RootElement.TryGetProperty("acts", out var acts)
            && acts.TryGetProperty(act.Value.ToString(), out var perAct))
        {
            foreach (var e in perAct.EnumerateArray()) Add(list, e);
        }

        if (doc.RootElement.TryGetProperty("defaults", out var defs))
        {
            foreach (var e in defs.EnumerateArray()) Add(list, e);
        }

        return list;
    }

    private static void Add(List<GuideItem> list, JsonElement e)
    {
        string title = Str(e, "title");
        string kind = Str(e, "kind");
        string keyword = Str(e, "keyword");
        string url = Str(e, "url");
        string bvid = Str(e, "bvid");

        switch (kind)
        {
            case "search":
                if (keyword.Length == 0) return;
                list.Add(new GuideItem(title, "search",
                    string.Format(BilibiliSearch, Uri.EscapeDataString(keyword)), false));
                break;

            case "video":
                if (bvid.Length == 0) return;
                list.Add(new GuideItem(title, "video",
                    string.Format(BilibiliPlayer, Uri.EscapeDataString(bvid)), true));
                break;

            case "url":
                if (url.Length == 0) return;
                list.Add(new GuideItem(title, "url", url, false));
                break;
        }
    }

    private static string Str(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return "";
        return v.GetString() ?? "";
    }

    /// <summary>当前幕的关键词（给"搜索"按钮用）。</summary>
    public static string ActKeyword(int act, string bossHint)
    {
        var suffix = string.IsNullOrEmpty(bossHint) ? $"第 {act} 幕" : bossHint;
        return $"幻想真境剧诗 {suffix}";
    }
}
