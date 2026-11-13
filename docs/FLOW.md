# 全流程图与流程审计（2026-11-08）

> 每条路径都标注了**实测状态**，并区分「能跑通」与「结果可信」——这两件事不是一回事。

## 0. 一页总览：三个执行环境

```
┌─────────────────── 本机 Windows（开发）───────────────────┐
│ 写代码 / WinForms 悬浮窗 / 真机抓屏                          │
│                                                          │
│  ✗ github.com:443 不通（TLS 000 超时）                     │
│  ✓ api.github.com 可达                                    │
│         │                                                │
│         └──> gh-commit.mjs（GitHub Contents API）───┐      │
└───────────────────────────────────────────────────│──────┘
                                                    │
                                    ┌───────────────▼──────────────┐
                                    │  GitHub  repo (main)         │
                                    │  data/ + app/ + cli/ + tools/│
                                    └───────────────┬──────────────┘
                                                    │
        ┌───────────────────────────────────────────┼──────────────────────┐
        │                                           │                      │
┌───────▼─────────────┐              ┌──────────────▼──────────┐  ┌────────▼─────────┐
│ GitHub Actions      │              │ 云端 box (Simular)       │  │ 玩家 Windows     │
│ (Windows runner)    │              │ Ubuntu / EPYC / 4核      │  │ 单文件 exe       │
│                     │              │                          │  │                  │
│ ci.yml      124 断言│              │ cloud-all.sh  17 步 PASS │  │ 悬浮窗 + 语音     │
│ release.yml tag→exe │              │ cloud-ocr.sh  v5/v6 交叉 │  │ 离线可用          │
│ video-ingest 采集    │              │ linux-loop.sh 持续回归    │  │                  │
│ knowledge-extract   │              │                          │  │                  │
│ verify → release    │              │ ✓ git push 可达           │  │                  │
│ season-plan 配队     │              │ ✗ 无 GPU / 无 cookie     │  │                  │
└─────────────────────┘              └──────────────────────────┘  └──────────────────┘
```

**为什么需要三个环境**：本机写代码但推不上去（走 API）；Actions 有 Windows 能力（真机模拟、exe 打包）但无持久状态；云端 box 有持久 Linux 和 git 通道，适合跑长流程回归。

## 1. 云端一条命令：`cloud-all.sh` 的 17 步

```
$ bash tools/ci/cloud-all.sh --frames ./frames-sample --sync

 ┌─ 0. (--sync) git fetch + reset --hard origin/main
 │
 ├─ 1. 能力矩阵 ......... 内置声明「能验/不能验」，先把边界写清
 │
 ├─ 2. 构建 CLI ......... cli/TheaterCli.csproj  → 只编纯逻辑（无 WinForms/OCR 依赖）
 ├─ 3. 构建 OcrProbe .... cli/OcrProbe           → RapidOcrNet + SkiaSharp
 │
 ├─ 4. --policy ......... 策略来自 data/decision-policy.json
 ├─ 5. --seasons ........ 自动发现 data/season*.json
 ├─ 6. --playbook ....... 幕内知识（首领/该做什么/需要什么元素）
 ├─ 7. --ocr-tuning ..... 线程 + DirectML 开关（云端必然 CPU）
 ├─ 8. --ocr ............ BetterGI 对齐：DropScore + 同排排序
 │
 ├─ 9. --enemy-profile .. 抗性/波次/机制窗口 → 需求向量
 ├─10. --quant-decide ... 需求 × 覆盖 → 分数 + 置信度 + 缺口
 ├─11. --team-solve ..... C(n,4) 完全枚举，全难度逐幕最优
 ├─12. --global-alloc ... 跨幕分配（同难度内角色不重复 + 治疗/破盾硬约束）
 ├─13. --season-smoke ... 临时造 season30（元素改 Pyro/Cryo/Dendro）验证自动适配
 ├─14. --orchestrate .... ◆量化 / ◇模型 / ·规则 三态
 │
 ├─15. --llm-probe ...... 主通道（有 LLM_API_KEY 才跑）
 ├─16. cloud-ocr.sh ..... v5-mobile vs v6-small 速度 + 交叉一致率
 └─17. python unittest .. 知识管线契约 34 项
 │
 └─> cloud-run-report.md / .json（每步退出码 + 关键输出 tail）
```

**设计要点**：每步失败**不中断**（一个环境小问题不该掩盖后面全部结果），全部跑完再汇总；`--push` 可把报告推回 GitHub。

## 2. 实际跑出来的结果（2026-11-08 云端）

```
════════ 汇总: PASS（17 步，失败 0）════════
```

| 步骤 | 退出码 | 判定 |
|---|---:|---|
| 能力矩阵 / 构建 ×2 / 数据策略 ×5 | 0 | PASS |
| 决策链 ×6（敌人→量化→配队→全局→赛季冒烟→编排） | 0 | PASS |
| 模型通道（主） | 0 | PASS（442 ms，pollinations ok） |
| 云端 OCR 验证 | 0 | PASS（v5 1193 ms / v6 1132 ms，相似度 85.5%） |
| Python 单测 | 0 | PASS（34 tests OK） |

## 3. 流程审计：PASS 的含金量分层（重要）

**17 步全 PASS 不等于 17 项都被验证到位**。按"判据强度"分三层：

| 层级 | 步骤 | 实际验证了什么 | 可信度 |
|---|---|---|---|
| **A. 有独立判据** | `--season-smoke` | 造 season30 后断言「当前=30、旧赛季保留、新元素生效、幕数>0」，4 项都真判 | **高** |
| | Python 单测 | 34 个断言含 held-out 隔离、证据治理、跨赛季不合并 | **高** |
| | OCR 交叉验证 | 两模型字符相似度 ≥80% 阈值 + 逐帧数值 | **高** |
| | `--orchestrate` 三态 | 断言输出来源必须是 ◆/◇/·，且用**无已验证知识的幕**（避免假绿） | **高** |
| **B. 有结构但不判对错** | `--quant-decide` | 能算出分数/缺口/来源；但"这个分数是否合理"无断言 | 中 |
| | `--team-solve` / `--global-alloc` | 能出 4 人队、无重复、缺口可列；但"队伍是否真能通关"无判据 | 中 |
| | `--playbook` / `--policy` / `--seasons` | 只证明"数据能被读到并打印" | 中 |
| **C. 仅证明能跑不崩** | `--enemy-profile` | 打印抗性/需求，无正确性断言 | 低 |
| | `--llm-probe` | 证明端点通、返回非空；不判断内容对不对 | 低 |
| | 能力矩阵 | 只是声明文本 | 低 |

**结论**：C 层（3 步）只保证可用性；B 层（5 步）保证可解释性；真正的正确性证据集中在 A 层（4 步）+ Actions 的 124 条 C# 断言。

## 4. 审计发现的问题（本轮已修 / 待修）

| # | 问题 | 影响 | 状态 |
|---:|---|---|---|
| 1 | 云端 17 步里 **5 步属 C 层**却与 A 层同样标 PASS | 容易误读为"全都验证过了" | **已修**：报告分能力矩阵 + 本文档标注含金量 |
| 2 | OCR 速度**两次测量结论相反**（上次 v5 更快，本次 v6 更快） | 3 张样本噪声过大，不能下"谁更快"的结论 | **待修**：需 ≥30 帧 + 多轮取中位数 |
| 3 | `cloud-all.sh` 的 `--push` 依赖 `GH_TOKEN`，云端未常驻配置 | 报告只能留在 box | **待定**：可作为可选步骤（已在代码里） |
| 4 | 云端无 `.env.local` 时 `--llm-probe` 曾用 pollinations 兜底 | 容易把"免 key 兜底通"误当成"自己的端点通" | **已修（2026-11-14）**：`data/llm.example.json` 默认 `enabled:false`，只有 `TA_LLM_ALLOW_EXAMPLE=1`（或显式 `LLM_ENDPOINT`）才会联网；报告打印实际端点名 |
| 5 | 本机 `github.com` 不通，只能靠 Contents API 逐文件提交 | 无法用 git push / 无法推 tag | **已绕过**：`gh-commit.mjs`；tag 仍需 Actions |

## 5. 不能由云端验证的三件事（明确声明）

| 能力 | 为什么不能 | 由谁验证 |
|---|---|---|
| WinForms 悬浮窗 + 真机抓屏 | Linux 无桌面会话、无 WinForms | GitHub Actions（真机模拟 8/8）+ 玩家真机 |
| DirectML 实际加速 | 上游包停更 1.24.4；runner/box 均无 GPU | 玩家真机（代码已就绪，反射探测 + 优雅回退） |
| B站采集（视频/字幕） | 需登录 cookie，云端无浏览器登录态 | GitHub Actions（`BILI_COOKIE` secret） |

## 6. 决策链数据流（被 17 步覆盖的部分）

```
data/season29.json ──┐
data/act-playbook.json ──┤
data/decision-policy.json ──┤
data/characters.json ──┤    ┌──────────────────────┐
data/substitutes.json ──┼──>│ QuantData.Enemy()    │ 抗性/波次/机制窗口
data/roster.json(可选) ──┘    │   → Need[element]    │ 需求向量（只收本季元素）
                              └──────────┬───────────┘
                                         ▼
                    ┌────────────────────────────────────┐
                    │ QuantScorer  需求 × 池覆盖         │ 分数 + 置信度 + 缺口
                    │ TeamSolver   C(n,4) 完全枚举        │ 逐幕最优 4 人
                    │ GlobalAllocator 同难度内不重复      │ 跨幕分配 + 治疗/破盾硬约束
                    └──────────────┬─────────────────────┘
                                   ▼
                    ┌────────────────────────────────────┐
                    │ DecisionOrchestrator 三级兜底       │
                    │  buff随机→◇模型  验证知识→★        │
                    │  语料→○   规则→·                    │
                    └──────────────┬─────────────────────┘
                                   ▼
                    ┌────────────────────────────────────┐
                    │ 悬浮窗（Windows）                  │
                    │  每行带来源标记 + 随机点 + 缺口     │
                    └────────────────────────────────────┘
```

## 7. 复现

```bash
# 云端（Linux）
git clone https://github.com/jingjiangze/genshin-theater-assistant.git
cd genshin-theater-assistant
bash tools/ci/cloud-all.sh --sync                       # 17 步全流程
bash tools/ci/cloud-ocr.sh . ./frames-sample            # 仅 OCR 交叉验证
LLM_API_KEY=... LLM_ENDPOINT=... bash tools/ci/cloud-all.sh   # 带模型通道
```
