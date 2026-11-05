# templates

放置 PNG 模板，文件名为识别锚点（`templates/<宽>x<高>/` 分桶，运行时按当前窗口宽度就近选桶）：

| 文件名 | 含义 |
|---|---|
| `act1.png` … `act12.png` | 直接判定为第 N 幕 |
| `choice.png` | 三选一（伙伴/神秘收获/辉彩祝福）界面 |
| `result.png` | 结算 / 回溯界面 |
| `hall.png` / `begin.png` / `diff.png` | 大厅 / 开始挑战 / 难度选择（可选） |

要求：

- 必须是**该分辨率**下从真实截图裁出的（跨分辨率会失配；1.10× 缩放靠多尺度兜底，实测 0.9771）
- 必须含明显纹理（文字/图标边缘）；纯色块会在全图到处满分命中
- 建议尺寸 ≤ 120×120，越大越慢（200×50 的模板在 1280×720 上约 430 ms/张，step=2）

## 一、批量适配（推荐）

不用手写坐标：把一个帧目录（游戏截图 / 录屏抽帧都行）丢给 exe，
它会用 OCR 找到「第N幕 / 演出准备 / 开始挑战 / 演出结算 …」这些文字锚点，
按 OCR 返回的四边形自动换算裁剪矩形，产出**该分辨率自己的一套模板**，并做双向量检：

| 口径 | 含义 | 门槛 |
|---|---|---|
| 在场一致性 `presentMin` | 同分辨率下同样识别出该标签的其它帧，必须仍在原位附近命中 | ≥ 0.92 |
| 缺席不误报 `absentMax` | 同分辨率下**没有**该标签的帧，整帧都不该命中 | < 0.80（低于运行时阈值 0.86） |

```powershell
# 1) 存帧：游戏里按 Ctrl+Alt+D 存到 samples\auto\（或自己放录屏抽帧）
# 2) 批量适配
.\TheaterAssistant.exe --batch samples\auto --out templates
# 3) 看报告
notepad templates\batch-report.md
```

产出：

- `templates/<宽x高>/<标签>.png` —— 只装通过质检的
- `templates/<宽x高>/_specs.json` —— 该分辨率的规格表，可用 `tools/make-templates.ps1` 复算
- `templates/batch-report.json` / `.md` —— 每条的在场最低分、缺席最高分、结论

同分辨率建议至少两帧（一帧"有该界面"、一帧"没有"），缺席那一帧就是误报样本；
只有一帧时报告会标注「单一样本（无缺席帧可验）」。

## 二、手工裁剪 / 标定

```powershell
.\crop-template.ps1 -Source samples\frame-xxxx.png -X 860 -Y 120 -W 240 -H 60 -Out templates\act3.png
```

标定阈值：

```powershell
powershell -File .\theater-overlay.ps1 -Learn samples\frame-xxxx.png -LearnTemplate templates\act3.png
```

实测建议阈值 **0.80**（缺失时最高误报 0.66，真实命中最差 0.927）。
