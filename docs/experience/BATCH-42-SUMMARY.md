# 经验汇总（自动蒸馏）

> 由 `--distill` 从 42 份 **replay** 报告生成（已排除 `*-ops.json` 候选文件）；数字是实测，不是估计。

| 指标 | 数值 |
|---|---|
| 视频/报告数 | 42 |
| 帧数合计 | 463 |
| 有真值帧 | 4（覆盖率 0.9%） |
| 幕次准确率（平稳后 / 原始） | **100.0% / 100.0%** |
| 跨幕跳变 | 1 次 |
| 状态抖动 | 33 次 |
| 平均每帧耗时 | 3412 ms |
| 状态分布 | act=165，team=97，unknown=93，difficulty=77，theater=30，choice=1 |

## 逐视频

| 报告 | 帧 | 真值帧 | 准确率(稳/原) | 跳变 | 抖动 |
|---|---|---|---|---|---|
| BV12hYw6vEzC-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 1 |
| BV15XYPzuExb-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV172Vd6xEKn-replay.json | 12 | 1 | 100.0% / 100.0% | 0 | 2 |
| BV18UTh6xEho-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 1 |
| BV193xp6WErn-replay.json | 12 | 1 | 100.0% / 100.0% | 0 | 2 |
| BV19R9VBhEds-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1AK1VBdExF-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 3 |
| BV1AgS6BkErD-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 1 |
| BV1BiPTz1Epi-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 1 |
| BV1CscnzSEJw-replay.json | 7 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1Ftw1zUESE-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1G86xBFEk5-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 1 |
| BV1GDSzBxEEf-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 1 |
| BV1Hc9LBjEmQ-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1LwRKBmE2n-replay.json | 12 | 1 | 100.0% / 100.0% | 0 | 0 |
| BV1PmxgzHEvn-replay.json | 5 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1QqVR6kEoY-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 2 |
| BV1TyHJz6EDP-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 1 |
| BV1UNABz5Ei7-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 1 |
| BV1VdDLBZEgm-replay.json | 5 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1WQAizNExV-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1WstM6KE9k-replay.json | 12 | 0 | 0.0% / 0.0% | 1 | 0 |
| BV1XSAdzTE3S-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 1 |
| BV1XZ9iBoEyS-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1ZTiLB8ETt-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1ZVGM6UEGf-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 3 |
| BV1aYAfz1Ex6-replay.json | 2 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1b4iLB2ESD-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1bKABzCENY-replay.json | 2 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1bPxH6gEgw-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1dKHnzsEeg-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 2 |
| BV1dPPuzLERs-replay.json | 10 | 0 | 0.0% / 0.0% | 0 | 1 |
| BV1f9xMzBEDb-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 1 |
| BV1hXTv6uECJ-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1im15B2EhM-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1kUtu6BEin-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1n9aPzyEDX-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1pA3s6qEWG-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 1 |
| BV1s5ABzMEgj-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 2 |
| BV1saaz6qEbH-replay.json | 12 | 1 | 100.0% / 100.0% | 0 | 3 |
| BV1uetT63EAF-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 2 |
| BV1zmGM6xEvh-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 0 |

## 错例（最多 20 条）

无

## 沉淀下来的规则（供程序与文档引用）

- 底部幕次标签的可靠读取窗口：纵向 `0.828–0.878h`（列表行在 0.89–0.94h，必须排除），横向 `0.28–0.74`。
- 条带里出现 ≥2 个「第N幕」→ 判为幕次列表，不采信。
- 幕次判定优先级：模板 > 底部条带 > 整帧文字。
- 时序稳定：倒退/跳级需连续 2 帧确认；未观测到幕次时保持上一次。
- `actN` 模板运行时阈值 0.90（跨幕相似度实测 0.80–0.82），其余 0.86。
