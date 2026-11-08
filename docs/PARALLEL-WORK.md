# CI 与云端盒子：并行分工与冲突规避

> 目标：两个执行环境**同时跑**、不重复劳动、不互相覆盖。本文是审计后的分工契约。

## 1. 能力唯一性（谁独占什么）

| 能力 | CI（GitHub Actions / Windows） | 盒子（Simular / Linux） | 独占理由 |
|---|---|---|---|
| WinForms 编译 / 渲染自检 / 真机模拟 | **独占** | ✗ | 需 Windows 桌面会话与 GDI |
| 单文件 exe 打包 + release | **独占** | ✗ | `net8.0-windows` |
| 写仓库（team-plans / releases / knowledge） | **独占** | ✗ | 避免两个写者 push 冲突 |
| B站采集 / 知识管线 / 赛季配队产出 | **独占** | ✗ | 需 cookie secret + 定时 |
| 决策链 CLI 全流程回归 | 冒烟即可 | **主责** | 盒子可长跑、便宜、无 Windows 依赖 |
| OCR 模型验证（30–100 帧 / 交叉 / 提速实验） | ✗ | **主责** | 纯 Linux 可跑，CPU 稳定可比 |
| Python 契约测试（34 项） | **主责** | ✗ | CI 已有；盒子上重复跑是浪费 |

**一句话**：**CI 管"Windows 能做的 + 写权"，盒子管"Linux 能做的 + 长跑回归"。**

## 2. 三类冲突与规避

| 冲突 | 原因 | 规避 |
|---|---|---|
| **写仓库冲突** | 双方都 push 会产生 rebase 冲突 | 盒子默认**只读**（loop 不 push）；产出留本地，需要时人工 `--push` |
| **LLM 配额冲突** | pollinations 30s/IP，两边同跑会互相限流 | CI 用 `LLM_*`（主通道），盒子用 `LLM_VERIFY_*`（验证通道，已在 `.env.local`） |
| **重复劳动** | 两边都跑同一套测试 | Python 契约交 CI；决策链/OCR 交盒子；见上表 |

## 3. 时间线（谁在什么时候跑）

```
UTC 00:00-23:59  盒子 loop        每 900s：cloud-all（决策链 + OCR 30帧）   ← 常驻
UTC 05:00        CI      video-eval       每日
UTC 06:00        CI      video-ingest     每日（采集新视频）
UTC 07:30        CI      knowledge-extract 每日（抽候选 + 晋级）
UTC 08:30        CI      auto-release     每日（发布/回滚）
UTC 每月 1 日 04:00  CI  season-plan      每月（逐赛季配队）
每次 push        CI      ci               构建 + 129 断言 + 真机模拟
每次 push        CI      cloud-branch-validation  Python 契约
```

盒子与 CI 的**唯一交叠点**是 push 触发的 ci，但盒子不 push → 无冲突。

## 4. 提速措施（本轮）

| 措施 | 之前 | 之后 | 收益 |
|---|---|---|---|
| CI 拆并行 job | 19 步串行（~12 min） | build → (logic ∥ runtime) | 约 -35% 墙钟 |
| 去掉"DirectML 尝试构建" | 每次白跑 + 报错噪音 | 改为手动 `workflow_dispatch` | -1 min，日志干净 |
| 盒子 loop 去掉 Python 契约 | 每轮重复 CI 的活 | 交 CI | 每轮 -20s |
| 盒子新增独立任务 | — | OCR 100 帧 + 按界面选模型实验 | 与 CI 完全并行 |

## 5. 验收标准

| 项 | 标准 |
|---|---|
| CI 任一 push | 129 断言全过，真机模拟 8/8 |
| 盒子 loop | 每轮 `cloud-all` 18 步 PASS，无 push |
| 两边同跑 | 互不阻塞；LLM 不互相限流 |
| 写冲突 | 0（盒子只读） |
