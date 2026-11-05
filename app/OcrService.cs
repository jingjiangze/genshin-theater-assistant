using System.Drawing;
using System.Drawing.Imaging;
using SkiaSharp;

namespace TheaterAssistant;

/// <summary>一条 OCR 结果：文字 + 帧内轴对齐包围盒（像素）+ 检测置信度。</summary>
internal sealed class OcrBox
{
    public string Text = "";
    public Rectangle Rect;
    public double Score;

    public override string ToString() => $"{Text} @({Rect.X},{Rect.Y} {Rect.Width}x{Rect.Height}) {Score:0.00}";
}

/// <summary>
/// OCR 适配层：RapidOcrNet（Apache-2.0，PP-OCRv5/v6 ONNX，只用 CPU/DirectML）。
/// 模型优先级：
///   1) models\v6\PP-OCRv6_{det,rec}_small.onnx + ppocrv6_small_dict.txt（多语种，含中文，约 31MB）
///   2) models\v5\ch_PP-OCRv5_rec_mobile_infer.onnx + ppocrv5_dict.txt（中文专用，约 10MB）
///   3) NuGet 自带的 PP-OCRv5 latin（无中文，只作链路自检）
/// 缺模型时自动降级为"仅模板匹配"，不会崩。
/// </summary>
internal sealed class OcrService : IDisposable
{
    private RapidOcrNet.RapidOcr _ocr;
    private Task _initTask;
    private volatile bool _ready;
    private volatile string _status = "OCR 未就绪";

    public string Status => _status;

    public Task InitializeAsync(string modelsDir)
    {
        return _initTask ??= Task.Run(() =>
        {
            try
            {
                modelsDir = ExtractEmbeddedModels() ?? modelsDir;
                var v6Det = Path.Combine(modelsDir, "v6", "PP-OCRv6_det_small.onnx");
                var v6Rec = Path.Combine(modelsDir, "v6", "PP-OCRv6_rec_small.onnx");
                var v6Key = Path.Combine(modelsDir, "v6", "ppocrv6_small_dict.txt");

                var v5Dir = Path.Combine(modelsDir, "v5");
                var v5Det = Path.Combine(v5Dir, "ch_PP-OCRv5_mobile_det.onnx");
                var v5Cls = Path.Combine(v5Dir, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");
                var v5RecCn = Path.Combine(v5Dir, "ch_PP-OCRv5_rec_mobile_infer.onnx");
                var v5KeyCn = Path.Combine(v5Dir, "ppocrv5_dict.txt");

                var ocr = new RapidOcrNet.RapidOcr();

                if (File.Exists(v6Det) && File.Exists(v6Rec) && File.Exists(v6Key))
                {
                    ocr.InitModels(v6Det, File.Exists(v5Cls) ? v5Cls : null, v6Rec, v6Key);
                    _status = "OCR: PP-OCRv6 small（中文）";
                }
                else if (File.Exists(v5Det) && File.Exists(v5RecCn) && File.Exists(v5KeyCn))
                {
                    ocr.InitModels(v5Det, v5Cls, v5RecCn, v5KeyCn);
                    _status = "OCR: PP-OCRv5 中文";
                }
                else
                {
                    ocr.InitModels(); // NuGet 自带 latin 模型，只能做链路自检
                    _status = "OCR: latin（缺中文模型，降级）";
                }

                _ocr = ocr;
                _ready = true;
            }
            catch (Exception ex)
            {
                _status = "OCR 初始化失败: " + ex.Message;
                _ready = false;
            }
        });
    }

    /// <summary>
    /// 把嵌入在 exe 里的模型解压到 %LOCALAPPDATA%\TheaterAssistant\models，
    /// 返回模型根目录；没有嵌入资源时返回 null（调用方回退到 exe 同级目录）。
    /// </summary>
    private static string ExtractEmbeddedModels()
    {
        try
        {
            var asm = typeof(OcrService).Assembly;
            var res = asm.GetManifestResourceNames()
                .Where(n => n.Contains(".models.", StringComparison.OrdinalIgnoreCase)
                            && (n.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)
                                || n.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (res.Count == 0) return null;

            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TheaterAssistant", "models");

            foreach (var name in res)
            {
                var idx = name.IndexOf(".models.", StringComparison.OrdinalIgnoreCase);
                var rel = name[(idx + ".models.".Length)..];      // 例：v5.ch_PP-OCRv5_rec_mobile_infer.onnx
                var dot = rel.IndexOf('.');
                if (dot <= 0) continue;

                var dir = Path.Combine(root, rel[..dot]);
                var file = rel[(dot + 1)..];
                var dest = Path.Combine(dir, file);
                Directory.CreateDirectory(dir);

                using var stream = asm.GetManifestResourceStream(name);
                if (stream == null) continue;

                // 已存在且大小一致就跳过，避免每次启动都写盘
                if (File.Exists(dest) && new FileInfo(dest).Length == stream.Length) continue;

                using var fs = File.Create(dest);
                stream.CopyTo(fs);
            }

            return root;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>识别整帧文字；未就绪/失败返回 null（调用方按"无 OCR"处理）。</summary>
    public Task<string> RecognizeAsync(Bitmap frame)
    {
        if (!_ready || _ocr == null) return Task.FromResult<string>(null);

        return Task.Run(() =>
        {
            try
            {
                using var ms = new MemoryStream();
                frame.Save(ms, ImageFormat.Png);
                ms.Position = 0;

                using var sk = SKBitmap.Decode(ms.ToArray());
                if (sk == null) return null;

                var result = _ocr.Detect(sk, RapidOcrNet.RapidOcrOptions.Default);
                return result?.StrRes ?? "";
            }
            catch
            {
                return null;
            }
        });
    }

    /// <summary>
    /// 只识别一个相对区域（0~1），并可放大若干倍——用于「第N幕」这种小字。
    /// 区域相对整个窗口，天然适配任意分辨率。
    /// </summary>
    public Task<string> RecognizeRegionAsync(Bitmap frame, RectangleF relative, int scale = 2)
    {
        if (!_ready || _ocr == null) return Task.FromResult<string>(null);

        return Task.Run(() =>
        {
            try
            {
                int x = (int)(frame.Width * relative.X);
                int y = (int)(frame.Height * relative.Y);
                int w = Math.Max(8, (int)(frame.Width * relative.Width));
                int h = Math.Max(8, (int)(frame.Height * relative.Height));
                if (x + w > frame.Width) w = frame.Width - x;
                if (y + h > frame.Height) h = frame.Height - y;
                if (w <= 0 || h <= 0) return null;

                using var crop = new Bitmap(w, h);
                using (var g0 = Graphics.FromImage(crop))
                {
                    g0.DrawImage(frame, new Rectangle(0, 0, w, h), new Rectangle(x, y, w, h), GraphicsUnit.Pixel);
                }
                using var scaled = new Bitmap(Math.Max(8, w * scale), Math.Max(8, h * scale));
                using (var g = Graphics.FromImage(scaled))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(crop, 0, 0, scaled.Width, scaled.Height);
                }

                using var ms = new MemoryStream();
                scaled.Save(ms, ImageFormat.Png);
                ms.Position = 0;
                using var sk = SKBitmap.Decode(ms.ToArray());
                if (sk == null) return null;

                var result = _ocr.Detect(sk, RapidOcrNet.RapidOcrOptions.Default);
                return result?.StrRes ?? "";
            }
            catch (Exception ex) { return "OCRERR:" + ex.Message; }
        });
    }

    /// <summary>
    /// 返回带坐标的 OCR 结果（批量模板工厂用）。
    /// 可先裁相对区域再放大识别，最后把坐标按同一变换折回整帧坐标系，
    /// 因此「小字放大后识别」也能得到正确的帧内矩形。
    /// 未就绪/失败返回 null。
    /// </summary>
    public Task<List<OcrBox>> RecognizeBoxesAsync(Bitmap frame, RectangleF? region = null, int scale = 1)
    {
        if (!_ready || _ocr == null || frame == null) return Task.FromResult<List<OcrBox>>(null);

        return Task.Run(() =>
        {
            Bitmap crop = null, scaled = null;
            try
            {
                int ox = 0, oy = 0;
                var src = frame;

                if (region.HasValue)
                {
                    ox = (int)(frame.Width * region.Value.X);
                    oy = (int)(frame.Height * region.Value.Y);
                    int w = Math.Max(8, (int)(frame.Width * region.Value.Width));
                    int h = Math.Max(8, (int)(frame.Height * region.Value.Height));
                    if (ox + w > frame.Width) w = frame.Width - ox;
                    if (oy + h > frame.Height) h = frame.Height - oy;
                    if (w <= 0 || h <= 0) return null;

                    crop = new Bitmap(w, h);
                    using (var g0 = Graphics.FromImage(crop))
                    {
                        g0.DrawImage(frame, new Rectangle(0, 0, w, h), new Rectangle(ox, oy, w, h), GraphicsUnit.Pixel);
                    }
                    src = crop;
                }

                if (scale > 1)
                {
                    scaled = new Bitmap(src.Width * scale, src.Height * scale);
                    using (var g = Graphics.FromImage(scaled))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(src, 0, 0, scaled.Width, scaled.Height);
                    }
                    src = scaled;
                }

                using var ms = new MemoryStream();
                src.Save(ms, ImageFormat.Png);
                using var sk = SKBitmap.Decode(ms.ToArray());
                if (sk == null) return null;

                var result = _ocr.Detect(sk, RapidOcrNet.RapidOcrOptions.Default);
                var list = new List<OcrBox>();
                if (result?.TextBlocks == null) return list;

                foreach (var block in result.TextBlocks)
                {
                    var pts = block.BoxPoints;
                    if (pts == null || pts.Length == 0 || string.IsNullOrWhiteSpace(block.Text)) continue;

                    int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
                    foreach (var p in pts)
                    {
                        if (p.X < minX) minX = p.X;
                        if (p.Y < minY) minY = p.Y;
                        if (p.X > maxX) maxX = p.X;
                        if (p.Y > maxY) maxY = p.Y;
                    }

                    // 放大/偏移的逆变换：折回整帧坐标系
                    int x = ox + (int)Math.Round(minX / (double)scale);
                    int y = oy + (int)Math.Round(minY / (double)scale);
                    int w = (int)Math.Round((maxX - minX) / (double)scale);
                    int h = (int)Math.Round((maxY - minY) / (double)scale);
                    if (w <= 0 || h <= 0) continue;

                    list.Add(new OcrBox { Text = block.Text.Trim(), Rect = new Rectangle(x, y, w, h), Score = block.BoxScore });
                }

                return list;
            }
            catch
            {
                return null;
            }
            finally
            {
                scaled?.Dispose();
                crop?.Dispose();
            }
        });
    }

    public void Dispose()
    {
        try { _ocr?.Dispose(); } catch { }
        _ocr = null;
        _ready = false;
    }
}
