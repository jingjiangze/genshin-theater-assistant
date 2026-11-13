using System.Drawing;
using System.IO;
using System.Linq;
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
    // ── 识别参数（2026-11-11 审计后调）───────────────────────────────────────
    // RapidOcrNet 的 Default 是给「整张截图」调的：Padding=50 白边、ImgResize=1024 长边上限、
    // DoAngle=true 每框跑角度分类。我们用在小 ROI 上，这三项都是纯浪费：
    //   · 白边把小 ROI 面积放大约 20-40%；
    //   · 长边 1024 对 960 画布没有压缩收益；
    //   · UI 文本都是横排，角度分类（cls）每框多一次模型前向。
    // BGI 的 det 是 limit_type=max（只缩不放）+ 无白边，这里对齐。
    //
    // 但 v6 不能套 Default：RapidOcrNet 4.2.0 README「Recommended options for v6」明确要求
    // 用 RapidOcrOptions.PPOCRv6（= PythonCompat，短边自适应 736、无白边），
    // Default 的 1024 长边 + 50px 白边会把 v6 检测器饿死在低分辨率上 → 漏框/乱码。
    // 所以两个 preset 改成实例字段，由 InitializeAsync 按实际选中的模型版本决定（见下）。
    private RapidOcrNet.RapidOcrOptions _optImage = RapidOcrNet.RapidOcrOptions.Default;

    /// <summary>条带（单行小字条）：v5/latin 用 Default（长边 1024，足够读「第N幕」）；v6 必须跟 _optImage 一样切 PPOCRv6。</summary>
    private RapidOcrNet.RapidOcrOptions _optStrip = RapidOcrNet.RapidOcrOptions.Default;

    private RapidOcrNet.RapidOcr _ocr;
    private Task _initTask;
    private volatile bool _ready;
    private volatile string _status = "OCR 未就绪";

    public string Status => _status;

    /// <summary>实际启用的模型版本："v6" / "v5" / "latin"（初始化完成前为 "none"）。</summary>
    public string ModelId { get; private set; } = "none";

    public Task InitializeAsync(string modelsDir)
    {
        return _initTask ??= Task.Run(() =>
        {
            try
            {
                OcrTuning.Load(AppPaths.Data("decision-policy.json"));   // 线程/DirectML 开关来自数据
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

                // RapidOcrNet 4.2.0 的 InitModels(..., SessionOptions) 是官方公开入口
                // （见 BobLd/RapidOcrNet RapidOcr.cs）。这里注入自建 SessionOptions，
                // 从而：① 线程数可控 ② 若运行时含 DirectML EP 则自动启用 GPU 推理。
                using var sessionOptions = BuildSessionOptions();
                var ep = OcrTuning.EpStatus;

                // 降级链：每跳过/失败一段都留一条原因，最终并入 Status，避免"静默降级无痕"。
                var degrade = new List<string>();

                // 单次尝试：失败就把半初始化的实例丢掉重建，保证还能继续试下一档。
                bool TryInit(string label, string det, string cls, string rec, string key)
                {
                    try
                    {
                        ocr.InitModels(det, cls, rec, key, sessionOptions);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        degrade.Add($"{label} 初始化失败({ex.GetType().Name})");
                        try { ocr.Dispose(); } catch { }
                        ocr = new RapidOcrNet.RapidOcr();
                        return false;
                    }
                }

                // ── 1) v6（多语种）：det+rec+key 齐全，且 cls 必须存在 ──────────────
                // RapidOcrNet 对 clsPath=null 会抛 FileNotFoundException，旧代码把这个异常
                // 交给外层 catch 吞掉 → OCR 整体不可用且不回落 v5/latin。
                // 因此找不到 cls 时不能"传 null 硬上"，而是跳过 v6、按顺序继续降级。
                var inited = false;
                // ocr.model 设为 v5 时直接跳过 v6（设置页的「模型」现在真的生效）
                var preferV5 = string.Equals(OcrTuning.ModelPreference, "v5", StringComparison.OrdinalIgnoreCase);
                if (!preferV5 && File.Exists(v6Det) && File.Exists(v6Rec) && File.Exists(v6Key))
                {
                    if (File.Exists(v5Cls))
                    {
                        if (TryInit("v6", v6Det, v5Cls, v6Rec, v6Key))
                        {
                            // 全帧走 PPOCRv6 preset（短边自适应 736、无白边）——上游明确要求 v6 不能用 Default。
                            // 条带**例外**：实测同样 6 帧真 1080p，条带用 PPOCRv6 时 band OCR ≈1200–1400ms/帧，
                            // 用 Default 只要 ≈65–90ms，而「第N幕」的识别结果两者一致（act 4/6、现在做 6/6）。
                            // 条带本来就是放大 2 倍的小图（大字），不存在上游警告的「小图被饿死」问题。
                            // preset 选择是**实测**定的，不是照上游抄的（2026-11-14，100 帧真 1080p 实景 A/B）：
                            //   PPOCRv6 preset：重帧 OCR p50 736–779 / p90 1237–2839，且两次独立运行都出现
                            //                   12–15s 的离群帧、条带 p90 掉到 ~1.1s（疑似极端尺寸下的重采样）；
                            //   Default      ：重帧 OCR p50 548–592 / p90 768–955，条带 p90 86–92ms，
                            //                  质量完全相同（现在做 60/100，kind 分布逐项一致）。
                            // 上游 README 只警告「小图用 Default 会饿死检测器」，我们的帧是 960 画布全帧 OCR，
                            // 不属于那个场景。需要复验时用 TA_OCR_PRESET=ppocrv6 切回。
                            var presetV6 = string.Equals(Environment.GetEnvironmentVariable("TA_OCR_PRESET"), "ppocrv6", StringComparison.OrdinalIgnoreCase);
                            _optImage = presetV6 ? RapidOcrNet.RapidOcrOptions.PPOCRv6 : RapidOcrNet.RapidOcrOptions.Default;
                            _optStrip = RapidOcrNet.RapidOcrOptions.Default;
                            ModelId = "v6";
                            _status = $"PP-OCRv6 small（中文）| {Path.GetFileName(v6Rec)} | {ep}";
                            inited = true;
                        }
                    }
                    else
                    {
                        degrade.Add($"v6 缺 cls 分类器（{Path.GetFileName(v5Cls)}）");
                    }
                }
                else
                {
                    degrade.Add("v6 模型不全");
                }

                // ── 2) v5 中文：同样要求 cls 存在 ──────────────────────────────────
                if (!inited && File.Exists(v5Det) && File.Exists(v5RecCn) && File.Exists(v5KeyCn))
                {
                    if (File.Exists(v5Cls))
                    {
                        if (TryInit("v5", v5Det, v5Cls, v5RecCn, v5KeyCn))
                        {
                            ModelId = "v5";
                            _status = $"PP-OCRv5 中文 | {Path.GetFileName(v5RecCn)} | {ep}";
                            inited = true;
                        }
                    }
                    else
                    {
                        degrade.Add($"v5 缺 cls 分类器（{Path.GetFileName(v5Cls)}）");
                    }
                }
                else if (!inited)
                {
                    degrade.Add("v5 模型不全");
                }

                // ── 3) NuGet 自带 latin：只能做链路自检 ────────────────────────────
                if (!inited)
                {
                    try
                    {
                        ocr.InitModels(sessionOptions);
                        ModelId = "latin";
                        _status = $"latin（NuGet 内置 latin_PP-OCRv5_rec_mobile_infer.onnx，缺中文模型，降级）| {ep}";
                        inited = true;
                    }
                    catch (Exception ex)
                    {
                        degrade.Add("latin 初始化失败(" + ex.GetType().Name + ")");
                    }
                }

                if (!inited)
                {
                    _status = "OCR 初始化失败: " + string.Join(" → ", degrade);
                    _ready = false;
                    return;
                }

                if (degrade.Count > 0)
                    _status += " | 降级链: " + string.Join(" → ", degrade);

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
    /// <summary>
    /// 构造 SessionOptions：线程数 + 尝试 DirectML。
    ///
    /// DirectML 用**反射**调用 AppendExecutionProvider_DML：
    ///   · 引用的是普通 Microsoft.ML.OnnxRuntime 时，方法不存在 → 静默回退 CPU（不会编译失败、不会崩）；
    ///   · 换成 Microsoft.ML.OnnxRuntime.DirectML 包后，同一份代码自动启用 GPU（核显/独显）。
    /// 这样"加速"是可选增益，不是运行前提，也不破坏单文件离线可用。
    /// </summary>
    private static Microsoft.ML.OnnxRuntime.SessionOptions BuildSessionOptions()
    {
        var op = new Microsoft.ML.OnnxRuntime.SessionOptions();
        try
        {
            op.GraphOptimizationLevel = Microsoft.ML.OnnxRuntime.GraphOptimizationLevel.ORT_ENABLE_EXTENDED;
            // 抓屏与游戏同机：限制线程，避免 OCR 把 CPU 占满拖慢游戏本身
            var threads = OcrTuning.NumThreads;
            if (threads > 0) { op.IntraOpNumThreads = threads; op.InterOpNumThreads = Math.Max(1, threads / 2); }
        }
        catch { }

        OcrTuning.EpStatus = "CPU";
        if (!OcrTuning.EnableDirectML) return op;

        try
        {
            var m = typeof(Microsoft.ML.OnnxRuntime.SessionOptions).GetMethod(
                "AppendExecutionProvider_DML",
                new[] { typeof(int) });
            if (m == null)
            {
                OcrTuning.EpStatus = "CPU（当前包无 DirectML EP）";
                return op;
            }
            m.Invoke(op, new object[] { OcrTuning.DirectMLDeviceId });
            OcrTuning.EpStatus = $"DirectML(gpu{OcrTuning.DirectMLDeviceId})";
        }
        catch (Exception ex)
        {
            // 驱动/显卡不支持时不要崩，明确降级
            OcrTuning.EpStatus = "CPU（DirectML 不可用: " + (ex.InnerException?.GetType().Name ?? ex.GetType().Name) + "）";
        }
        return op;
    }


    /// <summary>
    /// 「参考画布」（调用方使用）：超过 960 像素的帧先等比缩到 960 内再做 OCR/匹配（BetterGI 的 1080p 参考画布尺度）。
    /// 依据（2026-11-10 实测，1920x1080 真帧）：全分辨率整帧 OCR 777ms/帧，
    /// 缩到 ~960 后约 200-250ms；而仓库回归用的 960x540 帧本来就是这个尺度，识别质量一致。
    /// 返回的 Bitmap 由调用方 Dispose；scaled=false 表示没动过、不要 Dispose。
    /// </summary>
    internal static Bitmap ReferenceCanvas(Bitmap frame, out bool scaled)
    {
        scaled = false;
        if (frame == null) return null;
        var maxSide = Math.Max(frame.Width, frame.Height);
        if (maxSide <= 960) return frame;

        var k = 960f / maxSide;
        var w = Math.Max(16, (int)(frame.Width * k));
        var h = Math.Max(16, (int)(frame.Height * k));
        var small = new Bitmap(w, h);
        using var g = Graphics.FromImage(small);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
        g.DrawImage(frame, 0, 0, w, h);
        scaled = true;
        return small;
    }


    /// <summary>诊断：打印一张图上所有 OCR 文本框（位置/分数/文本），用于确定 ROI。CLI: --ocr-dump &lt;png&gt;</summary>
    public static int DumpBoxes(string pngPath)
    {
        if (!File.Exists(pngPath)) { Cli.Say("文件不存在: " + pngPath); return 2; }
        using var svc = new OcrService();
        svc.InitializeAsync(AppPaths.ModelsRoot).GetAwaiter().GetResult();
        Cli.Say("OCR: " + svc.Status);

        using var bmp = new Bitmap(pngPath);
        using var canvas = ReferenceCanvas(bmp, out var scaled);
        var use = scaled ? canvas : bmp;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var boxes = svc.RecognizeBoxesAsync(use).GetAwaiter().GetResult() ?? new List<OcrBox>();
        sw.Stop();
        Cli.Say($"框 {boxes.Count} 个｜{sw.ElapsedMilliseconds}ms｜画布 {use.Width}x{use.Height}{(scaled ? "（已缩放）" : "")}");
        foreach (var b in boxes.OrderBy(b => b.Rect.Y).ThenBy(b => b.Rect.X))
        {
            Cli.Say($"  ({b.Rect.X,5},{b.Rect.Y,5}) {b.Rect.Width,4}x{b.Rect.Height,-3} {b.Score:0.00}  {b.Text}");
        }
        return 0;
    }

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

                var result = _ocr.Detect(sk, _optImage);
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

                var result = _ocr.Detect(sk, _optStrip);
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

                var result = _ocr.Detect(sk, _optImage);
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
