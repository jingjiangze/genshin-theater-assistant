// ─────────────────────────────────────────────────────────────────────────────
// 本文件参考了 BetterGI（https://github.com/babalae/better-genshin-impact）的
// MaskWindowDrawingLayer 实现。原版权：Copyright (c) babalae 及 BetterGI contributors（GPL-3.0）。
// 上游文件：BetterGenshinImpact/View/Mask/MaskWindowDrawingLayer.cs
// 本项目在 WPF + .NET 8 下重写，修改时间 2026-11-09。以 GPL-3.0 分发，见 LICENSE / NOTICE.md。
// ─────────────────────────────────────────────────────────────────────────────
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace TheaterAssistant;

/// <summary>一个识别框：坐标是**游戏帧像素**，绘制时按窗口缩放换算成 DIP。</summary>
internal sealed class MaskBox
{
    public Rect Bounds;
    public Color Color = Color.FromRgb(0x7E, 0xB8, 0xE8);
    public double Thickness = 2;
    public string Label = "";
    public Color LabelColor = Color.FromRgb(0xE8, 0xF0, 0xFF);
    /// <summary>true = 只画角括号（识别框），false = 画整框（区域 ROI）。</summary>
    public bool CornersOnly = true;
}

/// <summary>
/// 遮罩绘制层：只负责把「识别框 + 标签」画到透明窗口上。
///
/// 与上游的差异：
///   · 上游是一整套 DrawingBoard（识别框/线/文字/技能 CD + 快照去重），
///     本项目只有一个信息来源（主循环的命中框），所以只保留 rect + 标签两条路径；
///   · 上游按 metrics.PixelToUnit 换算，这里直接按 ActualWidth/游戏帧宽 求比例——
///     遮罩窗口的尺寸本来就等于游戏客户区，两者等价，代码量少一半；
///   · 画笔 / 画刷按 (颜色,线宽) 缓存，避免每帧 new（上游也是这么做的）。
/// </summary>
public sealed class MaskDrawingLayer : FrameworkElement
{
    private const int MaxCache = 64;
    private const int CornerLength = 12;

    private IReadOnlyList<MaskBox> _boxes = Array.Empty<MaskBox>();
    private int _gameWidth, _gameHeight;

    private readonly Dictionary<(Color, double), Pen> _penCache = new();
    private readonly Dictionary<Color, SolidColorBrush> _brushCache = new();
    private static readonly Typeface Face = new(new FontFamily("Microsoft YaHei UI"),
        FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public MaskDrawingLayer()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
    }

    /// <summary>设置本帧的框（游戏像素坐标）与帧尺寸。尺寸变化会触发重绘。</summary>
    internal void SetFrame(IReadOnlyList<MaskBox> boxes, int gameWidth, int gameHeight)
    {
        _boxes = boxes ?? Array.Empty<MaskBox>();
        _gameWidth = gameWidth;
        _gameHeight = gameHeight;
        InvalidateVisual();
    }

    /// <summary>当前框数量（自检断言用）。</summary>
    internal int BoxCount => _boxes.Count;

    protected override void OnRender(DrawingContext dc)
    {
        if (_boxes.Count == 0 || _gameWidth <= 0 || _gameHeight <= 0) return;
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        var sx = ActualWidth / _gameWidth;
        var sy = ActualHeight / _gameHeight;
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        foreach (var box in _boxes)
        {
            var b = box.Bounds;
            if (b.Width <= 0 || b.Height <= 0) continue;

            var r = new Rect(b.X * sx, b.Y * sy, b.Width * sx, b.Height * sy);
            var pen = GetPen(box.Color, Math.Max(1, box.Thickness));
            var soft = GetPen(Color.FromArgb(0x40, box.Color.R, box.Color.G, box.Color.B), Math.Max(3, box.Thickness + 3));

            if (box.CornersOnly)
            {
                // 先画一圈淡边（让框在亮场景里也看得见），再画四个角括号
                dc.DrawRectangle(null, soft, r);
                DrawCorners(dc, r, pen);
            }
            else
            {
                dc.DrawRectangle(null, pen, r);
            }

            if (!string.IsNullOrEmpty(box.Label))
                DrawLabel(dc, r, box, pixelsPerDip);
        }
    }

    /// <summary>四角括号（识别框的通行画法，比整框更不挡画面）。</summary>
    private static void DrawCorners(DrawingContext dc, Rect r, Pen pen)
    {
        var len = Math.Min(CornerLength, Math.Min(r.Width, r.Height) / 2);
        if (len <= 0) return;

        // 左上
        dc.DrawLine(pen, new Point(r.Left, r.Top), new Point(r.Left + len, r.Top));
        dc.DrawLine(pen, new Point(r.Left, r.Top), new Point(r.Left, r.Top + len));
        // 右上
        dc.DrawLine(pen, new Point(r.Right, r.Top), new Point(r.Right - len, r.Top));
        dc.DrawLine(pen, new Point(r.Right, r.Top), new Point(r.Right, r.Top + len));
        // 左下
        dc.DrawLine(pen, new Point(r.Left, r.Bottom), new Point(r.Left + len, r.Bottom));
        dc.DrawLine(pen, new Point(r.Left, r.Bottom), new Point(r.Left, r.Bottom - len));
        // 右下
        dc.DrawLine(pen, new Point(r.Right, r.Bottom), new Point(r.Right - len, r.Bottom));
        dc.DrawLine(pen, new Point(r.Right, r.Bottom), new Point(r.Right, r.Bottom - len));
    }

    /// <summary>标签画在框的左上角外侧（框贴顶时自动翻到框内），深底浅字保证任何场景可读。</summary>
    private void DrawLabel(DrawingContext dc, Rect r, MaskBox box, double pixelsPerDip)
    {
        var ft = new FormattedText(box.Label, CultureInfo.GetCultureInfo("zh-cn"),
            FlowDirection.LeftToRight, Face, 11, GetBrush(box.LabelColor), pixelsPerDip);

        const double padX = 5, padY = 2;
        var x = r.Left;
        var y = r.Top - ft.Height - padY * 2 - 2;
        if (y < 0) y = r.Top + 2;                       // 贴到窗口顶端时画进框里
        if (x + ft.Width + padX * 2 > ActualWidth) x = Math.Max(0, ActualWidth - ft.Width - padX * 2);

        var pill = new Rect(x, y, ft.Width + padX * 2, ft.Height + padY * 2);
        dc.DrawRoundedRectangle(GetBrush(Color.FromArgb(0xD8, 0x0A, 0x0D, 0x12)),
            GetPen(Color.FromArgb(0x66, box.Color.R, box.Color.G, box.Color.B), 1), pill, 4, 4);
        dc.DrawText(ft, new Point(pill.Left + padX, pill.Top + padY));
    }

    private Pen GetPen(Color color, double thickness)
    {
        var key = (color, thickness);
        if (_penCache.TryGetValue(key, out var pen)) return pen;
        if (_penCache.Count >= MaxCache) _penCache.Clear();

        pen = new Pen(GetBrush(color), thickness);
        pen.Freeze();
        _penCache[key] = pen;
        return pen;
    }

    private SolidColorBrush GetBrush(Color color)
    {
        if (_brushCache.TryGetValue(color, out var brush)) return brush;
        if (_brushCache.Count >= MaxCache) _brushCache.Clear();

        brush = new SolidColorBrush(color);
        brush.Freeze();
        _brushCache[color] = brush;
        return brush;
    }
}
