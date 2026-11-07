# 幻想真境剧诗 · 助手（Theater Assistant）

**一句话：把"现在该做什么"直接告诉你的剧诗小窗——只读屏幕像素，不注入、不模拟按键。**

打剧诗时你不用再读攻略：小窗第一行永远是**可照做的一句指令**，有选项时直接说**选第几张**，
首领关附**手法两行**，需要细节时附**攻略原文 + 出处**。语音播报默认关闭，打开后连眼睛都不用抬。

```
▶ 现在做：用雷把怒气条推满，满条后 15 秒内把爆发全打出去
手法·0:15-0:45 怒气满后 15 秒力竭期：所有爆发全交（力竭全抗 -25%）
▶ 选第 3 张：雷晶·进阶（Electro 强化）
攻略·第 3 幕首领·翠翎恐蕈…［BV1bPxH6gEgw］
```

## 它到底怎么工作（全部离线、可解释）

```
窗口定位(GDI) → 模板匹配(半分辨率粗筛+定点细化) → 底部条带 OCR → 整帧 OCR(降频)
   → 幕次(模板 > 条带 > 整帧文字) → 时序稳定(倒退需连续确认) → 状态判定
   → 一句话「现在做」+ 手法 + 决策(选第N张) + 检索(带出处) + 可选语音
```

- **只读**：屏幕像素 + 本机 OCR 模型 + 本地语料；**不注入、不读进程内存、不模拟键鼠**
- **不联网**：只有你显式打开 `data/llm.json` 时才会调你自己配的端点（默认关闭，3s 超时，不发截图/账号）
- **不登录**：可选的角色池来自你手动导入 / 本机日志只读提取 / 有名字界面的 OCR

> 风险说明：这一档与录屏软件同级，但仍**不是零风险**。要 100% 零风险只有不使用任何第三方程序。

## 30 秒上手

1. 下载 [`TheaterAssistant.exe`](https://github.com/jingjiangze/genshin-theater-assistant/releases/latest)（单文件，双击即用）
2. 进剧诗，游戏切窗口化或窗口全屏
3. 小窗出现，第一行就是要做的事

热键：`Ctrl+Alt+H` 显隐 ｜ `Ctrl+Alt+L` 点击穿透 ⇄ 可拖动 ｜ `Ctrl+Alt+D` 存帧 ｜ `Ctrl+Alt+G` 攻略窗

## 和社区方案的关系

| 方案 | 思路 | 与本项目的差异 |
|---|---|---|
| **BetterGI**（17k★） | 注入 + 内存 + 模拟键鼠，做自动战斗/自动采集 | 我们不注入、不模拟输入；只借用它的"覆盖层 + 模板匹配"工程经验 |
| **MAA**（明日方舟） | 图像识别 + 模拟点击，做全自动 | 同上：只借"资源包可更新"的架构（`--packs`/`--check-update`） |
| **胡桃工具箱**（Snap.Hutao） | 登录态接口 + 本地日志解析拿账号数据 | 我们只取"本机日志里的角色字段"这一条，且只读、只存本机 |
| **本项目** | 只读像素 + 离线规则 + 检索 + 决策 | 目标不是"替你打"，而是**让你不用想** |

## 能力边界（如实）

| 能做 | 做不到 |
|---|---|
| 识别第几幕 / 首领关 / 三选一 / 队伍配置 / 难度面板 | 读游戏内存、自动按键、自动战斗 |
| 给"现在做"、给"选第几张"、给手法 | 保证 100% 识别正确（有弃权机制，不确定就不给建议） |
| 检索攻略并显示出处（BV 号/游戏内/本项目整理） | 保证攻略本身正确（以游戏内为准） |
| 本机日志只读提取角色 ID/等级 | 无登录情况下拿到完整养成数据 |

## 数据与更新（CI 持续跑）

| 工作流 | 频率 | 做什么 |
|---|---|---|
| `ci` | 每次 push | 自检 119 项 + 渲染 7 项 + 真机模拟 8 项 |
| `video-eval` | **每天** 05:00 UTC | 抽真实攻略视频帧 → 逐帧模拟运行 → 用视频自带「第N幕」对答案算准确率（低于 95% 会红） |
| `corpus-refresh` | 每周一/周四 | 搜新视频 OCR 抽攻略语料 + 抓 BWIKI + 蒸馏经验 + 刷新角色 ID 表 |
| `adapt-videos` | 每周一/周五 | 抽帧 → 批量适配模板（`actN`/`hall`/`diff` 等） |
| `release` | 打 tag | 三道门全过才发 exe（自检 / 渲染 / 真机模拟） |

## 批量回归实证（2026-11-06，42 个真实视频）

CI 对 42 个 B站攻略视频做了「抽帧 → 逐帧模拟运行 → 对答案 → 操作提取 → 蒸馏 → 跨视频共识」全链路：

| 指标 | 实测 |
|---|---|
| 视频 / 帧 | 42 / 463 |
| 幕次准确率 | 100%（4 个有真值帧全对；样本少，不宣称统计显著） |
| 跨幕跳变 / 状态抖动 | 1 次 / 33 次 |
| 跨视频操作共识 | 9 条（≥2 个 UP 主都这么写） |
| 产物 | [`BATCH-42-SUMMARY.md`](docs/experience/BATCH-42-SUMMARY.md)、[`BATCH-42-OPS-CONSENSUS.md`](docs/experience/BATCH-42-OPS-CONSENSUS.md) |

社区共识已并入 [`data/rotations.json`](data/rotations.json)：主 C 与辅助留给 8/10 幕和圣牌关、
敌人选择按从左到右、资源不足选 BUFF 别硬买。

## 跨平台语料（yt-dlp，v0.19.0）

按「优先用成熟轮子」接入 [yt-dlp](https://github.com/yt-dlp/yt-dlp) 与
[PySceneDetect](https://github.com/Breakthrough/PySceneDetect)（审计见
[`docs/VIDEO-TOOLING-AUDIT.md`](docs/VIDEO-TOOLING-AUDIT.md)）：

- B站搜索仍用 `search.mjs`（yt-dlp 的 `bilisearch:` 实测被 HTTP 412 风控拦），yt-dlp 接直连 BV 取
  **简介 / 字幕 / 章节 / 评论 / 弹幕**；YouTube 走 `ytsearch:`。
- 同一视频：OCR 只给 2–4 条碎片，UP 简介给 **17–25 条逐幕配队与 BUFF 路线**。
- 50 视频合并 → 429 候选 → **18 条跨 UP 主共识**，其中 `第7幕 推荐 行秋` 有 4 个视频支持。
- 产物格式与 `--extract-ops` 一致，可直接喂 `--consensus`；仅取公开内容，cookie 用完即删。

## 原操作对比与可替代性（v0.20.0）

- `--compare-ops <目录>`：把视频 UP 主写下的逐幕配队当**原操作真值**，与本项目推荐对答案，
  同时输出「职能命中率」（含同职能替代）。8 视频实测：精确 27.8% → 职能 **52.8%**。
- `--substitutes 行秋 --act 7`：没有某个角色时按职能给替代项（夜兰/心海/芭芭拉…）。
- 不下载视频本体：画面帧由 `hd.mjs` 产出，yt-dlp 只取文本元数据。

## 文档索引

| 想知道 | 看这里 |
|---|---|
| 本赛季完整思路（机制/节奏/首领/手法/决策） | [`docs/SEASON-SUMMARY.md`](docs/SEASON-SUMMARY.md) |
| 蒸馏出来的判断规程（含弃权规则） | [`docs/experience/SKILL.md`](docs/experience/SKILL.md) |
| 所有实测数字与踩坑（20+ 节审计） | [`AUDIT-2026-11-05.md`](AUDIT-2026-11-05.md) |
| 视频取帧与批量适配 | [`docs/VIDEO-FRAMES.md`](docs/VIDEO-FRAMES.md)、[`templates/README.md`](templates/README.md) |
| 二次开发 | [`docs/DEV-README.md`](docs/DEV-README.md) |

## 许可

- 代码：MIT（见 `LICENSE`）
- 依赖：RapidOcrNet / PP-OCR 模型 = Apache-2.0；WebView2 = 微软许可
- 游戏内容与数据：归 miHoYo 所有；本仓库只做玩家自用的辅助显示，不含自动化操作

---

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

## 模板制作：批量适配（v0.4.0，推荐）

不用手写像素坐标。把一个**帧目录**丢进去即可——游戏截图、`Ctrl+Alt+D` 存下来的帧、录屏抽帧都行：

```powershell
.\TheaterAssistant.exe --batch frames --out templates      # frames 换成你自己的帧目录
notepad templates\batch-report.md
```

> 程序识别到幕次时会自动把原始帧存到 exe 同级 `samples\auto\`（跑一次才有这个目录）；
> 也可以完全不用它，直接指向你自己整理的录屏抽帧目录。

它做的事：

1. 逐帧跑 OCR，拿到每条文字的**四边形坐标**（`OcrResult.TextBlocks.BoxPoints`，已折回整帧坐标系）
2. 用标签表把文字锚点映射成模板名：`第N幕 → actN`、`演出准备/助演嘉宾/演出回顾/奖励一览 → hall`、
   `选择角色 → choice`、`演出结算 → result`、`开始挑战 → begin`、`难度 → diff`
3. 按矩形裁图，并做**双向量检**：

| 口径 | 含义 | 门槛 | 为什么 |
|---|---|---|---|
| 在场一致性 `presentMin` | 同分辨率下同样识别出该标签的其它帧，模板必须仍在原位 ±24px 命中 | ≥ 0.92 | 证明不是裁到一次性画面 |
| 缺席不误报 `absentMax` | 同分辨率下**没有**该标签的帧，整帧扫描都不该命中 | < 0.80 | 低于运行阈值 0.86 才不会假阳性 |

4. 只把两条都过的模板写进 `templates/<宽>x<高>/`，并留下 `_specs.json`（**就是装出来的那几条的规格**，可直接喂 `tools/make-templates.ps1` 复算）与 `batch-report.json/.md`

坐标是从 OCR 现算的，所以**每个分辨率产出它自己的一套模板**：1080p 的帧进 `1920x1080/`，`2560x1440`、`3440x1440` 同理，不需要手工换算。
能产出多少条取决于**这帧里能读到多少锚点**——实测 640×360 的大厅帧只出 1 条（`hall`），1080p 合成帧出 4 条。

报告里每条模板都带 `双向 / 单向` 标记：`单向` = 该分辨率下没有"缺席帧"，所以只验了在场、没验误报（单帧目录就是这种情况，结论要打折看）。

**幕次列表帧不派生 `actN`**：幕次选择界面会把 第一幕…第十幕 并排列出，一次出现多个幕次时 `actN` 没有"当前幕次"语义（十个模板会同时命中），这类帧一律跳过；只有整帧只出现一个幕次时才派生。

配套命令：

```powershell
.\TheaterAssistant.exe --selftest              # 纯函数自检（不需要模型、不需要游戏）
.\TheaterAssistant.exe --selftest --with-ocr   # 追加"OCR 坐标是否可用"的端到端断言
.\TheaterAssistant.exe --synth tests\_tmp\frames   # 生成合成帧，用来验证整条流水线
```

## 模板制作：手工裁剪（保留）

```powershell
# 用 specs 指定要裁的区域（帧内像素坐标）
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\make-templates.ps1 `
  -Frame .\samples\auto\act3-1920x1080.png -Specs .\tools\specs.example.json
```

工具会拒绝越界坐标，并要求每个模板在原帧上的匹配分 ≥0.95 且坐标误差 ≤2px，否则退出码 1。

> 批量适配的已知限制（**实测，2026-11-05**）：
> 用登录 cookie 走 `playurl`（`qn=127&fnval=4048&fourk=1`）能拿到 B站 **1920×1080 AVC** dash 流（未登录只有 360p），
> 抽帧脚本见 `docs/VIDEO-FRAMES.md`。但拿 11 张真实 1080p 帧跑下来，**严格口径 0 条通过**：
> 同一句 UI 文字在不同界面位置/背景不一致（`hall` 在场 0.66、`title` 0.79），
> 幕次条十个标签彼此高度相似（`act10` 缺席仍得 0.98）、三选一相关文字互相近似（`choice` 缺席 0.96）。
> 放宽 `--min-present 0.60`（保留 `absentMax<0.80` 误报门）能出 2 条：`hall`、`title`——但那是「界面锚点」。
> **结论：视频素材能出界面锚点，出不了状态锚点（`actN`/`choice`）；状态锚点要用本机客户端截图**（无贴字、同布局、无压缩噪点）。


## 踩坑记录（PowerShell 5.1）

**WinExe 从 PowerShell 直接 `& .\xxx.exe --selftest` 不会同步返回**，`$LASTEXITCODE` 是空的：
用 `Start-Process -Wait -PassThru` 再读 `.ExitCode`，否则 CI 里会「命令成功但退出码为空」直接误判失败。

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

## 真机模拟实测（不打开游戏也能跑真链路）

开一个标题为「原神」的假游戏窗口，把一张真实剧诗帧当画面，然后走**和正式运行完全一样**的路径：
`EnumWindows` 找窗口 → `GetClientRect` 取客户区 → `GDI CopyFromScreen` 抓像素 → 模板匹配 + OCR + 状态判定。

```powershell
.\TheaterAssistant.exe --fakegame samples\frame.png --size 1920x1080   # 起假窗口，手动观察（关窗结束）
.\TheaterAssistant.exe --probe-live --title 原神 --frames 2            # 抓窗口出诊断（存到 samples\probe\）
.\TheaterAssistant.exe --selftest-live --png 某张帧.png --size 1600x900 # 全自动：起窗口→抓帧→断言→退出码
```

`--selftest-live` 的断言（CI 每次 push 都跑）：

| 断言 | 说明 |
|---|---|
| 窗口可被发现 | `Find` 有返回 |
| 命中的是假窗口 | 窗口 pid == 本进程 pid（防止抓到"标题里提到原神"的浏览器/聊天窗口） |
| 客户区可用 | 实测客户区，不假设等于请求尺寸（小桌面会把窗口夹住） |
| 抓帧尺寸=客户区 | `CaptureClient` 的输出尺寸与客户区一致 |
| **抓到的画面=源帧** | 中间 90% 区域灰度相似度 ≥0.95（证明 GDI 真的抓到了渲染内容，不是黑屏/别家窗口） |
| OCR 可用 / 从抓帧派生规格 / 判定不为空 | 识别链路真的跑通 |

退出码：`0` 通过、`1` 断言失败、`4` 抓屏为空白（无桌面会话 → 跳过，不算失败）。

实测记录：

| 环境 | 结果 |
|---|---|
| GitHub Actions（windows-latest，桌面 1024×768） | **8/8 通过**，客户区 1024×768，抓屏相似度 **0.9962**，OCR 3 文本框，派生 `act3,hall,title`，判定「第 3 幕」 |
| 本机（175% 缩放、多屏、真·原神客户端在跑） | 修复前 5 过 3 失败 → 修复后：弱候选被拒（`exit=2`）、精确窗口命中（`exit=0`），真机抓屏可用 |

> **这条曾被我自己误判**：先以为"agent shell 的窗口站与交互桌面不是同一合成面"。独立审计用 `--probe-live` 抓下一张 **230 万字节的真原神「演出回顾」界面（UID 508235600，1920×1080）**，证明本会话 GDI 抓屏完全正常 —— 真因是 `GameWindow.Find` 把**真游戏窗口**选走了（它有 +100/+50，任何 needle 都赢），`ClientToScreen` 于是指向游戏窗口所在显示器，抓到的自然是那一块桌面。

### 真机证据（真实客户端，非合成帧）

```powershell
.\TheaterAssistant.exe --probe-live --title 原神 --frames 2
# 窗口 0x… 客户区 1920x1080 · 抓帧 1920x1080 · 存 samples\probe\probe-*.png
# 抓到的画面 = 幻想真境剧诗「演出回顾」界面（月谕模式记录 + 开幕/待命角色 + UID 508235600）
```

全程只读屏幕像素，游戏内没有做任何操作。

### 审计后修的四个真问题

1. **`Find` 弱证据也认**：原来只要 `score>0` 就返回，游戏没开、浏览器开着原神攻略页时照样被当成游戏窗口。改为**最低分线 40**（只有"标题包含"这条弱证据不算找到），并加 `preferPid`（自检时 +200，真游戏在跑也能锁定假窗口）。实测：只开「原神攻略-浏览器」→ `exit=2 没找到`；开标题精确「原神」→ `exit=0 命中`。
2. **跳过等于无事发生**：抓屏空白返回 4 时连报告都不落盘，CI 只打一行 `SKIP` 且步骤是绿的。改为**所有退出路径都落盘报告**，CI 用 `::warning::` + Step Summary 显式标注，发布门也走同一套。
3. **未声明 DPI 感知**：`ClientToScreen` 逻辑坐标 vs `CopyFromScreen` 物理像素，缩放屏上抓屏错位。已声明 `PerMonitorV2`。
4. **攒素材会"每键永远只有一张"**：`_autoSaved` 只存内存、只按状态去重，同一幕的选人/战斗/结算只会留下第一张。改为**按 4×4 画面签名去重 + 每键最多 6 张**，存盘失败不再静默（写进诊断行）。`Ctrl+Alt+D` 也统一存到 `samples\auto\manual-HHmmss.png`，一条 `--batch samples\auto` 就能吃全。

## 实机素材实测（你本机的 5 张 1920×1080 截图）

用你给的截图（大厅 / 难度选择 / 队伍配置 ×3）跑 `--batch`，全过程只读像素：

```
帧 1-hall.png          9 个文本框 → 派生 hall(演出准备), title
帧 2-difficulty.png   19 个文本框 → 派生 diff(月谕模式), team
帧 3-team-opening.png 27 个文本框 → 派生 team(开幕角色), title
帧 4-team-standby.png 63 个文本框 → 派生 begin(前往挑战), team
帧 5-team-select.png  51 个文本框 → 派生 begin, team
→ 安装 2 / 拒绝 3（门槛：在场 ≥0.92 且缺席 <0.80）
```

| 模板 | 尺寸/位置 | 在场最低 | 缺席最高 | 结论 |
|---|---|---|---|---|
| `hall`「演出准备」 | 189×88 @1516,714 | 1.000 | 0.734 | **安装** |
| `diff`「月谕模式」 | 154×62 @113,397 | 1.000 | 0.726 | **安装** |
| `begin`「前往挑战」 | 143×63 @1550,984 | 0.986 | 0.817 | 拒绝：白底按钮彼此太像 |
| `team`「开幕角色」 | 144×60 @124,131 | 0.844 | 0.780 | 拒绝：在场帧里位置/写法不同 |
| `title`「幻想真境剧诗」 | 176×54 @131,5 | 0.729 | 0.729 | 拒绝：两种背景上的同一句字 |

装好后走**用户实际路径**（`templates\1920x1080\` + `--frame`）逐张回扫：

```
1-hall.png        → hits: hall=1         → 剧诗界面
2-difficulty.png  → hits: diff=1         → 难度选择
3/4/5-team*.png   → hits: 无             → 队伍配置（OCR 锚点）
```

**全部 5 张都没有被判成"第 N 幕"**。

这批素材同时暴露了两个只有真实截图才有的坑（已修，CI 有断言）：

1. 难度选择界面写着「完成『圣牌挑战』并通过**第十幕**演出可抽取『月谕圣牌』」—— 原逻辑据此判成"当前第 10 幕"。
   修法：先认菜单面板（`难度选择|参演要求|角色元素类型限制` → `difficulty`，`队伍配置|开幕角色|待命角色` → `team`），
   这两类界面不再参与幕次判定。
2. 队伍配置界面右上角有「**本期**辉彩祝福」按钮 —— "辉彩祝福"本属于三选一事件卡片，导致派生出一条错误模板。
   修法：`choice` 锚点用 `(?<!本期)辉彩祝福` 排除该按钮。

`templates/1920x1080/hall.png`、`diff.png` 已随仓库/发布包提供（本机客户端截图派生的小块文字模板，可直接用；其余按 `--batch` 自己生成）。

## 账号信息怎么拿（不登录优先）

| 路线 | 需要登录 | 拿得到什么 | b服可用 | 风险 | 现状 |
|---|---|---|---|---|---|
| **手动填 / GOOD JSON 导入** | 否 | 角色名 + 等级 | ✅ | 无 | ✅ 已支持（`roster.json`） |
| **`--roster-scan` 扫角色界面** | 否 | 有名字的界面能认出名字+等级 | ✅ | 与录屏同级 | ✅ 已实现（v0.6.0） |
| 头像模板匹配角色网格 | 否 | 只有头像的界面也能认人 | ✅ | 与录屏同级 | 🚧 需给 129 个角色做头像模板 |
| 剧诗「待命角色」页 | 否 | 只有头像 + Lv，**没有名字** | ✅ | — | ❌ OCR 认不出（实测 63 框 0 命中） |
| Enka.Network | 否 | 完整养成数据 | ❌ 仅国际服 | 低 | ❌ 不可用 |
| 米游社 / HoYoLAB 战绩接口 | **是**（cookie） | 完整养成数据 | ⚠️ 账号体系不同 | 账号风控 | ❌ 不做 |
| 读游戏本地文件（胡桃工具箱路线） | 否 | 完整数据 | ✅ | 读游戏文件，非零风险 | ⚠️ 不采纳 |

`--roster-scan` 用法（实测：从你给的队伍配置截图里认出 **夜兰 Hydro Lv.80**）：

```powershell
.\TheaterAssistant.exe --roster-scan samples\auto --out roster.json
```

**能力边界（实测，不夸大）**：只有**写了名字**的界面（角色一览、角色详情、队伍列表带名字的版本）能认出角色；
剧诗「待命角色」那种"头像 + Lv"的网格认不出（5 张截图 168 个文本框只命中 1 个名字）。
要把"只有头像"也认出来，得走**头像模板**路线（工程量：129 个头像 + 网格定位）。

## 视频批量适配（实测：你的视频 + 2 个同类视频 + 你本机截图，共 49 帧 1920×1080）

```powershell
# 1) 取 1080p 原生帧（登录 cookie 走环境变量，见 docs/VIDEO-FRAMES.md）
node tools/video-frames/hd.mjs BV1eExm6dEmt spread=24 out=./frames
# 2) 多来源合并成素材池，一次批量适配
.\TheaterAssistant.exe --batch frames --out templates
```

实测数字（49 帧）：

| 模板 | 来源 | 在场最低 | 缺席最高 | 结果 |
|---|---|---|---|---|
| `act5`「第五幕」 | BV1G86xBFEk5 第 5 幕画面 | 1.000 | 0.741 | **安装** |
| `act8`「第8幕」 | BV1eExm6dEmt 第 8 幕画面 | 1.000 | 0.810 | **安装** |
| `act9`「第9幕」 | BV1eExm6dEmt 第 9 幕画面 | 1.000 | 0.819 | **安装** |
| `hall`/`diff` | 你本机截图 | 1.000 | 0.73/0.73 | **安装**（v0.5.3 起随包） |
| `choice`「选择一个神秘收获效果」 | 视频 | 0.630 | 0.703 | 拒绝：只在 1 帧出现 |
| `begin`/`team`/`title` | 视频+截图 | 0.67–0.77 | 0.73–0.83 | 拒绝：跨界面位置/背景不一致 |

回扫验证（模板装进 `templates\1920x1080\` 后逐帧 `--frame`）：

| 帧 | 判定 | 来源 |
|---|---|---|
| 第 4 幕画面（无 act4 模板） | 第 4 幕 | OCR(条带) |
| 第 5 幕画面 | 第 5 幕 | 模板 act5=1 |
| 第 8 幕画面 | 第 8 幕 | 模板 act8=1 |
| 第 9 幕画面 | 第 9 幕 | 模板 act9=1 |
| 你本机大厅 / 难度选择 | 剧诗界面 / 难度选择 | 模板 hall=1 / diff=1 |

**模板阈值分级**：`actN` 这类"外形几乎一样、只有数字不同"的模板运行时阈值提到 **0.90**（跨幕相似度实测 0.80–0.82），
其余界面锚点仍是 0.86；批量适配的缺席门槛相应放到 **0.82**（仍低于 0.86）。

## 胡桃工具箱（Snap.Hutao）的账号数据路线，以及本项目为什么不走

公开资料与社区实现归纳（注意：`DGP-Studio/Snap.Hutao` 主仓库今天已 404，无法逐行核对源码，以下按公开文档与已知实现记录）：

| 路线 | 做法 | 需要什么 |
|---|---|---|
| ① 米游社 / HoYoLAB 战绩接口 | 登录米哈游通行证拿 cookie（`ltoken`/`ltuid`），调 `api-takumi-record.mihoyo.com`（国服）/ `bbs-api-os.hoyolab.com`（国际服）取角色、深境、剧诗数据；cookie 本地加密存 | 登录 + 账号风控暴露 |
| ② **本地游戏日志解析**（免登录的关键） | 原神客户端运行时会请求同一批接口，响应落在 Unity 的 `Player.log`（`output_log.txt`）里；工具读日志里的 JSON 抽出 UID/角色/战绩 | 读游戏日志文件 |
| ③ 抽卡记录 / 本地缓存 | 抽卡链接 `authkey` + 本地 SQLite 缓存 | 读本地文件 |
| ④ 游戏资源与元数据 | 内置 JSON/图片（角色/武器/圣遗物），或按官方 CDN 校验 | — |
| 技术栈 | C# + WinUI 3 / Windows App SDK、本地 SQLite、MSIX 打包 | — |

**本项目不采纳 ① ③**（要登录/读缓存，账号风险）**，也不采纳 ②**（读游戏日志=读游戏文件，属"非零风险"档，而且日志解析强度依赖游戏版本）。
我们只走「只读屏幕像素」这一档：OCR + 头像模板识别，最坏情况等价于录屏软件。

## 头像模板认角色池（实验，已实现骨架）

```powershell
node tools/fetch-avatars.mjs --out assets/avatars        # yatta 索引 + enka CDN 取图标（136 个角色）
.\TheaterAssistant.exe --roster-avatars frames --avatars assets\avatars --probe   # 诊断模式
.\TheaterAssistant.exe --roster-avatars frames --avatars assets\avatars --out roster.json
```

机制：OCR 找每个格子的 `Lv.NN` 当锚点 → 自校准格子尺寸（1.5–2.6 倍 Lv 宽度）→ 把头像模板缩放到格子内区做 SSD 比对。

**实测结论（你本机「待命角色」截图，1920×1080）**：

| 指标 | 数字 |
|---|---|
| Lv 锚点检出 | **27/27**（与画面里 27 个角色格子一致） |
| 自校准格子比例 | ×1.5，平均分 0.460 |
| 头像模板命中分 | 0.39–0.57（**远低于可用线 0.85**） |
| 结论 | **格子定位可用，图标模板不可直接用** —— enka 的头像是"头像卡面icon"，游戏格子里是另一套裁切 + 元素底色 + 等级条，SSD 比不出来 |

**下一步（明确的修法）**：改成 NCC/边缘相似度，或先用你的截图里"名字已知"的格子（如你给的队伍配置页右侧 `夜兰`）自动生成**本机风格的格子模板**，再做比对 —— 即"用自己的截图训练自己的头像模板"，这条路不依赖第三方图标风格。

## 离线攻略检索（`--advise`，v0.7.0）

不联网、不调用大模型：把中文攻略做成小语料库（`data/guide-corpus.json`，每条带出处），
用 **BM25（中文字符 bigram + ASCII 词）** 检索，命中后连**出处**一起显示。

```powershell
.\TheaterAssistant.exe --advise "第3幕 翠翎恐蕈 怎么打" --top 2
.\TheaterAssistant.exe --advise --act 6          # 只给幕次也行
```

输出格式（分数随查询变化，下面是结构示意）：

```
查询：<你的问题>（act=自动|N）｜语料 23 条 → 命中 K
[<BM25 分>] <条目标题>  （act=N）
    <条目正文（完整）>
    出处：<B站 BV号·画面位置 / 游戏内界面 / BWIKI / 本项目整理>
```

悬浮窗里也会自动带上检索结果（`攻略·标题：正文…［出处］`），**按当前幕次加权**，所以 3 幕时优先给 3 幕的条目。

### 为什么不上向量模型（审计结论）

| 方案 | 体积成本 | 实测召回 |
|---|---|---|
| **BM25 字符 bigram（当前）** | **0 MB** | `hit@1 = 17/17`、`hit@3 = 17/17`（17 条真实提问，`tools/audit-retrieval.mjs` 可复跑） |
| bge-small-zh ONNX 向量检索 | **+95 MB**（exe 从 100MB 变 ~200MB） | 未采用：现有语料量级下没有提升空间 |

语料涨到几百条、或要让"口语提问"也能命中时，再引入向量重排（模型作为可选下载包，不塞进 exe）。

## 语音播报（v0.10.0，零依赖离线）

状态或幕次变化时，程序会用 **Windows 内置语音**读一句"现在做"，你不用看屏幕。

```powershell
.\TheaterAssistant.exe --voices            # 列系统语音（本机有 Huihui 中文）
.\TheaterAssistant.exe --say "第3幕，用雷推怒气"   # 手动试一句
.\TheaterAssistant.exe --selftest-speak    # 自检（无语音则返回 4 跳过）
```

配置 `data/speech.json`：

```json
{ "enabled": true, "rate": 1, "volume": 100, "minSecondsBetween": 6, "onlyKinds": [] }
```

`onlyKinds` 非空时只播报这些状态（`act` / `choice` / `team` / `difficulty` / `theater` / `result`）。

## 资源包与更新（MAA 式）

```powershell
.\TheaterAssistant.exe --packs                    # 清单与安装状态
.\TheaterAssistant.exe --fetch-model bge-small-zh # 按镜像顺序下载 + SHA256 校验 + 原子替换
.\TheaterAssistant.exe --check-update             # 查最新 release
```

清单 `data/models.manifest.json`（**故意留空**，URL/SHA 必须实测后填）：

```json
{ "packs": [ { "name": "bge-small-zh", "kind": "embedding", "size": 24000000,
               "sha256": "<实测>", "urls": ["https://镜像/…", "https://github.com/…"] } ] }
```

下载落 `%LOCALAPPDATA%\TheaterAssistant\packs`，失败保留上一版；程序本身更新走 Releases 覆盖 exe，数据与资源包不用重下。

## 决策：直接告诉你选哪个（v0.13.0）

识别到买卡/三选一界面时，悬浮窗**第一行**就是可照做的指令，不用自己判断：

```
▶ 选第 3 张：雷晶·进阶（Electro 强化）
  依据：第3幕首领·翠翎恐蕈：雷推怒气条，满条后 15 秒力竭窗口
▶ 选「伙伴」（补人：当前 2/4）
```

规则（离线、可解释）：

| 场景 | 指令 |
|---|---|
| 首领幕（3/6/8/10） | 优先选**本幕机制需要的元素**强化（3 雷 / 8 岩 / 10 水·治疗） |
| 非首领幕 / 没有对应强化 | 优先**战斗卡补人** |
| 三选一 + 人数不足 | 选「伙伴」 |
| 三选一 + 人数已满 | 选「辉彩祝福」 |
| 大模型已启用 | 必须答「第 N 个」，否则**不采纳**、回落规则 |

```powershell
.\TheaterAssistant.exe --decide "战斗·普通`n雷晶·进阶`n感电·原态" --act 3 --team 3 --target 4
```
