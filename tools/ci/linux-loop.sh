#!/usr/bin/env bash
# 云端持续测试：每 N 秒跑一次全流程，追加日志；失败也继续（记录以便回溯）
set -uo pipefail
REPO="${1:-$(cd "$(dirname "$0")/../.." && pwd)}"
INTERVAL="${2:-600}"
cd "$REPO" || exit 2
LOG="$REPO/linux-loop.log"
CLI="dotnet cli/bin/TheaterCli.dll"
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
say() { echo "[$(date -u +%H:%M:%S)] $*" | tee -a "$LOG"; }

say "=== 持续测试启动 interval=${INTERVAL}s repo=$REPO ==="
n=0
while true; do
  n=$((n+1))
  git pull -q --ff-only 2>/dev/null || true
  dotnet build cli/TheaterCli.csproj -c Release -o cli/bin >>"$LOG" 2>&1
  b=$?
  if [ $b -ne 0 ]; then say "cycle#$n BUILD FAIL"; sleep "$INTERVAL"; continue; fi
  # 用统一 runner 跑完整流程（含质量闸门 / OCR 交叉 / Python 契约），
  # 而不是只跑 --flow——后者覆盖不到配队闸门与 OCR 模型验证。
  out=$(bash tools/ci/cloud-all.sh "$REPO" 2>&1)
  code=$?
  summary=$(echo "$out" | grep -E "汇总: " | tail -1)
  say "cycle#$n cloud-all exit=$code $summary"
  echo "$out" > "$REPO/last-flow.log"
  # 第二赛季冒烟：验证"丢 seasonNN.json 即生效"
  # 验证/模拟通道：读 LLM_VERIFY_*，与主通道分离（可用不同模型交叉对照）
  if [ -n "${LLM_VERIFY_API_KEY:-}" ]; then
    $CLI --llm-probe --verify >/tmp/llm-verify.log 2>&1 && say "cycle#$n llm-verify OK" || say "cycle#$n llm-verify WARN"
  fi
  # 模型端点探针（enabled 时）：可观测，不阻断
  if [ -n "${LLM_API_KEY:-}" ]; then
    $CLI --llm-probe >/tmp/llm-probe.log 2>&1 && say "cycle#$n llm OK" || say "cycle#$n llm WARN"
  fi
  ( cd tools/pipeline && python3 -m unittest >/dev/null 2>&1 && say "cycle#$n python OK" || say "cycle#$n python FAIL" )
  sleep "$INTERVAL"
done
