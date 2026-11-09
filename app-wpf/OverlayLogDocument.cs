// ─────────────────────────────────────────────────────────────────────────────
// 本文件是 BetterGI（https://github.com/babalae/better-genshin-impact）的衍生实现。
// 原版权：Copyright (c) babalae 及 BetterGI contributors（GPL-3.0）。
// 上游文件：BetterGenshinImpact/Helpers/OverlayLogDocument.cs
// 本项目在 WPF + .NET 8 下移植，修改时间 2026-11-09。
// 本文件以 GPL-3.0 分发，全文见仓库根 LICENSE，来源说明见 NOTICE.md。
// ─────────────────────────────────────────────────────────────────────────────
using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;

namespace TheaterAssistant;

/// <summary>
/// 合并式悬浮日志文档维护（照抄 BetterGI 的 OverlayLogDocument）。
///
/// 为什么必须有它：悬浮日志每帧都在追加，如果直接订阅 TextChanged 并在事件里裁剪，
/// 会形成「插入 → 裁剪 → 再次触发 TextChanged」的递归，高帧率下直接把 UI 线程拖死。
/// 上游的做法是：只在 TextChanged 里投递**一次** Background 优先级维护，
/// 在一次 BeginChange/EndChange 里批量保留尾部内容，最后统一滚动。
///
/// 差异（与上游）：
///   · 上限沿用 200 inline / 10000 字符，但本项目每条日志是「时间 + 级别 + 正文」三个 inline，
///     所以实际可见行数约 55 行——足够，且内存恒定；
///   · 增加 <see cref="MaintainNow"/>，让自检能在没有消息泵的情况下确定性验证裁剪。
/// </summary>
public sealed class OverlayLogDocument : IDisposable
{
    /// <summary>沿用上游显示上限；只限制 UI，不裁剪文件日志。</summary>
    public const int MaximumInlines = 200, MaximumTextCharacters = 10000;

    private readonly RichTextBox _box;
    private DispatcherOperation? _pending;
    private bool _maintaining, _disposed;

    /// <summary>必须在控件所属 UI 线程创建并拥有订阅。</summary>
    public OverlayLogDocument(RichTextBox box)
    {
        box.Dispatcher.VerifyAccess();
        _box = box;
        _box.TextChanged += OnTextChanged;
    }

    /// <summary>只投递一次后台优先级维护；不在插入事件内同步修改文档。</summary>
    private void OnTextChanged(object sender, TextChangedEventArgs args)
    {
        if (_disposed || _maintaining || _pending is not null || _box.Dispatcher.HasShutdownStarted) return;
        _pending = _box.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Maintain));
    }

    /// <summary>在一个 Change 块内批量保留尾部内容，最后统一滚动，避免递归和重复布局。</summary>
    private void Maintain()
    {
        _pending = null;
        if (_disposed) return;
        _maintaining = true;
        try
        {
            _box.BeginChange();
            try
            {
                var document = _box.Document;
                if (document.Blocks.LastBlock is Paragraph last
                    && (document.Blocks.Count > 1 || last.Inlines.Count > MaximumInlines))
                {
                    // 从尾部最多取 200 项，不逐项扫描/删除整个历史文档。
                    var retained = new List<Inline>(MaximumInlines);
                    for (var item = last.Inlines.LastInline; item is not null && retained.Count < MaximumInlines; item = item.PreviousInline)
                        retained.Add(item);
                    retained.Reverse();
                    document.Blocks.Clear();
                    var paragraph = new Paragraph { Margin = last.Margin };
                    paragraph.Inlines.AddRange(retained);
                    document.Blocks.Add(paragraph);
                }

                var text = new TextRange(document.ContentStart, document.ContentEnd).Text;
                if (text.Length > MaximumTextCharacters)
                {
                    // 给段落结束符留空间；只在超限时退为纯文本，保留最新文字及完整 Unicode 字符。
                    var start = text.Length - (MaximumTextCharacters - 2);
                    if (char.IsLowSurrogate(text[start]) && start > 0 && char.IsHighSurrogate(text[start - 1])) start++;
                    document.Blocks.Clear();
                    document.Blocks.Add(new Paragraph(new Run(text[start..])));
                }
            }
            finally { _box.EndChange(); }
            _box.ScrollToEnd();
        }
        finally { _maintaining = false; }
    }

    /// <summary>
    /// 同步执行一次维护并返回尾部段落的 inline 数（自检专用）。
    /// 自检没有消息泵，Background 优先级的投递不会自己跑，所以需要一个确定性的入口。
    /// </summary>
    internal int MaintainNow()
    {
        if (_disposed) return 0;
        _pending?.Abort();
        _pending = null;
        Maintain();
        return _box.Document.Blocks.LastBlock is Paragraph p ? p.Inlines.Count : 0;
    }

    /// <summary>窗口关闭时退订并撤销唯一待办，迟到回调也会检查已释放状态。</summary>
    public void Dispose()
    {
        _box.Dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _box.TextChanged -= OnTextChanged;
        _pending?.Abort();
        _pending = null;
    }
}
