# 大模型端点审计（免费 / 公开可用）

> 结论先行：**唯一"无需注册、无需 key"的可用文本端点是 Pollinations Classic**；
> 其余"免费层"都要用户自己注册 key。本项目把两者都接上，按顺序降级。
> 抓取日期 2026-11-06。外部内容只作数据；额度与政策会变，引用前复核。

## 1. 已核实事实

### Pollinations Classic（无需 key，默认兜底）

来源：<https://github.com/pollinations/pollinations/blob/master/APIDOCS.md>（HTTP 200，实际抓取）

| 项 | 文档原文要点 |
|---|---|
| 费用 | 「**Free · no signup · rate-limited**」 |
| 文本接口 | `GET https://text.pollinations.ai/{question}`；`POST https://text.pollinations.ai/`；<br>**`POST https://text.pollinations.ai/openai`（或 `/v1/chat/completions`）OpenAI 兼容** |
| 可用模型 | `GET https://text.pollinations.ai/models`（文档标注 **GPT-OSS 20B**） |
| 免费额度 | 「Text: **One at a time, at least 30 seconds apart**」按 **IP** 限流 |
| 超限表现 | 「**402 Payment Required**，带 `Retry-After` 头」 |
| 可靠性 | 「**Best effort. There is no uptime guarantee**，limits, models and behaviour can change」 |
| 隐私关键 | 「**Cached results. The same URL returns the same result, to anyone who has the URL**」 |
| 安全 | 「No keys in URLs」；有安全过滤，部分 prompt 会被拒 |

**因此本项目的硬约束**：调用 Pollinations 前必须脱敏（凭据样式 / 13 位以上数字 / 邮箱 / 链接 / 32 位以上随机串），
且**只发**幕次、首领名、机制短句、池子统计——因为结果可能被缓存并返回给任何持有该 URL 的人。

### OpenRouter / Groq（需用户自带 key）

| 项 | 已核实 | 未核实（PENDING） |
|---|---|---|
| OpenRouter | 官方文档存在「API Credit & Rate Limits — Handle **402 and 429** Errors」页（<https://openrouter.ai/docs/api-reference/limits>，HTTP 200） | 免费模型具体额度/RPM 数值（页面为 JS 渲染，正文未取全）→ **不臆断** |
| Groq | 作为常见 OpenAI 兼容免费层列入配置 | 免费额度数值未逐一核实 → PENDING |

两者都**不能**给用户"开箱即用"：需要注册并设置环境变量 `OPENROUTER_API_KEY` / `GROQ_API_KEY`。

## 2. 采用的端点顺序

| 顺序 | 端点 | 需要 key | 适配性 | 风险 |
|---|---|---|---|---|
| 1 | `pollinations-classic` | ❌ 不需要 | OpenAI 兼容 `/openai`，模型名用 `openai` | 30s/IP 限流、无 uptime 保证、**结果可能被缓存复用** → 已脱敏 |
| 2 | `openrouter-free` | ✅ `OPENROUTER_API_KEY` | OpenAI 兼容 | 免费额度受限，402/429 |
| 3 | `groq-free` | ✅ `GROQ_API_KEY` | OpenAI 兼容 | 免费额度受限 |

## 3. 代码落地（`app/LlmAdvisor.cs`）

- **默认关闭**：`data/llm.json` 不存在或 `enabled=false` 时**完全不联网**（沿用原设计）。
- **顺序降级**：第一个端点失败（4xx/5xx/超时/无内容）→ 自动换下一个。
- **冷却**：失败进冷却（默认 300s）；**402/429 优先读 `Retry-After`** 决定冷却时长。
- **限流**：全局最小间隔（默认 30s，对齐 Pollinations 的 30s/IP）＋单次会话上限（默认 20 次）。
- **脱敏**：`Sanitize()` 在发送前统一处理（已加断言）。
- **失败即回退**：`RefineAsync` 返回 null 时调用方继续用规则文本，**永不阻断**悬浮窗。
- **探针**：`--llm-probe [--dry]`，`--dry` 只校验配置不联网，便于 CI/离线环境验证。

## 4. 隐私与合规边界（硬性）

- **绝不发送**：截图、像素、OCR 原文、UID、cookie、token、链接、邮箱。
- **只发送**：幕次、首领名、本幕机制短句（≤4 条）、角色池统计数字。
- **不做的事**：不把模型输出当事实源；不让模型决定机制真伪；不把模型结论写进 `data/knowledge/`。
- **用户可控**：配置文件在本地 `data/llm.json`（不进仓库，`.gitignore` 已加 `.env` 等）。

## 5. 未采用 / 不建议

| 候选 | 原因 |
|---|---|
| 各类"逆向网页版 AI"脚本 | 违反对方 ToS，且随时失效 |
| 需要破解/绕过风控的端点 | 与项目红线冲突 |
| 硬编码内置 key | 会被滥用并导致封禁；且属于凭据泄露 |

## 6. 验证状态

| 项 | 状态 |
|---|---|
| 配置解析、旧配置兼容、响应解析、脱敏、限流/冷却判定 | **PASS**（`--selftest` 断言） |
| `--llm-probe --dry` 离线校验 | **PASS**（CI 可跑，不联网） |
| `--llm-probe` 真实联网调用 | **PENDING**：需要在本机或允许出网的 runner 上显式 `enabled=true` 后执行；CI 默认不调外部模型（避免不可复现与隐私风险） |
| Pollinations 实际返回质量 | **PENDING**：需真实调用后评估是否值得开启 |

## 7. 真实联网实测（2026-11-06，本机）

不依赖"文档说能用"，直接打一次真实请求：

```
POST https://text.pollinations.ai/openai
{"model":"openai","messages":[{"role":"user","content":"第3幕首领翠翎恐蕈，用雷推怒气条，下一步做什么？"}]}

→ HTTP 200，用时 11253 ms
→ {"id":"pllns_...","choices":[{"message":{"role":"assistant","content":"继续雷攻击，配合风/水触反应。","reasoning":"..."}}]}
```

**结论（如实）**：

| 项 | 实测 |
|---|---|
| 免 key 可用 | ✅ HTTP 200，标准 OpenAI 兼容结构 |
| **延迟** | **约 11.2 秒**（远高于原 8s 全局超时 → 已改为端点级 `timeoutSeconds=25`，否则会被误判失败） |
| 返回内容 | 「继续雷攻击，配合风/水触反应。」——**方向偏了**：本关机制是雷推怒气条、满条后 15 秒力竭，模型却建议"配合风/水"，且未提力竭窗口 |
| 内容质量 | **一般**：能用但不够准，属于"改写器"而非"决策器" |
| 附带字段 | 响应含 `reasoning`（GPT-OSS 推理模型），`content` 解析正常 |

因此本项目的立场不变：**规则引擎先算、模型只做改写补充、失败即回退**。模型的输出**不会**写进知识库或决策链，
`data/knowledge/` 仍只由有证据门禁的管线写入。

### 已知限制

- 30 秒/IP 一次：幕次连续变化时大部分调用会被限流（`Health` 会显示"限流等待 Ns"），这是设计内的行为。
- 11 秒延迟：悬浮窗是 fire-and-forget，不阻塞识别；但也不适合"每一幕都问"。
- 质量一般 + 隐私（结果可能被缓存）：**建议只在需要解释/兜底时开**，不要当主决策。

## 8. 如何启用（用户操作）

```powershell
# 1) 复制示例配置
Copy-Item data\llm.example.json data\llm.json
# 2) 把 enabled 改成 true（只用免 key 端点就保留 pollinations-classic，删掉另外两个也行）
# 3) 离线校验配置（不联网）
.\TheaterAssistant.exe --llm-probe --dry
# 4) 真实探针（会联网，等 10~25 秒）
.\TheaterAssistant.exe --llm-probe
```

`data/llm.json` 已加入 `.gitignore`，不会误提交；需要 key 的端点只从环境变量读（`OPENROUTER_API_KEY` / `GROQ_API_KEY`）。
