# BetterGI 审计结论与采纳决定（2026-11-10）

> 对象：`_tmp/bgi-src`（BetterGI 0.66.3-alpha.1，GPL-3.0）。
> 目的：把"照抄"落到具体文件，并明确哪些**不能**抄（红线）与哪些**不必**抄（它有而我们更强）。
> 状态词：`ADOPTED` 已落地 / `PLANNED` 计划内 / `REJECTED` 不采纳（附理由）。

## 一、UI 体系（观感来源）

| 结论 | 证据（上游） | 我们的处置 |
|---|---|---|
| 观感九成来自 **WPF-UI 4.3.0**（主题 + 全套控件模板），本地几乎不写主题，只覆盖圆角 6 / 页边距 | `App.xaml:13-19,25-26`；`Wpf.Ui.csproj:5`（支持 net8.0-windows） | `ADOPTED`：`app-wpf/TheaterAssistantWpf.csproj` 引 `WPF-UI 4.3.0`；`App.xaml` 合并 Dark 主题 + ControlsDictionary |
| 设置页 = `FluentWindow` + 自绘 `TitleBar` + `ui:NavigationView`；页面切换动画是 **FadeInWithSlide 200ms**（上游默认） | `View/MainWindow.xaml:23,75-276,397-422`；上游 `TransitionAnimationProvider.cs:88-121` | `ADOPTED`（自实现同参数）：`SettingsWindow.xaml` 用 FluentWindow + 左导航；`ShowPage` 做 200ms 淡入 + Y 12→0，`CubicEase` |
| 左侧选中指示条是 662 行的 Adorner 行为（280ms 弹性） | `View/Behavior/AnimatedNavigationSelectionIndicatorBehavior.cs:24-28,499-571` | `ADOPTED`（简化 60 行）：`SlideIndicator` 240ms 缓动滑到当前项。不搬 Adorner（依赖 `Microsoft.Xaml.Behaviors.Wpf` 且要处理跨层级） |
| 窗口淡入只在两个浮窗有（120ms 线性） | `PictureInPictureWindow.xaml.cs:56-63` | `PLANNED`：遮罩已用"显隐跟随"替代淡入，避免频繁闪烁 |
| 字体是 MiSans + Deluge LED + FluentSystemIcons（9 MB） | `App.xaml:21-22`、`Resources/Fonts/` | `REJECTED`：沿用系统「微软雅黑 UI」，省 9 MB（单文件 exe 体积是分发约束） |
| 遮罩/日志面板 | `View/MaskWindow.xaml:288-330` + `Helpers/OverlayLogDocument.cs` | `ADOPTED`：见上一轮（`MaskWindow`/`OverlayLogDocument`，差异见 `NOTICE.md`） |

**不采纳**：`I18n.TExtension`（1837 处本地化）、`ServiceLocator` 标记扩展、`DpiAwareness` 控制器（我们 manifest 已 PerMonitorV2，叠它会二次缩放，`app-wpf/app.manifest:6-9`）。

## 二、识别体系（对原神画面的取法）

| 主题 | BetterGI 做法 | 我们的处置 |
|---|---|---|
| 抓屏 | 五种后端（BitBlt/WGC/DWM/WGC-HDR/WGCv2），默认 BitBlt；`GetDC(hWnd)` 取窗口自身画面，**不怕被遮挡**；失败无跨后端自动降级 | `ADOPTED`（自写同思路）：`ScreenCapture.CaptureByBitBlt` 优先，抽样检测全黑则回落屏幕拷贝。**未搬** Fischless 子模块（其许可证离线无法确认，写进 PENDING） |
| 抓屏自愈 | 首次失败重建 session；每 1s 检查客户区变化重建；上层再 3×30ms 重试 | `PLANNED`：我们每帧重取 Rect，重建成本低，暂不做 session 池 |
| 注册表改写 | Win11 上改 `HKCU\\...\\UserGpuPreferences` 让 BitBlt 可用 | `REJECTED`：改全机游戏设置，超出"只读助手"边界 |
| 区域换算 | 1920×1080 参考画布 + `AssetScale` + `ReferenceRect`（anchor）+ JSON DSL | `ADOPTED`（部分）：`data/modes.json` 的 `band` 用帧比例（分辨率无关），并保留"整帧 1080p 归一"的口径；DSL 不引 |
| 模板匹配 | OpenCV `MatchTemplate`（CCoeffNormed 0.8）+ 色键 mask + NMS(IoU 0.5)；**单尺度**（加载期缩一次） | `PLANNED`：我们自写灰度 RMSE + 4 档缩放。收益在"去掉多尺度粗扫"（4K 下省一半时间），需先量化准确率损失 |
| OCR 引擎 | PaddleOCR det+rec（ONNX，PP-OCRv6），det `limit_side_len=960` 只缩不放 | 我们 RapidOcrNet（同为 PP-OCR ONNX）→ 无必要替换 |
| OCR 后处理 | 左上角 Y/X 排序 + 相邻（<10px）插入交换；`DropScore=0.5`；**replace 纠错字典**（幕/莫/暮） | `ADOPTED`：主循环现已走 `OcrTextPipeline.ToLines`（此前生产路径未接线，属真缺陷）；纠错字典 `PLANNED` |
| 界面判定 | 三类模板（Talk/BigMap/Main）每帧重判；UI 类别变化后 30s 内所有触发器跑一遍 | `REJECTED`：这是"自动任务调度"概念，我们不做自动操作 |
| 时序稳定 | 只有"画面静止"检测（缩 320×180，CCoeff≥0.98 连续 2 帧），**没有**幕次单调稳定性 | 保留自有 `ActStabilizer`（倒退需连续确认 + 时间弃权）——BGI 这块更弱，不必照抄 |
| 输入/执行层 | `Core/Simulator`、`SendInput`、JS 脚本、键鼠宏 | `REJECTED`：红线（本项目不注入、不模拟输入） |

## 三、本轮因此改掉的三个真问题

1. **抓屏被遮挡就抓到别人的像素**（旧实现 `CopyFromScreen(ClientToScreen)`）→ 改 BitBlt 优先。
2. **`OcrTextPipeline` 生产路径未接线**（只在 CLI/自检里跑）→ 主循环改用 `RecognizeBoxesAsync` + `ToLines`。
3. **「选第几张」在生产路径从未生效**（`Context.Options` 恒为空）→ 主循环真读选项并把 `teamSize/target` 传进编排器。

## 四、PENDING

- `Fischless.GameCapture` 子模块许可证未确认（离线查不到）→ 我们没有搬它的代码，只自写同思路实现。
- 16:10 / 21:9 下条带 ROI 的实际落点未真机验证（改了 `modes.json` 即可调，不用改代码）。
- 单尺度匹配的准确率损失未量化（`PLANNED` 前置：先跑 `--quant` 对比）。

## 五、GPU 优先审计与执行（2026-11-10 深夜追加）

### 1) 三条 GPU 路径的可行性（实测）

| 路径 | 结果 | 证据 |
|---|---|---|
| **torch XPU（Intel Arc，oneAPI）** | ✅ **打通** | `torch 2.15.0+xpu` → `xpu avail: True, device=Intel(R) Arc(TM) B580 Graphics`；serve.py 自动选 `torch-xpu`+bf16 |
| DirectML（OCR 用） | ❌ 阻塞 | `Microsoft.ML.OnnxRuntime.DirectML` 最高 **1.24.4**，RapidOcrNet 4.2.0 需 **1.29.0** 托管程序集 → 版本不匹配（nuget flatcontainer 实测） |
| Intel OpenVINO EP（OCR 用） | ❌ 阻塞 | `intel.ml.onnxruntime.openvino` 最高 **1.24.1**，同上 |
| OpenVINO IR（LLM 用） | ⏸ 版本三角 | optimum-intel ↔ datasets ↔ huggingface-hub（transformers 5.x 要 hub<2.0、datasets 要 2.x） |

**结论：OCR 暂时只能 CPU**（两个 GPU EP 都落后于 ORT 1.29）；LLM 走 torch XPU 已可用。

### 2) XPU 轮子获取配方（官方 index 会卡死，R2 CDN 可用）

```
https://download-r2.pytorch.org/whl/xpu/torch-2.15.0%2Bxpu-cp312-cp312-win_amd64.whl   # 781,337,330 B
# 8 路 range 并行 + 逐块补断点（单连接 ~200KB/s，R2 并发实测 ~5MB/s）
# 装完还要补 oneAPI 运行库，否则 c10_xpu.dll 报 WinError 126：
intel-cmplr-lib-rt/-ur/-lic-rt==2026.1.2、intel-sycl-rt==2026.1.2、onemkl-sycl-{blas,dft,lapack,rng,sparse}==2026.1.0
```
注意：`ThreadingHTTPServer` 默认 allow_reuse_address，**同端口会双开两个实例**（新版与旧版各占一个）→ 排查时先 `netstat -ano | grep :17915` 确认单实例。

### 3) 速度实测（26M 模型，64 tok）

| 配置 | 热态延迟 | 说明 |
|---|---|---|
| CPU fp32 | 919ms | 基准 |
| **XPU bf16** | **795–1487ms（中位 ~1.0s）** | 与 CPU 持平：26M 是 launch-bound，不是算力-bound |
| XPU + fp8 weight-only | 1092ms（**+20%**） | 输出逐字一致；torchao 在 XPU 上仍是逐 op 解量化 |
| CPU + fp8 / int8 | 2877 / 1839ms | 4x / 2x 慢 |

**fp8 结论（CPU 与 GPU 都试过）：弃用**。收益前提是后端有融合 fp8 GEMM，torchao weight-only 不满足。

### 4) 真正的收益：模型让出 CPU → OCR 提速 2.4x

| 场景（30 帧回放，在环） | 识别步均 | 说明 |
|---|---|---|
| ◇模型在 CPU（同机） | 2771ms | LLM 与 OCR 抢 CPU |
| **◇模型在 XPU** | **1160ms** | CPU 全给 OCR；wall 4622ms/帧，◇在环 30/30 |

叠加此前 `numThreads=6`（P 核）钉核，本机单帧从最初的 **8s → 1.16s（约 7x）**。

## 六、内嵌小模型 + 端侧延迟审计（2026-11-10）

用户要求：模型**内嵌或可选下载**、**延迟太高**（端侧小模型）、**看性能占用**。

### 1) 三条内嵌路线的实测结论

| 路线 | 结果 | 证据 |
|---|---|---|
| LLamaSharp（进程内 P/Invoke 封装） | ❌ **否决（4 轮 CI）** | NuGet 0.30 包公开 API 与源码 tag 不符（`WithSearchDirectories`/`WithLibrary` 均 CS1061）；原生探测要求 `LLamaSharpRuntimes/win-x64/native/<avx>/`，单文件与裸 publish 均拿不到（`No LLamaSharp backend was installed`） |
| 随包 **llama-server 子进程** | ✅ **采用** | 官方二进制（llama.cpp b4400 avx2），应用首用拉起、退出即杀；实测 59+64 token = **147ms** |
| 外部 Python HTTP（上一轮方案） | ⚠ 降为备选 | 985–1487ms，且要求用户装 Python 环境——与「内嵌」目标冲突 |

**落地**：`native/llama-server/`（llama-server.exe + llama/ggml/llava_shared 共 ~5MB）+ `models/llm/minimind-26m-q8.gguf`（26.4MB）随包；`LocalBrain` 负责端口探测（18923 起，被占顺延、已健康则复用）、`/completion` 调用、退出清理。

GGUF 转出链路（含两个坑）：
1. llama.cpp 上游 `convert_hf_to_gguf.py` 已拆包 → 用自带完整实现的 tag **b4400**；
2. **transformers 5.x 把 RoPE 写成 `rope_parameters`**，旧转换器只读 `rope_theta` → 生成的 GGUF 缺 `llama.rope.freq_base`（默认 1e4 vs 实际 1e6）→ 生成空串。补回 `rope_theta` 后输出正确。

### 2) 延迟（26M Q8，本机 i5-13400F）

| 项 | 数值 | 对比 |
|---|---|---|
| 模型加载（一次） | **35ms** | — |
| prompt 59 token | **19.7ms**（3402 tok/s） | — |
| 生成 64 token | **49.5ms**（1273 tok/s） | torch CPU 64 token 919ms |
| HTTP 端到端（热） | **147ms** | Python 服务 985–1487ms |
| 冷启动（含起进程+加载） | 532ms（本机）/ 3299ms（4 核 CI） | — |
| 回放单帧识别步（中位） | **418ms** | 原 2771ms（HTTP 模型抢 CPU）/ 最初 8000ms |
| 回放整帧 wall（含 ◇） | **859ms/帧** | 原 4694ms |

### 3) 资源占用

| 进程 | RSS | 说明 |
|---|---|---|
| llama-server（Q8 26M + 1K ctx） | **52.9MB** | 常驻；应用退出即回收 |
| OCR 线程 | 6（P 核） | 混合架构钉核，见 §68 |

### 4) OCR 延迟优化（用户点名「参考现有项目」）

- **帧指纹跳过**：32×32 采样哈希（≈1ms），静态界面（选卡/结算/暂停）整帧复用上一帧结果，省掉整帧 OCR。
- **全帧 OCR 节拍 3 → 4 帧**（条带 OCR 仍每帧）。
- 保留 `numThreads=6`（P 核）——这是单帧从 5.0s→1.56s 的主因（§68）。

### 5) 验证

| 验证 | 结果 |
|---|---|
| CI（WPF 自检） | **14/14 PASS**，含「内嵌模型可用」；`llama-server 就绪｜3299ms｜<正确输出>` |
| 真机 `--selftest-live --llm`（内嵌） | **11/11 PASS**，编排来源=◇模型，冷启动 532ms |
| 真机 `--replay --llm`（30 帧，内嵌） | **◇在环 30/30**、现在做 18/30、检索 30/30、识别步中位 418ms |

### 6.1 视频模拟实测（真 1080p / 123 帧，2026-11-10 收尾）

**发现**：仓库回归用的 `testdata/ocr-frames` 实为 **960×540**（文件名里的 1920×1080 是源分辨率），
所以 CI 的 e2e「1080p 时序回归」名不副实。真 1080p 帧来自 `_tmp/local-train-20261107/frames-all`（BV1eExm6dEmt 共 123 帧），
本机按时间序重命名（零填充）后作为真 1080p 基准。

| 阶段 | 单帧总耗时（123 帧均值 / 步中位） | 说明 |
|---|---|---|
| 优化前（全分辨率 OCR + 全分辨率精匹配） | 2953ms 均值 / p50 2925ms | 模板匹配占 2176ms（74%）、OCR 777ms |
| **参考画布**（OCR ≤1100 长边；Matcher 半分辨率画布） | **906ms 均值 / p50 704ms / p90 982ms** | 3.3x；准确率不降反升（见下） |

**准确率没有变差，幕次序列反而更连贯**：

| 指标 | 优化前 | 优化后 |
|---|---|---|
| 现在做 | 68/123（unknown 55 帧 = 非剧诗画面，不产生建议）| 68/123（**识别到剧诗界面的 68 帧全部给建议 = 100%**）|
| 检索命中 | 123/123 | 123/123 |
| 原始幕次序列 | 2→4→5→10→7→3→9（跳变 5） | 2→3→4→10→7→8→9（跳变 2） |
| 稳定后序列 | 2→7（跳变 1） | 2→3→4→7→8→9（跳变 1） |
| ◇模型在环 | 119/119（含 team/UI 帧，语境不对） | **17/17（只 act/choice）** |

**界面帧单测**（大堂/难度/开幕队伍/待命/选人/合成帧，6 张）：
`kind` 全部正确（theater×1、difficulty×1、team×4）、现在做 6/6、检索 6/6、◇ 按设计不调用（纯 UI 交规则+检索）。

**结论**：◇ 的职责边界收敛为「幕内指令 + 三选一」；队伍/难度/大堂这类界面答案来自 playbook 确定性文本，
模型插嘴只会给出语境错的答案（实测 team 帧被回 t1 式「优先带行秋」）。

### 6.2 1080p 提速：照 BetterGI 的实现改（2026-11-10/11）

用户口径：**「当前速度还是太慢，看其他的是怎么运行的实现的」**。逐项对照 BetterGI 的做法后，分三步改：

| 步 | 改动 | 依据（BetterGI 怎么做） |
|---|---|---|
| ① 换匹配引擎 | 自写灰度 RMSE → **OpenCV `MatchTemplate`(CCoeffNormed)**，`OpenCvSharp4` 双项目引入；原生库不可用时自动回退旧实现 | BGI 用 `Cv2.MatchTemplate`（阈值 0.8、色键 mask、NMS），不自己写像素循环 |
| ② 参考画布 | 帧按宽度归一（>960 缩到 960），模板同比缩放，命中框再映射回原分辨率 | BGI 把一切归一到 1920×1080 参考画布（`ScaleTo1080PRatio`），模板按分辨率目录预缩放 |
| ③ 只算区域 | 模板按名字取 **ROI**（`data/template-rois.json`，act 徽章限定底部条带）；OCR 从**全帧**改为**选项区 ROI**（0.10–0.95 × 0.22–0.82 ≈ 36% 面积） | BGI 每个识别对象自带 `rect(...)`/`ReferenceRect`，从不全帧扫描；OCR 也只跑 ROI |

**实测（123 帧真 1080p 视频，步耗时 = 单帧 OCR+匹配+决策）**：

| 版本 | 单帧均值 | 说明 |
|---|---|---|
| 自写 RMSE 全帧全分辨率 | 2953ms | 模板匹配 2176ms（74%） |
| + 参考画布（OCR≤1100 / 匹配半分辨率） | 906ms | — |
| + **OpenCV 引擎 + ROI** | **639ms** | act 检测 10 帧不变（召回没丢） |
| + **参考画布 960 + 条带放大 2x** | **568ms** | — |
| + 选项区 ROI + 自适应节拍 | 待复测 | 代码已落（见下） |

**为什么还不到 BGI 的 20fps 水平**：BGI 的 20Hz 只是「触发器轮询」，真正重的识别（OCR/多模板）也是低频；
我们现在是「1s 节拍 + 单次决策 ~500ms」，属于**同量级但不是同一指标**。要继续压只能：
① OCR 直接吃 DirectML/OpenVINO EP（包版本与 ORT 1.29 不匹配，已审计否决）；
② 把 26M 的 det/rec 换成更小的专用模型（BGI 是 PP-OCRv6 det/rec small，我们 v5 mobile 同档）。

**CI 1080p 门（2026-11-11 上线）**：仓库新增 `testdata/frames1080`（6 张真 1080p，13MB，覆盖 act/team/choice/theater/unknown），
`ci` 新增步骤「1080p 真实帧回归（耗时门）」——打印 `总耗时` 并断言 **≤1200ms/帧（2026-11-14 收紧）**。
首次运行（v5 期数据；GitHub runner 4 核 + OCR 6 线程，存在超订）：**1025ms/帧**，通过。
这条门的意义：窗口化 1080p 是常态分辨率，此前的 CI 回归只覆盖 960×540，1080p 的性能退化没人拦。

**本地时间线（123 帧真 1080p，i5-13400F / 6 线程 OCR）**：

```
自写 RMSE 全帧全分辨率     2953ms
+ 参考画布（1100/半分辨率）  906ms
+ OpenCV 引擎 + act ROI     639ms
+ 画布 960 + 条带 2x        568ms   ← 已实测
+ 选项区 ROI + 自适应节拍    代码已落（CI 绿），本地复测待下载产物（CDN 限速）
```

**与 BGI 的差距在哪**：BGI 的 20Hz 是「触发器轮询」频率，重识别同样低频；它的单次识别便宜是因为
①每个对象只在自己的 rect 里做 ②OpenCV 的 matchTemplate 是 SIMD ③OCR 只跑小 ROI。
我们三步都做了；剩下的差距主要是 **OCR 引擎本身**（RapidOcrNet v5 mobile vs BGI 的 PP-OCRv6 small）
和 **CPU 线程超订**（CI runner 4 核跑 6 线程）。要让单帧进 300ms 级，需要 GPU EP（已被 ORT 版本卡住）
或换更小的 det/rec 模型。

### 6.3 「BGI 的 OCR 为什么快」审计 + 参数对齐（2026-11-11）

用户问：BGI 只 OCR 小 ROI，延迟很低，怎么做到的？

**审计（读 RapidOcrNet 4.2.0 源码 + BGI PaddleOcr 实现）**：

| 项 | BGI | 我们（改之前） |
|---|---|---|
| det 缩放 | `limit_side_len=960` + **`limitType=max`（只缩不放）**，无白边 | `RapidOcrOptions.Default`：**Padding=50 白边**、`ImgResize=1024` 长边上限 |
| 角度分类 | 按需（`use_angle_cls` 可关） | `DoAngle=true` → **每个框都跑一次 cls 前向** |
| rec | 批处理 `batch=min(8,核数)` | RapidOcrNet 内部逐框 |
| 调用开销 | 只在触发器的对象 rect 上跑 | 之前是整帧（已在上一步改成 ROI） |

**结论**：`RapidOcrOptions.Default` 是给「整张截图」调的一组参数——白边把小 ROI 面积放大 20–40%，
`DoAngle` 在多框场景每个框多一次模型前向。这两项在小 ROI 上是纯浪费。

**执行**（`app/OcrService.cs` 两套预设，`record with`）：

```csharp
OptImage = Default with { Padding = 0, ImgResize = 960, DoAngle = false };  // 整帧/选项区
OptStrip = Default with { Padding = 0, ImgResize = 640, DoAngle = false };  // 条带（单行小字）
```

**实测（CI runner 4 核，同硬件对比，1080p 6 帧门）**：

| 版本 | 1080p 单帧 |
|---|---|
| OpenCV + ROI + 960 画布 | 1025ms（v5 期） |
| **+ det 参数对齐（无白边/960/无 cls）** | **529ms** |

**1.9 倍**，且「真机模拟实测 10/10」「WPF 自检 14/14」全过（OCR 能力未被参数调整削弱）。

**还剩的差距**：BGI 的 rec 是**批处理**（一次前向处理多个文本框），RapidOcrNet 是逐框；
要再压就得绕开 RapidOcrNet 自己组 rec 批（成本高、收益约 -80~120ms/帧），或换 v6 small 模型。

### 6.4 「BGI 的 OCR 为什么快」追到底：三个真因（2026-11-11）

**① 参数只占小头（已试并回退）**
`RapidOcrOptions.Default` 是给整张截图调的（Padding=50 白边、ImgResize=1024、DoAngle 每框一次 cls）。
去掉白边/关 cls 后单次确实快（-40%），但**实测质量掉**（现在做 68→52、team 44→11）——因为
剧诗界面判类靠文本关键词，缩小画布/条带会把小字读丢。**已回退到 Default**。

**② 真正的大头是「每帧都跑重 OCR」（测量口径错了）**
回放此前每帧都跑整帧 OCR；生产 `MainLoop` 本来就是「条带每帧 + 重 OCR 每 2/8 帧」。
让回放与生产同节拍后（本次改动：`--pace` 模拟 1s tick、重 OCR 每 2/8 帧、段落文本跨帧缓存）：

| 指标（123 帧真 1080p，本机 13400F） | 每帧重 OCR | **生产节拍** |
|---|---|---|
| 单帧 total p50 | 4117ms | **274ms** |
| 单帧 total p90 | 5065ms | 1933ms（重帧） |
| 现在做覆盖 | 68/123 | **89/123**（缓存文本让队伍界面判得更准）|
| 检索命中 | 123/123 | 123/123 |

**③ 连续满载会把整机拖慢（不是算法问题）**
同一份代码：3 帧短跑 ocr 400–650ms；长跑第 4 帧起 ocr 2–5s，而且**连纯 OpenCV 的 match 也从 50ms 涨到 300ms**
（match 与内容无关）→ 判定为持续满载下的降频/调度退化。这也是为什么必须用「生产节拍 + pace」测量：
否则测的是压测值，不是产品值。

**结论**：BGI 快不是因为它单次 OCR 有多神，而是它**每个触发器只在自己的小 rect 上、且按需触发**。
我们现在做到的是：条带每帧（~90ms）+ 重 OCR 1/2 帧（未定幕）或 1/8 帧（已稳定），
1080p 稳态 **p50 274ms**（从 2953ms 降 10.7x），质量还升了（68→89 帧有建议）。

**新增诊断工具**：`--ocr-dump <png>` 打印一张图的全部文本框（位置/分数/文本），用于定 ROI；
`--pace <ms>` 模拟真机节拍；`TA_TIMING=1` 打印每帧 match/ocr/band 分段耗时。

### 6.5 视觉验收（三轮，2026-11-12）

用 `verify-visual`（原生多模态，只读）对 8 张渲染图做验收，按像素证据迭代：

| 轮 | 结果 | 被点出的问题（摘要） |
|---|---|---|
| R1 | 遮罩 **0/4**、其它 **2/4** | ① 幕次条带蓝框被面板压掉 26%（开口矩形）② 默认态多一行且被「…」截断 ③ 框线 1px/α0.13-0.25 在亮景里近乎消失，与 2px 不透明蓝框不一致 ④ 日志底部 2px 半截行 ⑤ 日志停在顶部看不到最新 ⑥ 设置「蓝条在首页、选中在悬浮窗」⑦ 桌宠气泡纯白且与角色间 120px 断层 ⑧ 悬浮球发光被画布截断成方形 |
| R2 | 遮罩 0/4、其它 3/4 | 自检夹具里**还在喂条带框**（生产已删、夹具没改 → 图里必然有）；柔光写的是 `0x55` 被读成"55%"，实为 33%；日志滚动因为 `Render()` 重新 Measure/Arrange 被重置；桌宠自检仍按 360 渲染 |
| R3 | 遮罩 **2/4**、桌宠 **PASS** | 默认态与合成态通过；展开态仍停在第 250–257 行（最新 299 不可见）——根因是 `SetLogExpanded(true)` 后没有触发滚动（折叠时 ScrollToEnd 是空操作） |
| R4（终验） | **3/3 PASS** | 日志 291–299（末行=最新）、手法行无「…」、桌宠 260×300 且描边为连续实边 |

**沉淀的规则**（写进设计文档）：
1. 遮罩上**不画内部 ROI**（条带框）——玩家要的是指令，ROI 是诊断信息；
2. RichTextBox 滚到最新必须 **插入符移到 ContentEnd + ScrollToEnd**，且**展开时也要滚一次**；自检渲染前再滚一次（重排会重置）；
3. 柔光/描边用十六进制 alpha 时，注释里把百分比写对（`0x8C=55%`，`0x55=33%`）；
4. 自检夹具必须与生产路径同源（条带框、手法截断都要走同一个函数，否则验收的是"另一份代码"）。

### 6.6 换 PP-OCRv6 small（2026-11-13）

上一轮结论是「要再压只能绕开 RapidOcrNet 自组 rec 批或换 v6 small」。两条都摸了：

| 路径 | 结论 |
|---|---|
| 自组 det/rec 批 | **不可行（公开 API 不足）**：RapidOcrNet 只暴露 `Detect(bitmap, options)` / `DetectBoxes(...)`，rec 类 `TextRecognizer` 不公开、也没有「给定框做 rec」的入口；要批处理只能反射进内部对象，版本一升就碎 |
| **换 PP-OCRv6 small** | **可行且直接生效**：`OcrService` 本来就优先 v6（`models/v6` 存在即用），只需把模型取下来 |

**模型**（modelscope RapidAI/RapidOCR v3.10.0，SHA256 校验通过）：
`PP-OCRv6_det_small.onnx` 9.5MB · `PP-OCRv6_rec_small.onnx` 20.3MB · `ppocrv6_small_dict.txt` 0.1MB

**实测（123 帧真 1080p，生产节拍 pace=700）**：

| 模型 | total p50 | p90 | max | 现在做 | kind 分布 |
|---|---|---|---|---|---|
| PP-OCRv5 mobile | 240–274ms | 800–2000ms | ~2.9s | 89/123 | team62/unknown34/act15/theater12 |
| **PP-OCRv6 small** | **146ms** | **619ms** | **1012ms** | **89/123（不变）** | **完全相同** |

OCR 自身 p90 从 ~1.5s 降到 **465ms**（检测框数 p50 9，v5 是 14–16）。
即：**同一质量下，最坏帧从 ~2.9s 降到 ~1.0s，中位从 ~270ms 降到 146ms**。

CI 也改成取 v6（`fetch-models.ps1 -V6`，ci 与 e2e 一致），缓存键改 `ocr-models-v56-`。

#### 6.6.1 重 OCR 节拍能不能调密（TA_HEAVY）

v6 把单次 OCR 压到 ~0.5s 后，自然要问：能不能把「稳定后每 8 帧重扫」调成 4 或 2？
两者都加了 `TA_HEAVY` 环境变量口子（`MainLoop` 与 `ReplayRunner` 同一语义），实测 100 帧
**游戏内实景** 1080p（video-dense，框数最多 41 个），v6 small，pace=700：

| TA_HEAVY | 重扫帧数 | total p50 | total p90 | 重帧 OCR p50/p90 | 给出现在做 |
|---|---|---|---|---|---|
| **8（默认）** | 14/100 | **103–128ms** | 549–619ms | 512 / 687ms | **60/100** |
| 4 | 26/100 | 589ms（冷） | 4159ms（冷） | 2796 / 5623ms（冷） | 52/100 |
| 2 | 50/100 | 465–509ms | 760–815ms | 514–550 / 677–765ms | 54/100 |

（注：那一轮落在冷跑区间，见下；数字偏大，不作为结论）

结论：**默认保持 8，不调密**。理由不是慢——2 反而更慢（p50 从 ~110 涨到 ~480ms，等于每帧多付 ~190ms 摊销）——
而是**判得更差**：重扫越密，「现在做」从 60/100 掉到 52–54/100。原因是多扫到的帧里有一半是
菜单切换/动画中间态，把稳定的文本缓存覆盖成噪声，决策层于是弃权。**更多 OCR ≠ 更好，是采样噪声更多。**

方法论补充（踩过）：**冷跑第一轮不能采信**。同一批帧、同一参数，首轮重帧 OCR 出现 3–8s，
隔几分钟重跑即降到 0.5–0.7s（杀软扫描新构建的 DLL / 文件缓存全冷）。所有数字都要跑两轮取第二轮；
这次首轮 8607ms 与复跑 790ms 是同一帧、同一模型。

#### 6.6.2 OCR preset：v6 模型到底该配哪个 preset（2026-11-14 实测改默认）

上游 RapidOcrNet README 要求 v6 配 `RapidOcrOptions.PPOCRv6`（`Default` 会"饿死"小图检测器）。
我们按上游改成 PPOCRv6 后，在 100 帧真 1080p 实景上做了 A/B（`TA_OCR_PRESET=ppocrv6|default`）：

| preset（全帧 OCR） | 重帧 OCR p50 / p90 / max | 条带 OCR p90 | 质量（现在做 / kind 分布） |
|---|---|---|---|
| PPOCRv6 | 736–779 / 1237–2839 / 12100–13854ms | 1065–1138ms | 60/100；team44/unknown40/theater10/act6 |
| **Default** | **548–592 / 768–955 / 2247–4823ms** | **86–92ms** | **完全一致** |

两次独立运行里 PPOCRv6 都出现 12–15s 的离群帧，条带（小图）也慢 12 倍 —— 与我们这种
「960 画布全帧 OCR + 条带放大 2 倍」的用法相反，上游的警告场景是小字检测，不是这里。
**结论：默认 Default，`TA_OCR_PRESET=ppocrv6` 保留给复验。** 这条与上游口径相反，是实测定的。

同时修掉：条带 OCR 与全帧 OCR 各自选 preset（条带恒 Default）、v6 缺 cls 时不再传 null
（旧行为是 `FileNotFoundException` 被吞掉 → OCR 整体不可用且不回落 v5/latin）。
