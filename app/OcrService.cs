using System.Drawing;
using System.Drawing.Imaging;
using SkiaSharp;

namespace TheaterAssistant;

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

                using var crop = frame.Clone(new Rectangle(x, y, w, h), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
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
            catch { return null; }
        });
    }

    public void Dispose()
    {
        try { _ocr?.Dispose(); } catch { }
        _ocr = null;
        _ready = false;
    }
}
