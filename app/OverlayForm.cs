using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TheaterAssistant;

/// <summary>
/// 单文件 exe 若开启 IncludeAllContentForSelfExtract，AppPaths.Dir 可能指向解包临时目录；
/// 用户自己放在 exe 旁边的 templates/data 就找不到。统一以 exe 所在目录为准。
/// </summary>
internal static class AppPaths
{
    /// <summary>exe 所在目录（用户可见的落盘位置：报告、素材、roster.json）。</summary>
    public static string Dir
    {
        get
        {
            var p = Environment.ProcessPath;
            var d = string.IsNullOrEmpty(p) ? null : Path.GetDirectoryName(p);
            return string.IsNullOrEmpty(d) ? AppContext.BaseDirectory : d;
        }
    }

    /// <summary>单文件 exe 的解包目录：data/templates/models 被 IncludeAllContentForSelfExtract 解到这里。</summary>
    public static string Base => AppContext.BaseDirectory;

    /// <summary>
    /// 依次在「exe 同级 → 解包目录」里找相对路径，返回第一个存在的。
    /// 这是必须的：发布包把 data/、templates/ 打进单文件 exe，运行期只在解包目录里存在；
    /// 而用户自己往 exe 旁边放的同名目录要优先（覆盖内置）。都没有就返回 exe 同级，便于报错定位。
    /// </summary>
    public static string Resolve(string relative)
    {
        foreach (var root in new[] { Dir, Base })
        {
            var p = Path.Combine(root, relative);
            if (File.Exists(p) || Directory.Exists(p)) return p;
        }
        return Path.Combine(Dir, relative);
    }

    public static string Data(string name) => Resolve(Path.Combine("data", name));
    public static string TemplatesRoot => Resolve("templates");
    public static string ModelsRoot => Resolve("models");
}

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 必须在任何窗口创建之前声明 DPI 感知：否则 ClientToScreen 返回逻辑坐标、
        // CopyFromScreen 按物理像素取，缩放屏（125%/150%）上抓屏会整体错位。
        try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch { }

        try
        {
            return RunCore(args);
        }
        catch (Exception ex)
        {
            // 无控制台的 WinExe 崩了只会给一个 0xE0434352，这里落盘堆栈，CI 与用户都能查
            try
            {
                var path = Path.Combine(AppPaths.Dir, "crash-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                File.WriteAllText(path, ex.ToString(), System.Text.Encoding.UTF8);
                Cli.Attach();
                Cli.Say("崩溃，堆栈已写入: " + path);
                Cli.Say(ex.ToString());
            }
            catch { }
            return 3;
        }
    }

    private static int RunCore(string[] args)
    {
        if (args.Length >= 1)
        {
            switch (args[0])
            {
                // 离线全链路自检：一张图 → 模板 + OCR + 判定 + 攻略
                case "--frame":
                    if (args.Length >= 2) { OfflineRunner.Run(args[1]); return 0; }
                    break;

                // 批量适配：帧目录 → 模板（OCR 锚点派生 + 在场/缺席双向量检）
                case "--batch":
                    if (args.Length >= 2)
                    {
                        return BatchRunner.Run(args[1], Opt(args, "--out"),
                            OptDouble(args, "--min-present", 0.92),
                            OptDouble(args, "--max-absent", 0.82),
                            OptDouble(args, "--pad", 0.15));
                    }
                    break;

                // 生成合成帧（自检/CI 用，不需要用户的游戏截图）
                case "--synth":
                    if (args.Length >= 2) return BatchRunner.WriteSynth(args[1]);
                    break;

                // 纯函数自检；--with-ocr 追加 OCR 坐标端到端断言
                case "--selftest":
                    return SelfTest.Run(args.Contains("--with-ocr", StringComparer.OrdinalIgnoreCase));

                // 悬浮窗渲染自检（把窗画到 bitmap 上断言，不需要桌面会话）
                case "--selftest-visual":
                    return SelfTest.RunVisual();

                // 免登录角色池：角色界面截图 → OCR + 角色名词典 → roster.json
                case "--roster-scan":
                    if (args.Length >= 2) return RosterScan.Run(args[1], Opt(args, "--out"));
                    break;

                // 角色可替代性 / 模拟运行对比视频原操作
                case "--substitutes":
                    {
                        var nm = args.Length >= 2 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null;
                        int? sa = null; if (int.TryParse(Opt(args, "--act"), out var sn2)) sa = sn2;
                        return OpCompare.RunSubstitutes(nm, sa);
                    }
                case "--compare-ops":
                    if (args.Length >= 2) return OpCompare.RunCompare(args[1], Opt(args, "--out"));
                    break;

                // 跨视频操作共识：--consensus frames\ --out OPS-CONSENSUS.md
                case "--consensus":
                    {
                        var ins = args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
                        return OpsConsensus.Run(ins, Opt(args, "--out"));
                    }

                // 从视频帧提取"操作用法"候选（连招/购买/配队/行动），需人工复核
                case "--extract-ops":
                    if (args.Length >= 2) return OpsExtract.Run(args[1], Opt(args, "--out"), Opt(args, "--bvid"), Opt(args, "--act"));
                    break;

                // 手法：--routine --act 6 / --routine
                case "--routine":
                    {
                        int? ra = null;
                        if (int.TryParse(Opt(args, "--act"), out var rn)) ra = rn;
                        return Routine.RunCli(ra);
                    }

                // 多幕规划：--plan-theater --difficulty Lunar --roster roster.json --out plan.md
                case "--plan-theater":
                    return ActPlanner.RunCli(
                        Opt(args, "--difficulty") ?? "Visionary",
                        Opt(args, "--roster"),
                        Opt(args, "--out"));

                // 本地日志读角色数据（只读日志、只取角色字段）
                case "--log-scan":
                    return LogScan.Run(args.Length >= 2 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null,
                        Opt(args, "--out"));

                // 把多份 replay 报告蒸馏成经验（数字 + 规则）
                case "--distill":
                    {
                        var ins = args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
                        return Distiller.Run(ins, Opt(args, "--out"));
                    }

                // 决策：--decide "战斗·普通\n雷晶·进阶\n感电·原态" --act 3 --team 3 --target 4
                case "--decide":
                    {
                        var txt = args.Length >= 2 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null;
                        return ChoiceDecider.RunCli(txt, (int)OptDouble(args, "--act", 0),
                            (int)OptDouble(args, "--team", 0), (int)OptDouble(args, "--target", 4));
                    }

                // 视频模拟运行：按时间顺序逐帧跑完整链路，输出时序报告
                case "--replay":
                    if (args.Length >= 2)
                        return ReplayRunner.Run(args[1], Opt(args, "--out"), Opt(args, "--json"),
                            (int)OptDouble(args, "--min-stable", 2));
                    break;

                // 帧驱动量化对照：软件指示 vs 该帧客观事实（人物/决策/战斗）
                case "--quant":
                    if (args.Length >= 2)
                        return QuantRunner.Run(args[1], Opt(args, "--out"), Opt(args, "--json"),
                            (int)OptDouble(args, "--max", 0));
                    break;

                // 跨幕全局分配：角色只能用一次 + 治疗/破盾硬约束
                case "--global-alloc":
                    return GlobalAllocator.RunCli((int)OptDouble(args, "--season", 0), Opt(args, "--out"), Opt(args, "--roster"));

                // OCR 后处理参数（BetterGI 对齐：置信度过滤 + 同排排序）
                case "--ocr":
                    return OcrTextPipeline.RunCli();

                // OCR 引擎调优（线程 / DirectML EP 实际状态）
                case "--ocr-tuning":
                    return OcrTuning.RunCli();

                // 真实初始化 OCR 引擎并报告执行提供器（验证 DirectML 探测是否按预期回退/启用）
                case "--ocr-ep":
                    {
                        OcrTuning.Load(AppPaths.Data("decision-policy.json"));
                        using var probe = new OcrService();
                        probe.InitializeAsync(AppPaths.ModelsRoot).GetAwaiter().GetResult();
                        Cli.Say("引擎状态: " + probe.Status);
                        Cli.Say("执行提供器: " + OcrTuning.EpStatus);
                        Cli.Say($"DirectML 配置: enable={OcrTuning.EnableDirectML} device={OcrTuning.DirectMLDeviceId} threads={(OcrTuning.NumThreads == 0 ? "自动" : OcrTuning.NumThreads.ToString())}");
                        // 约定：初始化成功即算通过（EP 是 CPU 还是 DirectML 都合法，只要不是崩）
                        return probe.Status.StartsWith("OCR:") ? 0 : 3;
                    }

                // 决策策略清单（确认决策内容来自数据而非代码）
                case "--policy":
                    return DecisionPolicy.RunCli();

                // 赛季注册表（自动发现 data/season*.json，未来赛季无需改代码）
                case "--seasons":
                    return SeasonRegistry.RunCli();

                // 决策编排：可计算本地算，随机/未知交 API，语料/规则兜底
                case "--orchestrate":
                    return DecisionOrchestrator.RunCli((int)OptDouble(args, "--act", 0), Opt(args, "--difficulty"));

                // 通用最优配队求解（假设全角色 → 排列组合 → 每难度最优）
                case "--team-solve":
                    return TeamSolver.RunCli(Opt(args, "--difficulty"), (int)OptDouble(args, "--act", 0), Opt(args, "--out"),
                        (int)OptDouble(args, "--season", 0), Opt(args, "--roster"));

                // 量化决策：敌人需求 × 我方覆盖 → 可解释分数 + 置信度
                case "--quant-decide":
                    return QuantScorer.RunCli((int)OptDouble(args, "--act", 0), Opt(args, "--difficulty"), Opt(args, "--out"));

                // 敌人侧量化档案（抗性/波次/机制窗口/需求向量）
                case "--enemy-profile":
                    return QuantData.RunEnemyCli((int)OptDouble(args, "--act", 0), Opt(args, "--difficulty"));

                // 我方侧量化档案（池子覆盖；没有 roster.json 时明确标 Full=false）
                case "--team-profile":
                    return QuantData.RunTeamCli();

                // 幕内知识清单（确认内容来自数据而非代码）
                case "--playbook":
                    return Playbook.RunCli(Opt(args, "--out"));

                // 大模型端点探针：--llm-probe [--dry]（dry=只校验配置不联网）
                case "--llm-probe":
                    return LlmAdvisor.RunProbe(AppPaths.Data("llm.json"), args.Any(a => a == "--dry"), args.Any(a => a == "--verify"));

                // 资源包 / 更新（MAA 式：程序与资源都能更新）
                case "--check-update":
                    return Updater.CheckUpdate();
                case "--packs":
                    return Updater.Run(null, true);
                case "--fetch-model":
                    return Updater.Run(args.Length >= 2 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null, false);

                // 语音：--say "文本" 直接念；--voices 列系统语音；--selftest-speak 自检
                case "--say":
                    return Speech.RunCli(args.Length >= 2 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null, false);
                case "--voices":
                    return Speech.RunCli(null, true);
                case "--selftest-speak":
                    return Speech.RunCli("剧诗助手语音自检通过。", true);

                // 从视频帧抽攻略文字，补充语料（--corpus-scan frames --out ... --bvid BVxxx）
                case "--corpus-scan":
                    if (args.Length >= 2) return CorpusScan.Run(args[1], Opt(args, "--out"), Opt(args, "--bvid"));
                    break;

                // 离线攻略检索：BM25 检索 data/guide-corpus.json，结果带出处
                case "--advise":
                    {
                        var q = args.Length >= 2 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null;
                        int? actArg = null;
                        var asArg = Opt(args, "--act");
                        if (int.TryParse(asArg, out var an)) actArg = an;
                        return GuideIndex.RunCli(q, actArg, (int)OptDouble(args, "--top", 3));
                    }

                // 免登陆角色池（头像路线）：只有头像+Lv 的网格也能认人
                case "--roster-avatars":
                    if (args.Length >= 2)
                    {
                        return AvatarRoster.Run(args[1], Opt(args, "--avatars"), Opt(args, "--out"),
                            args.Contains("--probe", StringComparer.OrdinalIgnoreCase),
                            OptDouble(args, "--min-score", 0.55));
                    }
                    break;

                // 真机模拟：把一张帧当真游戏画面显示出来（手动观察用）
                case "--fakegame":
                    if (args.Length >= 2)
                    {
                        var (fw, fh) = ParseSize(Opt(args, "--size"), 1920, 1080);
                        return LiveProbe.RunFakeGame(args[1], fw, fh, Opt(args, "--title"));
                    }
                    break;

                // 真机链路探测：找窗口 → GDI 抓帧 → 模板 + OCR + 判定
                case "--probe-live":
                    return LiveProbe.RunProbe(Opt(args, "--title"), (int)OptDouble(args, "--frames", 1), Opt(args, "--out"));

                // 自动化的"真机模拟实测"：起假窗口 → 抓帧 → 比对源帧 → 识别 → 断言（4=无桌面会话跳过）
                case "--selftest-live":
                    {
                        var (lw, lh) = ParseSize(Opt(args, "--size"), 1920, 1080);
                        return LiveProbe.RunSelfTest(Opt(args, "--png"), lw, lh, Opt(args, "--title"));
                    }

                case "--version":
                    Cli.Attach();
                    Cli.Say("TheaterAssistant 0.34.0 · 只读像素，不注入/不读内存/不模拟键鼠");
                    return 0;
            }
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new OverlayForm());
        return 0;
    }

    private static string Opt(string[] args, string name)
    {
        int i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static double OptDouble(string[] args, string name, double fallback)
    {
        var s = Opt(args, name);
        return double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    /// <summary>解析 "1920x1080" 形式的尺寸参数。</summary>
    private static (int W, int H) ParseSize(string s, int defW, int defH)
    {
        if (!string.IsNullOrWhiteSpace(s))
        {
            var m = System.Text.RegularExpressions.Regex.Match(s, @"^\s*(\d{3,5})\s*[xX*]\s*(\d{3,5})\s*$");
            if (m.Success) return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
        }
        return (defW, defH);
    }
}

/// <summary>
/// 剧诗悬浮窗：置顶、不抢焦点、默认点击穿透、半透明圆角。
/// 样式参照 genshin_artifact_auxiliary（深色半透明无边框 + 顶部状态行）与 BetterGI 的覆盖层观感。
/// 只读屏幕像素，不注入、不读进程内存、不模拟键鼠。
/// </summary>
internal sealed class OverlayForm : Form
{
    private const int WM_HOTKEY = 0x0312;
    private const int MOD_ALT = 0x0001;
    private const int MOD_CONTROL = 0x0002;
    private const int HOTKEY_TOGGLE = 1;   // Ctrl+Alt+H 显隐
    private const int HOTKEY_CLICK = 2;    // Ctrl+Alt+L 穿透 ⇄ 可拖动
    private const int HOTKEY_SAVE = 3;     // Ctrl+Alt+D 存帧
    private const int HOTKEY_GUIDE = 4;    // Ctrl+Alt+G 攻略窗

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly Font _titleFont = new("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
    private readonly Font _bodyFont = new("Microsoft YaHei UI", 9.5f, FontStyle.Regular);

    private readonly OcrService _ocr = new();
    private readonly List<TemplateEntry> _templates = new();
    private RosterData _roster;
    private GuideWindow _guide;
    private LlmAdvisor _llm;
    private GuideNotes _notes;
    private int? _llmForAct;
    private string _bucketKey = "";
    private readonly HashSet<string> _autoSaved = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _autoSavedCount = new(StringComparer.Ordinal);
    private string _autoSaveHint = "";
    private string _lastAnnounced = "";
    private int _tickCount;
    private int _textAge;
    private string _cachedText = "";
    private readonly ActStabilizer _actStab = new();
    private int _teamSize;   // 已编入人数（从界面文字读，读不到就用上次值）
    private int _teamTarget = 4;
    private int? _currentAct;
    private TheaterData _theater;

    private bool _clickThrough = true;
    private bool _busy;
    private string _header = "剧诗助手 · 等待游戏窗口";
    private readonly List<string> _lines = new() { "启动中…" };
    private string _lastCaptureHint = "";

    private Point _dragStart;
    private bool _dragging;

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(18, 21, 30);
        Size = new Size(600, 236);
        Location = new Point(40, 80);
        DoubleBuffered = true;
        Text = "剧诗助手";

        _theater = TheaterData.Load(AppPaths.Data("season29.json"));
        _roster = RosterData.TryLoad(Path.Combine(AppPaths.Dir, "roster.json"));

        _llm = LlmAdvisor.TryLoad(AppPaths.Data("llm.json"));
        _notes = GuideNotes.Load(AppPaths.Data("guide-notes.json"));
        GuideIndex.LoadWithExtra(AppPaths.Data("guide-corpus.json"));
        Speech.LoadAndConfigure(AppPaths.Data("speech.json"));
        Routine.Load(AppPaths.Data("rotations.json"));
        Playbook.Load(AppPaths.Data("act-playbook.json"));
        LoadTemplatesFor(0, 0);

        _timer.Tick += OnTick;
        Shown += OnShown;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | (int)0x00000080; // 80 = WS_EX_TOOLWINDOW，不进 Alt+Tab
            if (_clickThrough) cp.ExStyle |= WS_EX_TRANSPARENT;
            return cp;
        }
    }

    private void OnShown(object sender, EventArgs e)
    {
        SetWindowDisplayAffinity(Handle, 0x11); // WDA_EXCLUDEFROMCAPTURE：自己不被截进画面
        ApplyRoundedRegion();
        RegisterHotKey(Handle, HOTKEY_TOGGLE, MOD_CONTROL | MOD_ALT, (int)Keys.H);
        RegisterHotKey(Handle, HOTKEY_CLICK, MOD_CONTROL | MOD_ALT, (int)Keys.L);
        RegisterHotKey(Handle, HOTKEY_SAVE, MOD_CONTROL | MOD_ALT, (int)Keys.D);
        RegisterHotKey(Handle, HOTKEY_GUIDE, MOD_CONTROL | MOD_ALT, (int)Keys.G);

        _ = _ocr.InitializeAsync(AppPaths.ModelsRoot);
        _timer.Start();
    }

    private void ApplyRoundedRegion()
    {
        using var path = RoundedRect(new Rectangle(0, 0, Width, Height), 10);
        Region = new Region(path);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d - 1, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d - 1, r.Bottom - d - 1, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d - 1, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            int id = m.WParam.ToInt32();
            if (id == HOTKEY_TOGGLE) Visible = !Visible;
            else if (id == HOTKEY_CLICK) SetClickThrough(!_clickThrough);
            else if (id == HOTKEY_SAVE) SaveFrame();
            else if (id == HOTKEY_GUIDE) ToggleGuide();
            return;
        }

        if (m.Msg == 0x0084 && _clickThrough) // WM_NCHITTEST
        {
            m.Result = (IntPtr)(-1); // HTTRANSPARENT
            return;
        }
        base.WndProc(ref m);
    }

    private void SetClickThrough(bool on)
    {
        _clickThrough = on;
        int ex = GetWindowLong(Handle, GWL_EXSTYLE);
        ex = on ? (ex | WS_EX_TRANSPARENT) : (ex & ~WS_EX_TRANSPARENT);
        SetWindowLong(Handle, GWL_EXSTYLE, ex);
    }

    private async void OnTick(object sender, EventArgs e)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var win = GameWindow.Find("原神");
            if (win == IntPtr.Zero)
            {
                _header = "剧诗助手 · 没找到游戏窗口";
                _lines.Clear();
                _lines.Add("请把原神切到窗口化 / 窗口全屏，并让它在前台可见");
                Invalidate();
                return;
            }

            GameWindow.GetClientRect(win, out var cr);
            LoadTemplatesFor(cr.Right - cr.Left, cr.Bottom - cr.Top);

            using var bmp = ScreenCapture.CaptureClient(win);
            if (bmp == null)
            {
                _lines.Clear();
                _lines.Add("截图失败（窗口最小化？）");
                Invalidate();
                return;
            }

            // 性能策略（1080p 实测）：整帧 OCR 是最贵的一步（~2s/次），没必要每 tick 都做。
            //   · 每 tick：模板匹配（半分辨率粗筛）+ 底部条带 OCR（面积小，~0.2s）
            //   · 整帧 OCR：每 3 tick 一次，或条带没给出幕次时立刻补一次
            var hits = Matcher.Scan(bmp, _templates);
            bool actFromTemplate = hits.Any(h => System.Text.RegularExpressions.Regex.IsMatch(h.Name, @"^act\d{1,2}$"));
            string bandText = actFromTemplate ? null
                : await _ocr.RecognizeRegionAsync(bmp, new RectangleF(0.28f, 0.828f, 0.46f, 0.05f), 4);

            _tickCount++;
            bool needFullText = _tickCount % 3 == 0 || string.IsNullOrWhiteSpace(bandText) || _cachedText.Length == 0;
            if (needFullText)
            {
                _cachedText = await _ocr.RecognizeAsync(bmp) ?? _cachedText;
                _textAge = 0;
            }
            else _textAge++;
            string text = _cachedText;

            var advice = TheaterLogic.Evaluate(hits, text, _theater, _roster, bandText);

            // 时序稳定：单帧的倒退/跳级先不采信，连续确认后才改（避免小窗乱报）
            var stableAct = _actStab.Push(advice.Act);
            if (stableAct != advice.Act)
                advice = advice with { Act = stableAct, Kind = stableAct.HasValue ? "act" : advice.Kind };

            // 弃权：连续多帧都没认出幕次 → 明确说"暂不判断"，不再沿用旧幕次给过时指令。
            // 实测约 25~30% 帧属于沿用（AUDIT §36），长期沿用比弃权更容易误导。
            // 沿用提示：本帧没识别到幕次、仍在用上一帧的结论 —— 必须让玩家知道这是"旧信息"
            if (!_actStab.ShouldAbstain && _actStab.ConsecutiveMisses > 0 && _actStab.Committed.HasValue)
            {
                advice = advice with
                {
                    Lines = advice.Lines.Concat(new[]
                    {
                        $"⚠ 幕次沿用上一帧（本帧未识别 {_actStab.ConsecutiveMisses} 次），落地前请核对屏幕"
                    }).ToList()
                };
            }

            if (_actStab.ShouldAbstain)
            {
                advice = advice with
                {
                    Header = $"暂不判断（连续 {_actStab.ConsecutiveMisses} 帧未认出幕次）",
                    Lines = new List<string>
                    {
                        $"▶ 暂不判断：已连续 {_actStab.ConsecutiveMisses} 帧没认出幕次，别按旧指示硬打",
                        "看一眼屏幕底部当前幕次，或避开结算/贴图遮挡再让本窗读一次",
                    },
                    Kind = "abstain",
                    Act = null,
                };
            }

            // 自动攒素材：按「状态 + 幕次 + 分辨率 + 画面签名」去重存原始帧。
            // 用签名而不是只按 key 去重：同一幕的不同界面（选人/战斗/BUFF 结算）都要能攒下来；
            // 每个 key 最多 6 张，避免长局把磁盘写爆。存盘失败不再静默，直接写进诊断行。
            var stateKind = advice.Kind ?? "unknown";
            if (stateKind != "unknown")
            {
                var key = $"{stateKind}-{advice.Act?.ToString() ?? "-"}-{bmp.Width}x{bmp.Height}";
                var sig = QuickSignature(bmp);
                if (_autoSaved.Add(key + "#" + sig))
                {
                    try
                    {
                        var n = _autoSavedCount.TryGetValue(key, out var c) ? c + 1 : 1;
                        if (n > 6)
                        {
                            _autoSaved.Remove(key + "#" + sig);
                        }
                        else
                        {
                            _autoSavedCount[key] = n;
                            var dir = Path.Combine(AppPaths.Dir, "samples", "auto");
                            Directory.CreateDirectory(dir);
                            var baseName = advice.Act.HasValue ? $"act{advice.Act.Value}" : stateKind;
                            var fileName = n == 1
                                ? $"{baseName}-{bmp.Width}x{bmp.Height}.png"
                                : $"{baseName}-{bmp.Width}x{bmp.Height}-{n}.png";
                            bmp.Save(Path.Combine(dir, fileName), System.Drawing.Imaging.ImageFormat.Png);
                            _autoSaveHint = $"素材 +1 · {fileName}";
                        }
                    }
                    catch (Exception ex) { _autoSaveHint = "素材存盘失败: " + ex.Message; }
                }
            }

            _header = advice.Header;
            _lines.Clear();
            _lines.AddRange(advice.Lines);

            // 语音播报：只在「状态/幕次」真的变了时读一句"现在做"，避免刷屏
            var announceKey = (advice.Kind ?? "unknown") + "-" + (advice.Act?.ToString() ?? "-");
            if (advice.Kind != "unknown" && announceKey != _lastAnnounced)
            {
                _lastAnnounced = announceKey;
                Speech.Announce(advice.Kind, advice.Act, advice.Lines.FirstOrDefault() ?? "");
            }

            // 决策优先：屏幕上出现可选项（买卡/三选一）时，直接给出"选第几张"，不用玩家自己判断
            var optLines = (text ?? "").Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            if (bandText != null) optLines.Add(bandText.Trim());
            var (teamNow, teamMax) = ChoiceDecider.ParseTeamSize(text ?? "");
            if (teamNow > 0) _teamSize = teamNow;
            if (teamMax > 0) _teamTarget = teamMax;

            var options = ChoiceDecider.ParseOptions(optLines);
            if (options.Count >= 2)
            {
                var dec = ChoiceDecider.Decide(options, advice.Act ?? 0, _teamSize, _teamTarget);
                if (dec != null) _lines.Insert(0, "▶ " + dec.Sentence);
            }

            // 手法提示：给出"手该怎么按"，最多 2 行（首领幕优先专属，其余给通用）
            if (advice.Act.HasValue)
                foreach (var l in Routine.LinesFor(advice.Act, 2)) _lines.Add(l);

            // 离线攻略检索：用"第N幕 + 当前判定"当查询，命中的中文攻略条目连出处一起显示
            if (GuideIndex.Loaded && advice.Kind != "unknown")
            {
                var q = (advice.Act.HasValue ? $"第{advice.Act}幕 " : "") + (advice.Lines.FirstOrDefault() ?? "");
                foreach (var l in GuideIndex.ToLines(GuideIndex.Search(q, advice.Act, 2), 2)) _lines.Add(l);
            }
            if (advice.Act.HasValue)
            {
                var note = _notes.ActNote(advice.Act.Value);
                if (!string.IsNullOrWhiteSpace(note)) _lines.Add("节奏: " + note);
            }
            else if (!string.IsNullOrWhiteSpace(_ocr.Status))
            {
                _lines.Add("OCR: " + _ocr.Status);
            }
            _lastCaptureHint = advice.Diagnostics;
            if (!string.IsNullOrEmpty(_autoSaveHint)) _lines.Add(_autoSaveHint);
            _currentAct = advice.Act;

            if (_llm is { Enabled: true } && advice.Act.HasValue && _llmForAct != advice.Act)
            {
                _llmForAct = advice.Act;
                var snapshot = advice.Lines.Take(3).ToList();
                var header = advice.Header;
                var rosterLine = _roster.Loaded ? "已导入角色池" : "未导入角色池";
                _ = _llm.RefineAsync(header, snapshot, rosterLine).ContinueWith(t =>
                {
                    var extra = t.Result;
                    if (string.IsNullOrWhiteSpace(extra) || IsDisposed) return;
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            _lines.Insert(0, "AI: " + extra.Replace("\n", " ").Trim());
                            Invalidate();
                        }));
                    }
                    catch { }
                });
            }
            Invalidate();
        }
        catch (Exception ex)
        {
            _lines.Clear();
            _lines.Add("错误: " + ex.Message);
            Invalidate();
        }
        finally
        {
            _busy = false;
        }
    }

    private void ToggleGuide()
    {
        _guide ??= new GuideWindow();
        if (_guide.Visible) { _guide.Hide(); return; }
        _guide.ShowFor(_currentAct);
    }

    /// <summary>
    /// 按游戏客户区分辨率挑模板目录：templates\<宽>x<高>\ 优先，
    /// 允许 ±12% 宽度差（窗口微调/DPI 缩放），否则退回 templates\ 根目录。
    /// </summary>
    private void LoadTemplatesFor(int width, int height)
    {
        var root = AppPaths.TemplatesRoot;
        if (!Directory.Exists(root)) return;

        var dir = root;
        if (width > 0)
        {
            var best = Directory.GetDirectories(root)
                .Select(d => new { Dir = d, Ok = TryParseSize(Path.GetFileName(d), out int dw, out int dh), W = dw, H = dh })
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
            try { _templates.Add(new TemplateEntry(name, new Bitmap(f), TemplateEntry.ThresholdFor(name))); } catch { }
        }
    }

    private static bool TryParseSize(string name, out int w, out int h)
    {
        w = h = 0;
        var m = System.Text.RegularExpressions.Regex.Match(name, @"^(\d{3,5})x(\d{3,5})$");
        if (!m.Success) return false;
        w = int.Parse(m.Groups[1].Value);
        h = int.Parse(m.Groups[2].Value);
        return true;
    }

    private void SaveFrame()
    {
        try
        {
            var win = GameWindow.Find("原神");
            if (win == IntPtr.Zero) return;
            GameWindow.GetClientRect(win, out var cr);
            LoadTemplatesFor(cr.Right - cr.Left, cr.Bottom - cr.Top);

            using var bmp = ScreenCapture.CaptureClient(win);
            if (bmp == null) return;
            // 手动存帧与自动攒素材放同一目录：一条 --batch samples\auto 就能吃全
            var dir = Path.Combine(AppPaths.Dir, "samples", "auto");
            Directory.CreateDirectory(dir);
            var p = Path.Combine(dir, $"manual-{DateTime.Now:HHmmss}.png");
            bmp.Save(p, System.Drawing.Imaging.ImageFormat.Png);
            _lines.Add("已存帧: " + Path.GetFileName(p));
            Invalidate();
        }
        catch (Exception ex) { _lines.Add("存帧失败: " + ex.Message); Invalidate(); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (!_clickThrough && e.Button == MouseButtons.Left)
        {
            _dragging = true;
            _dragStart = e.Location;
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging)
        {
            Left += e.X - _dragStart.X;
            Top += e.Y - _dragStart.Y;
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _dragging = false;
        base.OnMouseUp(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        DrawContent(e.Graphics);
    }

    /// <summary>
    /// 只给自检用：注入一组状态并渲染成 bitmap（不开定时器、不注册热键、不显示窗口）。
    /// 这样"绘制到底对不对"能在没有桌面会话的 CI 里被断言，也能存成图让人眼看。
    /// </summary>
    internal Bitmap RenderPreview(string header, IEnumerable<string> lines, bool clickThrough, Size size)
    {
        _header = header ?? "";
        _lines.Clear();
        if (lines != null) _lines.AddRange(lines);
        _clickThrough = clickThrough;
        ClientSize = size;

        var bmp = new Bitmap(size.Width, size.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            DrawContent(g);
        }
        return bmp;
    }

    /// <summary>4x4 灰度缩略签名：用来区分"同一状态的不同画面"（只花 16 次 GetPixel）。</summary>
    private static string QuickSignature(Bitmap bmp)
    {
        var sb = new System.Text.StringBuilder(16);
        for (int gy = 0; gy < 4; gy++)
        {
            for (int gx = 0; gx < 4; gx++)
            {
                int x = Math.Min(bmp.Width - 1, bmp.Width * (gx * 2 + 1) / 8);
                int y = Math.Min(bmp.Height - 1, bmp.Height * (gy * 2 + 1) / 8);
                var p = bmp.GetPixel(x, y);
                sb.Append(((p.R + p.G + p.B) / 3 / 16).ToString("x"));
            }
        }
        return sb.ToString();
    }

    private void DrawContent(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using (var bg = new LinearGradientBrush(ClientRectangle,
                   Color.FromArgb(26, 30, 42), Color.FromArgb(15, 17, 24), 90f))
        {
            g.FillRectangle(bg, ClientRectangle);
        }

        // 左侧强调条
        using (var accent = new SolidBrush(Color.FromArgb(198, 168, 96)))
        {
            g.FillRectangle(accent, 0, 0, 4, Height);
        }

        // 顶部状态行
        using (var headBrush = new SolidBrush(Color.FromArgb(236, 240, 252)))
        {
            g.DrawString(_header, _titleFont, headBrush, new PointF(16, 10));
        }
        using (var pen = new Pen(Color.FromArgb(52, 58, 76)))
        {
            g.DrawLine(pen, 14, 34, Width - 14, 34);
        }

        // 正文
        using var bodyBrush2 = new SolidBrush(Color.FromArgb(198, 206, 224));
        float y = 40;
        foreach (var line in _lines)
        {
            var size = g.MeasureString(line, _bodyFont, Width - 32);
            g.DrawString(line, _bodyFont, bodyBrush2, new RectangleF(16, y, Width - 32, size.Height + 4));
            y += size.Height + 2;
            if (y > Height - 26) break;
        }

        // 底部：热键提示 + 诊断
        using var footBrush = new SolidBrush(Color.FromArgb(140, 148, 168));
        using var footFont = new Font("Microsoft YaHei UI", 8f);
        var foot = _clickThrough
            ? "Ctrl+Alt+L 拖动 · Ctrl+Alt+H 显隐 · Ctrl+Alt+D 存帧 · Ctrl+Alt+G 攻略"
            : "拖动中（穿透已关）· Ctrl+Alt+L 恢复穿透";
        g.DrawString(foot, footFont, footBrush, new PointF(16, Height - 20));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
            UnregisterHotKey(Handle, HOTKEY_TOGGLE);
            UnregisterHotKey(Handle, HOTKEY_CLICK);
            UnregisterHotKey(Handle, HOTKEY_SAVE);
            UnregisterHotKey(Handle, HOTKEY_GUIDE);
            _guide?.Shutdown();
            _ocr.Dispose();
            _llm?.Dispose();
            foreach (var t in _templates) t.Bitmap.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed record TemplateEntry(string Name, Bitmap Bitmap, double Threshold)
{
    /// <summary>
    /// 模板级阈值：`actN` 这类"外形几乎一样、只有数字不同"的模板要更严（实测跨幕相似度 0.80–0.81，
    /// 若继续用 0.86 会擦边误命中）；其余界面锚点用 0.86。
    /// </summary>
    public static double ThresholdFor(string name) =>
        name != null && name.StartsWith("act", StringComparison.Ordinal) && name.Length <= 5 ? 0.90 : 0.86;
}
