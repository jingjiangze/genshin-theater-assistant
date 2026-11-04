using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace TheaterAssistant;

/// <summary>
/// 攻略窗：左侧清单（当前幕优先），右侧内嵌播放器/网页。
/// 只加载公开页面（B站官方嵌入播放器、站内搜索页、BWIKI）；不注入脚本、不读 cookie、不登录。
/// WebView2 运行时缺失时自动降级为系统浏览器打开。
/// </summary>
internal sealed class GuideWindow : Form
{
    private readonly ListBox _list = new();
    private readonly Label _status = new();
    private readonly Button _openExternal = new();
    private readonly WebView2 _web = new();

    private List<GuideItem> _items = new();
    private bool _webReady;
    private bool _allowClose;

    public GuideWindow()
    {
        Text = "剧诗攻略";
        StartPosition = FormStartPosition.Manual;
        Location = new Point(120, 120);
        Size = new Size(980, 620);
        MinimumSize = new Size(640, 420);
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(16, 18, 26);
        ForeColor = Color.FromArgb(226, 232, 246);
        Font = new Font("Microsoft YaHei UI", 9f);

        _status.Dock = DockStyle.Top;
        _status.Height = 26;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Padding = new Padding(10, 0, 10, 0);
        _status.Text = "正在初始化嵌入播放器…";

        _openExternal.Text = "在浏览器打开";
        _openExternal.Dock = DockStyle.Bottom;
        _openExternal.Height = 30;
        _openExternal.FlatStyle = FlatStyle.Flat;
        _openExternal.Click += (_, _) => OpenExternal();

        _list.Dock = DockStyle.Top;
        _list.Height = 132;
        _list.IntegralHeight = false;
        _list.BackColor = Color.FromArgb(22, 26, 36);
        _list.ForeColor = Color.FromArgb(226, 232, 246);
        _list.BorderStyle = BorderStyle.None;
        _list.DoubleClick += (_, _) => OpenSelected();
        _list.SelectedIndexChanged += (_, _) => { if (_list.Focused) OpenSelected(); };

        _web.Dock = DockStyle.Fill;
        _web.DefaultBackgroundColor = Color.FromArgb(16, 18, 26);

        Controls.Add(_web);
        Controls.Add(_list);
        Controls.Add(_status);
        Controls.Add(_openExternal);

        Load += async (_, _) => await InitWebAsync();
    }

    public void ShowFor(int? act)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "data", "guides.json");
        _items = GuideService.Load(path, act);
        _list.Items.Clear();
        foreach (var it in _items) _list.Items.Add(FormatLine(it));
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;

        Text = act.HasValue ? $"剧诗攻略 · 第 {act} 幕" : "剧诗攻略";
        Show();
        BringToFront();

        if (_webReady && _items.Count > 0) _web.CoreWebView2.Navigate(_items[0].Url);
        else if (_items.Count > 0 && !_webReady)
            _status.Text = "嵌入播放器未就绪（清单仍可点，点选后用系统浏览器打开）";
    }

    private static string FormatLine(GuideItem it) =>
        it.Kind switch
        {
            "video" => "▶ " + it.Title,
            "search" => "🔍 " + it.Title,
            _ => "🌐 " + it.Title
        };

    private async Task InitWebAsync()
    {
        try
        {
            await _web.EnsureCoreWebView2Async(null);
            _web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _web.CoreWebView2.Settings.IsZoomControlEnabled = true;
            _webReady = true;
            _status.Text = "嵌入式播放器就绪 · 点清单切换 · WebView2 Runtime 153+";
        }
        catch (Exception ex)
        {
            _webReady = false;
            _status.Text = "WebView2 不可用，改用系统浏览器：" + ex.Message;
        }
    }

    private void OpenSelected()
    {
        if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _items.Count) return;
        var it = _items[_list.SelectedIndex];

        if (_webReady)
        {
            _web.CoreWebView2.Navigate(it.Url);
            _status.Text = it.Embed ? "内嵌播放：" + it.Title : "页面：" + it.Title;
        }
        else
        {
            Process.Start(new ProcessStartInfo(it.Url) { UseShellExecute = true });
        }
    }

    private void OpenExternal()
    {
        if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _items.Count) return;
        Process.Start(new ProcessStartInfo(_items[_list.SelectedIndex].Url) { UseShellExecute = true });
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    public void Shutdown()
    {
        _allowClose = true;
        try { _web.Dispose(); } catch { }
        Close();
    }
}
