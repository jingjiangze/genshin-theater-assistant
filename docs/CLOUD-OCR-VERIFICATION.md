# 云端专用模型验证（2026-11-08 实测）

> 用户要求：**云端验证/模拟要跑针对云端环境的模型**。本文件是先审计、再实测的结论。
> 机器：Simular box，Ubuntu 6.8 / **AMD EPYC 9254 / 4 核 / 7.9G** / AVX-512（含 vnni、bf16）/ dotnet 8.0.427

## 1. 审计：云端与桌面端的差异（决定为什么不能照搬）

| 维度 | 桌面端（玩家机） | 云端 box | 结论 |
|---|---|---|---|
| 执行提供器 | CPU 或 **DirectML**（可选） | **仅 CPU**（Linux 无 DirectML） | 云端必须 `enableDirectML=false` |
| CPU | 未知（i5/R7 等） | EPYC 9254，**AVX-512**，但只 4 核且共享 | 线程要限（云端用 2），且不能把云端速度当桌面基线 |
| 模型资产 | 嵌入单文件 exe | **仓库不含 .onnx**（.gitignore 排除） | 云端必须自取（公开源 + SHA256 校验） |
| 模型选择 | v6 small 优先（更准） | v6 在 CPU 上更慢；v5 更轻 | 云端默认 v5-mobile，v6 作交叉对照 |

**关键 API 约束（实测踩到）**：`RapidOcr.InitModels(det, cls, rec, keys, op)` **对 clsPath=null 不做检查**，
会直接走到 `TextClassifier.InitModel("")` 抛 `FileNotFoundException`。v6 目录没有 cls 模型，
必须**回退复用 v5 的同一个方向分类器**（v6 官方也用这个）。

**另一条必守规则（源码注释明确）**：v6 只能用 `RapidOcrOptions.PPOCRv6`，
用它配 `Default`（Padding=50 + ImgResize=1024）会让 v6 检测器分辨率不足而漏检。

## 2. 落地

| 文件 | 作用 |
|---|---|
| `cli/OcrProbe/` | 云端可跑（只依赖 RapidOcrNet + SkiaSharp，**不依赖 System.Drawing/WinForms**）的 OCR 探针：`--model v5\|v6\|both`、`--threads`、`--limit`、`--out` |
| `data/decision-policy.cloud.json` | 云端覆盖层：`enableDirectML=false`、`numThreads=2`、默认 `v5`、`verify.minAgreementPct=80` |
| `tools/ci/cloud-ocr.sh` | 公开源下载 v5/v6 + **SHA256 校验** → 构建探针 → 跑单模型 + 交叉验证 → 出结论 |

## 3. 云端实测结果（3 张真实游戏帧，1280×720，线程 2）

| 模型 | 平均 | P95 | 非空 | options |
|---|---:|---:|---:|---|
| **v5-mobile**（云端默认） | **1197 ms/帧** | 1537 ms | 3/3 | `Default` |
| v6-small（对照） | 1366 ms/帧 | 1623 ms | 3/3 | `PPOCRv6` |

**交叉验证（字符 bigram Jaccard）**：平均 **85.5%**，最低 **84.6%**（5-team-select）→ 达 80% 阈值 → **PASS**。

## 4. 人工核对差异（证明"相似度"不是空话）

同帧两模型的实际差异，逐条看过：

| 位置 | v5 | v6 | 谁更对 |
|---|---|---|---|
| 标题 | `幻想真境剧诗**]**挑战` | `幻想真境剧诗**」**挑战` | **v6**（中文右引号） |
| 祝福等级 | `祝福等级**]**：` | `祝福等级**」**:` | **v6** |
| 等级大小写 | `LV.90` / `Lv.90` 混用 | 统一 `Lv.90` | **v6** |
| 按钮 | 漏 `下一步` | 识别出 `下一步` | **v6** |
| 数字 | `017`（应为 17） | `17` | **v6** |
| 噪声 | 多出 `中`、`品26` | 无 | **v6** |
| bullet | 保留 `• 获得120幻剧之花` | 丢 `•` | **v5** |
| 速度 | 更快（1197 vs 1366 ms） | 慢 14% | **v5** |

**结论**：v6 在中文标点/数字/一致性上更强，v5 更快且保留符号。
云端用 v5 做**回归**（快、够用），桌面端继续用 v6（准）。

## 5. 本轮修掉的两个度量/判定错误（都是"看起来有结论其实是假象"）

| # | 错误 | 现象 | 修法 |
|---:|---|---|---|
| 1 | 交叉验证用「整段文本完全相等」 | 三张图各几十行，一个标点不同即判整帧不一致 → **0/3（0%）** 的假象 | 改字符 **bigram Jaccard**，得 85.5% |
| 2 | `cloud-ocr.sh` 结论 grep 旧文案 | 已 PASS 却报「PASS(部分)：交叉验证未得出」 | 判定对齐 OcrProbe 实际输出 |

## 6. 与桌面端的关系（不夸大）

- 云端 1197 ms/帧**不能**直接换算成桌面端性能：分辨率（1280×720 vs 1920×1080）、CPU、线程数都不同。
- 桌面端此前的 ~10 s/帧来自 1080p 全帧 + 单线程路径；引入 DirectML（反射探测，包内无 EP 自动回退 CPU）后才有望接近云端水平。
- 因此：**云端负责"快回归"（模型可用性、速度趋势、两模型一致性），桌面端负责"真准确率"（真机模拟 8/8）。**

## 7. 复现命令

```bash
# 云端（Linux）
bash tools/ci/cloud-ocr.sh /workspace/theater /workspace/theater/frames-sample
# 产物：cloud-ocr-v5.json / cloud-ocr-v6.json / cloud-ocr-cross.json / cloud-ocr.log
```
