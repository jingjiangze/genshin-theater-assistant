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

## 攻略接入（B站 / BWIKI / 米游社 / NGA）

悬浮窗识别出「第几幕」后，`Ctrl+Alt+G` 打开攻略窗：左侧是当前幕优先的入口清单，右侧内嵌网页/播放器。

`data/guides.json` 里三类入口：

| kind | 行为 | 例子 |
|---|---|---|
| `search` | 拼 B站站内搜索深链（按播放量排序） | `{"title":"B站：第3幕","kind":"search","keyword":"幻想真境剧诗 翠翎恐蕈 打法"}` |
| `video` | 用 **B站官方嵌入播放器** 内嵌播放 | `{"title":"某UP主讲解","kind":"video","bvid":"BV1xxxxxxxxx"}` |
| `url` | 直接打开页面（BWIKI / 米游社 / NGA） | `{"title":"BWIKI 总览","kind":"url","url":"https://..."}` |

边界（写死的行为）：

- **不抓取**页面、**不调用** B站非公开接口（不用 wbi 签名、不带 cookie）、**不登录**账号
- 嵌入仅使用官方外链播放器 `player.bilibili.com/player.html?bvid=...`（已实测返回 200）
- WebView2 Runtime 缺失时自动退回系统浏览器打开，不报错
- `guides.json` 是纯文本，可自己加 BV 号、换成别的社区链接

> 已核验：B站官方嵌入播放器可达。**未核验**（PENDING）：米游社 / NGA 的搜索 URL 结构可能变化，失效就在 `guides.json` 里改。
## 分辨率 / 无边框

- 截帧用 `GetClientRect`，**无边框窗口（Borderless）与窗口化全屏天然支持**；独占全屏不保证（GDI 读不到）。
- 模板按分辨率分桶：`templates/1920x1080/act3.png`。运行时按当前客户区宽度自动选目录，允许 ±12% 偏差；找不到就退回 `templates/` 根目录。
- 为什么必须分桶：实测把 1.0× 模板拿到 1.10× 画面上匹配，分数从 1.0000 掉到 0.78–0.87（低于 0.86 阈值）。

常用分辨率桶：`1280x720`、`1600x900`、`1920x1080`、`2560x1440`、`3440x1440`、`3840x2160`。

## AI 顾问（可选，默认关闭）

`data/llm.example.json` 复制成 `data/llm.json`，填 `enabled: true` 与端点/模型，密钥只从环境变量读：

```json
{ "enabled": true, "endpoint": "https://api.deepseek.com/v1/chat/completions",
  "model": "deepseek-chat", "apiKeyEnv": "DEEPSEEK_API_KEY" }
```

- 只在**幕次变化时**调用一次，3 秒超时，失败静默回退规则文本
- 只发送：幕次、首领名、规则要点、池子统计；**不发截图、不发账号、不发 cookie**
- 规则引擎永远先算，模型只做「把建议压成 3 条能立刻执行的动作」

## 选"最符合当前账号"的打法

同一幕通常有 3 个遭遇变体。`PlanRanker` 本地打分：

```
分数 = Σ(推荐元素命中池子人数, 上限 3×2) + Σ(推荐角色命中 ×3) − 首领关扣 1
```

输出「优先遇到哪个 + 备选哪两个」，分数与理由直接显示在悬浮窗里。**不打分给模型，避免幻觉。**

## 账号数据（不登录的可行路线）

| 路线 | 是否需要登录 | 风险 | 状态 |
|---|---|---|---|
| 手动填 / GOOD JSON 导入 | 否 | 无 | ✅ 已支持（`roster.json`） |
| OCR 扫角色界面生成池子 | 否 | 与录屏同级 | 🚧 待做（Inventory_Kamera 路线：MIT，纯截图 OCR） |
| Enka.Network | 否 | 仅国际服，**国服/b服 不支持** | ❌ 不可用 |
| 米游社/HoYoLAB 战绩接口 | **是**（cookie） | 账号风控；b服账号体系不同 | ❌ 不做 |
| 胡桃工具箱等本地缓存读取 | 否 | 读游戏文件，非零风险 | ⚠️ 不建议 |

结论：**官方没有公开的账号数据接口**，"尽量不登录"的唯一干净路径是 OCR 扫角色界面，其次手动导入。

## 模板制作（一条命令）

```powershell
# 1) 打一场剧诗，程序会把识别到幕次的原始帧存到 samples/auto/
#    或随时按 Ctrl+Alt+D 存帧

# 2) 用 specs 指定要裁的区域（帧内像素坐标）
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\make-templates.ps1 `
  -Frame .\samples\auto\act3-1920x1080.png -Specs .\tools\specs.example.json

# 3) 输出：templates/1920x1080/*.png，并当场用匹配器自检（分数 + 坐标）
```

工具会拒绝越界坐标，并要求每个模板在原帧上的匹配分 ≥0.95 且坐标误差 ≤2px，否则退出码 1。
`templates/<宽>x<高>/` 会被运行时按当前客户区自动选中（±12% 容差）。

## 踩坑记录（PowerShell 5.1）

**带 `[string]` 类型约束的 param 变量，赋别的类型会被静默 ToString()。**

```powershell
param([string]$Frame)
$frame = [System.Drawing.Bitmap]::FromFile($path)
$frame.GetType().FullName   # => System.String  ← 直接被转成字符串
```

本仓库被这个坑绊过两次（`$Src/$src`、`$Frame/$frame`）。规则：**param 名与局部变量名不要同名**，或局部改用 `$img` / `$srcPath` 这类名字。
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
