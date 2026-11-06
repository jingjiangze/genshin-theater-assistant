# HANDOFF（2026-11-06 16:24 Asia/Shanghai）

会话健康闸命中：本会话轮数 / 时长已超阈值，按 AGENTS.md §2 收口，新会话继续。

## 1. 当前真值（已实测，勿凭记忆）

| 项 | 值 |
|---|---|
| 仓库 | `C:\DDDD\Agent Work\_tmp\theater-overlay`（唯一工作树，main） |
| 本地 HEAD | `e83081b28242d524a11fad7b6a8ee52721573dee` |
| 远端 main | 同上 |
| `automation-data` | `99652d28a568a8d512f0c0ba5744114b305545a9` |
| 工作树 | 干净 |
| 其他分支 | `automation/cloud-auto-evolution`（67c4c64，已合并进 main）、`automation-data` |
| 最新 release | `v0.20.0`（exe 100,757,879 B） |

## 2. 本会话最后完成的事（turn 44）

1. 接管并行会话停下的云端分支：审计确认 `merge-tree` 无冲突，`git merge origin/automation/cloud-auto-evolution --no-edit` 成功（合并提交 `c0779ea`），main 上后续的 3 个提交（`--substitutes` / `--compare-ops` / 职能命中率）全部保留。
2. 修复并行分支遗留的 **`video-eval` UTF-8 BOM 崩溃**（`json.decoder.JSONDecodeError: Unexpected UTF-8 BOM`）：
   - 新增 `tools/pipeline/jsonio.py`（`read_json`/`read_text` 用 `utf-8-sig`，`write_json` 无 BOM+LF）
   - 4 个脚本接入：`build_external_eval.py` / `extract_knowledge.py` / `persist_batch.py` / `release_knowledge.py`
   - 机械替换曾把 `extract_knowledge.py` / `release_knowledge.py` 的本地 `read_json` 变成**无限自递归**，已改为 `jsonio_read_json` 别名并核对无残留
   - 新增 `JsonIoBomTest.test_bom_tolerant_json`
3. 补齐 `data/schemas/evaluation.schema.json`（独立真值来源禁止 `production_ocr_prediction`、按 video/creator/season 分组隔离、无真值时准确率必须 N/A）。四类 Schema 齐全。
4. 审计写入 `AUDIT-2026-11-05.md` §33；`findings.md` 在本目录。

## 3. 已验证的 CI（全部真实 run，勿重复跑）

| run | 工作流 | 结果 |
|---|---|---|
| `42182354751` | ci | success：自检 **74/0**、渲染 7/7、真机 8/8 |
| `42182750954` | cloud-branch-validation | success：`Ran 19 tests`，`test_bom_tolerant_json ... ok` |
| `42182755143` | pipeline-ledger-smoke | success |
| `42182880450` | knowledge-extract | success：`candidates=37`，无新证据 |
| `42182916029` | auto-release | success：无 VERIFIED 候选 → NOOP |
| `42182875598` | video-eval | **success**（BOM 修复验证）：19 步全过，外部文本评测 + 批次持久化 |
| `42182354751` 后的 `ci main 08:13:29` | ci（文档推送触发） | success |

video-eval 批次 `42182875598-attempt-1`，状态 `INSUFFICIENT_EXTERNAL_LABELS`（外部双通道标签不足，按设计不虚报准确率），账本提交 `99652d28`。

## 4. 持续监听结论

两轮 10+9 分钟 + 一轮 6 分钟（带 API 重试/空值容错）：main `e83081b`、automation-data `99652d28` 全程稳定，无新增 workflow 运行、无并行推送。一次 `gh api` 瞬时返回空属网络抖动（下一轮恢复），不是真实变更。后台 job `pwsh-db2cc6a070cd4f0c` 仍在跑（纯监听，可 `job_kill`）。

## 5. 待办（下一会话从这里继续，按优先级）

1. **P1 独立评测出可用标签**：当前 `INSUFFICIENT_EXTERNAL_LABELS`，字幕+章节双通道一致的样本不足。需更多带章节的视频，或降低/调整窗口策略（但不能退回用 OCR 同源真值）。
2. **P2 知识门禁**：`verified=0`、`auto-release` NOOP 是正确行为；要让候选晋级，需 `holdout_samples>=20` + strategy gate + counter-evidence resolved。检查 `release_knowledge.py:eligible()` 的字段是否由某条工作流填充，若无人填充则设计自动验证器。
3. **作者独立性**：并行分支已加 `creator_id` 去重，需确认 `ingest.py` 是否真的写入 `creator_id`（`yt-dlp` 的 `uploader_id`），否则 `creator_count` 恒为 0。
4. **旧数据源兼容**：`corpus-refresh.yml` 已改为候选批次、不自动推 main；确认 `commit` 默认值已 false 且无回退。
5. **版本发布**：main 已远超 v0.20.0，但尚未打新 tag；待 1–4 有结论后发布 v0.21.0（release 工作流三道门：自检/渲染/真机）。
6. **边界不变**：YouTube 需用户提供 cookie（不绕登录墙）；B站流下载 HTTP 567 未绕过；用户贴过的 YouTube 登录态 cookie 未读取/未写仓库/未使用，建议轮换。

## 6. 红线提醒（给下一会话）

- `.zcode/` / `.mimosa/` 不碰；临时态放 `_tmp/<task>-*/`；不覆盖他会话文件
- 不在本机跑 CI/构建（本机无 python/dotnet SDK）；一律 GitHub Actions
- 不 `git checkout .` / `restore .` / `clean`
- 提交用 `git -c commit.gpgsign=false commit`；tag 用 `git -c tag.gpgsign=false tag`
- 并行分支 `automation/cloud-auto-evolution` 已合并，如仍在推送先核对再动
