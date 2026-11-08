#!/usr/bin/env bash
# 云端（Linux）OCR 验证：下载"针对云端环境的模型"→ 编译 OcrProbe → 跑速度与交叉验证
#
# 为什么单独一套：云端无 DirectML、4 核共享、内存 8G。桌面端的 DirectML + 高线程 + v6 大模型
# 在云端既不生效也可能拖慢；这里用 v5-mobile 作默认、v6-small 作交叉对照，参数来自
# data/decision-policy.cloud.json。
#
# 用法：bash tools/ci/cloud-ocr.sh [仓库目录] [帧目录]
set -uo pipefail

REPO="${1:-$(cd "$(dirname "$0")/../.." && pwd)}"
FRAMES="${2:-$REPO/frames-sample}"
MODELS="${THEATER_MODELS:-$REPO/models}"
LOG="$REPO/cloud-ocr.log"
: > "$LOG"
say() { echo "$*" | tee -a "$LOG"; }

cd "$REPO" || { echo "仓库不存在: $REPO"; exit 2; }
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"; export PATH="$DOTNET_ROOT:$PATH"

say "== 环境 =="
say "kernel=$(uname -sr) cores=$(nproc) mem=$(free -m | awk '/^Mem:/{print $2"MB"}')"
say "cpu=$(lscpu | awk -F': *' '/Model name/{print $2; exit}')"
say "avx512=$(grep -o -m1 'avx512f' /proc/cpuinfo || echo none)"
say "dotnet=$(dotnet --version 2>/dev/null || echo 缺)"
say "配置=$( [ -f data/decision-policy.cloud.json ] && echo decision-policy.cloud.json || echo '(缺云端配置，用默认)')"

# ---------- 1) 下载模型（公开源 + SHA256 校验，与 models/fetch-models.ps1 同源） ----------
BASE="https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/onnx"
DICT_BASE="https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/paddle"

fetch() { # url dest sha
  local url="$1" dest="$2" sha="$3"
  [ -f "$dest" ] && { say "  已存在 $(basename "$dest")"; return 0; }
  mkdir -p "$(dirname "$dest")"
  curl -sSL --retry 3 --max-time 300 -o "$dest" "$url" || { say "  下载失败 $(basename "$dest")"; rm -f "$dest"; return 1; }
  if [ -n "$sha" ]; then
    local got; got=$(sha256sum "$dest" | awk '{print $1}')
    if [ "$got" != "$sha" ]; then say "  ⚠ SHA256 不符 $(basename "$dest")（期望 ${sha:0:12}… 实得 ${got:0:12}…）"; return 1; fi
  fi
  say "  ok $(basename "$dest") $(du -h "$dest" | cut -f1)"
}

say "== 下载 v5-mobile（云端默认）=="
fetch "$BASE/PP-OCRv5/det/ch_PP-OCRv5_det_mobile.onnx" "$MODELS/v5/ch_PP-OCRv5_mobile_det.onnx" "4d97c44a20d30a81aad087d6a396b08f786c4635742afc391f6621f5c6ae78ae"
fetch "$BASE/PP-OCRv5/rec/ch_PP-OCRv5_rec_mobile.onnx" "$MODELS/v5/ch_PP-OCRv5_rec_mobile_infer.onnx" "5825fc7ebf84ae7a412be049820b4d86d77620f204a041697b0494669b1742c5"
fetch "$BASE/PP-OCRv5/cls/ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx" "$MODELS/v5/ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx" "54379ae5174d026780215fc748a7f31910dee36818e63d49e17dc598ecc82df7"
fetch "$DICT_BASE/PP-OCRv5/rec/ch_PP-OCRv5_rec_mobile/ppocrv5_dict.txt" "$MODELS/v5/ppocrv5_dict.txt" ""

say "== 下载 v6-small（交叉验证用）=="
fetch "$BASE/PP-OCRv6/det/PP-OCRv6_det_small.onnx" "$MODELS/v6/PP-OCRv6_det_small.onnx" "090f04abcd9d9a7498bc4ebf677e4cb9bdce1fe4197ddb7e529f1ef44e1ff94f"
fetch "$BASE/PP-OCRv6/rec/PP-OCRv6_rec_small.onnx" "$MODELS/v6/PP-OCRv6_rec_small.onnx" "6f327246b50388f3c176ae304bd95767ea6dc0c9ae92153ef8cbe210b3c14884"
fetch "$DICT_BASE/PP-OCRv6/rec/PP-OCRv6_rec_small/ppocrv6_dict.txt" "$MODELS/v6/ppocrv6_small_dict.txt" ""

say "== 模型盘点 =="
for d in v5 v6; do
  if [ -d "$MODELS/$d" ]; then say "  $d: $(ls "$MODELS/$d" | tr '\n' ' ') ($(du -sh "$MODELS/$d" | cut -f1))"; else say "  $d: 缺"; fi
done

# ---------- 2) 编译探针 ----------
say "== 构建 OcrProbe =="
dotnet build cli/OcrProbe/OcrProbe.csproj -c Release -o cli/OcrProbe/bin >>"$LOG" 2>&1
if [ $? -ne 0 ]; then say "构建失败"; tail -25 "$LOG"; exit 1; fi
say "构建 OK"

# ---------- 3) 跑验证 ----------
if [ ! -d "$FRAMES" ] || [ -z "$(ls "$FRAMES"/*.png "$FRAMES"/*.jpg 2>/dev/null | head -1)" ]; then
  say "== 无帧目录（$FRAMES）→ 跳过推理，只报告模型与运行时就绪 =="
  dotnet cli/OcrProbe/bin/OcrProbe.dll --models "$MODELS" --model v5 2>&1 | tail -6 | tee -a "$LOG"
  say "结论：模型与环境就绪（未跑真实帧）"
  exit 0
fi

say "== 单模型：v5（云端默认）=="
dotnet cli/OcrProbe/bin/OcrProbe.dll --models "$MODELS" --dir "$FRAMES" --model v5 --threads 2 \
  --out cloud-ocr-v5.json 2>&1 | tail -8 | tee -a "$LOG"

say "== 单模型：v6（对照）=="
dotnet cli/OcrProbe/bin/OcrProbe.dll --models "$MODELS" --dir "$FRAMES" --model v6 --threads 2 \
  --out cloud-ocr-v6.json 2>&1 | tail -8 | tee -a "$LOG"

say "== 交叉验证：v5 vs v6 同帧一致率 =="
dotnet cli/OcrProbe/bin/OcrProbe.dll --models "$MODELS" --dir "$FRAMES" --model both --threads 2 \
  --out cloud-ocr-cross.json 2>&1 | tail -6 | tee -a "$LOG"

say "== 结论 =="
# 判定依据来自 OcrProbe 的输出（阈值读 decision-policy.cloud.json 的 minAgreementPct）
if grep -q "可用轻量模型承担回归" "$LOG"; then
  say "PASS：两模型相似度达标 → 云端可用 v5-mobile 承担回归，结论可外推"
elif grep -q "不能直接套用桌面端" "$LOG"; then
  say "WARN：两模型相似度不足 → 云端结论不能直接套用桌面端，需人工核对差异帧"
else
  say "PASS(部分)：模型与运行时验证完成，但交叉验证未产出判定（检查帧目录）"
fi
