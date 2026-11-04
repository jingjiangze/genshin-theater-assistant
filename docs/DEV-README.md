# 幻想真境剧诗 · 状态识别悬浮窗 v0.2（BetterGI 分层参照版）

## 0. 红线（代码级，不是口头承诺）

| 层 | 做 | 不做 |
|---|---|---|
| 采集 | `Graphics.CopyFromScreen`（GDI，等价 BetterGI 的 BitBlt 后端） | 不 hook、不接管渲染、不读显存 |
| 识别 | 灰度模板匹配（纯托管 C#）+ Windows 自带离线 OCR | 不读进程内存、不读游戏文件 |
| 决策 | 本文件内的规则表 + `data\season29.json` | — |
| 显示 | WinForms 置顶 + `WS_EX_TRANSPARENT`(点击穿透) + `WS_EX_NOACTIVATE`(不抢焦点) + `WDA_EXCLUDEFROMCAPTURE`(自身不被截) | 不注入绘制 |
| 输入 | 只有 `GetAsyncKeyState` **读**热键 | **没有** `SendInput` / `PostMessage` / `keybd_event` / `mouse_event` / 手柄模拟 |

> 结论：这是「看屏幕 + 画自己的窗」，与录屏/OBS 同一档。**不是 0 风险**，只是不触碰游戏进程。要 0 风险只有不运行。

## 1. 与 BetterGI 的对应关系（参考设计，不复制代码）

| BetterGI | 本项目 | 说明 |
|---|---|---|
| `Fischless.GameCapture/BitBlt/*` | `Capture-Window` | 同一种 GDI 截图思路 |
| `Fischless.GameCapture/Graphics/*`（WGC） | **未实现** | WGC 能在窗口被遮挡时截图，但需要 C#/WinRT 项目；PS 5.1 做不了，故要求游戏在前台 |
| `Core/BgiVision/BvLocator` | `Matcher::Find` | 找图 → 判页；我们只有模板匹配，没有 YOLO |
| `Core/Recognition/ONNX/BgiYoloPredictor`（ONNX+YoloSharp） | **未实现** | 需要 ONNX Runtime + 模型；留作下一步 |
| `Core/Recognition/OCR/Paddle/*` | `Read-OcrText`（Windows.Media.Ocr） | 精度不如 PaddleOCR，但零依赖、离线 |
| `Core/Input/**`（SendInput / PostMessage / 手柄） | **整层不引入** | 这是红线 |
| License | — | BetterGI 是 **GPL-3.0**；本项目仅参考分层，不复制其代码 |

## 2. 运行

需要 **Windows PowerShell 5.1**（`powershell.exe`，不是 `pwsh`）：

```powershell
cd "C:\DDDD\Agent Work\_tmp\theater-overlay"

# 只识别、不开窗（测链路）
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\theater-overlay.ps1 -SelfTest

# 正式跑（无模板时只走 OCR）
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\theater-overlay.ps1

# 带模板
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\theater-overlay.ps1 -Templates .\templates\templates.json

# 采样（做模板用）
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\theater-overlay.ps1 -Collect -CollectSeconds 90
```

参数：`-WindowTitle`（默认 `原神`）、`-Roi "0,15,100,85"`（只用窗口上部 85%，提速）、`-IntervalMs`、`-MatchStep`（默认 2；4 更快）、`-TemplateThreshold`（默认 0.86）。

热键：`Ctrl+Alt+H` 显隐 ／ `Ctrl+Alt+L` 点击穿透⇄可拖动 ／ `Ctrl+Alt+D` 存当前帧 + OCR 文本到 `samples\`。

## 3. 模板工作流（这是 v0.2 的重点）

```powershell
# ① 采样：开着它打一场剧诗
. .\theater-overlay.ps1 -Collect -CollectSeconds 120

# ② 挑一帧，裁出"幕次文字/三选一面板/结算面板"等锚点
.\crop-template.ps1 -Source .\samples\frame-013245-120.png -X 860 -Y 120 -W 240 -H 60 -Out .\templates\act3.png

# ③ 标定阈值：看它在原帧里的匹配分
.\theater-overlay.ps1 -Learn .\samples\frame-013245-120.png -LearnTemplate .\templates\act3.png

# ④ 写进清单，正式启用
Copy-Item .\templates\templates.example.json .\templates\templates.json   # 再改名/改 threshold
.\theater-overlay.ps1 -Templates .\templates\templates.json
```

模板命名约定（会被 `Get-State` 解释）：
`act1`…`act12` → 直接判定为第 N 幕；`choice` → 三选一界面；`result` → 结算/回溯界面。

**模板要求**：必须有明显纹理（文字、图标边缘）。纯色/渐变区域会到处满分命中（实测：80×44 的空背景块在 2194×1234 帧里同时在 x=94 与 x=300 满分）。

## 4. 实测（2026-11-05，本机）

| 项 | 结果 |
|---|---|
| OCR 引擎 | `zh-Hans-CN` 创建成功，单帧 460 ms（1070 字） |
| 抓帧（2194×1234） | 568 ms（首次含 GDI 初始化） |
| 模板匹配 30×30 @ 800×600，step 2 | 35 ms，**植入标记召回 score=1.0000，坐标 (123,77) 精确命中** |
| 模板匹配 80×44 @ 2194×1234，step 2 / step 4 | **673 ms / 181 ms** |
| 默认节拍 | 1000 ms；建议：`-Roi` 收窄 + `-MatchStep 4` + 模板 ≤120×120 |

> 模板匹配已用「合成图植入唯一标记」的受控用例验证：先是 0.7758 未命中（粗扫/细化抽样口径不一致的真 bug），修正为全分辨率细化后 score=1.0000、坐标精确。这条记录留着，防止以后误以为是模板质量问题。

## 5. 已知限制

1. **游戏必须在可见位置**：GDI 截不到被遮挡窗口（实测游戏在后台时截到的是前面浏览器）。全屏独占可能截黑 → 用窗口化/窗口全屏。
2. **分辨率敏感**：模板是按当前分辨率裁的，改窗口大小/画质档位要重裁。
3. **没有 YOLO**：多目标、复杂界面（一排卡片）用模板很笨；要上 YOLO 就得引 ONNX Runtime + 模型，那是另一个量级的工程。
4. **OCR 中文会逐字插空格**（`第 一 幕`），代码已去空白比较，但仍可能被图标字符打断。
5. **幻剧之花数字 / 耐力心形未实现**：等采样到真实 UI 再做（数字可用「模板数字切图」，心形可用色块计数）。

## 6. 目录

```
theater-overlay/
├─ theater-overlay.ps1        # 主程序（单文件，零外部依赖）
├─ crop-template.ps1          # 从截图裁模板
├─ data\season29.json         # 第 29 期数据（5 档难度全部幕次/遭遇）
├─ templates\                 # 模板 + templates.json
├─ samples\                   # 采样帧 / Ctrl+Alt+D 的 dump
├─ logs\                      # 每帧 OCR 日志（kind/act/src/hits/原文）
└─ build-data.mjs             # 由上位数据重新生成 season29.json
```
---

## 7. 角色池接入（v0.2 新增）

```powershell
# 报告模式：算出你能进哪一档（不启动悬浮窗）
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\theater-overlay.ps1 -Roster .\samples\roster\big.json -RosterReport

# 悬浮窗模式：每帧末尾多一行「池子 N 人：最高可进【卓越】」
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\theater-overlay.ps1 -Templates .\templates\templates.json -Roster .\samples\roster\big.json
```

- 输入格式：**GOOD JSON**（`{"format":"GOOD","characters":[{"key":"Yelan","level":90},...]}`），来源可以是 Inventory_Kamera（MIT）、Irminsul，或你自己手写的清单。
- 资格公式（对齐 BWIKI）：当期元素内且等级达标的角色 + 开幕里你没有的（试用算名额）+ 特邀里你有的 + 旅行者 1。
- 名字归一：GOOD 的官方 key 与本地表不一致时走别名（`KamisatoAyaka→Ayaka`、`AratakiItto→Itto`、`RaidenShogun→Raiden Shogun` 等）。
- **本地表里没有的新角色不计入**（宁可低估，不让你误判）。

验证用例：

| 合成池 | 期望 | 实测 |
|---|---|---|
| 小池 14 人 | 轻简可进，普通差 1 | `11 人 → 轻简；再补 1 人可进【普通】` |
| 大池 27 人 | 月谕刚好 28 | `28 人 → 月谕` |

## 8. 同类项目审计

见 `AUDIT-2026-11-05.md`：含 7 个画面识别项目、3 个幻想分析项目、1 个 BetterGI 分层拆解，以及红线 grep 结果。