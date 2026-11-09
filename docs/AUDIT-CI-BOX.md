# CI 自动流与 box 审计（2026-11-09）

## 一、CI（GitHub Actions）

### 结构：12 个工作流全部 active

| 工作流 | 触发 | 职责 |
|---|---|---|
| `ci` | push / PR / 手动 | 编译 + 140 断言 + 真机模拟 + WPF 发布 |
| `video-ingest` | push(paths) / 每日 06:00 / 手动 | 采集文本语料 + 优选清单 |
| `video-eval` | push / 每日 05:00 | 抽帧 + 量化对照 |
| `knowledge-extract` | push / 每日 07:30 | 证据治理 + 候选 |
| `auto-release` | push / 每日 08:30 | 知识发布 |
| `corpus-refresh` | 每周一/四 04:00 | 语料刷新 |
| `season-plan` | 每月 1 日 04:00 | 逐赛季配队 |
| `box-wake` | workflow_run / 每 6 小时 | 按策略唤醒 box |
| `cloud-branch-validation` | push | Python 契约 |
| `adapt-videos` / `pipeline-ledger-smoke` / `release` | 手动 | 补充用途 |

### 审计发现

| # | 现象 | 判定 |
|---:|---|---|
| 1 | `schedule` 事件**确实发生过**（video-ingest 3 次、video-eval 4 次、auto-release 3 次、corpus-refresh 1 次） | 定时**能用**，但频次远低于配置 |
| 2 | 最近 20 小时无定时触发（最后一次 11-08T17:32） | **不可靠**：GitHub 高峰期会延迟/跳过 schedule，属平台行为 |
| 3 | `ci` 大量 `cancelled` | `concurrency` + 密集 push 所致；最后一次 success 说明代码没问题 |
| 4 | **优选清单停在 12 条（旧），而 CI 曾成功推到 34 条** | **严重：双写冲突** |

### 关键缺陷：双写冲突（已修）

**证据链**：

```
CI run 42505478504 → [pick] 清单共 34 条 → commit 4ddb93d "[skip ci]" → 已推送
但远端只有 12 条，generatedAt = 11-08T15:50（更早）
```

**根因**：`sync-to-github.mjs` 做**全量同步**，用本地旧的 12 条覆盖了 CI 刚推的 34 条。

**修复**：

1. `sync-to-github.mjs` 增加 `CI_OWNED` 排除列表（5 个文件）：
   - `data/pure-sources.json`（video-ingest 累积）
   - `data/team-plans-season29.json` / `data/global-alloc-season29.json`（season-plan 产出）
   - `data/knowledge-release.json`（auto-release 发布）
   - `data/box-state.json`（box-wake 状态）

2. 重新触发 `video-ingest` 让 CI 自己重建清单 → 当前 **19 条**（本轮采集 20 个视频新增）。
   历史 34 条因被覆盖已丢失，会随每日采集逐步累积回来。

**沉淀的规矩**：**写权必须唯一**。CI 产出的数据文件本地只读，绝不参与全量同步。

## 二、box（云端 Linux）

| 项 | 审计时状态 | 处置 |
|---|---|---|
| 状态 | `hibernated`（空闲休眠） | 已 `box_ensure` 唤醒 → `running` |
| `linux-loop.sh` | **已停**（休眠时进程随之消失） | 已重启（pid 15004，900s 间隔） |
| 语料 | 同步前 **23 条**（旧） | `git reset --hard` 后 **786 条** ✅ |
| 优选清单 | 12 条 | 跟随 CI 恢复为 19 条 |
| 上一轮 loop | `cycle#1 cloud-video exit=0` / `llm OK` / `python OK` | 运行正常 |

### 审计发现

| # | 现象 | 判定 |
|---:|---|---|
| 1 | box 会因空闲自动 hibernate，loop 随之停止 | **平台行为**，非故障；已配 `box-wake` 事件驱动唤醒 |
| 2 | 语料停留在旧版（23 条） | box 不做自动 pull（loop 里 `git pull --ff-only` 静默失败），需手动 reset |
| 3 | `box-wake` 只运行过 1 次（手动触发） | 它的 schedule 与 workflow_run 触发尚未真正生效，需观察 |

## 三、数据流现状

```
B站视频（采集） → 字幕 → act-teams（每幕真实编队）
                                    ↓
BWIKI / 米游社（攻略平台） → 语料 786 条 → GuideIndex → ○语料兜底层
                                    ↓
知识管线（ingest → extract → verify → release）→ knowledge-release.json
                                    ↓
客户端（WPF）→ DecisionOrchestrator → 悬浮球
```

各环节当前状态：

| 环节 | 状态 |
|---|---|
| 语料（786 条） | ✅ 已同步到 box 与客户端 |
| 优选清单 | ⚠️ 19 条（被覆盖后重建中） |
| 知识 release | ✅ ACTIVE（verified 2 条） |
| 客户端决策层 | ✅ 已接入生产（★已验证 0.70） |
| 主循环 | ✅ 已接（抓屏→OCR→决策→悬浮球） |

## 四、遗留风险

| 风险 | 影响 | 建议 |
|---|---|---|
| **schedule 不可靠** | 采集/发布可能几天不跑 | ① 加 `box-wake` 的 workflow_run 兜底；② 关键链路改为 push 顺带执行 |
| **box 会休眠** | loop 停，需人工唤醒 | 接受此行为；或改用 GitHub Actions 承担长跑（box 仅作加速器） |
| **CONCURRENCY 取消** | 密集提交时浪费运行 | 保留（防止堆积），但注意别连续推同一个工作流 |
| 清单历史丢失 | 需重新累积 | 靠每日采集自然恢复 |

## 五、本次修复清单

| # | 修复 | 文件 |
|---:|---|---|
| 1 | 双写冲突（CI_OWNED 排除 5 个文件） | `_tmp/sync-to-github.mjs`（工具，未入仓） |
| 2 | 唤醒 box + 重启 loop | 云端 |
| 3 | box 仓库同步到最新（语料 786 条） | 云端 |
| 4 | 重新生成优选清单（12 → 19 条） | CI `video-ingest` |
