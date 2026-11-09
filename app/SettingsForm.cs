using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TheaterAssistant;

/// <summary>
/// 设置面板：像其他原神工具一样提供一个可视化入口，而不是只能靠命令行参数。
///
/// 设计取舍（用户要求「做成和其他原神工具类似的」「默认设置面板」）：
///   · 不做"全功能设置中心"——那会变成第二个产物；这里只放**玩家真正会改的项**；
///   · 所有项直接读写 `data/*.json`（单一数据源），保存后重启生效，不做隐式热加载；
///   · 只读诊断信息（OCR 状态、角色池来源）单独一栏，方便反馈问题时截图。
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly NumericUpDown _interval = new() { Minimum = 300, Maximum = 5000, Increment = 100, Width = 90 };
    private readonly NumericUpDown _opacity = new() { Minimum = 40, Maximum = 100, Increment = 5, Width = 90 };
    private readonly NumericUpDown _font = new() { Minimum = 7, Maximum = 14, Increment = 1, Width = 90 };
    private readonly ComboBox _ocrModel = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    private readonly CheckBox _llmOn = new() { Text = "启用大模型顾问（◇模型通道）", AutoSize = true };
    private readonly TextBox _uid = new() { Width = 160 };
    private readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Bottom, Height = 150, Font = new Font("Microsoft YaHei UI", 8.5f) };

    public SettingsForm()
    {
        Text = "剧诗助手 · 设置";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        ClientSize = new Size(520, 470);
        BackColor = Color.FromArgb(28, 32, 44);
        ForeColor = Color.FromArgb(226, 232, 244);
        Font = new Font("Microsoft YaHei UI", 9f);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(16) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddRow(root, "悬浮窗刷新间隔 (ms)", _interval);
        AddRow(root, "悬浮窗不透明度 (%)", _opacity);
        AddRow(root, "正文字号", _font);
        AddRow(root, "OCR 模型", _ocrModel);
        AddRow(root, "游戏 UID（留空=自动从画面读）", _uid);
        root.Controls.Add(new Label { Text = "", AutoSize = true });
        root.Controls.Add(_llmOn);

        _ocrModel.Items.AddRange(new object[] { "v6（推荐）", "v5", "自动" });

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
        var save = new Button { Text = "保存并关闭", Width = 110, Height = 30 };
        var openDir = new Button { Text = "打开配置目录", Width = 110, Height = 30 };
        var openLog = new Button { Text = "打开日志目录", Width = 110, Height = 30 };
        save.Click += (_, _) => { SaveAll(); Close(); };
        openDir.Click += (_, _) => OpenFolder(AppPaths.Dir);
        openLog.Click += (_, _) => OpenFolder(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TheaterAssistant"));
        buttons.Controls.Add(save);
        buttons.Controls.Add(openDir);
        buttons.Controls.Add(openLog);

        Controls.Add(root);
        Controls.Add(buttons);
        Controls.Add(_status);

        LoadAll();
        RefreshStatus();
    }

    private static void AddRow(TableLayoutPanel t, string label, Control c)
    {
        t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 8, 0) });
        c.Anchor = AnchorStyles.Left; c.Margin = new Padding(0, 6, 0, 0);
        t.Controls.Add(c);
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

    private void LoadAll()
    {
        try
        {
            var ui = ReadNode("ui.json");
            _interval.Value = Clamp(ui?["scanIntervalMs"]?.GetValue<int>() ?? 1000, 300, 5000);
            _opacity.Value = Clamp(ui?["opacityPct"]?.GetValue<int>() ?? 96, 40, 100);
            _font.Value = Clamp(ui?["bodyFontPt"]?.GetValue<int>() ?? 10, 7, 14);

            var pol = ReadNode("decision-policy.json");
            _ocrModel.SelectedIndex = (pol?["ocr"]?["model"]?.GetValue<string>() ?? "v6") switch
            {
                "v5" => 1, "auto" => 2, _ => 0,
            };

            var llm = ReadNode("llm.json");
            _llmOn.Checked = llm?["enabled"]?.GetValue<bool>() ?? true;
        }
        catch { }
    }

    private void SaveAll()
    {
        try
        {
            WriteNode("ui.json", new JsonObject
            {
                ["scanIntervalMs"] = (int)_interval.Value,
                ["opacityPct"] = (int)_opacity.Value,
                ["bodyFontPt"] = (int)_font.Value,
            });

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

            if (!string.IsNullOrWhiteSpace(_uid.Text) && _uid.Text.Trim().Length == 9)
                File.WriteAllText(Path.Combine(AppPaths.Dir, "uid.txt"), _uid.Text.Trim());
        }
        catch (Exception ex)
        {
            MessageBox.Show("保存失败：" + ex.Message, "剧诗助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static int Clamp(int v, int lo, int hi) => Math.Max(lo, Math.Min(hi, v));

    private static JsonNode ReadNode(string name)
    {
        try
        {
            var p = AppPaths.Data(name);
            return File.Exists(p) ? JsonNode.Parse(File.ReadAllText(p)) : null;
        }
        catch { return null; }
    }

    private static void WriteNode(string name, JsonNode node)
    {
        try
        {
            var p = Path.Combine(AppPaths.Dir, name);
            File.WriteAllText(p, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>只读诊断：把"能不能用"的关键状态集中展示，方便反馈问题时直接截图。</summary>
    private void RefreshStatus()
    {
        var lines = new System.Text.StringBuilder();
        lines.AppendLine("— 诊断信息（只读）—");
        lines.AppendLine($"配置目录：{AppPaths.Dir}");
        lines.AppendLine($"数据目录：{AppPaths.Data(".")}");
        lines.AppendLine($"决策层：{(DecisionBridge.Ready ? "已就绪" : "未就绪")}　{DecisionBridge.PoolNote}");
        lines.AppendLine($"OCR 线程：{OcrTuning.NumThreads}（0=自动）　EP：{OcrTuning.EpStatus}");
        lines.AppendLine($"OCR 参数来源：{OcrTuning.Source}");
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TheaterAssistant");
        if (Directory.Exists(logDir))
        {
            var crash = Directory.GetFiles(logDir, "crash-*.log").OrderByDescending(f => f).FirstOrDefault();
            if (crash != null) lines.AppendLine($"最近一次崩溃：{Path.GetFileName(crash)}（点『打开日志目录』查看）");
        }
        _status.Text = lines.ToString();
    }
}
