# OCR 模型放置说明（内置进 exe 用）

RapidOcrNet 的 NuGet 只内置 **PP-OCRv5 latin** 模型（中文识别会输出乱码），
要读剧诗的中文界面，必须自己补一套中文识别模型。两条路，二选一：

## 方案 A（推荐）：PP-OCRv6 small —— 多语种，约 31 MB

放到 `models/v6/`，文件名必须完全一致：

| 文件 | 说明 |
|---|---|
| `PP-OCRv6_det_small.onnx` | 检测（DBNet） |
| `PP-OCRv6_rec_small.onnx` | 识别（含中文） |
| `ppocrv6_small_dict.txt` | 字符表，必须与 rec 模型配套 |

分类模型复用 v5 的 `ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx`（NuGet 会放在 `models/v5/`）。

## 方案 B（更小）：PP-OCRv5 中文专用 —— 约 10 MB

放到 `models/v5/`：

| 文件 | 说明 |
|---|---|
| `ch_PP-OCRv5_mobile_det.onnx` | 检测 |
| `ch_PP-OCRv5_rec_mobile_infer.onnx` | 中文识别 |
| `ppocrv5_dict.txt` | 字符表 |
| `ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx` | 方向分类（NuGet 自带） |

## 下载地址

模型清单与直链见 RapidOCR 官方：

<https://github.com/RapidAI/RapidOCR/blob/main/python/rapidocr/default_models.yaml>

按上面的文件名挑对应条目下载即可（该 yaml 由 RapidOCR 维护，URL 会随版本更新，所以本仓库不写死直链）。

## 许可

- 模型：PaddleOCR 项目，**Apache-2.0**
- 推理库：RapidOcrNet，**Apache-2.0**（依赖 SkiaSharp / Microsoft.ML.OnnxRuntime）

## 缺模型时

程序不崩：`OcrService` 会自动降级为「仅模板匹配」，悬浮窗标题会显示 `OCR: latin（缺中文模型，降级）`。
此时只能靠模板识别幕次，读不到「伙伴·随机」这类事件名。
