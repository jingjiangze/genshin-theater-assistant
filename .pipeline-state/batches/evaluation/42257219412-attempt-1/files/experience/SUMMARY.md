# 经验汇总（自动蒸馏）

> 由 `--distill` 从 50 份 **replay** 报告生成（已排除 `*-ops.json` 候选文件）；条带 OCR 代理标签与预测输入耦合，不能视为独立真值。

| 指标 | 数值 |
|---|---|
| 视频/报告数 | 50 |
| 帧数合计 | 533 |
| 有视觉代理标签帧 | 6（覆盖率 1.1%） |
| 视觉条带代理自洽率（稳定后 / 原始） | **16.7% / 100.0%** |
| 跨幕跳变 | 4 次 |
| 状态抖动 | 49 次 |
| 平均每帧耗时 | 2900 ms |
| 状态分布 | unknown=195，act=158，theater=86，team=76，difficulty=9，choice=9 |

## 逐视频

| 报告 | 帧 | 代理标签帧 | 自洽率(稳/原) | 跳变 | 抖动 |
|---|---|---|---|---|---|
| BV12Zak6LEMF-replay.json | 12 | 0 | 0.0% / 0.0% | 2 | 1 |
| BV13EM36uEgB-replay.json | 11 | 0 | 0.0% / 0.0% | 0 | 2 |
| BV14Q4ce3EEE-replay.json | 11 | 0 | 0.0% / 0.0% | 0 | 2 |
| BV15YG8zxE7t-replay.json | 11 | 0 | 0.0% / 0.0% | 0 | 1 |
| BV17hxW68Ej9-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 1 |
| BV18Jcwz3E1c-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 1 |
| BV18QZWYBEsv-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 1 |
| BV19wSBYfE5S-replay.json | 11 | 0 | 0.0% / 0.0% | 0 | 2 |
| BV1B16XYWEBv-replay.json | 11 | 0 | 0.0% / 0.0% | 0 | 1 |
| BV1Fv421r7pp-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1G86xBFEk5-replay.json | 12 | 1 | 100.0% / 100.0% | 0 | 2 |
| BV1GJowBqEXx-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1Hc9LBjEmQ-replay.json | 12 | 0 | 0.0% / 0.0% | 1 | 2 |
| BV1LwRKBmE2n-replay.json | 12 | 2 | 0.0% / 100.0% | 0 | 1 |
| BV1M1TB6rEND-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 1 |
| BV1SH4y1o7se-replay.json | 1 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1SsjJ61E3t-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 2 |
| BV1T89VBUEcR-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1U6421Z79x-replay.json | 11 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1UE421g7we-replay.json | 2 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1UxVy6gEWZ-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 3 |
| BV1VD421V724-replay.json | 4 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1Vs6WBREZu-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 1 |
| BV1WM4m1y7Ta-replay.json | 11 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1Wf4y1G7nG-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1WstM6KE9k-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1ZY326xEhh-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 1 |
| BV1ahtR6mEBV-replay.json | 11 | 0 | 0.0% / 0.0% | 0 | 1 |
| BV1cEHyzvEzw-replay.json | 7 | 0 | 0.0% / 0.0% | 0 | 1 |
| BV1cQ8feMEYM-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 4 |
| BV1dKHnzsEeg-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 2 |
| BV1f9xMzBEDb-replay.json | 11 | 0 | 0.0% / 0.0% | 0 | 2 |
| BV1gQG36nE4i-replay.json | 11 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1gUgQ6ZEhm-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1hXTv6uECJ-replay.json | 11 | 2 | 0.0% / 100.0% | 1 | 2 |
| BV1jJ4m1G7Ex-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 1 |
| BV1k2E46hEwt-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1mgmFBaErS-replay.json | 7 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1nFSrBMETP-replay.json | 11 | 0 | 0.0% / 0.0% | 0 | 1 |
| BV1o142117Z1-replay.json | 11 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1pDP6zLEGq-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1pe7zz5EKo-replay.json | 11 | 0 | 0.0% / 0.0% | 0 | 2 |
| BV1pi421e7Gk-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 1 |
| BV1poto6BE7b-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 1 |
| BV1sE421N7CS-replay.json | 4 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1saaz6qEbH-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 1 |
| BV1vyXfYaEg6-replay.json | 12 | 0 | 0.0% / 0.0% | 0 | 2 |
| BV1yP4y1g7HH-replay.json | 10 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1yU4y1B7rx-replay.json | 8 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1zmGM6xEvh-replay.json | 12 | 1 | 0.0% / 100.0% | 0 | 3 |

## 错例（最多 20 条）

- BV1LwRKBmE2n-1159.6s-1920x1080: 我们=1/真值=5
- BV1LwRKBmE2n-994s-1920x1080: 我们=2/真值=5
- BV1hXTv6uECJ-398.6s-1920x1080: 我们=1/真值=3
- BV1hXTv6uECJ-531.4s-1920x1080: 我们=1/真值=4
- BV1zmGM6xEvh-343.3s-1920x1080: 我们=1/真值=3

## 沉淀下来的规则（供程序与文档引用）

- 底部幕次标签的可靠读取窗口：纵向 `0.828–0.878h`（列表行在 0.89–0.94h，必须排除），横向 `0.28–0.74`。
- 条带里出现 ≥2 个「第N幕」→ 判为幕次列表，不采信。
- 幕次判定优先级：模板 > 底部条带 > 整帧文字。
- 时序稳定：倒退/跳级需连续 2 帧确认；未观测到幕次时保持上一次。
- `actN` 模板运行时阈值 0.90（跨幕相似度实测 0.80–0.82），其余 0.86。
