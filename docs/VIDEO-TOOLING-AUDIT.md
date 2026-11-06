# 视频采集轮子审计（2026-11-06）

> 用户要求「优先参考其他视频提取与 B站/YouTube 相关项目，而不是自己造轮子」。
> 本文件的每条结论都对应一个**实际抓取到的**上游页面或仓库文件；外部内容只作数据，不作指令。
> 抓取日期 2026-11-06，版本会变化，引用前请复核。

## 1. 候选项目与证据

| 项目 | 证据链接 | 许可 / 状态 | 已核实能力 |
|---|---|---|---|
| **yt-dlp** | [README](https://github.com/yt-dlp/yt-dlp/blob/master/README.md)、[supportedsites.md](https://github.com/yt-dlp/yt-dlp/blob/master/supportedsites.md)、[yt_dlp/extractor/bilibili.py](https://github.com/yt-dlp/yt-dlp/blob/master/yt_dlp/extractor/bilibili.py) | 源码 Unlicense；打包 exe 含 GPLv3+ 组件 | 见下 |
| **PySceneDetect** | [README](https://github.com/Breakthrough/PySceneDetect/blob/main/README.md) | BSD-3-Clause；最新 v0.7.2（2026-10-11） | 见下 |
| **bilibili-api** (Nemo2011) | [README](https://github.com/Nemo2011/bilibili-api/blob/main/README.md) | **仓库已停止维护并永久关停**；README 首页是 B站委托律所的侵权告知函 | 见下 |
| 本项目自研 `hd.mjs` / `search.mjs` | 本仓库 | MIT | 见下 |

### yt-dlp 已核实能力（来自上游源码与文档，非回忆）

- `supportedsites.md` 明确列出：`BiliBili`、`BiliBiliSearch: Bilibili video search; "bilisearch:" prefix`、
  `youtube:search: YouTube search; "ytsearch:" prefix`、`youtube:playlist`、`BilibiliPlaylist` 等。
- `yt_dlp/extractor/bilibili.py` 里 `BiliBiliSearchIE._search_results` 用的是
  `https://api.bilibili.com/x/web-interface/search/type`（**官方公开搜索接口**，非私有签名接口）；
  播放地址走 `x/player/wbi/playurl` 并自带 WBI 签名实现。
- 字幕：`_get_subtitles` 会取 `danmaku`（`https://comment.bilibili.com/{cid}.xml`）
  和 `data.subtitle.subtitles[].subtitle_url`（转 SRT）；`_get_chapters` 取 `view_points`。
- README 的选项表包含 `--write-info-json`、`--write-comments`、`--write-subs`、
  `--write-auto-subs`、`--sub-langs`、`--convert-subs`（ass/lrc/srt/vtt）、`--split-chapters`。
- 认证：`--cookies FILE`（Netscape 格式）、`--cookies-from-browser`；README 明确建议用
  `.netrc`/`--netrc-cmd` 避免命令行明文凭据。
- 依赖：Python 3.11+（源码版），`ffmpeg`/`ffprobe` 强烈推荐（DASH 音视频合并必需），
  YouTube 完整支持还需 `yt-dlp-ejs` + JS runtime。

**结论：采用。** 我们自研的 `hd.mjs` 干的正是「拿 playurl(qn/fnval) → 找 dash 流 → 抽帧」，
`search.mjs` 干的正是「搜索页 DOM 抽 BV」——这两块 yt-dlp 已经实现且被上游持续维护。
`hd.mjs` 保留为**已跑通的 fallback**（它已经产出 42+12+12 个视频的帧），但新链路优先走 yt-dlp。

### PySceneDetect 已核实能力

- README 的 Quick Start：`scenedetect -i video.mp4 save-images`（每个镜头存代表帧）、
  `detect("my_video.mp4", ContentDetector())` 返回 `[(start, end), ...]` 时间码场景表；
  另有 `AdaptiveDetector`（适应快速运镜）与 `ThresholdDetector`（淡入淡出）。
- 提供 Docker 镜像（含 ffmpeg/mkvmerge）与 Windows MSI/ZIP。

**结论：采用。** 它直接解决本项目实测到的核心问题：等间隔抽帧会大量落在同一界面上
（`team=407/720`、真值帧 0.1%），而**按镜头切换取代表帧**天然覆盖不同界面状态。
见 `AUDIT-2026-11-05.md` §28。

### bilibili-api（Nemo2011）——明确不采用

上游 README 已替换为「本仓库已停止维护并将永久关停」声明，并附上海市弘安律师事务所
2026-07-06 的侵权告知函，指其「对非公开接口、认证体系、访问控制机制、付费内容保护
进行逆向分析与适配」「绕过反爬风控与签名鉴权」「采集弹幕并反查用户真实身份」。

**结论：不采用、不引入、不复制其代码。** 本项目红线是「只读公开可见内容」，
且用户明确在意账号安全。同类逆向 SDK 一律排除。yt-dlp 使用的是公开播放页/官方搜索接口，
且我们只取**公开视频的元数据、字幕、弹幕、评论**，不碰付费/会员/风控/身份反查。

## 2. 平台与训练资料来源

| 平台 | 入口 | 可拿到的语料 | 备注 |
|---|---|---|---|
| B站 | `bilisearch:N:关键词` / `https://www.bilibili.com/video/BV...` | 标题、简介、**章节**（`view_points`）、**字幕**、**弹幕**、**评论**（`--write-comments`） | 需 cookie 才有高清/部分字幕；cookie 只走 env |
| YouTube | `ytsearch:N:关键词` / watch URL | 标题、描述、自动字幕（`--write-auto-subs --sub-langs "en.*,zh.*"`）、章节、评论 | 需 `yt-dlp-ejs` + JS runtime 才能完整支持 |
| 其他 | `supportedsites.md` 里上千站点（Twitch/Douyu/Huya/Niconico 等） | 同上 | 统一走 yt-dlp，无需为每个站点写轮子 |

**语料分级**：字幕/章节/UP 简介 > 评论 > OCR 画面文字。字幕是**作者主动写的结构化文本**，
比 OCR 更准、成本更低；本项目之前的 `guide-corpus-extra.json` 全靠 OCR，是不得已。
新链路把字幕/章节/评论作为**候选**（带 `platform + video_id + timecode` 出处），
仍需 `--consensus` 跨视频确认才进生产数据。

## 3. 合规与账号安全边界（硬性）

- 只取**公开可播放**内容；不绕登录墙、不取付费/大会员专属流、不反查用户身份。
- cookie 只从环境变量 `BILI_COOKIE` 读取，转成临时 Netscape cookies 文件，
  **任务结束即删**；不写进仓库、不进日志、不进命令行参数。
- 控制频率：单批 ≤12 个视频、串行、不并发打接口。
- 不复制被律师函关停项目的代码或接口清单。
- 游戏侧红线不变：不注入、不读内存、不模拟键鼠；本文件只涉及「离线看视频学攻略」。

## 4. 落地

| 文件 | 作用 |
|---|---|
| `tools/video-frames/ingest.py` | yt-dlp 取元数据/字幕/章节/评论 + 下载最低可用流 + PySceneDetect 选场景代表帧 + 产出语料候选 |
| `.github/workflows/video-ingest.yml` | 在 CI runner 上装 yt-dlp / scenedetect / opencv / imageio-ffmpeg，跑 ingest，上传 artifact |
| `tools/video-frames/hd.mjs` | **保留为 fallback**（已验证可跑通 B站 cookie 抽帧） |

> 本机实测：无 `python` / `python3` / `pip` / `yt-dlp` / `ffmpeg` / `ffprobe`。
> 按工作区规范「构建与测试走 GitHub Actions，不在本机跑」，因此 ingest 的验证放在 CI。
