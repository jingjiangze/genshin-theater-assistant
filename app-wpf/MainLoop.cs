using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Drawing;

namespace TheaterAssistant;

/// <summary>
/// 主循环：抓屏 → 识别 → 决策 → 驱动悬浮球。
///
/// 这是 WPF 版此前唯一缺失的一环（悬浮球能呼吸但不知道游戏里发生了什么）。
/// 逻辑与 WinForms 版一致，只把 `System.Windows.Forms.Timer` 换成 `DispatcherTimer`，
/// 决策出口换成 `DecisionOrchestrator`（旧 TheaterLogic 不引入，见 AUDIT-OVERALL P0）。
///
/// 红线不变：只读像素（GDI BitBlt 自己的进程内），不注入、不读内存、不模拟输入。
/// </summary>
internal sealed class MainLoop
{
    private readonly FloatingBall _ball;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly OcrService _ocr = new();
    private readonly List<TemplateEntry> _templates = new();
    private readonly ActStabilizer _stab = new();
    private string _bucketKey = "";
    private string _cachedText = "";
    private int _tickCount;
    private bool _busy;

    public string LastStatus { get; private set; } = "初始化…";
    public int? LastAct { get; private set; }
    public string LastSource { get; private set; } = "";
    public List<string> LastLines { get; private set; } = new();

    public MainLoop(FloatingBall ball)
    {
        _ball = ball;
        _timer.Tick += async (_, _) => await TickAsync();
    }

    public async Task StartAsync()
    {
        try
        {
            await _ocr.InitializeAsync(AppPaths.ModelsRoot);
            LastStatus = _ocr.Status;
            _ball.SetState(FloatingBall.BallState.Idle);
        }
        catch (Exception ex)
        {
            LastStatus = "OCR 初始化失败：" + ex.GetType().Name;
            App.LogCrash(ex, "MainLoop.StartAsync");
        }
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private async Task TickAsync()
    {
        if (_busy) return;     // 上一帧还没跑完就跳过，避免堆积
        _busy = true;
        try
        {
            _tickCount++;

            // ① 找游戏窗口（标题兜底）
            var win = GameWindow.Find("原神", 0);
            if (win == IntPtr.Zero) win = GameWindow.Find("GenshinImpact", 0);
            if (win == IntPtr.Zero)
            {
                LastStatus = "未找到游戏窗口，等待中…";
                _ball.SetState(FloatingBall.BallState.Waiting);
                return;
            }

            // ② 抓屏（只读）
            using var bmp = ScreenCapture.CaptureClient(win);
            if (bmp == null) { LastStatus = "抓屏失败"; _ball.SetState(FloatingBall.BallState.Waiting); return; }

            // ③ 模板：分辨率变了就重载
            LoadTemplatesFor(bmp.Width, bmp.Height);
            var hits = Matcher.Scan(bmp, _templates);

            // ④ 条带 OCR（幕次）：只在底部 0.828–0.878 高、0.28–0.74 宽的条带里认，快且准
            var band = await _ocr.RecognizeRegionAsync(bmp, new RectangleF(0.28f, 0.828f, 0.46f, 0.05f), 4);

            // ⑤ 全帧 OCR：每 3 帧兜一次（不必要每帧全扫，省 CPU）
            if (_tickCount % 3 == 0)
            {
                var full = await _ocr.RecognizeAsync(bmp);
                if (!string.IsNullOrWhiteSpace(full)) _cachedText = full;
            }
            var text = _cachedText;

            // ⑥ 幕次判定：条带优先，其次全帧里的「第N幕」，最后是模板命中
            int? act = ParseAct(band) ?? ParseAct(text);
            var stable = _stab.Push(act);
            LastAct = stable;

            // ⑦ 弃权：连续未识别超时就不给过时指令
            if (_stab.ShouldAbstain)
            {
                LastStatus = $"幕次未识别 {_stab.MissedMilliseconds}ms";
                LastLines = new List<string> { "· 暂不判断：请在剧诗界面停留一下（或切换幕次）" };
                LastSource = "·";
                _ball.SetState(FloatingBall.BallState.Waiting);
                return;
            }

            // ⑧ 决策
            var diff = QuantData.Difficulties.LastOrDefault() ?? "";
            var enemy = stable.HasValue ? QuantData.Enemy(stable.Value, diff) : null;
            var d = DecisionOrchestrator.Decide(new DecisionOrchestrator.Context
            {
                Act = stable,
                Difficulty = diff,
                Enemy = enemy,
                Team = TeamSolver.AsTeamProfile(),
                Options = Array.Empty<ChoiceDecider.Option>(),
                Kind = stable.HasValue ? "act" : (hits.Count > 0 ? "ui" : ""),
            });

            LastSource = d.Source;
            LastLines = d.Lines;
            LastStatus = stable.HasValue ? $"第{stable}幕 · {d.Source}" : d.Source;

            // ⑨ 驱动悬浮球：状态色 + 角标
            _ball.SetState(d.Source switch
            {
                var s when s.StartsWith("★") => FloatingBall.BallState.Done,
                var s when s.StartsWith("◆") => FloatingBall.BallState.Idle,
                var s when s.StartsWith("◇") => FloatingBall.BallState.Thinking,
                _ => FloatingBall.BallState.Idle,
            });
        }
        catch (Exception ex)
        {
            LastStatus = "循环异常：" + ex.GetType().Name;
            App.LogCrash(ex, "MainLoop.Tick");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>从文本里取「第N幕」；「一二三…」这类中文数字也认。</summary>
    private static int? ParseAct(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(s, @"第\s*(\d{1,2})\s*幕");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n)) return n;

        var cn = System.Text.RegularExpressions.Regex.Match(s, @"第\s*([一二三四五六七八九十]{1,2})\s*幕");
        if (!cn.Success) return null;
        var map = new Dictionary<char, int> {
            {'一',1},{'二',2},{'三',3},{'四',4},{'五',5},
            {'六',6},{'七',7},{'八',8},{'九',9},{'十',10},
        };
        var t = cn.Groups[1].Value;
        if (t.Length == 1) return map.TryGetValue(t[0], out var v) ? v : null;
        // 十一、十二
        if (t[0] == '十') return map.TryGetValue(t[1], out var u) ? 10 + u : null;
        if (t.Length == 2 && t[1] == '十') return map.TryGetValue(t[0], out var w) ? w * 10 : null;
        return null;
    }

    /// <summary>按客户区尺寸选最接近的模板目录并加载（与 WinForms 版同逻辑）。</summary>
    private void LoadTemplatesFor(int width, int height)
    {
        var root = AppPaths.TemplatesRoot;
        if (!Directory.Exists(root)) return;

        var dir = root;
        if (width > 0)
        {
            var best = Directory.GetDirectories(root)
                .Select(d => new { Dir = d, Ok = TryParseSize(Path.GetFileName(d), out var dw, out _), W = dw })
                .Where(x => x.Ok && Math.Abs(x.W - width) <= Math.Max(32, (int)(width * 0.12)))
                .OrderBy(x => Math.Abs(x.W - width))
                .FirstOrDefault();
            if (best != null) dir = best.Dir;
        }

        var key = dir + "|" + width + "x" + height;
        if (key == _bucketKey) return;
        _bucketKey = key;

        foreach (var t in _templates) t.Bitmap.Dispose();
        _templates.Clear();

        foreach (var f in Directory.GetFiles(dir, "*.png"))
        {
            var name = Path.GetFileNameWithoutExtension(f);
            if (name.StartsWith('_')) continue;
            try { _templates.Add(new TemplateEntry(name, new Bitmap(f), TemplateEntry.ThresholdFor(name))); }
            catch { }
        }
    }

    private static bool TryParseSize(string s, out int w, out int h)
    {
        w = h = 0;
        var m = System.Text.RegularExpressions.Regex.Match(s, @"(\d{3,4})x(\d{3,4})");
        return m.Success && int.TryParse(m.Groups[1].Value, out w) && int.TryParse(m.Groups[2].Value, out h);
    }
}
