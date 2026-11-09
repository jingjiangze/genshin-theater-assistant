using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using MessageBox = System.Windows.MessageBox;

namespace TheaterAssistant;

/// <summary>
/// 设置窗口（WPF 版）——页面分类照抄 BetterGI 的导航结构。
///
/// BetterGI 有 53 个 Page（XAML 合计 1.5MB，单页最大 267KB），我们只移植**需要的 7 页**：
///   首页 / 通用 / 识别 / 悬浮窗 / 热键 / 角色池 / 诊断
/// 每页都做**淡入过渡**（BetterGI 用 Wpf.Ui 做页面动画，这里用原生 DoubleAnimation，不引额外包）。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly TabControl _tabs = new() { Margin = new Thickness(12, 12, 12, 0) };

    // 通用
    private readonly Slider _interval = Slider(300, 5000, 100, 1000);
    private readonly Slider _opacity = Slider(40, 100, 5, 96);
    private readonly Slider _font = Slider(8, 16, 1, 12);

    // 识别
    private readonly ComboBox _ocrModel = Combo(new[] { "v6（推荐）", "v5", "自动" });
    private readonly ComboBox _preprocess = Combo(new[] { "off（不处理）", "boost（白字提纯，推荐）", "mask（只留白字）" });
    private readonly CheckBox _llmOn = Check("启用大模型顾问（◇模型通道）", true);

    // 悬浮窗
    private readonly CheckBox _showSource = Check("指示行显示来源标记（◆ ★ ◇ ○ ·）", true);
    private readonly CheckBox _hideWhenMissing = Check("未检测到游戏窗口时自动隐藏", true);

    // 热键
    private readonly CheckBox _hotkeyEnable = Check("启用全局热键", true);
    private readonly TextBlock _hotkeyList = new() { FontSize = 12, LineHeight = 22, Margin = new Thickness(0, 8, 0, 0) };

    // 角色池
    private readonly TextBox _uid = new() { Width = 240, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ComboBox _poolSource = Combo(new[] { "自动（GOOD ∪ Enka 合并）", "仅本地 GOOD", "仅 Enka 展柜" });

    // 诊断 / 首页
    private readonly TextBlock _diag = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, LineHeight = 20 };
    private readonly TextBlock _home = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, LineHeight = 24 };

    public SettingsWindow()
    {
        InitializeComponent();

        // 照 BetterGI 的页面分类（按我们能提供的范围裁剪到 7 页）
        _tabs.TabPages().Add(Page("首页", HomePanel()));
        _tabs.TabPages().Add(Page("通用", Stack(
            Label("悬浮窗刷新间隔（毫秒）"), _interval,
            Label("不透明度（%）"), _opacity,
            Label("正文字号"), _font)));
        _tabs.TabPages().Add(Page("识别", Stack(
            Label("OCR 模型"), _ocrModel,
            Label("OCR 预处理（D5 白字掩膜）"), _preprocess,
            _llmOn,
            Hint("说明：D3 竖排 / D4 透视裁剪 RapidOcrNet 已内置，无需配置。"))));
        _tabs.TabPages().Add(Page("悬浮窗", Stack(_showSource, _hideWhenMissing, Hint("热键：Ctrl+Alt+L 拖动 · H 显隐 · D 存帧 · G 攻略"))));
        _tabs.TabPages().Add(Page("热键", HotkeyPanel()));
        _tabs.TabPages().Add(Page("角色池", Stack(
            Label("游戏 UID（留空=自动从画面读）"), _uid,
            Label("池来源策略"), _poolSource,
            Hint("Enka 只能读到「角色展柜」公开的角色，本地 GOOD 可能更全；默认取并集。"))));
        _tabs.TabPages().Add(Page("诊断", new ScrollViewer { Content = _diag, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }));

        // 页面切换淡入（BetterGI 用 Wpf.Ui 的页面过渡，这里用原生动画等效）
        _tabs.SelectionChanged += (_, _) => FadeInCurrentPage();

        var buttons = new System.Windows.Controls.StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12),
        };
        buttons.Children.Add(Button("配置目录", (_, _) => OpenFolder(AppPaths.Dir)));
        buttons.Children.Add(Button("日志目录", (_, _) => OpenFolder(App.LogDir)));
        buttons.Children.Add(Button("保存并关闭", (_, _) => { SaveAll(); Close(); }));

        var root = Shell;   // 填充 XAML 里的 Shell（避免与 Content 重复赋值）
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(_tabs);
        Content = root;

        LoadAll();
        RefreshDiag();
        RefreshHome();
    }

    // ── 控件工厂（少写样板）─────────────────────────────────────
    private static Slider Slider(double min, double max, double tick, double val) =>
        new() { Minimum = min, Maximum = max, TickFrequency = tick, Value = val, Width = 320, HorizontalAlignment = HorizontalAlignment.Left, IsSnapToTickEnabled = true };

    private static ComboBox Combo(string[] items)
    {
        var c = new ComboBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left, SelectedIndex = 0 };
        foreach (var i in items) c.Items.Add(i);
        return c;
    }

    private static CheckBox Check(string text, bool on) =>
        new() { Content = text, IsChecked = on, Margin = new Thickness(0, 8, 0, 0) };

    private static TextBlock Label(string t) => new() { Text = t, Margin = new Thickness(0, 14, 0, 4), Opacity = 0.85 };

    private static TextBlock Hint(string t) =>
        new() { Text = t, Margin = new Thickness(0, 14, 0, 0), FontSize = 11, Opacity = 0.55, TextWrapping = TextWrapping.Wrap };

    private static System.Windows.Controls.Button Button(string text, RoutedEventHandler onClick)
    {
        var b = new System.Windows.Controls.Button { Content = text, Width = 110, Height = 32, Margin = new Thickness(8, 0, 0, 0) };
        b.Click += onClick;
        return b;
    }

    private static StackPanel Stack(params UIElement[] items)
    {
        var s = new StackPanel { Margin = new Thickness(18) };
        foreach (var i in items) s.Children.Add(i);
        return s;
    }

    private static TabItem Page(string title, UIElement content) => new() { Header = title, Content = content };

    private void FadeInCurrentPage()
    {
        if (_tabs.SelectedContent is not FrameworkElement fe) return;
        fe.Opacity = 0;
        fe.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    // ── 首页 / 热键 / 诊断 ──────────────────────────────────────
    private StackPanel HomePanel()
    {
        var s = new StackPanel { Margin = new Thickness(18) };
        s.Children.Add(_home);
        return s;
    }

    private StackPanel HotkeyPanel()
    {
        _hotkeyList.Text = string.Join("\n", new[]
        {
            "Ctrl + Alt + H　　显示 / 隐藏悬浮球",
            "Ctrl + Alt + L　　切换拖动模式（关闭点击穿透）",
            "Ctrl + Alt + D　　保存当前帧（排查识别问题用）",
            "Ctrl + Alt + G　　打开攻略面板",
            "双击悬浮球　　　 打开本设置窗口",
        });
        return Stack(Label("全局热键（Win32 RegisterHotKey，不注入不模拟）"), _hotkeyEnable, _hotkeyList);
    }

    private void RefreshHome()
    {
        var b = new StringBuilder();
        b.AppendLine("剧诗助手 · 幻境决策层");
        b.AppendLine();
        b.AppendLine($"● 识别：{(string.IsNullOrEmpty(OcrTuning.EpStatus) ? "未初始化" : OcrTuning.EpStatus)}");
        b.AppendLine($"● 决策层：{(DecisionBridge.Ready ? "已就绪" : "未就绪")}");
        b.AppendLine($"● 角色池：{DecisionBridge.PoolNote}");
        b.AppendLine($"● 语料库：{GuideIndex.Count} 条");
        b.AppendLine($"● OCR 预处理：{OcrPreprocess.Source}");
        b.AppendLine();
        b.AppendLine("操作：双击悬浮球 → 打开本窗口；托盘右键 → 快速菜单。");
        _home.Text = b.ToString();
    }

    private void RefreshDiag()
    {
        var b = new StringBuilder();
        b.AppendLine($"配置目录：{AppPaths.Dir}");
        b.AppendLine($"数据目录：{AppPaths.Data(".")}");
        b.AppendLine();
        b.AppendLine($"OCR 线程：{OcrTuning.NumThreads}（0=自动）");
        b.AppendLine($"OCR 执行提供器：{OcrTuning.EpStatus}");
        b.AppendLine($"预处理：{OcrPreprocess.Source}");
        b.AppendLine();
        try
        {
            if (Directory.Exists(App.LogDir))
            {
                var files = Directory.GetFiles(App.LogDir, "crash-*.log");
                b.AppendLine(files.Length == 0 ? "崩溃日志：无" : $"最近崩溃：{Path.GetFileName(files[^1])}（共 {files.Length} 个）");
            }
            else b.AppendLine("崩溃日志：无（目录未创建）");
        }
        catch { }
        _diag.Text = b.ToString();
    }

    // ── 读写 ────────────────────────────────────────────────────
    private static JsonNode ReadNode(string name)
    {
        try { var p = AppPaths.Data(name); return File.Exists(p) ? JsonNode.Parse(File.ReadAllText(p)) : null; }
        catch { return null; }
    }

    private void LoadAll()
    {
        try
        {
            var ui = ReadNode("ui.json");
            _interval.Value = ui?["scanIntervalMs"]?.GetValue<int>() ?? 1000;
            _opacity.Value = ui?["opacityPct"]?.GetValue<int>() ?? 96;
            _font.Value = ui?["bodyFontPt"]?.GetValue<int>() ?? 12;
            _showSource.IsChecked = ui?["showSourceMark"]?.GetValue<bool>() ?? true;
            _hideWhenMissing.IsChecked = ui?["hideWhenGameMissing"]?.GetValue<bool>() ?? true;

            var pol = ReadNode("decision-policy.json");
            _ocrModel.SelectedIndex = (pol?["ocr"]?["model"]?.GetValue<string>() ?? "v6") switch { "v5" => 1, "auto" => 2, _ => 0 };
            _preprocess.SelectedIndex = (pol?["ocr"]?["preprocess"]?["mode"]?.GetValue<string>() ?? "off") switch { "boost" => 1, "mask" => 2, _ => 0 };

            var llm = ReadNode("llm.json");
            _llmOn.IsChecked = llm?["enabled"]?.GetValue<bool>() ?? true;

            var uidPath = Path.Combine(AppPaths.Dir, "uid.txt");
            if (File.Exists(uidPath)) _uid.Text = File.ReadAllText(uidPath).Trim();
        }
        catch { }
    }

    private void SaveAll()
    {
        try
        {
            File.WriteAllText(Path.Combine(AppPaths.Dir, "ui.json"), new JsonObject
            {
                ["scanIntervalMs"] = (int)_interval.Value,
                ["opacityPct"] = (int)_opacity.Value,
                ["bodyFontPt"] = (int)_font.Value,
                ["showSourceMark"] = _showSource.IsChecked == true,
                ["hideWhenGameMissing"] = _hideWhenMissing.IsChecked == true,
            }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            var polPath = AppPaths.Data("decision-policy.json");
            if (File.Exists(polPath))
            {
                var pol = JsonNode.Parse(File.ReadAllText(polPath)) as JsonObject ?? new JsonObject();
                if (pol["ocr"] is not JsonObject ocr) pol["ocr"] = ocr = new JsonObject();
                ocr["model"] = _ocrModel.SelectedIndex switch { 1 => "v5", 2 => "auto", _ => "v6" };
                if (ocr["preprocess"] is not JsonObject pp) ocr["preprocess"] = pp = new JsonObject();
                pp["mode"] = _preprocess.SelectedIndex switch { 1 => "boost", 2 => "mask", _ => "off" };
                File.WriteAllText(polPath, pol.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }

            var llmPath = AppPaths.Data("llm.json");
            if (File.Exists(llmPath))
            {
                var llm = JsonNode.Parse(File.ReadAllText(llmPath)) as JsonObject ?? new JsonObject();
                llm["enabled"] = _llmOn.IsChecked == true;
                File.WriteAllText(llmPath, llm.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }

            var uid = (_uid.Text ?? "").Trim();
            if (uid.Length == 9) File.WriteAllText(Path.Combine(AppPaths.Dir, "uid.txt"), uid);
        }
        catch (Exception ex)
        {
            MessageBox.Show("保存失败：" + ex.Message, "剧诗助手", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch { }
    }
}

/// <summary>TabControl.TabPages 在 WPF 里不存在（那是 WinForms），这里补个等价扩展。</summary>
internal static class TabControlExtensions
{
    public static ItemCollection TabPages(this TabControl tabs) => tabs.Items;
}
