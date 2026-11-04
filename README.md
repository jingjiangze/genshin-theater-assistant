# 幻想真境剧诗 · 助手（Theater Assistant）

只做一件事：**在你打《原神》幻想真境剧诗时，识别当前第几幕 / 是否在三选一界面，并在置顶小窗里给出该幕的打法要点。**

- 只读屏幕像素（GDI 抓窗口客户区）+ 本机离线 OCR + 模板匹配
- **不注入、不读进程内存、不模拟键鼠、不联网、不登录账号**
- 只针对幻想真境剧诗，不含地图/任务/自动战斗等通用功能

> 风险说明：这一档与录屏软件同级，但仍**不是零风险**。要 100% 零风险只有不使用任何第三方程序。

## 两种形态

| 形态 | 说明 | 现状 |
|---|---|---|
| **PowerShell 版**（`theater-overlay.ps1`） | 零依赖，Win10/11 自带 PowerShell 5.1 + 系统 OCR，开箱即用 | ✅ 已实测 |
| **C# 单文件版**（`app/`） | .NET 8 + RapidOcrNet（PP-OCRv5/v6），中文识别更强，打包成单个 exe | 🚧 GitHub Actions 构建中 |

## 快速开始（PowerShell 版，无需安装任何东西）

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\theater-overlay.ps1 -SelfTest   # 先自检
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\theater-overlay.ps1              # 正式跑
```

热键：`Ctrl+Alt+H` 显隐 ／ `Ctrl+Alt+L` 点击穿透 ⇄ 可拖动 ／ `Ctrl+Alt+D` 存当前帧

常用参数：

| 参数 | 作用 |
|---|---|
| `-Roster <GOOD.json>` | 接入角色池，直接算出你能进哪一档（轻简/普通/困难/卓越/月谕） |
| `-RosterReport` | 只出报告，不开悬浮窗 |
| `-Templates templates\templates.json` | 启用模板识别（比 OCR 更稳） |
| `-Roi "0,15,100,85"` | 只看窗口上部，提速 |
| `-MatchStep 4` | 模板匹配步长，4 比 2 快约 3 倍 |
| `-Classify "第 3 幕"` | 把一段文字喂给状态机，验证锚点 |
| `-Collect -CollectSeconds 120` | 连续采样到 `samples\`，用来做模板 |

## 构建（不在本地构建，走 GitHub Actions）

- `ci.yml`：push / PR 时跑 —— PowerShell 语法检查 → 测试套件 → `dotnet build`（验证 C# 可编译）
- `release.yml`：打 `v*` tag 时跑 —— 发布单文件 exe + 便携 zip，并附 SHA256

本地复现（可选）：

```powershell
pwsh -File .\build.ps1            # PS 版：语法 + 测试 + 打包
pwsh -File .\build-exe.ps1        # C# 版：需要 .NET SDK 8+
```

## 数据从哪来

`data/season29.json` 由社区整理的赛季数据（Fandom MediaWiki 赛季模板 / BWIKI 规则页）转换而来，
包含五档难度、全部幕次、每条遭遇的抗性/限时/星章线/机制。**不是官方数据**，与游戏内不符时以游戏内为准。

`data/characters.json` 是角色 → 元素映射（129 名），用于资格计算。

## 识别原理

```
窗口定位 → GDI 抓帧 → ① 模板匹配（快、准） ② OCR（兜底）
        → 状态判定（act / choice / result） → 建议文本 → 置顶窗显示
```

模板命名约定：`act1`…`act12` 直接判幕次，`choice` 判三选一，`result` 判结算/回溯。

## 精度（实测，2026-11-05）

合成画面 + 已知坐标锚点，6 种扰动：

| 扰动 | 结果 |
|---|---|
| 原图 / 位移(+1,+2) | 全部满分命中，坐标误差 ≤ 2px |
| 亮度 ×0.90 / ×1.08 | 0.927 – 0.991，仍命中 |
| JPEG q=60 / 噪点 σ=6 | 0.981 – 0.989，仍命中 |
| **缩放 ×1.10** | **0.78 – 0.87，失配**（模板必须与游戏分辨率一致） |

缺失标记时最高分 0.66 → 建议阈值 **0.80**（默认 0.86 偏严，亮度变化时会漏）。

## 许可

- 代码：MIT（见 `LICENSE`）
- 依赖：RapidOcrNet / PP-OCR 模型 = Apache-2.0
- 游戏内容与数据：归 miHoYo 所有；本仓库只做玩家自用的辅助显示
