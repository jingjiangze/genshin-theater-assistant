# 跨视频操作共识（自动汇总）

> 来自 42 个视频的 276 条操作候选；按**不同 BV 号**计数，同一视频重复帧只算 1。
> 共识条目（≥2 个视频）：**10** 条；单例手法/行动：61 条。

## 共识操作（多个 UP 主都这么写，可信度最高）

| 视频数 | 类别 | 操作 | 出处 |
|---|---|---|---|
| 7 | buy | 买一个角色 | BV12hYw6vEzC、BV18UTh6xEho、BV19R9VBhEds、BV1UNABz5Ei7 |
| 7 | buy | 敌人选择（优先级：从左到右） | BV12hYw6vEzC、BV18UTh6xEho、BV19R9VBhEds、BV1UNABz5Ei7 |
| 6 | buy | 买两个角色 | BV18UTh6xEho、BV19R9VBhEds、BV1UNABz5Ei7、BV1ZVGM6UEGf |
| 5 | buy | 看情况买角色或者选BUFF | BV12hYw6vEzC、BV18UTh6xEho、BV1ZVGM6UEGf、BV1bPxH6gEgw |
| 4 | buy | 看情况选核心角色 | BV12hYw6vEzC、BV18UTh6xEho、BV1ZVGM6UEGf、BV1uetT63EAF |
| 2 | buy | 买一级水岩 | BV19R9VBhEds、BV1bPxH6gEgw |
| 2 | buy | 买到四级水雷（左边) | BV1bPxH6gEgw、BV1uetT63EAF |
| 2 | buy | 优先选最后两关主C与辅助 | BV12hYw6vEzC、BV1ZVGM6UEGf |
| 2 | buy | 开局：买3融化1挂件 | BV172Vd6xEKn、BV1AgS6BkErD |
| 2 | action | 过去的魔女留下的挑战，需要许多伙伴组成的队伍共同攻克。 | BV1QqVR6kEoY、BV1XSAdzTE3S |

## 单例手法 / 行动（需人工复核，不直接进生产数据）

- [action] 2.如果没有副C打第五幕，可先打第五幕！ ← BV1UNABz5Ei7@100s
- [action] 2.试用皇第幕和第三幕需要保证队伍不断和雷。 ← BV1bPxH6gEgw@275s
- [action] 4.建议丝柯克6、7，火神8、圣牌2，木偶圣牌1、10 ← BV18UTh6xEho@100s
- [action] 优先5、6幕主C，其次风水挂件 ← BV12hYw6vEzC@100s
- [action] 优先②号C，其次持续挂水角色 ← BV19R9VBhEds@150s
- [action] 优先③号C、其次岩挂件 ← BV19R9VBhEds@150s
- [action] 优先六、八幕C，其次凑双岩BUFF ← BV19R9VBhEds@150s
- [action] 优先六、八幕C，其次水挂件 ← BV19R9VBhEds@150s
- [action] 优先剩下两个C，其次破次数盾角色 ← BV1UNABz5Ei7@100s
- [action] 优先安伯和有嘲讽的角色 ← BV18UTh6xEho@100s
- [action] 优先挂冰挂件 ← BV1UNABz5Ei7@100s
- [action] 优先爱诺、③号C ← BV19R9VBhEds@150s
- [action] 优先皇女，其次冰挂件 ← BV1UNABz5Ei7@100s
- [action] 优先皇女，其次凑双雷BUFF ← BV1UNABz5Ei7@100s
- [action] 优先砂糖，其次冰挂件 ← BV1UNABz5Ei7@100s
- [action] 优先破岩盾角色，其次芭芭拉 ← BV19R9VBhEds@150s
- [action] 优先第五幕主C，其次保证第四幕有水雷 ← BV1bPxH6gEgw@100s
- [action] 优先第五幕主C，其次保证第四幕有雷 ← BV1bPxH6gEgw@125s
- [action] 优先第八幕主C ← BV1uetT63EAF@100s
- [action] 优先第八幕主C与相应辅助 ← BV12hYw6vEzC@100s
- [action] 优先第八幕主C与雷后台 ← BV1ZVGM6UEGf@100s
- [action] 优先第六幕主C，其次持续挂水角色 ← BV1uetT63EAF@100s
- [action] 优先给第二关凑双水、其次雷挂件 ← BV1bPxH6gEgw@100s
- [action] 优先行秋，其次冰水挂件 ← BV1ZVGM6UEGf@100s
- [action] 优先行秋，其次雷挂件 ← BV1ZVGM6UEGf@100s
- [action] 先尝试学会什么叫盲打 ← BV1BiPTz1Epi@225s
- [action] 别去打狂猎，就打这个 ← BV1WstM6KE9k@75s
- [action] 就是开局前六幕123456 ← BV1AgS6BkErD@25s
- [action] 开局刷1级绽放+1草挂件，没有就重开 ← BV1WstM6KE9k@75s
- [action] 开局：把钱花光 ← BV1TyHJz6EDP@100s

## 过滤说明

- 过滤 UI 文案：`可选角色`、`已选开幕与待命角色`、`请选择一名待命角色…`、`队伍配置` 等；
- 只有 1 个视频支持、且不是 combo/action 的条目不列出（噪声大）；
- 本文件是**候选证据**，要进 `data/rotations.json` 仍需人工确认。
