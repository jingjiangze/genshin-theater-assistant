using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TheaterAssistant;

/// <summary>
/// 设置面板 —— 结构照抄 BetterGI（babalae/better-genshin-impact，GPL-3.0）的**页面分类**：
/// 它把设置拆成「通用 / 任务 / 热键 / 通知 / 悬浮窗 / 关于」等独立页。
/// 注意：只照抄**界面结构与交互**（不受版权保护），代码按 WinForms 重写；
/// 直接复制其源码会让本项目受 GPL-3.0 传染，不做。
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };

    // 通用
    private readonly NumericUpDown _interval = new() { Minimum = 300, Maximum = 5000, Increment = 100, Width = 90 };
    private readonly NumericUpDown _opacity = new() { Minimum = 40, Maximum = 100, Increment = 5, Width = 90 };
    private readonly NumericUpDown _font = new() { Minimum = 7, Maximum = 14, Increment = 1, Width = 90 };

    // 识别
    private readonly ComboBox _ocrModel = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    private readonly CheckBox _llmOn = new() { Text = "启用大模型顾问（◇模型通道）", AutoSize = true };

    // 悬浮窗
    private readonly CheckBox _showSource = new() { Text = "指示行显示来源标记（◆★◇○·）", AutoSize = true, Checked = true };
    private readonly CheckBox _hideWhenGameMissing = new() { Text = "未检测到游戏窗口时自动隐藏", AutoSize = true, Checked = true };

    // 角色池
    private readonly TextBox _uid = new() { Width = 160 };
    private readonly ComboBox _poolSource = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };

    // 关于/诊断
    private readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 8.5f) };

    public SettingsForm()
    {
        Text = "剧诗助手 · 设置";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(620, 520);
        ClientSize = new Size(680, 560);
        BackColor = Color.FromArgb(28, 32, 44);
        ForeColor = Color.FromArgb(226, 232, 244);
        Font = new Font("Microsoft YaHei UI", 9f);

        // 与 BetterGI 的页面分类对齐（按我们能提供的范围裁剪）
        _tabs.TabPages.Add(BuildGeneralPage());
        _tabs.TabPages.Add(BuildRecognizePage());
        _tabs.TabPages.Add(BuildOverlayPage());
        _tabs.TabPages.Add(BuildPoolPage());
        _tabs.TabPages.Add(BuildAboutPage());

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 48,
            FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 12, 0),
        };
        var save = new Button { Text = "保存并关闭", Width = 110, Height = 30 };
        var openDir = new Button { Text = "配置目录", Width = 100, Height = 30 };
        var openLog = new Button { Text = "日志目录", Width = 100, Height = 30 };
        save.Click += (_, _) => { SaveAll(); Close(); };
        openDir.Click += (_, _) => OpenFolder(AppPaths.Dir);
        openLog.Click += (_, _) => OpenFolder(LogDir);
        buttons.Controls.Add(save);
        buttons.Controls.Add(openDir);
        buttons.Controls.Add(openLog);

        Controls.Add(_tabs);
        Controls.Add(buttons);

        LoadAll();
        RefreshStatus();
    }

    internal static string LogDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TheaterAssistant");

    // ── 页面构造 ────────────────────────────────────────────────
    private TabPage NewPage(string title)
    {
        var p = new TabPage(title) { BackColor = Color.FromArgb(34, 38, 52), ForeColor = Color.FromArgb(226, 232, 244), Padding = new Padding(16) };
        p.Controls.Add(new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2,
            ColumnStyles = { new ColumnStyle(SizeType.Absolute, 200), new ColumnStyle(SizeType.Percent, 100) },
        });
        return p;
    }

    private static TableLayoutPanel Grid(TabPage p) => (TableLayoutPanel)p.Controls[0];

    private static void Row(TableLayoutPanel t, string label, Control c)
    {
        t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 10, 8, 0) });
        c.Anchor = AnchorStyles.Left; c.Margin = new Padding(0, 8, 0, 0);
        t.Controls.Add(c);
    }

    private TabPage BuildGeneralPage()
    {
        var p = NewPage("通用");
        var t = Grid(p);
        Row(t, "刷新间隔 (ms)", _interval);
        Row(t, "不透明度 (%)", _opacity);
        Row(t, "正文字号", _font);
        return p;
    }

    private TabPage BuildRecognizePage()
    {
        var p = NewPage("识别");
        var t = Grid(p);
        _ocrModel.Items.AddRange(new object[] { "v6（推荐）", "v5", "自动" });
        Row(t, "OCR 模型", _ocrModel);
        t.Controls.Add(new Label { Text = "", AutoSize = true });
        t.Controls.Add(_llmOn);
        return p;
    }

    private TabPage BuildOverlayPage()
    {
        var p = NewPage("悬浮窗");
        var t = Grid(p);
        t.Controls.Add(new Label { Text = "", AutoSize = true });
        t.Controls.Add(_showSource);
        t.Controls.Add(new Label { Text = "", AutoSize = true });
        t.Controls.Add(_hideWhenGameMissing);
        Row(t, "热键（固定）", new Label
        {
            Text = "Ctrl+Alt+L 拖动 · H 显隐 · D 存帧 · G 攻略",
            AutoSize = true, ForeColor = Color.FromArgb(150, 158, 178),
        });
        return p;
    }

    private TabPage BuildPoolPage()
    {
        var p = NewPage("角色池");
        var t = Grid(p);
        Row(t, "游戏 UID（留空=自动读画面）", _uid);
        _poolSource.Items.AddRange(new object[] { "自动（GOOD ∪ Enka 合并）", "仅本地 GOOD", "仅 Enka 展柜" });
        Row(t, "池来源策略", _poolSource);
        t.Controls.Add(new Label
        {
            Text = "说明：Enka 只能读到「角色展柜」公开的角色，\r\n本地 GOOD 可能更全；默认取并集，强度字段优先用有值一方。",
            AutoSize = true, ForeColor = Color.FromArgb(150, 158, 178), Margin = new Padding(0, 16, 0, 0),
        });
        return p;
    }

    private TabPage BuildAboutPage()
    {
        var p = NewPage("诊断");
        _status.Dock = DockStyle.Fill;
        p.Controls.Clear();
        p.Controls.Add(_status);
        return p;
    }

    // ── 读写 ────────────────────────────────────────────────────
    private void LoadAll()
    {
        try
        {
            var ui = ReadNode("ui.json");
            _interval.Value = Clamp(ui?["scanIntervalMs"]?.GetValue<int>() ?? 1000, 300, 5000);
            _opacity.Value = Clamp(ui?["opacityPct"]?.GetValue<int>() ?? 96, 40, 100);
            _font.Value = Clamp(ui?["bodyFontPt"]?.GetValue<int>() ?? 10, 7, 14);
            _showSource.Checked = ui?["showSourceMark"]?.GetValue<bool>() ?? true;
            _hideWhenGameMissing.Checked = ui?["hideWhenGameMissing"]?.GetValue<bool>() ?? true;

            var pol = ReadNode("decision-policy.json");
            _ocrModel.SelectedIndex = (pol?["ocr"]?["model"]?.GetValue<string>() ?? "v6") switch
            {
                "v5" => 1, "auto" => 2, _ => 0,
            };

            var llm = ReadNode("llm.json");
            _llmOn.Checked = llm?["enabled"]?.GetValue<bool>() ?? true;

            var uidPath = Path.Combine(AppPaths.Dir, "uid.txt");
            if (File.Exists(uidPath)) _uid.Text = File.ReadAllText(uidPath).Trim();
            _poolSource.SelectedIndex = 0;
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
                ["showSourceMark"] = _showSource.Checked,
                ["hideWhenGameMissing"] = _hideWhenGameMissing.Checked,
            }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            var polPath = AppPaths.Data("decision-policy.json");
            if (File.Exists(polPath))
            {
                var pol = JsonNode.Parse(File.ReadAllText(polPath)) as JsonObject ?? new JsonObject();
                if (pol["ocr"] is not JsonObject ocr) pol["ocr"] = ocr = new JsonObject();
                ocr["model"] = _ocrModel.SelectedIndex switch { 1 => "v5", 2 => "auto", _ => "v6" };
                File.WriteAllText(polPath, pol.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }

            var llmPath = AppPaths.Data("llm.json");
            if (File.Exists(llmPath))
            {
                var llm = JsonNode.Parse(File.ReadAllText(llmPath)) as JsonObject ?? new JsonObject();
                llm["enabled"] = _llmOn.Checked;
                File.WriteAllText(llmPath, llm.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }

            var uid = _uid.Text.Trim();
            var uidPath = Path.Combine(AppPaths.Dir, "uid.txt");
            if (uid.Length == 9) File.WriteAllText(uidPath, uid);
        }
        catch (Exception ex)
        {
            MessageBox.Show("保存失败：" + ex.Message, "剧诗助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static int Clamp(int v, int lo, int hi) => Math.Max(lo, Math.Min(hi, v));

    private static JsonNode ReadNode(string name)
    {
        try { var p = AppPaths.Data(name); return File.Exists(p) ? JsonNode.Parse(File.ReadAllText(p)) : null; }
        catch { return null; }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch { }
    }

    private void RefreshStatus()
    {
        var b = new System.Text.StringBuilder();
        b.AppendLine("— 运行状态（只读）—");
        b.AppendLine();
        b.AppendLine($"版本：{Assembly.GetExecutingAssembly().GetName().Version}");
        b.AppendLine($"配置目录：{AppPaths.Dir}");
        b.AppendLine($"数据目录：{AppPaths.Data(".")}");
        b.AppendLine();
        b.AppendLine($"决策层：{(DecisionBridge.Ready ? "已就绪" : "未就绪")}");
        b.AppendLine($"角色池：{DecisionBridge.PoolNote}");
        b.AppendLine();
        b.AppendLine($"OCR 线程：{OcrTuning.NumThreads}（0=自动）");
        b.AppendLine($"OCR 执行提供器：{OcrTuning.EpStatus}");
        b.AppendLine($"OCR 参数来源：{OcrTuning.Source}");
        b.AppendLine();
        if (Directory.Exists(LogDir))
        {
            var crash = Directory.GetFiles(LogDir, "crash-*.log").OrderByDescending(f => f).FirstOrDefault();
            b.AppendLine(crash == null
                ? "崩溃日志：无（说明没崩过）"
                : $"最近崩溃：{Path.GetFileName(crash)}");
        }
        else b.AppendLine("崩溃日志：目录尚未创建（说明没崩过）");
        _status.Text = b.ToString();
    }
}
