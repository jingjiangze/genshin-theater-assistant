# NOTICE · 第三方来源与许可证声明

本项目的**整体许可证**是 **GNU General Public License v3.0 or later（GPL-3.0-or-later）**（见仓库根 `LICENSE`，sha256 `3972dc9744f6499f0f9b2dbf76696f2ae7ad8af9b23dde66d6af86c9dfb36986`，与 gnu.org GPL-3 原文一致）。

## 为什么是 GPL-3.0

本项目在实现上参考/衍生了 **BetterGI（更好的原神）** 的部分实现，而 BetterGI 采用 GPL-3.0。
GPL-3.0 具有传染性，因此本项目整体以 GPL-3.0 分发。

历史上本项目曾使用 MIT（原文留档于 `docs/legal/LICENSE-MIT-original.txt`），
自 2026-11 起为合规改为 GPL-3.0。

## 上游项目

| 项目 | 地址 | 许可证 | 本项目对它的使用方式 |
|---|---|---|---|
| **BetterGI（更好的原神）** | https://github.com/babalae/better-genshin-impact | **GPL-3.0** | 参考并衍生其界面结构、OCR 后处理口径、识别阈值策略与异常处理结构 |
| RapidOcrNet | https://github.com/BobLd/RapidOcrNet | Apache-2.0 | 作为 NuGet 依赖使用（PP-OCR ONNX 推理） |
| Genshin Optimizer (GOOD 格式) | https://github.com/frzyc/genshin-optimizer | MIT | 仅采用 **GOOD v1 数据格式**（格式规范本身不受版权保护） |
| Enka.Network | https://enka.network | 公开 API | 只读查询角色展柜数据 |

### BetterGI 衍生说明

原版权：Copyright (c) babalae 及 BetterGI contributors

以下文件包含对 BetterGI 实现的参考或衍生（各文件头部亦有标注）：

- `app/OcrTextPipeline.cs` —— 置信度过滤（DropScore=0.5）与「同排按 X 排序」的文本框后处理口径
- `app/OcrTuning.cs` —— OCR 线程/执行提供器的可配置化思路
- `app/OverlayForm.cs` —— 悬浮窗的窗口样式（NOACTIVATE / TRANSPARENT / TOOLWINDOW）与热键组织方式
- `app/Recognition.cs` —— 多尺度模板匹配的阈值策略
- `app/Routine.cs` —— 按幕组织角色轮换的数据结构
- `app/SelfTest.cs` —— 断言式自检的组织方式

### BetterGI 遮罩层移植（v0.61，2026-11-09）

WPF 版的游戏内遮罩直接按 BetterGI 的遮罩结构移植（用户要求「照抄它的 UI 遮罩」）：

| 本项目文件 | 上游文件 | 移植内容 | 差异 |
|---|---|---|---|
| `app-wpf/MaskWindow.xaml(.cs)` | `View/MaskWindow.xaml.cs` + `View/MaskWindow.xaml` | 窗口样式（TRANSPARENT/NOACTIVATE/TOOLWINDOW）、防截屏、日志面板结构 | 不挂 `WS_CHILD` 到游戏窗口（不改变其他进程窗口树）；不引入上游的 MVVM/DI/地图点位 |
| `app-wpf/MaskWindowHost.cs` | `View/Mask/MaskWindowHost.cs` | 「只上报事实 → 合并投递 Apply」的显隐/置顶/跟随策略 | 去掉配置服务与日志框架；显隐策略抽成纯函数 `MaskPolicy.Decide` 以便断言 |
| `app-wpf/MaskDrawingLayer.cs` | `View/Mask/MaskWindowDrawingLayer.cs` | 按帧像素→窗口比例绘制、画笔/画刷按 (颜色,线宽) 缓存、标签底衬 | 只保留 rect + 标签（上游是识别框/线/文字/技能 CD 全套快照） |
| `app-wpf/OverlayLogDocument.cs` | `Helpers/OverlayLogDocument.cs` | 200 inline / 10000 字符上限、`Dispatcher.BeginInvoke(Background)` 单次合并维护、`BeginChange/EndChange` 批量裁剪 | 增加 `MaintainNow()` 供无消息泵的自检确定性断言 |

这些文件均为 WPF + .NET 8 下的改写，不是上游同一份代码；修改时间 2026-11。

**修改说明**：上述文件均由本项目在 WinForms + .NET 8 技术栈下重写或改写，
与 BetterGI 的 WPF 实现并非同一份代码；修改时间为 2026-11。

## 本项目的独立实现（非衍生）

以下部分为本项目原创，不来自 BetterGI：

- 决策层：`QuantData` / `QuantScorer` / `TeamSolver` / `GlobalAllocator` / `CuratedPicks` / `CharacterStrength` / `DecisionOrchestrator` / `QualityGates` / `DecisionBridge`
- 数据层：`SeasonRegistry` / `Playbook` / `DecisionPolicy` / `RosterResolver` / `RosterMerge` / `EnkaClient`
- 云端管线：`tools/pipeline/*`、`tools/video-frames/*`、`tools/ci/*`
- 知识治理：证据去重、赛季周期隔离、固定 holdout 晋级、不可变 release

## 合规义务提醒

以 GPL-3.0-or-later 分发意味着：

1. 分发二进制时**必须提供对应源码**（本项目源码公开在 GitHub，满足此条）；
2. 必须保留本 NOTICE 与各文件头部的来源声明；
3. 修改过的文件需标注修改事实与时间；
4. 不得附加额外限制（如禁止商用、强制署名以外的限制）。

## 安全红线（与许可证无关，但必须声明）

本项目**只做只读像素采集与离线识别**，明确不使用：

`OpenProcess` / `ReadProcessMemory` / `WriteProcessMemory` / `CreateRemoteThread` /
`SendInput` / `keybd_event` / `mouse_event` / `SetWindowsHookEx`

以上 8 项在代码库中 **0 命中**，CI 中有对应检查。
