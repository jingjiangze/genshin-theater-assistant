# 从视频取高清帧（给 `--batch` 当素材）

目的：没有本机截图时，用**已登录账号的 cookie** 向 B站取 dash 流，抽出原生分辨率帧喂给
`TheaterAssistant.exe --batch`。未登录时 B站只给 360p（实测 `16:640x360`），登录后才有 `80:1920x1080`。

## 脚本

`tools/video-frames/hd.mjs`（Playwright + Thorium/Chromium，node ≥ 18；需要先在该目录 `npm i playwright-core`）：

```powershell
$env:BILI_COOKIE = 'SESSDATA=...; bili_jct=...; ...'   # 只走环境变量，绝不写进文件/仓库
node tools/video-frames/hd.mjs BV1xxxxxxxxx 20 60 120 200 out=./hd
```

它做的事：

1. `ctx.addCookies()` 注入 cookie（domain `.bilibili.com`）
2. 页面内同源 `fetch` 调 `x/web-interface/view`（拿 cid）与 `x/player/playurl`
   （`qn=127&fnval=4048&fnver=0&fourk=1`，自动带 cookie）→ 列出所有清晰度
3. 优先取 **AVC（codecid=7）** 的最高清晰度流（HEVC 在 Chromium 里常解不了）
4. 建一个隐藏 `<video>` 播这个 video-only dash 流，`crossOrigin='anonymous'`
5. 逐时间点 seek → `canvas.drawImage` → `toDataURL('image/png')` 落盘

取像素的两条路：

| 模式 | 条件 | 备注 |
|---|---|---|
| `canvas` | CDN 回 `Access-Control-Allow-Origin`（本次实测可用） | 像素精确，1:1 无缩放 |
| `screenshot` | 画布被 CORS 污染（抛 `SecurityError: Tainted canvases`） | 退回 `page.screenshot(clip)`，把 video 铺满 1920×1080 再截 |

## 实测结果（2026-11-05，BV193xp6WErn，22 分钟，11 帧 @1920×1080）

| 项目 | 结果 |
|---|---|
| 清晰度候选 | `80:1920x1080`、`64:1280x720`、`32:852x480`、`16:640x360`（各含 AVC/HEVC/AV1） |
| 取流 | `codec=7 1920x1080`，`video ready {w:1920,h:1080,dur:1369.7}` |
| 抽帧 | 11 张全 1920×1080，canvas 模式，单帧 PNG 1.2–2.5 MB |
| 派生（严格 0.92/0.80） | 候选 5–7 条，**安装 0** |
| 派生（`--min-present 0.60`） | **安装 2**（`hall` 演出准备 148×61、`title` 幻想真境剧诗 202×60） |

为什么状态锚点出不来：

- 同一句 UI 文字在不同界面**位置不同、背景不同**（`hall` 在场最低 0.66、`title` 0.79）
- 幕次条 `第一幕…第十幕` 十个标签外形几乎一致 → 模板彼此误伤（`act10` 缺席帧仍得 0.98）
- 三选一界面的文字互相近似（`choice` 缺席帧 0.96）
- UP 主的贴字/摄像头把画面两侧污染，部分标签直接来自贴字而非游戏 UI（`diff` 在场只有 0.42）

**结论**：视频素材适合出「界面锚点」；`actN`/`choice` 这类「状态锚点」要用本机 `Ctrl+Alt+D` 截图
（同布局、无贴字、无 H.264 压缩噪点）。

## 合规与安全

- cookie 只在进程环境变量里存活，**不进仓库、不进日志、不写文件**；脚本不打印任何 cookie 值
- 只读公开视频流，不做批量抓取/转码分发；抽出的帧仅作本机模板素材，不提交到仓库
- 分享/撤销：B站「退出登录」即失效（`SESSDATA` 作废）
