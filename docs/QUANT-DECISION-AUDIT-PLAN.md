# 量化决策体系：审计方案（2026-11-07）

> 目标：大量样本化 → 敌人/我方数据量化 → 大量真实数据 → API 自动决策 + 语料兜底。
> 本文件是**执行前审计**：先讲清现状、能做什么、不能做什么，再给分阶段计划与验收标准。

## 1. 现状审计（实测，非估计）

### 1.1 已有数据资产

| 侧 | 文件 | 内容 | 可用性 |
|---|---|---|---|
| 敌人 | `data/season29.json` | 5 难度 × 12 幕：`level`、`boss`、`lunar`、`encounters[{name, enemies(波次编成), res(逐怪抗性%)}]`；`bossStages` 给出 act→首领 | **可直接量化**（结构化、带抗性数值） |
| 敌人（文本机制） | `data/act-playbook.json` | 每幕 `boss` 机制描述（怒气条/护盾/破锅/标记）、`next_action`、`need_element` | 部分量化（文本+"需要什么元素"） |
| 我方 | `data/characters.json` | 129 个 `zhNames`、`elements` 元素表 | 静态、可用 |
| 我方 | `data/substitutes.json` | 20 角色 + 7 条职能替代规则 + `seasonElements` | 静态、可用 |
| 我方（**真池子**） | `roster.json` | **不存在** | ❌ 缺：不知道你有谁/练度/耐力 |
| 知识 | registry（账本） | 695 候选 / 767 证据 / 50 视频 / PROVISIONAL 22 / **VERIFIED 2** | 可用，已发布 release |
| 知识 | `data/knowledge-release.json` | `第3幕 推荐 爱诺`、`第2幕 推荐 夜兰`（各 2 作者、holdout 22 通过） | 客户端已能读 |

### 1.2 缺口（必须先讲清，不能假装有）

1. **我方池子是空的**：没有 `roster.json` 就没有"你的最优"，只能给"通用最优"。
   获取方式：① 游戏内跑 `--log-scan`（需要游戏启动一次）；② 手填 `roster.json`。
2. **敌人机制缺时间窗的量化**：`season29.json` 有抗性和编成，但"满条后 15 秒力竭"这类只在 `act-playbook.json` 的文本里。
   要做量化就得把文本里的时间窗抽成字段（`window_s`），且**必须标注来源**，不能编。
3. **样本带行动标签的很少**：现有 533 帧量化里，能同时确定"幕次+编队+选项"的仅 44 帧；
   真正"玩家当时点了什么"的标签目前是 0（视频里选人界面的选中态还没识别）。
4. **LLM 成本**：Pollinations 免 key 但 ~11.2 s/次、30 s/IP，**不能做每帧决策**，只能低频解释/裁决。

## 2. 量化模型（设计）

### 2.1 敌人侧 `enemy_profile(act, difficulty)`

```jsonc
{
  "act": 3, "level": 95, "boss": "翠翎恐蕈",
  "waves": [{"name": "战斗·普通", "enemies": "…"}],
  "resist": {"Physical": -20, "Electro": 0, "...": null},   // % ，来自 season29.json
  "mechanic": {"window_s": 15, "trigger": "Electro", "effect": "力竭+全抗-25%",
               "source": "act-playbook.json"},               // 文本里的时间窗，带来源
  "need": {"Electro": 1.0, "Geo": 0.0}                       // 需求向量（见 2.3）
}
```

### 2.2 我方侧 `team_profile(roster)`

```jsonc
{
  "members": [{"name": "行秋", "element": "Hydro", "role": "HydroApplier",
               "level": 90, "trial": false, "stamina_cost": 1}],
  "cover":  {"Hydro": 1, "Electro": 0, "Geo": 0},   // 元素覆盖人数
  "roles":  {"HydroApplier": 1, "Healer": 0},        // 职能覆盖
  "full": false                                      // false = 池子缺失/不完整
}
```

### 2.3 需求向量 `need[element]`

来源优先级（每一档都写进 `source`，缺就留空，不外推）：
1. `res[element] < 0`（抗性为负 = 该元素有效）→ 权重按 `-res/20` 归一
2. `playbook.need_element`（机制要求，如第3幕 Electro）→ 权重 1.0
3. `seasonElements`（本季开放元素，只能从中选人）→ 作为**约束**而非加分

### 2.4 决策评分（可解释、纯函数）

```
score(team | act, difficulty) =
      w_res  × Σ_e  need[e] × cover_count[e]          // 抗性利用
    + w_role × role_coverage(need_role)               // 职能（治疗/破盾/挂元素）
    + w_know × verified_bonus(act)                    // 云端 VERIFIED 命中（2+ 作者共识）
    − w_stam × stamina_cost(team)                     // 耐力代价（留给 3/6/8/10 幕）
confidence = 1 − (次优分 / 最优分)                    // 分差归一，0..1
```

输出结构固定：`{decision, score, parts{res,role,know,stam}, confidence, sources[]}`。
**每个分项都要能单独解释**，方便审计"为什么推荐这个"。

## 3. 决策编排（三级兜底 + 来源标注）

```
状态识别（act/difficulty/kind/options/team）
   ↓
[1] 量化评分器（本地，毫秒）  ── confidence ≥ 0.6 ──→ 直接出指示
   │ confidence < 0.6 或用户要看解释
   ↓
[2] LLM（Pollinations → OpenRouter → Groq）
   │ 输入：量化摘要 + 需求向量 + 语料片段 + 云端已验证知识（已脱敏）
   │ 超时/失败/无 key ──→ 降级
   ↓
[3] 语料兜底：knowledge-release（★） → act-playbook（◇） → guide-corpus 检索（○）
   │ 仍无
   ↓
[4] 规则兜底：现有 TheaterLogic（·）
```

**硬约束**
- LLM 输出**永不进知识库**，只做当场措辞/裁决；
- 每行指示必须带来源标记：`★云端已验证 / ◆量化 / ◇模型 / ○语料 / ·规则`；
- 任何一级失败都必须仍能给出指示（**不允许空白**）；
- 池子缺失时明确写"未读到你的角色池，按通用池给"。

## 4. 样本化（收集大量真实数据）

### 4.1 状态指纹（每帧落盘）

```jsonc
{"frame":"BV1xx-120s","act":3,"difficulty":"Lunar","kind":"shop",
 "options":["战斗·普通","伙伴"],"team":{"size":3,"target":4},
 "chars":["行秋","菲谢尔"],"stamina":null,"ts":120}
```

### 4.2 行动标签（需要识别"选中态"）

视频里选人/选卡界面的**高亮框/选中边框**是行动标签来源。未做之前，标签数为 0 —— 这是 P4 的主要工作量。

### 4.3 目标量（可验收）

| 指标 | 现状 | 目标 |
|---|---:|---:|
| 带状态指纹的帧 | 533（quant rows） | ≥ 2000 |
| 同时含 act+队伍+选项的帧 | 44 | ≥ 300 |
| 带真实行动标签的帧 | 0 | ≥ 200 |
| 跨作者 VERIFIED 知识 | 2 | ≥ 20 |

## 5. 分阶段执行计划

| 阶段 | 内容 | 验收 |
|---|---|---|
| **P1** | 数据契约 + 加载器：`EnemyProfile`（从 season29 + playbook 合成）、`TeamProfile`（从 roster/substitutes 合成）；`--enemy-profile` / `--team-profile` CLI | 12 幕都能输出或明确说"无数据"；缺 roster 时 `full=false` |
| **P2** | 量化评分器（纯函数）+ 断言 | 覆盖增加→分数单调增；需求元素缺失→分数降低；置信度 ∈[0,1] |
| **P3** | 三级兜底编排 + 来源标注 | LLM 关闭/超时/失败三态都能出指示，且来源标记正确 |
| **P4** | 样本化落盘（状态指纹）+ 行动标签（选中态识别） | 指纹 ≥2000 帧；标签 ≥200（可 PENDING） |
| **P5** | CI 门禁 + 真机验证 | 自检全绿；真机模拟 8/8 |

## 6. 风险与对策

| 风险 | 对策 |
|---|---|
| 敌人机制时间窗来自文本，可能过时 | 每条带 `source` 与抓取日期；只有 `inGame` 来源才用于"硬"决策，其余降级为提示 |
| roster 缺失 → 个性化决策无意义 | `full=false` 时明确标注"通用池"，不假装个性化 |
| LLM 幻觉写成事实 | LLM 输出不进知识库；只允许改写量化结论，不允许新增机制事实 |
| 样本标签靠 OCR 选中态，准确率未知 | 先量测再启用；未达标前不计入训练 |
| 评分权重拍脑袋 | 权重进 `data/quant-model.json`，可调、可回归；断言锁定单调性 |

## 7. 本轮执行的边界（诚实声明）

- P1/P2 可**完全本地 + CI 验证**，不依赖网络与游戏。
- P3 依赖 LLM 端点：默认开启（Pollinations 免 key），失败自动降级，**CI 里容错不阻断**。
- P4 的"行动标签"需要选中态识别，属新识别能力，先做量测再决定是否纳入训练。
- 真机验收仍需你启动一次游戏（`--log-scan` 拿池子 + 悬浮窗实测）。
