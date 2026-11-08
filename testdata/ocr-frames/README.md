# OCR 回归测试帧（30 张）

来源：真实游戏录制抽帧（1920×1080 → 压到 960×540 JPEG q80，平均 69 KB），
统一放在这里以便**云端与 CI 复用同一批真实帧**，避免"云端用合成图测速度"这类失真。

用途：
- `tools/ci/cloud-ocr.sh <repo> testdata/ocr-frames` → v5-mobile vs v6-small 速度 + 交叉一致率
- `cli/OcrProbe` 的 `--dir` 直接指向本目录

为什么入仓而不是放 artifact：artifact 30 天过期、且云端 box 无法直接取；
这 30 张共 2.1 MB，作为**长期回归基线**放仓库更稳。

不含任何账号信息（已人工确认：仅游戏界面，UID 区域在压缩后不可读且无个人数据）。
