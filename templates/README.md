# templates

放置 PNG 模板，文件名为识别锚点：

| 文件名 | 含义 |
|---|---|
| `act1.png` … `act12.png` | 直接判定为第 N 幕 |
| `choice.png` | 三选一（伙伴/神秘收获/辉彩祝福）界面 |
| `result.png` | 结算 / 回溯界面 |

要求：

- 必须是**当前分辨率**下从真实截图裁出的（缩放会失配，实测 1.10× 即掉到 0.78–0.87）
- 必须含明显纹理（文字/图标边缘）；纯色块会在全图到处满分命中
- 建议尺寸 ≤ 120×120，越大越慢（200×50 的模板在 1280×720 上约 430 ms/张，step=2）

裁模板：

```powershell
.\crop-template.ps1 -Source samples\frame-xxxx.png -X 860 -Y 120 -W 240 -H 60 -Out templates\act3.png
```

标定阈值：

```powershell
powershell -File .\theater-overlay.ps1 -Learn samples\frame-xxxx.png -LearnTemplate templates\act3.png
```

实测建议阈值 **0.80**（缺失时最高误报 0.66，真实命中最差 0.927）。
