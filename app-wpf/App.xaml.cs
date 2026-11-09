using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;
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
    private OverlayWindow _overlay;
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

            _overlay = new OverlayWindow();
            _overlay.Show();
            InitTray();
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

    private void ToggleOverlay()
    {
        if (_overlay == null) return;
        if (_overlay.IsVisible) _overlay.Hide();
        else _overlay.Show();
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
