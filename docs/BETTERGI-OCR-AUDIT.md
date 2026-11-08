# BetterGI OCR/识别实现审计（2026-11-08）

> 目的：用户要求"OCR 和识别照抄 BetterGI 的实现"。本文件是**先审计再动手**的结论。
> 被测版本：`babalae/better-genshin-impact` @ `a9e08fc`（1959 个 .cs 文件）
> 方法：只读源码，不运行其程序。

## 1. 它的 OCR 架构

| 层 | 实现 | 位置 |
|---|---|---|
| 门面 | `OcrFactory`，OCR 是**可插拔能力**（Pluggable），默认 PP-OCRv6，可切 YasModel/YapModel | `Core/Recognition/OCR/OcrFactory.cs` |
| 引擎 | `PaddleOcrService`（检测 `PpOcrDetV6` + 识别 `PpOcrRecV6`，`slim.onnx`） | `OCR/Paddle/PaddleOcrService.cs` |
| 模型元信息 | 从模型目录的 **`inference.yml`** 读标签表 | `PaddleOcrService.DefaultRecLabelFunc` |
| 运行后端 | ONNX Runtime，可 CPU/DirectML，CPU 有专门调参（intra/inter-op、spinning） | `Core/Recognition/ONNX/*` |

## 2. 识别流水线（逐环节，含实测参数）

```
图 → 检测模型 RunBoxes → SortBoxes → 每个框 GetRotateCropImage → 批量识别 → 按 DropScore 过滤 → OcrResultRegion[]
```

| 环节 | 它的做法 | 参数/细节 |
|---|---|---|
| 检测 | `_localDetModel.RunBoxes(src)` | 返回四点框 `Point2f[4]` |
| **排序** | 先按 `Points[0].Y` 再按 `X`；再做**同排归并**：若相邻两框 Y 差 **< 10px**，则按 X 交换 | 这是"多列同一行"的关键，我们目前只按 OCR 输出顺序 |
| **裁剪** | `GetRotateCropImage`：**透视变换**（`GetPerspectiveTransform` + `WarpPerspective`，Cubic + Replicate），宽高取对边长 | 我们目前是轴对齐矩形裁剪，遇到倾斜/缩放会偏 |
| **竖排** | 裁出的图若 `高/宽 ≥ 1.5`，`Rotate90Counterclockwise` 转正 | 我们完全没做 |
| **置信度过滤** | `DropScore = 0.5f`，`region.Score >= DropScore` 才保留 | 我们未做置信度过滤 |
| 批量识别 | `_localRecModel.Run(mats[], batchSize)` | 批处理，减少推理次数 |
| 并发 | `lock (_inferenceLock)` 串行推理；批量同锁内完成 | 避免 ONNX 并发问题 |

## 3. 业务层怎么用 OCR（比引擎更值得抄）

| 模式 | 它的做法 | 对我们的意义 |
|---|---|---|
| **先模板定锚，再在锚内 OCR** | `BvOcr.FindFKeyText`：先在 `ImageRegion` 找图标模板 → 用命中矩形 + `scale` 算文字子矩形 → 才 OCR | 与我们"模板命中就跳过条带"同思路，但它**用 scale 换算**，支持任意分辨率 |
| **白字掩膜提取** | `Cv2.InRange(roi, (230,230,230), (255,255,255), whiteMask)` 提白字再做包围盒 | 我们缺：文字有描边/彩底时纯 OCR 会漏 |
| **坐标基准 1920×1080** | 全项目以 1080P 为基准，`ScaleTo1080PRatio` 换算 | 我们按 `img.Width` 动态找模板目录，思路等价但**没有统一基准比例** |
| 按区域分阈值 | `OcrResultAssembler.Accept(regions, nameThreshold, dialogueThreshold)` 不同区域用不同阈值 | 我们只有单一 `DropScore` 概念（且未实现） |

## 4. 与我们现状的差异（可执行清单）

| # | 差异 | 影响 | 优先级 |
|---:|---|---|---|
| D1 | 未做**置信度过滤** | 低分幻觉文本混入（我们现有噪声过滤是字符串规则，不是置信度） | **P0** |
| D2 | 未做**同排归并排序**（Y 差 < 10px 按 X 排） | 同一行多卡片（商店/三选一）顺序可能错，导致"第N张"选错 | **P0** |
| D3 | 未做**竖排旋转**（高/宽 ≥1.5） | 竖排文字整段识别失败 | P1 |
| D4 | 未做**透视裁剪**（只用轴对齐矩形） | 缩放/倾斜时裁偏 | P1 |
| D5 | 未做**白字掩膜** | 彩底/描边文字漏识别 | P1 |
| D6 | 无统一 **1080P 基准 + 比例** | 多分辨率下区域定位靠每处手算 | P2 |
| D7 | 未做**分区域阈值** | 名称/正文用同一阈值，误拒或误收 | P2 |

## 5. 不该照抄的部分（理由）

| 它的做法 | 为什么不抄 |
|---|---|
| 可插拔引擎 + 运行时下载 ONNX Runtime 包 | 我们是单文件 exe + 内置模型，引入插件系统会破坏"零依赖、不联网也能用" |
| `YasModel/YapModel`（YOLO 辅助） | 与幻想剧诗场景无关，且要额外模型体积 |
| DirectML/GPU 后端 | 我们要低占用、避免抢游戏 GPU |
| 大量 MAA 式脚本引擎 | 与"只做幻想真辅"的定位冲突 |

## 6. 本轮落地结论（按上表 P0）

**先做 D1 + D2（都能纯逻辑实现、可断言、不依赖模型重训）**：
- `OcrTextPipeline.FilterByScore`：按阈值过滤（默认 0.5，与 BetterGI 一致，可配置）
- `OcrTextPipeline.OrderSameLine`：Y 差 < 10px 视为同一排，按 X 排序（阈值同样可配置）

D3–D7 需要引入 OpenCV（`Cv2.WarpPerspective`/`InRange`），单文件体积与依赖会增加，
列为下一轮评估项，不在本轮擅自引入。

## 7. 验证方式

- 纯函数单测（合成坐标数据）+ 现有 50 视频产物的顺序回归
- 断言：同排不同 X 输入乱序 → 输出按 X；Y 差 ≥10 → 保持原相对顺序
- 断言：score < 阈值被丢弃；阈值来自 `decision-policy.json`（不硬编码）
