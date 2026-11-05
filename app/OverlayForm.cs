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
    public static string Dir
    {
        get
        {
            var p = Environment.ProcessPath;
            var d = string.IsNullOrEmpty(p) ? null : Path.GetDirectoryName(p);
            return string.IsNullOrEmpty(d) ? AppContext.BaseDirectory : d;
        }
    }
}

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
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
                            OptDouble(args, "--max-absent", 0.80));
                    }
                    break;

                // 生成合成帧（自检/CI 用，不需要用户的游戏截图）
                case "--synth":
                    if (args.Length >= 2) return BatchRunner.WriteSynth(args[1]);
                    break;

                // 纯函数自检；--with-ocr 追加 OCR 坐标端到端断言
                case "--selftest":
                    return SelfTest.Run(args.Contains("--with-ocr", StringComparer.OrdinalIgnoreCase));

                case "--version":
                    Cli.Attach();
                    Cli.Say("TheaterAssistant 0.4.0 · 只读像素，不注入/不读内存/不模拟键鼠");
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
    private readonly HashSet<int> _autoSaved = new();
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

        _theater = TheaterData.Load(Path.Combine(AppPaths.Dir, "data", "season29.json"));
        _roster = RosterData.TryLoad(Path.Combine(AppPaths.Dir, "roster.json"));

        _llm = LlmAdvisor.TryLoad(Path.Combine(AppPaths.Dir, "data", "llm.json"));
        _notes = GuideNotes.Load(Path.Combine(AppPaths.Dir, "data", "guide-notes.json"));
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

        _ = _ocr.InitializeAsync(Path.Combine(AppPaths.Dir, "models"));
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

            var hits = Matcher.Scan(bmp, _templates);
            string text = await _ocr.RecognizeAsync(bmp) ?? "";
            var advice = TheaterLogic.Evaluate(hits, text, _theater, _roster);
            if (!advice.Act.HasValue)
            {
                var bandText = await _ocr.RecognizeRegionAsync(bmp, new RectangleF(0.28f, 0.82f, 0.46f, 0.14f), 3);
                if (!string.IsNullOrWhiteSpace(bandText))
                    advice = TheaterLogic.Evaluate(hits, text + " " + bandText, _theater, _roster, bandText);
            }

            // 没有模板时，自动为每个识别到的幕存一张原始帧，作为后续做模板的素材
            if (advice.Act.HasValue && _templates.Count == 0 && _autoSaved.Add(advice.Act.Value))
            {
                try
                {
                    var dir = Path.Combine(AppPaths.Dir, "samples", "auto");
                    Directory.CreateDirectory(dir);
                    bmp.Save(Path.Combine(dir, $"act{advice.Act.Value}-{bmp.Width}x{bmp.Height}.png"),
                             System.Drawing.Imaging.ImageFormat.Png);
                }
                catch { }
            }

            _header = advice.Header;
            _lines.Clear();
            _lines.AddRange(advice.Lines);
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
        var root = Path.Combine(AppPaths.Dir, "templates");
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
            try { _templates.Add(new TemplateEntry(name, new Bitmap(f), 0.86)); } catch { }
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
            var dir = Path.Combine(AppPaths.Dir, "samples");
            Directory.CreateDirectory(dir);
            var p = Path.Combine(dir, $"dump-{DateTime.Now:HHmmss}.png");
            bmp.Save(p, System.Drawing.Imaging.ImageFormat.Png);
            _lines.Add("已存帧: " + Path.GetFileName(p));
            Invalidate();
        }
        catch { /* 静默 */ }
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
        var g = e.Graphics;
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

internal sealed record TemplateEntry(string Name, Bitmap Bitmap, double Threshold);
