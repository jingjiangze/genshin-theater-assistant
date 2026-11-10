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
    private readonly GameModeProfile _mode;
    private LlmAdvisor _llm;
    private LocalBrain _brain;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly OcrService _ocr = new();
    private readonly List<TemplateEntry> _templates = new();
    private readonly Dictionary<string, (int W, int H)> _templateSizes = new();
    private readonly ActStabilizer _stab = new();
    private string _bucketKey = "";
    private string _cachedText = "";
    private int _tickCount;
    private bool _busy;
    private int _teamSize;
    private int _teamTarget = 4;

    // 画面去重（静态界面省算力）：采样哈希 + 上次决策结果
    private ulong _lastFrameHash;
    private int _lastDecideTick;
    private int? _lastStable;
    private string _lastBandText = "";
    private IReadOnlyList<MaskHit> _lastHits = Array.Empty<MaskHit>();

    /// <summary>
    /// 每帧快照（给遮罩用）：游戏句柄 + 帧尺寸 + 幕次 + 决策来源 + 命中框。
    /// 遮罩只消费这个事件，不认识识别/决策的内部类型——保持单向依赖。
    /// </summary>
    public event Action<TickSnapshot> Updated;

    public string LastStatus { get; private set; } = "初始化…";
    public int? LastAct { get; private set; }
    public string LastSource { get; private set; } = "";
    public List<string> LastLines { get; private set; } = new();

    public MainLoop(FloatingBall ball, GameModeProfile mode = null)
    {
        _ball = ball;
        _mode = mode ?? GameModes.Default;
        _timer.Tick += async (_, _) => await TickAsync();
    }

    /// <summary>当前玩法档案（剧诗/深渊…）。识别区域与编号规则都从这里取。</summary>
    public GameModeProfile Mode => _mode;

    public async Task StartAsync()
    {
        try
        {
            // OCR 后处理阈值（DropScore / 同排容差）来自策略文件，和自检/CLI 用同一份
            try { OcrTextPipeline.LoadFromPolicy(AppPaths.Data("decision-policy.json")); } catch { }

            // 战斗中手法数据（rotations.json）：Routine.Loaded 才会追加「手法·」行
            try { Routine.Load(AppPaths.Data("rotations.json")); }
            catch (Exception ex) { App.LogCrash(ex, "MainLoop.RoutineLoad"); }

            // ◇模型层（可选）：优先进程内 GGUF（实测 147ms/答），没有再走 data/llm.json 的 HTTP 端点。
            // 两者都没有 = 不联网，走原兜底（○语料 / ·规则）。
            try { _brain = LocalBrain.TryLoad(AppPaths.LocalModelPath); }
            catch (Exception ex) { App.LogCrash(ex, "MainLoop.BrainLoad"); }
            try { _llm = LlmAdvisor.TryLoad(AppPaths.Data("llm.json")); }
            catch (Exception ex) { App.LogCrash(ex, "MainLoop.LlmLoad"); }

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

    public void Stop()
    {
        _timer.Stop();
        try { _brain?.Dispose(); } catch { }
        try { _llm?.Dispose(); } catch { }
    }

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
                Emit(new TickSnapshot { GameHandle = IntPtr.Zero, Status = LastStatus });
                return;
            }

            // ② 抓屏（只读）
            using var bmp = ScreenCapture.CaptureClient(win);
            if (bmp == null)
            {
                LastStatus = "抓屏失败";
                _ball.SetState(FloatingBall.BallState.Waiting);
                Emit(new TickSnapshot { GameHandle = win, Status = LastStatus });
                return;
            }

            // ②a 画面没变就整帧跳过（模板 + OCR 都不做）——静态界面（选卡/结算/暂停）下省掉 90% 的算力。
            //     采样 32×32 像素算哈希（≈1ms），比跑一遍 OCR（~1.1s）便宜三个数量级。
            var frameHash = FrameHash(bmp);
            if (frameHash == _lastFrameHash && _lastStable.HasValue && _tickCount - _lastDecideTick < 5)
            {
                Emit(new TickSnapshot
                {
                    GameHandle = win,
                    FrameWidth = bmp.Width,
                    FrameHeight = bmp.Height,
                    Act = _lastStable,
                    Status = LastStatus,
                    Source = LastSource,
                    BandText = _lastBandText,
                    Lines = LastLines,
                    Hits = _lastHits,
                    Skipped = true,
                });
                return;
            }
            _lastFrameHash = frameHash;

            // ②b OCR 预处理（D5 白字掩膜，配置驱动；off 时返回 null 用原图）
            OcrPreprocess.Load(AppPaths.Data("decision-policy.json"));
            using var pre = OcrPreprocess.Apply(bmp);
            var ocrFrame = pre ?? bmp;

            // ③ 模板：分辨率变了就重载
            LoadTemplatesFor(bmp.Width, bmp.Height);
            var hits = Matcher.Scan(bmp, _templates);

            // ④ 条带 OCR（幕次）：ROI 来自玩法档案（剧诗默认 0.28/0.828/0.46/0.05），快且准
            var band = _mode.HasBand
                ? await _ocr.RecognizeRegionAsync(ocrFrame, new RectangleF(
                    (float)_mode.BandLeft, (float)_mode.BandTop, (float)_mode.BandWidth, (float)_mode.BandHeight), 4)
                : "";

            // ⑤ 全帧 OCR：每 3 帧兜一次（不必要每帧全扫，省 CPU）
            //    走带框识别 + OcrTextPipeline：先按置信度丢噪声，再按「同排从左到右」排序——
            //    否则「第N张」的 N 会和屏幕上的实际位置对不上（选错卡）。
            if (_tickCount % 4 == 0)
            {
                var boxes = await _ocr.RecognizeBoxesAsync(ocrFrame);
                if (boxes != null && boxes.Count > 0)
                {
                    var pipelineBoxes = boxes.Select(b => new OcrTextPipeline.Box
                    {
                        X = b.Rect.X,
                        Y = b.Rect.Y,
                        Score = b.Score,
                        Text = b.Text,
                    });
                    var lines = OcrTextPipeline.ToLines(pipelineBoxes);
                    if (lines.Count > 0) _cachedText = string.Join("\n", lines);
                }
            }
            var text = _cachedText;

            // ⑥ 幕次判定：条带优先，其次全帧里的「第N幕/层」（正则与中文数字都由档案决定）
            int? act = _mode.ParseAct(band) ?? _mode.ParseAct(text);
            var stable = _stab.Push(act);
            LastAct = stable;

            // ⑦ 弃权：连续未识别超时就不给过时指令
            if (_stab.ShouldAbstain)
            {
                LastStatus = $"幕次未识别 {_stab.MissedMilliseconds}ms";
                LastLines = new List<string> { "· 暂不判断：请在剧诗界面停留一下（或切换幕次）" };
                LastSource = "·";
                _ball.SetState(FloatingBall.BallState.Waiting);
                Emit(new TickSnapshot
                {
                    GameHandle = win,
                    FrameWidth = bmp.Width,
                    FrameHeight = bmp.Height,
                    Act = stable,
                    Status = LastStatus,
                    Source = LastSource,
                    BandText = band ?? "",
                    Abstained = true,
                    MissedMs = _stab.MissedMilliseconds,
                    Lines = LastLines,
                    Hits = BuildHits(hits),
                });
                return;
            }

            // ⑧ 决策
            //  ⑧a 先把屏幕上的可选项读出来（三选一/商店卡）——「选第几张」是本项目的主功能，
            //      没有 Options 时编排器只能给通用建议。
            var optLines = (text ?? "").Split('\n').Select(s => s.Trim())
                .Where(s => s.Length > 0).ToList();
            if (!string.IsNullOrWhiteSpace(band)) optLines.Add(band.Trim());

            var (teamNow, teamMax) = ChoiceDecider.ParseTeamSize(text ?? "");
            if (teamNow > 0) _teamSize = teamNow;
            if (teamMax > 0) _teamTarget = teamMax;

            var options = ChoiceDecider.ParseOptions(optLines);
            var diff = QuantData.Difficulties.LastOrDefault() ?? "";
            var enemy = stable.HasValue ? QuantData.Enemy(stable.Value, diff) : null;

            // ⑧b ◇模型层在环：只在「选项未读到」时调用。
            //  ① 进程内 GGUF（LocalBrain，147ms）→ ② data/llm.json 的 HTTP 端点（约 1s）。
            //  都没有 = null，编排器自动走 ○语料/·规则。
            string llmText = null;
            var llmAttempted = false;
            if (options.Count < 2 && (_brain is { Ready: true } || _llm is { Enabled: true }))
            {
                try
                {
                    var ask = (stable.HasValue ? $"我在剧诗第{stable}幕，难度{diff}。" : "我在剧诗界面，幕次还没认出来。")
                              + (enemy != null && enemy.HasAnyData ? "这是首领关。" : "")
                              + "一句话概括该干嘛。";

                    if (_brain is { Ready: true })
                    {
                        llmText = _brain.Ask(ask, maxTokens: 72, timeoutMs: 1200);
                    }
                    else
                    {
                        var rosterLine = TeamSolver.AsTeamProfile() is { } tp && tp.Full ? "已导入角色池" : "未导入角色池";
                        llmText = await _llm.RefineAsync(
                            stable.HasValue ? $"第{stable}幕 {diff}" : "剧诗界面（幕次未定）",
                            optLines.Count > 0 ? optLines.Take(3).ToList() : (IReadOnlyList<string>)Array.Empty<string>(),
                            rosterLine);
                    }
                    llmAttempted = llmText != null;
                }
                catch (Exception ex)
                {
                    App.LogCrash(ex, "MainLoop.Llm");
                }
            }

            var d = DecisionOrchestrator.Decide(new DecisionOrchestrator.Context
            {
                Act = stable,
                Difficulty = diff,
                Enemy = enemy,
                Team = TeamSolver.AsTeamProfile(),
                Options = options,
                TeamSize = _teamSize,
                TeamTarget = _teamTarget,
                LlmText = llmText,
                LlmAttempted = llmAttempted,
                Kind = stable.HasValue ? "act" : (hits.Count > 0 ? "ui" : ""),
            });

            // ⑧c 战斗中的指导：当前幕的手法步骤（数据来自 rotations.json，只读文本、不模拟输入）。
            // 审计发现：WinForms 版有这条（OverlayForm 调 Routine.LinesFor），WPF 迁移时丢了——
            // 玩家在战斗里看到的是"该上谁"，缺"手该怎么按"。取第一条「手法·」行，放最后，
            // 不顶掉首行的「现在做」；选人参考行不掺和（那是配队层的事）。
            if (stable.HasValue && Routine.Loaded)
            {
                var technique = Routine.LinesFor(stable, 3).FirstOrDefault(l => l.StartsWith("手法·"));
                if (!string.IsNullOrWhiteSpace(technique) && !d.Lines.Contains(technique))
                    d.Lines.Add(technique);
            }

            LastSource = d.Source;
            LastLines = d.Lines;
            _lastStable = stable;
            _lastBandText = band ?? "";
            _lastHits = BuildHits(hits);
            _lastDecideTick = _tickCount;
            LastStatus = stable.HasValue ? $"第{stable}幕 · {d.Source}" : d.Source;

            // ⑨ 驱动悬浮球：状态色 + 角标
            _ball.SetState(d.Source switch
            {
                var s when s.StartsWith("★") => FloatingBall.BallState.Done,
                var s when s.StartsWith("◆") => FloatingBall.BallState.Idle,
                var s when s.StartsWith("◇") => FloatingBall.BallState.Thinking,
                _ => FloatingBall.BallState.Idle,
            });

            // ⑩ 遮罩快照（日志 + 识别框都由这里驱动，见 MaskWindowHost）
            Emit(new TickSnapshot
            {
                GameHandle = win,
                FrameWidth = bmp.Width,
                FrameHeight = bmp.Height,
                Act = stable,
                Status = LastStatus,
                Source = LastSource,
                BandText = band ?? "",
                Lines = LastLines,
                Hits = BuildHits(hits),
            });
        }
        catch (Exception ex)
        {
            LastStatus = "循环异常：" + ex.GetType().Name;
            App.LogCrash(ex, "MainLoop.Tick");
            Emit(new TickSnapshot { Status = LastStatus, Source = "⚠" });
        }
        finally
        {
            _busy = false;
        }
    }

    private void Emit(TickSnapshot snapshot)
    {
        try
        {
            // 玩法信息与条带 ROI 统一在这里补，调用点不用重复写
            snapshot.ModeId = _mode.Id;
            snapshot.ModeName = _mode.DisplayName;
            snapshot.BandRect ??= BandRectOf(snapshot.FrameWidth, snapshot.FrameHeight);
            Updated?.Invoke(snapshot);
        }
        catch (Exception ex) { App.LogCrash(ex, "MainLoop.Updated"); }
    }

    /// <summary>模板命中 → 遮罩框（坐标是帧像素；四周各留 2px 便于看清）。</summary>
    private List<MaskHit> BuildHits(List<Matcher.Hit> hits)
    {
        var list = new List<MaskHit>();
        if (hits == null) return list;

        foreach (var h in hits)
        {
            if (!_templateSizes.TryGetValue(h.Name, out var sz)) continue;
            list.Add(new MaskHit
            {
                Name = h.Name,
                Score = h.Score,
                X = h.X - 2,
                Y = h.Y - 2,
                W = sz.W + 4,
                H = sz.H + 4,
            });
        }
        return list;
    }

    /// <summary>一帧的可视事实。遮罩只读这些字段，不触碰识别/决策内部。</summary>
    internal sealed class TickSnapshot
    {
        public IntPtr GameHandle;
        public int FrameWidth;
        public int FrameHeight;
        public int? Act;
        public string ModeId = "theater";
        public string ModeName = "幻想真境剧诗";
        public string Status = "";
        public string Source = "";
        public string BandText = "";
        public bool Abstained;
        /// <summary>true = 画面未变，本帧跳过识别（复用上一帧结果）。</summary>
        public bool Skipped;
        public long MissedMs;
        public IReadOnlyList<string> Lines = Array.Empty<string>();
        public IReadOnlyList<MaskHit> Hits = Array.Empty<MaskHit>();

        /// <summary>底部条带 ROI（帧像素），遮罩上画一个区域框便于核对。由玩法档案决定。</summary>
        public System.Windows.Rect? BandRect;
    }

    /// <summary>
    /// 32×32 采样的帧指纹：静态界面（选卡/结算/暂停）下整帧跳过识别。
    /// 用 GetPixel 采样 1024 次 ≈ 1ms，比一次全帧 OCR（~1.1s）便宜三个数量级。
    /// </summary>
    private static ulong FrameHash(System.Drawing.Bitmap bmp)
    {
        ulong h = 1469598103934665603UL;
        int sx = Math.Max(1, bmp.Width / 32), sy = Math.Max(1, bmp.Height / 32);
        for (int y = 0; y < bmp.Height; y += sy)
        {
            for (int x = 0; x < bmp.Width; x += sx)
            {
                var c = bmp.GetPixel(x, y);
                h = (h ^ (uint)(c.R | (c.G << 8) | (c.B << 16))) * 1099511628211UL;
            }
        }
        return h;
    }

    /// <summary>条带 ROI（帧像素）；玩法无条带时返回 null。</summary>
    private System.Windows.Rect? BandRectOf(int frameWidth, int frameHeight)
        => frameWidth > 0 && frameHeight > 0 && _mode.HasBand
            ? new System.Windows.Rect(
                frameWidth * _mode.BandLeft,
                frameHeight * _mode.BandTop,
                frameWidth * _mode.BandWidth,
                frameHeight * _mode.BandHeight)
            : (System.Windows.Rect?)null;

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
        _templateSizes.Clear();

        foreach (var f in Directory.GetFiles(dir, "*.png"))
        {
            var name = Path.GetFileNameWithoutExtension(f);
            if (name.StartsWith('_')) continue;
            try
            {
                var t = new TemplateEntry(name, new Bitmap(f), TemplateEntry.ThresholdFor(name));
                _templates.Add(t);
                _templateSizes[name] = (t.Bitmap.Width, t.Bitmap.Height);
            }
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

/// <summary>模板命中框（帧像素坐标），遮罩据此画识别框。</summary>
internal sealed class MaskHit
{
    public string Name = "";
    public double Score;
    public int X, Y, W, H;
}
