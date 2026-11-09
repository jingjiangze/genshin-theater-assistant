using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace TheaterAssistant;

/// <summary>
/// WPF 版入口。
///
/// 与 WinForms 版的差异（照抄 BetterGI 的做法）：
///   ① 三处全局异常注册，缺一处就会静默丢异常（WinForms 版此前只有一处 → 进程静默消失）；
///   ② 崩溃日志落到 %LOCALAPPDATA%\TheaterAssistant\crash-*.log，任何时候都能查原因；
///   ③ ShutdownMode=OnExplicitShutdown：托盘常驻，关掉窗口不等于退出。
/// </summary>
public partial class App : System.Windows.Application
{
    private FloatingBall _ball;
    private MainLoop _loop;
    private OverlayWindow _detail;
    private MaskWindowHost _maskHost;
    private IntPtr _maskGameHandle;
    private string _maskLogKey = "";

    /// <summary>供子窗口回调（如悬浮球双击展开详情）。</summary>
    public static App CurrentApp => Current as App;
    private System.Windows.Forms.NotifyIcon _tray;

    internal static string LogDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TheaterAssistant");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ① 三处异常注册（顺序无所谓，但必须都挂上）
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => LogCrash(ev.ExceptionObject as Exception, "AppDomain");
        DispatcherUnhandledException += (_, ev) => { LogCrash(ev.Exception, "Dispatcher"); ev.Handled = true; };
        TaskScheduler.UnobservedTaskException += (_, ev) => { LogCrash(ev.Exception, "Task"); ev.SetObserved(); };

        try
        {
            // WPF 自检：在 CI 里真实实例化两个窗口并渲染成 PNG。
            // 这样 XamlParseException 这类"只有运行时才炸"的问题在 CI 就能暴露，
            // 而不是等用户双击时才看到崩溃弹窗（本次踩过：Color 当 Brush 用）。
            if (Array.IndexOf(e.Args, "--selftest-wpf") >= 0)
            {
                Shutdown(WpfSelfTest.Run());
                return;
            }

            // 决策层初始化（失败不阻塞悬浮窗）
            try { DecisionBridge.Init(); } catch (Exception ex) { LogCrash(ex, "DecisionBridge.Init"); }

            _ball = new FloatingBall();
            _ball.Show();
            InitTray();

            // 遮罩宿主（全屏透明窗 + 左下日志 + 识别框）。窗口按需创建，由游戏窗口事实驱动。
            _maskHost = new MaskWindowHost(() => new MaskWindow());

            // 主循环：抓屏 → 识别 → 决策 → 驱动悬浮球 + 遮罩
            _loop = new MainLoop(_ball);
            _loop.Updated += OnLoopUpdated;
            _ = _loop.StartAsync();
        }
        catch (Exception ex)
        {
            LogCrash(ex, "Startup");
            MessageBox.Show(
                $"启动失败：{ex.GetType().Name}\r\n\r\n{ex.Message}\r\n\r\n日志：{LogDir}",
                "剧诗助手", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    // ── 托盘（用 WinForms 的 NotifyIcon：WPF 没有内置托盘，这是社区标准做法）──
    private void InitTray()
    {
        try
        {
            _tray = new System.Windows.Forms.NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                Text = "剧诗助手 · 左键显示/隐藏 · 右键菜单",
                Visible = true,
            };
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("显示 / 隐藏悬浮窗", null, (_, _) => ToggleOverlay());
            menu.Items.Add("显示 / 隐藏游戏内遮罩", null, (_, _) => ToggleMask());
            menu.Items.Add("设置…", null, (_, _) => OpenSettings());
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("打开配置目录", null, (_, _) => OpenFolder(AppPaths.Dir));
            menu.Items.Add("打开日志目录", null, (_, _) => OpenFolder(LogDir));
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("退出", null, (_, _) => Shutdown(0));
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (_, _) => ToggleOverlay();
        }
        catch (Exception ex)
        {
            LogCrash(ex, "InitTray");
        }
    }

    /// <summary>打开/收起详细面板（悬浮球双击、托盘菜单都走这里）。</summary>
    public void OpenDetail() => ToggleOverlay();

    /// <summary>托盘/菜单入口：展开或收起详细面板。</summary>
    private void ToggleOverlay()
    {
        try
        {
            if (_detail != null && _detail.IsVisible) { _detail.Hide(); return; }
            _detail ??= new OverlayWindow();
            _detail.Show();
            _detail.Activate();
        }
        catch (Exception ex) { LogCrash(ex, "ToggleOverlay"); }
    }

    /// <summary>托盘入口：手动收起/恢复游戏内遮罩（优先于自动跟随策略）。</summary>
    private void ToggleMask()
    {
        try
        {
            if (_maskHost == null) return;
            _maskHost.ManualHidden = !_maskHost.ManualHidden;
        }
        catch (Exception ex) { LogCrash(ex, "ToggleMask"); }
    }

    // ── 主循环快照 → 遮罩 ─────────────────────────────────────────────────
    private void OnLoopUpdated(MainLoop.TickSnapshot s)
    {
        try { UpdateMask(s); }
        catch (Exception ex) { LogCrash(ex, "OnLoopUpdated"); }
    }

    /// <summary>
    /// 把一帧事实推给遮罩：附着/跟随窗口、画识别框、写日志。
    ///
    /// 日志只在「幕次 / 来源 / 弃权 / 状态」变化时写一行——主循环每秒一帧，
    /// 无条件写会把面板刷成滚屏并让 OCR 日志失去意义。
    /// </summary>
    private void UpdateMask(MainLoop.TickSnapshot s)
    {
        if (_maskHost == null) return;

        if (s.GameHandle == IntPtr.Zero)
        {
            _maskGameHandle = IntPtr.Zero;
            _maskHost.Detach();
            return;
        }

        if (_maskGameHandle != s.GameHandle)
        {
            _maskGameHandle = s.GameHandle;
            _maskLogKey = "";
            _maskHost.ManualHidden = false;     // 换了游戏窗口，恢复自动跟随
            _maskHost.Attach(s.GameHandle);
            _maskHost.EnsureWindow()?.AppendLog("INF", $"已附着游戏窗口 {s.FrameWidth}x{s.FrameHeight}");
        }

        _maskHost.Report(MaskGameState.Read(s.GameHandle));

        var w = _maskHost.EnsureWindow();
        if (w == null) return;

        var boxes = new List<MaskBox>();
        if (s.BandRect is { } band)
        {
            boxes.Add(new MaskBox
            {
                Bounds = band,
                Color = System.Windows.Media.Color.FromRgb(0x7E, 0xB8, 0xE8),
                Thickness = 1.5,
                Label = "幕次条带",
                CornersOnly = false,
                LabelAlignRight = true,   // 左边是被日志面板盖住的区域
            });
        }
        foreach (var h in s.Hits)
        {
            boxes.Add(new MaskBox
            {
                Bounds = new Rect(h.X, h.Y, h.W, h.H),
                Color = System.Windows.Media.Color.FromRgb(0x7E, 0xD8, 0xA0),
                Thickness = 2,
                Label = $"{h.Name} {h.Score:0.00}",
            });
        }
        w.SetFrame(boxes, s.FrameWidth, s.FrameHeight);

        var actText = s.Act.HasValue ? $"第{s.Act}幕" : "";
        w.SetStatus(actText, s.Source, s.Abstained ? $"弃权 {s.MissedMs}ms" : "");

        // 「现在做」：弃权时清空（不展示过期指令），否则用决策层的第一句
        w.SetAdvice(s.Abstained || s.Lines.Count == 0 ? "" : s.Lines[0]);

        var key = $"{s.Act}|{s.Source}|{s.Abstained}|{s.Status}";
        if (key == _maskLogKey) return;
        _maskLogKey = key;

        if (s.Abstained)
        {
            w.AppendLog("WRN", s.Status);
        }
        else if (s.Act.HasValue)
        {
            w.AppendLog("DEC", $"第{s.Act}幕 → {s.Source}");
        }
        else
        {
            var bandText = string.IsNullOrWhiteSpace(s.BandText) ? "" : $"（条带：{s.BandText}）";
            w.AppendLog("INF", s.Status + bandText);
        }

        if (s.Hits.Count > 0)
            w.AppendLog("OCR", "命中 " + string.Join("、", s.Hits.Select(h => $"{h.Name} {h.Score:0.00}")));
    }

    private void OpenSettings()
    {
        try
        {
            var w = new SettingsWindow();
            w.Show();
            w.Activate();
        }
        catch (Exception ex)
        {
            LogCrash(ex, "OpenSettings");
            MessageBox.Show("打开设置失败：" + ex.Message, "剧诗助手", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _loop?.Stop(); } catch { }
        try { _maskHost?.Close(); } catch { }
        try { _tray?.Dispose(); } catch { }
        base.OnExit(e);
    }

    /// <summary>统一崩溃落盘：三处入口都走这里，"进程静默消失"永远能查到原因。</summary>
    internal static void LogCrash(Exception ex, string source)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            var f = Path.Combine(LogDir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(f,
                $"{DateTime.Now:O}\r\nSOURCE: {source}\r\n{ex}\r\n\r\n--- args ---\r\n{string.Join(" ", Environment.GetCommandLineArgs())}\r\n");
        }
        catch { }
    }
}
