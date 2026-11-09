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
