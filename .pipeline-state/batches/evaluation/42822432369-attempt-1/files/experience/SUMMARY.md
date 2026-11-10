# 经验汇总（自动蒸馏）

> 由 `--distill` 从 50 份 **replay** 报告生成（已排除 `*-ops.json` 候选文件）；条带 OCR 代理标签与预测输入耦合，不能视为独立真值。

| 指标 | 数值 |
|---|---|
| 视频/报告数 | 50 |
| 帧数合计 | 350 |
| 有视觉代理标签帧 | 12（覆盖率 3.4%） |
| 视觉条带代理自洽率（稳定后 / 原始） | **66.7% / 100.0%** |
| 跨幕跳变 | 0 次 |
| 状态抖动 | 25 次 |
| 平均每帧耗时 | 1631 ms |
| 状态分布 | unknown=218，theater=39，team=32，act=31，difficulty=16，choice=14 |

## 逐视频

| 报告 | 帧 | 代理标签帧 | 自洽率(稳/原) | 跳变 | 抖动 |
|---|---|---|---|---|---|
| BV14Q4ce3EEE-replay.json | 7 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV172Vd6xEKn-replay.json | 8 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV177vfBSEic-replay.json | 8 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV17hxW68Ej9-replay.json | 8 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV18QZWYBEsv-replay.json | 7 | 4 | 100.0% / 100.0% | 0 | 0 |
| BV193xp6WErn-replay.json | 8 | 1 | 100.0% / 100.0% | 0 | 1 |
| BV195iLBREaz-replay.json | 8 | 2 | 50.0% / 100.0% | 0 | 1 |
| BV1Fv421r7pp-replay.json | 8 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1G86xBFEk5-replay.json | 8 | 0 | 0.0% / 0.0% | 0 | 1 |
| BV1HBxa6oEAw-replay.json | 8 | 0 | 0.0% / 0.0% | 0 | 1 |
| BV1Hc9LBjEmQ-replay.json | 7 | 0 | 0.0% / 0.0% | 0 | 1 |
| BV1KSiqBGERh-replay.json | 6 | 0 | 0.0% / 0.0% | 0 | 1 |
| BV1LZYP6dE9u-replay.json | 5 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1LwRKBmE2n-replay.json | 8 | 1 | 0.0% / 100.0% | 0 | 0 |
| BV1SNxW67Ed1-replay.json | 1 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1T89VBUEcR-replay.json | 8 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1ThRyBGECJ-replay.json | 8 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1U4P3zME3q-replay.json | 3 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1UE421g7we-replay.json | 1 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1UxVy6gEWZ-replay.json | 8 | 0 | 0.0% / 0.0% | 0 | 1 |
| BV1Wf4y1G7nG-replay.json | 8 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1WfiPeiEb9-replay.json | 8 | 0 | 0.0% / 0.0% | 0 | 2 |
| BV1XSAdzTE3S-replay.json | 8 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1Z5Gg6dE5U-replay.json | 8 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1ZTiLB8ETt-replay.json | 7 | 0 | 0.0% / 0.0% | 0 | 1 |
| BV1b8qWB6EDP-replay.json | 2 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1bKABzCENY-replay.json | 2 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1epFZeVEuA-replay.json | 8 | 0 | 0.0% / 0.0% | 0 | 1 |
| BV1fw411N77H-replay.json | 8 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1gMuF6gEqs-replay.json | 8 | 0 | 0.0% / 0.0% | 0 | 1 |
| BV1gUgQ6ZEhm-replay.json | 8 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1hXTv6uECJ-replay.json | 8 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1k2E46hEwt-replay.json | 8 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1kUtu6BEin-replay.json | 8 | 4 | 50.0% / 100.0% | 0 | 1 |
| BV1oi6pBtEjA-replay.json | 8 | 0 | 0.0% / 0.0% | 0 | 1 |
| BV1pDP6zLEGq-replay.json | 8 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1poABzzEYi-replay.json | 7 | 0 | 0.0% / 0.0% | 0 | 2 |
| BV1poto6BE7b-replay.json | 8 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1qphuz7EjD-replay.json | 7 | 0 | 0.0% / 0.0% | 0 | 1 |
| BV1rj1LBdEBc-replay.json | 7 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1rkHpzBEn9-replay.json | 7 | 0 | 0.0% / 0.0% | 0 | 1 |
| BV1saaz6qEbH-replay.json | 8 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1seF9eJEuJ-replay.json | 7 | 0 | 0.0% / 0.0% | 0 | 1 |
| BV1t9u36MEQH-replay.json | 8 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1tEPEzoEd9-replay.json | 7 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1tLtM6GErx-replay.json | 8 | 0 | 0.0% / 0.0% | 0 | 1 |
| BV1wRaSzrEEK-replay.json | 8 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1yU4y1B7rx-replay.json | 5 | 0 | 0.0% / 0.0% | 0 | 0 |
| BV1ynvvBbEkn-replay.json | 8 | 0 | 0.0% / 0.0% | 0 | 2 |
| BV1zmGM6xEvh-replay.json | 7 | 0 | 0.0% / 0.0% | 0 | 3 |

## 错例（最多 20 条）

- BV195iLBREaz-780.1s-1920x1080: 我们=7/真值=6
- BV1LwRKBmE2n-260.3s-1920x1080: 我们=10/真值=1
- BV1kUtu6BEin-751.2s-1920x1080: 我们=1/真值=6
- BV1kUtu6BEin-939s-1920x1080: 我们=1/真值=8

## 沉淀下来的规则（供程序与文档引用）

- 底部幕次标签的可靠读取窗口：纵向 `0.828–0.878h`（列表行在 0.89–0.94h，必须排除），横向 `0.28–0.74`。
- 条带里出现 ≥2 个「第N幕」→ 判为幕次列表，不采信。
- 幕次判定优先级：模板 > 底部条带 > 整帧文字。
- 时序稳定：倒退/跳级需连续 2 帧确认；未观测到幕次时保持上一次。
- `actN` 模板运行时阈值 0.90（跨幕相似度实测 0.80–0.82），其余 0.86。
