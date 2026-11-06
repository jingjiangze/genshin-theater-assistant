# P0 云端运行基础审计（2026-11-06，实测）

> 用户贴出的架构审计以 HEAD `4551ebb` 为基线；实测当前 main = `4ee0d28`，`4551ebb` 是其祖先。
> 之后 5 个提交已做：`--substitutes` / `--compare-ops` / 职能命中率 / NullReference 修复 / ingest 去下载。
> 审计结论需按当前真值更新，不能沿用旧快照。

## 1. 实测事实

| 项 | 真值 | 证据 |
|---|---|---|
| 当前 main HEAD | `4ee0d28` | `git rev-parse HEAD` |
| 远端 main | 同 `4ee0d28` | `git ls-remote origin refs/heads/main` |
| 工作树 | 干净 | `git status --short` 为空 |
| 本地分支 | 仅 main | `git branch -a` |
| active workflows（GitHub API） | 10 个：6 个在 main 树里，另有 `auto-release` / `cloud-branch-validation` / `knowledge-extract` / `pipeline-ledger-smoke` 来自**其他分支的运行** | `gh api .../actions/workflows` + `git/trees/main?recursive=1` |
| main 树里的工作流 | `adapt-videos` / `ci` / `corpus-refresh` / `release` / `video-eval` / `video-ingest` | 同上 |
| 数据目录 | 12 个扁平 `data/*.json`，**无** `schemas/ evidence/ candidates/ knowledge/ manifests/ evaluations/` | `Get-ChildItem data` |
| 自动写 main 的工作流 | `corpus-refresh`（默认 commit=true，直接 `git push`）、`adapt-videos`（可提交模板） | 文件内容 |
| 唯一 ubuntu runner | `video-ingest`；其余 5 个都是 `windows-latest` | 工作流 `runs-on` |
| 数据引用 | 运行时仍直接读 `guide-corpus.json` / `rotations.json` / `season29.json` / `substitutes.json` 等扁平文件 | `Select-String app/*.cs AppPaths.Data(` |

## 2. 与用户路线图的差距（P0 要解决的）

1. **没有版本化数据生命周期**：所有产物都直接写 `data/*.json`；没有批次 ID、manifest、增量游标。
2. **自动采集可直接改正式知识**：`corpus-refresh` 定时跑完就 `git push` 语料文件，违反「自动采集只出候选，正式知识需门禁」。
3. **来源只有字符串**：`*-ops.json` 里有 `source: bilibili:BVxxx@10s`，但没有独立作者 ID、平台、采集批次、原始证据哈希。
4. **共识按 BV 计数**：`OpsConsensus.Videos` 统计不同 BV 号，无法区分同作者多视频 / 转载 / 同文案搬运。
5. **评测真值同源**：`ReplayRunner` 从同一条带 OCR 产生 `TruthAct`，又用它评测预测（审计已指出）；且模板命中时 band=null 导致真值缺失。
6. **云端结果不可复用**：artifact 只保留 30 天，没有 `data/manifests/` 记录"上次处理到哪一批"。
7. **并行会话存在**：其他分支已有 `auto-release` / `cloud-branch-validation` / `knowledge-extract` 等运行，需在 main 上小步推进、不覆盖他人文件。

## 3. P0 实施范围（本次，最小可验证）

按「云端优先、自动门禁、取消人工审核、不重写客户端」的要求，本轮**只做数据与门禁骨架**：

- 新增 `data/schemas/`：`evidence.schema.json`、`candidate.schema.json`、`knowledge.schema.json`、`evaluation.schema.json`
- 新增 `data/manifests/pipeline.json`：批次 ID、游标、算法版本、知识版本、上次成功发布版本
- 新增 `tools/pipeline/`（纯 CPU、本地可跑、CI 可跑）：
  - `validate.py`：Schema 校验 + 证据/候选/知识完整性 + 重复检测
  - `evolve.py`：候选 → 自动状态机（CANDIDATE/PROVISIONAL/VERIFIED/CONFLICTED/STALE/REJECTED）+ 版本化知识发布/回滚
  - `dedup.py`：来源去重（精确 ID/内容哈希/SimHash 近似）+ 作者独立性统计
- 新增 `.github/workflows/knowledge-validate.yml`：L0（每次 PR/push 校验）+ L1（增量处理）+ 自动门禁；**不自动改正式知识除非通过**
- 把 `corpus-refresh.yml` 的 `commit` 默认值改为 `false`，改成"产出候选 + 报告"，由 `knowledge-evolve` 门禁决定是否发布
- 不改 C# 客户端主链路、不引数据库/向量库/模型微调

## 4. 明确不做（避免与并行分支冲突）

- 不创建 `auto-release.yml` / `cloud-branch-validation.yml` / `knowledge-extract.yml` / `pipeline-ledger-smoke.yml`（其他分支已有）
- 不重写 `ci.yml` / `video-eval.yml` / `video-ingest.yml` 的既有成功逻辑
- 不删除或迁移现有 `data/*.json`；旧文件继续由客户端读取，新体系并行写入 `data/evidence|candidates|knowledge|evaluations`
- 不启用任何需要 YouTube cookie / B站流下载的路径
