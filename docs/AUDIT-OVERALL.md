# 项目整体架构审计（2026-11-09，v0.51.0 时点）

> 方法：以代码结构、调用关系、数据耦合与断言性质为证据，找**结构性**问题（不是局部 bug）。
> 每条给出「证据 / 影响 / 更好的方法」，末尾给优先级。

## 结论先行

**项目最大的问题不是某个功能没做对，而是：我们花了大部分力气建的「决策层」，玩家实际用的还是旧的规则引擎。**

其余问题都是次要的。封号红线守得住（8 项禁用 API 全部 0 命中）。

---

## P0（致命）：决策层未接入产品主循环

### 证据

| 事实 | 数据 |
|---|---|
| 生产主循环调用的是旧逻辑 | `app/OverlayForm.cs:566` → `TheaterLogic.Evaluate(hits, text, _theater, _roster, bandText)` |
| 旧逻辑**完全不含量化体系** | `grep -nE "QuantScorer\|TeamSolver\|CuratedPicks\|CharacterStrength\|DecisionOrchestrator" app/TheaterLogic.cs` → **0 命中** |
| 新决策层只被测试与 CLI 调用 | `DecisionOrchestrator.Decide` 的调用方只有 `LiveProbe.cs`、`SelfTest.cs` |
| 未接入的代码规模 | `QuantData 374 + QuantScorer ~200 + TeamSolver 443 + GlobalAllocator 399 + CuratedPicks ~200 + CharacterStrength ~290 + DecisionOrchestrator ~200 + QualityGates ~330` ≈ **2400 行** |

### 影响

- 幕级推荐（社区共识 + 已验证知识）、角色强度、幕间优先级、量化缺口分析、三级兜底
  **全部只在 CI/CLI 里跑，玩家看到的指示仍来自硬规则**；
- 我们为"实现幻想的决策层"做的所有工作，**在产品侧收益为零**；
- 更糟：CI 全绿（139 断言）会让人以为这些能力已上线，属于**测量对象错位**。

### 更好的方法（渐进，不推翻现有）

**不要一次性替换**（旧 `Advice` 没有 `Options` 字段，直接换会丢信息、风险高）。分三步：

1. **合并输出而非替换**：`TheaterLogic.Evaluate` 结果保留，追加 `DecisionOrchestrator.Decide` 的行，
   用 `◆/★/○/·` 来源标记区分。玩家立刻能看到量化配队与推荐。
2. **让 `DecisionOrchestrator` 成为唯一"建议文本"产地**：`TheaterLogic` 退化为数据加载器
   （它现在 471 行里约 2/3 是 Loader）。
3. **最后**才删旧分支，并把 `--quality-gates` 的判据接到生产路径上（现在只在 CLI）。

判定接入成功的标准（可断言）：真机模拟里悬浮窗的 Lines **必须含 `◆` 或 `★` 来源标记**。

---

## P1（高）：139 条断言是"自证"，缺外部真值

### 证据

G1–G7、Q1–Q6、赛季时效、强度量化……全部是「实现是否符合我写的规格」。
唯一的外部观测是 `--quant` 的**人物命中率 / 决策编号有效率**，但它来自视频帧，且人物命中率只有 12.5%–54.5%。

### 影响

- 断言全绿**不能推出"玩家能打满"**（这一点我在 §54 已明确，但架构上仍未补）；
- 反复出现"CI 绿、box 红"（§63 的 G1 违规 14 项就是 CI 没覆盖 `CheckAllocation`）。

### 更好的方法

1. **扩大外部观测面**：把 `--quant` 的指标（决策编号有效性、人物识别率、幕次检测率）设为 CI 硬门槛，
   而不只是打印；
2. **引入可证伪假设**：对每条已验证知识记下"若第 N 幕推荐 X，则 X 应在该幕出场"，
   用真实视频帧验证**出场**（可自动），这比"推荐分高"强得多；
3. 最理想是用户的一次真实通关结果作为 holdout——但这依赖用户，不能设计成必要路径。

---

## P2（中）：数据层无 schema 校验，静默降级

### 证据

`season29.json / decision-policy.json / characters.json` 各被 **13–16 个源文件**引用，
读取全部是 `TryGetProperty` 宽容式解析，缺字段就静默跳过。

历史上已经因此出过多次问题：
- `LoadVerified` 正则丢反斜杠 → 静默 0 条，断言却通过；
- `characters.json` 结构误判 → 覆盖度 0、分数全 0.20；
- `roster` 只读了 `key/level`，把 GOOD 的强度字段全丢了。

### 更好的方法

1. **关键字段 fail-fast**：启动时校验必需字段（元素列表、幕数、`teamSize`、`zhNames` 非空），
   缺失**直接报错退出**，而不是静默用默认值；
2. 给 `data/*.json` 加 JSON Schema，CI 里跑校验（可复用已有 34 条 Python 契约的位置）；
3. 把"静默降级"改为"显式降级"——所有降级必须出现在 `Provenance` 里（编排器已有这个字段，但数据层没有）。

---

## P2（中）：五个 Runner 重复实现「帧 → 事实」

### 证据

| 文件 | 行数 | 职责 |
|---|---:|---|
| `QuantRunner` | 361 | 帧 → 量化对照 |
| `BatchRunner` | 421 | 帧 → 模板 |
| `LiveProbe` | 394 | 假窗口 → 抓屏 → 识别 |
| `ReplayRunner` | 251 | 帧 → 重放 |
| `OfflineRunner` | 122 | 离线批跑 |
| 合计 | **1549** | |

它们各自做 `SKBitmap/Bitmap 解码 → Matcher.Scan → OcrService.Recognize → 组装 text`，
**同一段流程写了五遍**。

### 更好的方法

抽一个 `FrameFacts`（帧 → `{act, kind, hits, text, bandText}`）的**单一实现**，
五个 Runner 只保留各自的「输入来源」与「输出格式」。预期能删掉 300–400 行重复，
且新增一种观测方式时不用再改五处。

---

## P3（低）：审计文档 64 节按时间线堆积，已难检索

`AUDIT-2026-11-05.md` 按时间顺序写了 64 节，找"某个问题修没修"要全文搜索。

**更好的方法**：保留时间线（历史价值），另建**按主题的索引**：
`技术债清单 / 已修与证据 / 已知限制`，每项指向具体节号。

---

## P3（低）：测试代码 1315 行 > 产品主窗体 936 行

`SelfTest.cs` 1315 行是 `app/` 里最大的文件。
对这个项目（判据密度决定可信度）不一定是坏事，但要承认它带来两个成本：
改一处逻辑往往要同步改断言；断言本身写错会制造"假绿"（已发生 3 次）。

**更好的方法**：断言按模块拆分文件（`SelfTest.Gates.cs` / `SelfTest.Quant.cs` / …），
并给每条断言标 `[Spec]`（验证规格）或 `[Observed]`（验证外部观测），后者才是真可信。

---

## 底线确认：封号红线

| 禁用 API | 命中 |
|---|---:|
| `OpenProcess` / `ReadProcessMemory` / `WriteProcessMemory` | 0 |
| `CreateRemoteThread` / `SetWindowsHookEx` | 0 |
| `SendInput` / `keybd_event` / `mouse_event` | 0 |

**全部 0 命中**，红线守住。这一项是项目唯一不能商量的约束，当前状态正确。

---

## 优先级建议

| 序 | 事项 | 理由 |
|---:|---|---|
| 1 | **P0 决策层接入生产**（三步渐进） | 决定整个项目是否真的交付了目标 |
| 2 | P1 外部观测设为硬门槛 | 否则断言再多也证明不了"能打满" |
| 3 | P2 数据层 fail-fast | 已实际导致 3 次静默错误 |
| 4 | P2 抽 `FrameFacts` 去重 | 纯工程收益，风险低 |
| 5 | P3 文档索引 / 断言拆分 | 长期可维护性 |

**先做 1**：它是"实现幻想决策层"这个目标的唯一实质卡点。
