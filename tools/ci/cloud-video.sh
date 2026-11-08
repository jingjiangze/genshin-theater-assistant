#!/usr/bin/env bash
# 云端视频链路（Linux/box）：搜索 → 抽帧 → OCR 验证 → 汇总
#
# 为什么放 box（见 docs/PARALLEL-WORK.md）：
#   抽帧是最耗时的环节（每条视频几十秒），且**不依赖 Windows**——
#   hd.mjs 已支持 BROWSER_PATH 复用系统浏览器，box 上正好有 google-chrome，
#   因此不必下载 chromium，npm 只装 playwright-core（几 MB）。
#   CI 则专注它独有的部分：量化对照（--quant，依赖 System.Drawing）与真机模拟（GDI）。
#
# 用法：
#   bash tools/ci/cloud-video.sh /workspace/theater BV193xp6WErn BV1R3xp6WEKz
#   可选环境：BILI_COOKIE（有则拿高清）、EVERY=30 MAX=12
set -uo pipefail

REPO="${1:-/workspace/theater}"; shift || true
BVIDS=("$@")
[ ${#BVIDS[@]} -eq 0 ] && { echo "用法: cloud-video.sh <repo> <BV...>"; exit 2; }

cd "$REPO" || exit 2
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"; export PATH="$DOTNET_ROOT:$PATH"
[ -f .env.local ] && . ./.env.local 2>/dev/null || true

EVERY="${EVERY:-30}"; MAX="${MAX:-12}"
LOG="$REPO/cloud-video.log"; : > "$LOG"
say() { echo "$*" | tee -a "$LOG"; }

say "════ 云端视频链路 $(date -u +%Y-%m-%dT%H:%M:%SZ) ════"

# ── 0) 环境自检 ──────────────────────────────────────────────
say ""
say "──── 环境 ────"
CHROME="$(command -v google-chrome || command -v chromium || command -v chromium-browser || true)"
say "chrome: ${CHROME:-未找到}"
say "node:   $(node --version 2>/dev/null || echo 缺)"
say "dotnet: $(dotnet --version 2>/dev/null || echo 缺（OCR 验证会跳过）)"
say "cookie: $([ -n "${BILI_COOKIE:-}" ] && echo 有 || echo 无（只拿 480p）)"
[ -z "$CHROME" ] && { say "✗ 无浏览器，无法抽帧"; exit 3; }

# ── 1) 装 playwright-core（不下载浏览器，复用系统 chrome）────
say ""
say "──── 安装 playwright-core（不下载 chromium）────"
cd tools/video-frames || exit 2
if [ ! -d node_modules/playwright-core ]; then
  PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1 npm i --no-save --no-audit --no-fund playwright-core 2>&1 | tail -3 | tee -a "$LOG"
else
  say "已安装，跳过"
fi
NPX_OK=$([ -d node_modules/playwright-core ] && echo 1 || echo 0)
[ "$NPX_OK" = "0" ] && { say "✗ playwright-core 安装失败"; exit 4; }

# ── 2) 逐条抽帧（BROWSER_PATH 复用系统 chrome）─────────────
say ""
say "──── 抽帧 ────"
export BROWSER_PATH="$CHROME"
FAILED=0
for BV in "${BVIDS[@]}"; do
  say "→ $BV (every=$EVERY max=$MAX)"
  if node hd.mjs "$BV" out="$REPO/frames/$BV" every="$EVERY" max="$MAX" 2>&1 | tail -4 | tee -a "$LOG"; then
    n=$(ls "$REPO/frames/$BV"/*.png 2>/dev/null | wc -l)
    say "  帧数: $n"
    [ "$n" -eq 0 ] && { say "  ⚠ 未抽到帧"; FAILED=$((FAILED+1)); }
  else
    say "  ✗ 抽帧失败"; FAILED=$((FAILED+1))
  fi
done

# 回到仓库根：上面 cd 到了 tools/video-frames，后面的相对路径都基于仓库根
cd "$REPO" || exit 2

# ── 3) OCR 验证（OcrProbe，Linux 原生）─────────────────────
say ""
say "──── OCR 验证（box 原生，与 CI 的量化对照互补）────"
if dotnet --version >/dev/null 2>&1; then
  dotnet build cli/OcrProbe/OcrProbe.csproj -c Release -o cli/OcrProbe/bin 2>&1 | grep -E "error|Error\(s\)" | head -3 | tee -a "$LOG"
  if [ -d models/v5 ] && [ -d models/v6 ]; then
    for BV in "${BVIDS[@]}"; do
      [ -d "$REPO/frames/$BV" ] || continue
      say "→ OCR $BV"
      dotnet cli/OcrProbe/bin/OcrProbe.dll --models models --dir "../../frames/$BV" \
        --model both --threads 2 --out "$REPO/frames/$BV-ocr.json" 2>&1 | grep -E "平均|非空|相似度|已写" | tee -a "$LOG"
    done
  else
    say "SKIP：无模型（先跑 cloud-ocr.sh）"
  fi
else
  say "SKIP：无 dotnet（抽帧已完成，OCR 验证需安装 dotnet）"
fi

# ── 4) 汇总 ────────────────────────────────────────────────
say ""
say "──── 汇总 ────"
TOT=0
for BV in "${BVIDS[@]}"; do
  n=$(ls "$REPO/frames/$BV"/*.png 2>/dev/null | wc -l)
  say "  $BV: $n 帧"
  TOT=$((TOT+n))
done
say "合计 $TOT 帧，失败 $FAILED 条"
say "帧目录：$REPO/frames/"
[ "$FAILED" -eq 0 ] && exit 0 || exit 1
