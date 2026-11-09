using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;

namespace TheaterAssistant;

/// <summary>
/// 设置窗口（WPF 版）。页面分类照抄 BetterGI 的导航结构：
/// 通用 / 识别 / 悬浮窗 / 角色池 / 诊断。
/// 所有项直接读写 data/*.json（单一数据源），保存后重启生效。
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        LoadAll();
        RefreshDiag();

        SaveBtn.Click += (_, _) => { SaveAll(); Close(); };
        OpenConfigBtn.Click += (_, _) => OpenFolder(AppPaths.Dir);
        OpenLogBtn.Click += (_, _) => OpenFolder(App.LogDir);
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

    private static JsonNode ReadNode(string name)
    {
        try
        {
            var p = AppPaths.Data(name);
            return File.Exists(p) ? JsonNode.Parse(File.ReadAllText(p)) : null;
        }
        catch { return null; }
    }

    private void LoadAll()
    {
        try
        {
            var ui = ReadNode("ui.json");
            IntervalSlider.Value = ui?["scanIntervalMs"]?.GetValue<int>() ?? 1000;
            OpacitySlider.Value = ui?["opacityPct"]?.GetValue<int>() ?? 96;
            FontSlider.Value = ui?["bodyFontPt"]?.GetValue<int>() ?? 12;
            ShowSourceCheck.IsChecked = ui?["showSourceMark"]?.GetValue<bool>() ?? true;
            HideWhenMissingCheck.IsChecked = ui?["hideWhenGameMissing"]?.GetValue<bool>() ?? true;

            var pol = ReadNode("decision-policy.json");
            OcrModelBox.SelectedIndex = (pol?["ocr"]?["model"]?.GetValue<string>() ?? "v6") switch
            {
                "v5" => 1, "auto" => 2, _ => 0,
            };

            var llm = ReadNode("llm.json");
            LlmCheck.IsChecked = llm?["enabled"]?.GetValue<bool>() ?? true;

            var uidPath = Path.Combine(AppPaths.Dir, "uid.txt");
            if (File.Exists(uidPath)) UidBox.Text = File.ReadAllText(uidPath).Trim();
        }
        catch { }
    }

    private void SaveAll()
    {
        try
        {
            File.WriteAllText(Path.Combine(AppPaths.Dir, "ui.json"), new JsonObject
            {
                ["scanIntervalMs"] = (int)IntervalSlider.Value,
                ["opacityPct"] = (int)OpacitySlider.Value,
                ["bodyFontPt"] = (int)FontSlider.Value,
                ["showSourceMark"] = ShowSourceCheck.IsChecked == true,
                ["hideWhenGameMissing"] = HideWhenMissingCheck.IsChecked == true,
            }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            var polPath = AppPaths.Data("decision-policy.json");
            if (File.Exists(polPath))
            {
                var pol = JsonNode.Parse(File.ReadAllText(polPath)) as JsonObject ?? new JsonObject();
                if (pol["ocr"] is not JsonObject ocr) pol["ocr"] = ocr = new JsonObject();
                ocr["model"] = OcrModelBox.SelectedIndex switch { 1 => "v5", 2 => "auto", _ => "v6" };
                File.WriteAllText(polPath, pol.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }

            var llmPath = AppPaths.Data("llm.json");
            if (File.Exists(llmPath))
            {
                var llm = JsonNode.Parse(File.ReadAllText(llmPath)) as JsonObject ?? new JsonObject();
                llm["enabled"] = LlmCheck.IsChecked == true;
                File.WriteAllText(llmPath, llm.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }

            var uid = (UidBox.Text ?? "").Trim();
            if (uid.Length == 9) File.WriteAllText(Path.Combine(AppPaths.Dir, "uid.txt"), uid);
        }
        catch (Exception ex)
        {
            MessageBox.Show("保存失败：" + ex.Message, "剧诗助手", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RefreshDiag()
    {
        var b = new StringBuilder();
        b.AppendLine("— 运行状态（只读）—");
        b.AppendLine();
        b.AppendLine($"配置目录：{AppPaths.Dir}");
        b.AppendLine($"数据目录：{AppPaths.Data(".")}");
        b.AppendLine();
        b.AppendLine($"决策层：{(DecisionBridge.Ready ? "已就绪" : "未就绪")}");
        b.AppendLine($"角色池：{DecisionBridge.PoolNote}");
        b.AppendLine();
        b.AppendLine($"OCR 线程：{OcrTuning.NumThreads}（0=自动）");
        b.AppendLine($"OCR 执行提供器：{OcrTuning.EpStatus}");
        b.AppendLine();
        try
        {
            if (Directory.Exists(App.LogDir))
            {
                var files = Directory.GetFiles(App.LogDir, "crash-*.log");
                b.AppendLine(files.Length == 0
                    ? "崩溃日志：无（说明没崩过）"
                    : $"最近崩溃：{Path.GetFileName(files[^1])}（共 {files.Length} 个）");
            }
            else b.AppendLine("崩溃日志：目录尚未创建（说明没崩过）");
        }
        catch { }
        DiagText.Text = b.ToString();
    }
}
