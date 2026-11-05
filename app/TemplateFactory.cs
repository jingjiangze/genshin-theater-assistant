using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TheaterAssistant;

/// <summary>
/// 批量适配工厂：把「一帧游戏画面」变成「若干经过质检的模板」。
///
/// 为什么不做手工坐标表：幻想真境剧诗的 UI 会随分辨率/无边框/DX 缩放变化，
/// 手写像素坐标必然漂移。这里改成——先用 OCR 找到窗体上的文字锚点，
/// 用 OCR 返回的四边形（TextBlock.BoxPoints）直接换算成裁剪矩形，
/// 于是「任何分辨率的一帧」都能自动产出该分辨率自己的模板。
///
/// 质检口径（两个方向都要过）：
///   1) 在场一致性 presentMin：同一分辨率下，凡是识别出该标签的帧，模板都要命中（≥0.92）；
///   2) 缺席不误报 absentMax：同一分辨率下，凡是没有该标签的帧，模板都不能命中（&lt;0.80，低于运行时阈值 0.86）。
/// 只有两条都过的模板才会被安装进 templates/&lt;宽x高&gt;/。
/// </summary>
internal static class TemplateFactory
{
    /// <summary>一个标签规则：正则命中 OCR 文本后，产出对应名字的模板。</summary>
    internal sealed class Rule
    {
        /// <summary>固定模板名（如 hall / choice / result）。为 null 时由 <see cref="Prefix"/> + 数字生成。</summary>
        public string Name;
        /// <summary>前缀（如 act → act3）。</summary>
        public string Prefix;
        /// <summary>正则；第二个用途是捕获幕次数字。</summary>
        public string Pattern;
        public int MinWidth = 16;
        public int MinHeight = 10;
    }

    /// <summary>
    /// 标签表。名字要和 TheaterLogic.Evaluate 认的语义对齐：
    ///   actN → 第 N 幕；choice → 选人界面；result → 结算界面。
    /// test 仅用于自检（ASCII，不依赖中文字体）。
    /// </summary>
    internal static readonly Rule[] Rules =
    {
        new() { Name = "test",   Pattern = @"TESTLABEL" },
        new() { Prefix = "act",  Pattern = @"第\s*([一二三四五六七八九十0-9]{1,3})\s*幕" },
        new() { Name = "title",  Pattern = @"幻想真境剧诗|真境剧诗" },
        new() { Name = "hall",   Pattern = @"演出准备|助演嘉宾|演出回顾|奖励一览|演出详情|助演阵容" },
        new() { Name = "choice", Pattern = @"选择角色|试用角色|选择一名|助演角色|待命角色|可选角色|邀请助演|编入的角色" },
        new() { Name = "result", Pattern = @"演出结算|演出完成|演出结束|结算奖励|挑战结算" },
        new() { Name = "begin",  Pattern = @"开始挑战|开始演出|进入演出|开始剧诗" },
        new() { Name = "diff",   Pattern = @"难度|普通模式|困难模式|极难模式|无畏模式|月谕|卓越" },
    };

    /// <summary>严格幕次正则（必须带“幕”字），只用于判断“整帧是不是幕次列表”。</summary>
    private static readonly Regex ActNumRegex = new(@"第\s*([一二三四五六七八九十0-9]{1,3})\s*幕", RegexOptions.Compiled);

    /// <summary>一条模板规格（坐标是帧内像素，与被裁剪的那一帧绑定）。</summary>
    internal sealed class SpecRow
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("x")] public int X { get; set; }
        [JsonPropertyName("y")] public int Y { get; set; }
        [JsonPropertyName("w")] public int W { get; set; }
        [JsonPropertyName("h")] public int H { get; set; }
        [JsonPropertyName("text")] public string Text { get; set; } = "";
        [JsonPropertyName("score")] public double Score { get; set; }
    }

    /// <summary>灰度帧缓存：一帧只转一次，后续所有局部比对都复用它。</summary>
    internal sealed class GrayFrame
    {
        public byte[] Data;
        public int W, H;

        public static GrayFrame From(Bitmap src)
        {
            var g = new GrayFrame { W = src.Width, H = src.Height, Data = new byte[src.Width * src.Height] };
            using var bmp = new Bitmap(g.W, g.H, PixelFormat.Format24bppRgb);
            using (var gfx = Graphics.FromImage(bmp))
            {
                gfx.DrawImage(src, 0, 0, g.W, g.H);
            }
            var data = bmp.LockBits(new Rectangle(0, 0, g.W, g.H), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                int stride = data.Stride;
                var buf = new byte[stride * g.H];
                Marshal.Copy(data.Scan0, buf, 0, buf.Length);
                for (int y = 0; y < g.H; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < g.W; x++)
                    {
                        int i = row + x * 3;
                        g.Data[y * g.W + x] = (byte)((buf[i] * 29 + buf[i + 1] * 150 + buf[i + 2] * 77) >> 8);
                    }
                }
            }
            finally { bmp.UnlockBits(data); }
            return g;
        }
    }

    /// <summary>坐标裁剪 + 边距 + 边界收敛。返回 null 表示这一条不可用。</summary>
    public static SpecRow MakeSpec(string name, string text, double score, Rectangle box, int frameW, int frameH, double padRatio = 0.15)
    {
        if (box.Width <= 0 || box.Height <= 0) return null;
        int pad = Math.Max(2, (int)Math.Round(box.Height * padRatio));
        int x = box.X - pad, y = box.Y - pad;
        int w = box.Width + pad * 2, h = box.Height + pad * 2;
        if (x < 0) { w += x; x = 0; }
        if (y < 0) { h += y; y = 0; }
        if (x + w > frameW) w = frameW - x;
        if (y + h > frameH) h = frameH - y;
        if (w < 12 || h < 8) return null;
        // 防止 OCR 把整块面板当一行：超过帧面积 6% 的矩形不像"标签"，不拿来做模板
        if ((long)w * h > (long)frameW * frameH * 0.06) return null;
        return new SpecRow { Name = name, Text = text, Score = score, X = x, Y = y, W = w, H = h };
    }

    /// <summary>OCR 文本框 → 模板规格（同名只保留置信度最高的一条）。</summary>
    public static List<SpecRow> Derive(IEnumerable<OcrBox> boxes, int frameW, int frameH, double padRatio = 0.15)
    {
        var best = new Dictionary<string, SpecRow>(StringComparer.Ordinal);
        if (boxes == null) return new List<SpecRow>();

        // 幕次选择条会把 第一幕…第十幕 并排列出：这种"列表行"不是当前幕次，
        // 不能拿来做 actN 锚点（否则十个模板同时命中）。判定方式：一排 y/h 相近、幕次不同的框。
        // 另外要求 actN 来自画面下半部（幕内标签在大堂底部/战斗 HUD 底部），避免把 UP 贴字当成锚点。
        var actBoxes = new List<(int Num, Rectangle Rect)>();
        foreach (var b in boxes)
        {
            if (b == null || string.IsNullOrWhiteSpace(b.Text)) continue;
            foreach (Match mm in ActNumRegex.Matches(b.Text))
            {
                var num = ParseActNumber(mm.Groups[1].Value);
                if (num.HasValue) actBoxes.Add((num.Value, b.Rect));
            }
        }

        bool InListRow(int i)
        {
            int peers = 0;
            for (int j = 0; j < actBoxes.Count; j++)
            {
                if (i == j) continue;
                if (actBoxes[j].Num == actBoxes[i].Num) continue;
                if (Math.Abs(actBoxes[j].Rect.Y - actBoxes[i].Rect.Y) > 8) continue;
                if (Math.Abs(actBoxes[j].Rect.Height - actBoxes[i].Rect.Height) > 8) continue;
                peers++;
            }
            return peers >= 2;                      // 同一排至少三个不同幕次 → 是列表
        }

        var candidates = new HashSet<int>();
        for (int i = 0; i < actBoxes.Count; i++)
        {
            if (InListRow(i)) continue;
            if (actBoxes[i].Rect.Y < frameH * 0.5) continue;   // 只要下半部
            candidates.Add(actBoxes[i].Num);
        }
        bool actAmbiguous = candidates.Count > 1;

        foreach (var box in boxes)
        {
            if (box == null || string.IsNullOrWhiteSpace(box.Text)) continue;
            var text = box.Text.Trim();

            foreach (var rule in Rules)
            {
                Match m = null;
                try { m = Regex.Match(text, rule.Pattern); }
                catch { continue; }
                if (m == null || !m.Success) continue;
                if (box.Rect.Width < rule.MinWidth || box.Rect.Height < rule.MinHeight) continue;

                string name = rule.Name;
                if (name == null)
                {
                    var n = ParseActNumber(m.Groups.Count > 1 ? m.Groups[1].Value : "");
                    if (!n.HasValue) continue;
                    // 幕次列表行 / 上半部贴字 → 不当锚点；一帧里出现多个候选幕次也不派生
                    if (actAmbiguous || !candidates.Contains(n.Value)) continue;
                    name = rule.Prefix + n.Value;
                }

                var row = MakeSpec(name, text, box.Score, box.Rect, frameW, frameH, padRatio);
                if (row == null) continue;
                if (!best.TryGetValue(name, out var cur) || row.Score > cur.Score) best[name] = row;
                break; // 一条文本只归一个标签，避免同一处产出多个模板
            }
        }

        return best.Values.OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>幕次数字：支持阿拉伯数字与中文数字（一…十九、二十、二十一…）。</summary>
    public static int? ParseActNumber(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (int.TryParse(s, out int direct)) return direct > 0 ? direct : (int?)null;

        var digits = new Dictionary<char, int>
        {
            ['一'] = 1, ['二'] = 2, ['三'] = 3, ['四'] = 4, ['五'] = 5,
            ['六'] = 6, ['七'] = 7, ['八'] = 8, ['九'] = 9,
        };

        int total = 0, cur = 0;
        bool any = false;
        foreach (var ch in s)
        {
            if (ch == '十')
            {
                total += (cur == 0 ? 1 : cur) * 10;
                cur = 0;
                any = true;
            }
            else if (digits.TryGetValue(ch, out int d))
            {
                cur = d;
                any = true;
            }
            else return null;
        }
        if (!any) return null;
        int value = total + cur;
        return value > 0 ? value : (int?)null;
    }

    /// <summary>在 (x,y) 处算灰度 SSD 相似度（1=完全一致）。</summary>
    public static double ScoreAt(GrayFrame frame, GrayFrame tpl, int x, int y)
    {
        if (x < 0 || y < 0 || x + tpl.W > frame.W || y + tpl.H > frame.H) return 0.0;
        long sum = 0;
        for (int j = 0; j < tpl.H; j++)
        {
            int fo = (y + j) * frame.W + x;
            int to = j * tpl.W;
            for (int i = 0; i < tpl.W; i++)
            {
                int d = frame.Data[fo + i] - tpl.Data[to + i];
                sum += (long)d * d;
            }
        }
        double rmse = Math.Sqrt(sum / Math.Max(1.0, tpl.W * (double)tpl.H));
        return 1.0 - rmse / 255.0;
    }

    /// <summary>
    /// 只在窗口内搜索模板左上角（同分辨率布局一致，不需要全帧扫描）。
    /// window 是「左上角的搜索范围」，左上角与右下角都含。
    /// </summary>
    public static (double Score, int X, int Y) BestIn(GrayFrame frame, GrayFrame tpl, Rectangle window)
    {
        double best = 0; int bx = -1, by = -1;
        int x0 = Math.Max(0, window.X);
        int y0 = Math.Max(0, window.Y);
        int x1 = Math.Min(frame.W - tpl.W, window.X + window.Width);
        int y1 = Math.Min(frame.H - tpl.H, window.Y + window.Height);
        if (x1 < x0) x1 = x0;
        if (y1 < y0) y1 = y0;

        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                double s = ScoreAt(frame, tpl, x, y);
                if (s > best) { best = s; bx = x; by = y; }
            }
        }
        return (best, bx, by);
    }

    /// <summary>全帧粗扫（供质检用）：位置与像素双重抽样，够用且不慢。返回最优分与位置。</summary>
    public static (double Score, int X, int Y) BestInFrame(GrayFrame frame, GrayFrame tpl, int step = 4, int sample = 2)
    {
        if (tpl.W > frame.W || tpl.H > frame.H) return (0.0, -1, -1);
        if (step < 1) step = 1;
        if (sample < 1) sample = 1;
        double best = 0; int bx = -1, by = -1;
        int cols = (tpl.W + sample - 1) / sample;
        int rows = (tpl.H + sample - 1) / sample;
        double norm = Math.Max(1.0, cols * (double)rows);
        for (int y = 0; y + tpl.H <= frame.H; y += step)
        {
            for (int x = 0; x + tpl.W <= frame.W; x += step)
            {
                long sum = 0;
                for (int j = 0; j < tpl.H; j += sample)
                {
                    int fo = (y + j) * frame.W + x;
                    int to = j * tpl.W;
                    for (int i = 0; i < tpl.W; i += sample)
                    {
                        int d = frame.Data[fo + i] - tpl.Data[to + i];
                        sum += (long)d * d;
                    }
                }
                double rmse = Math.Sqrt(sum / norm);
                double s = 1.0 - rmse / 255.0;
                if (s > best) { best = s; bx = x; by = y; }
            }
        }
        return (best, bx, by);
    }

    public static Bitmap Crop(Bitmap frame, SpecRow spec) =>
        frame.Clone(new Rectangle(spec.X, spec.Y, spec.W, spec.H), PixelFormat.Format32bppArgb);

    public static string BucketName(int w, int h) => $"{w}x{h}";

    public static JsonSerializerOptions JsonOpts => new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
