#!/usr/bin/env bash
# 云端 / Linux 全流程测试：装 .NET 8（若缺）→ 构建跨平台 CLI → 跑全部命令 → 汇总
# 用法：bash tools/ci/linux-flow.sh [仓库目录]
set -uo pipefail

REPO="${1:-$(cd "$(dirname "$0")/../.." && pwd)}"
cd "$REPO" || { echo "仓库目录不存在: $REPO"; exit 2; }
LOG="$REPO/linux-flow.log"
: > "$LOG"
say() { echo "$*" | tee -a "$LOG"; }

say "== 仓库 =="; say "$(pwd)"; say "$(git log --oneline -1 2>/dev/null || echo '(非 git)')"
say "== 环境 =="; say "kernel=$(uname -sr) node=$(node -v 2>/dev/null || echo -) python=$(python3 -V 2>&1 | awk '{print $2}')"

# 1) .NET 8 SDK
if ! command -v dotnet >/dev/null 2>&1; then
  say "== 安装 .NET 8 SDK（用户级）=="
  curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh || { say "下载 dotnet-install 失败"; exit 1; }
  bash /tmp/dotnet-install.sh --channel 8.0 --install-dir "$HOME/.dotnet" >>"$LOG" 2>&1 || { say "dotnet 安装失败"; exit 1; }
fi
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
say "dotnet=$(dotnet --version 2>/dev/null || echo 缺)"

# 2) 构建跨平台 CLI
say "== 构建 CLI =="
dotnet build cli/TheaterCli.csproj -c Release -o cli/bin >>"$LOG" 2>&1
BUILD=$?
say "build exit=$BUILD"
[ $BUILD -ne 0 ] && { tail -30 "$LOG"; exit 1; }

CLI="dotnet cli/bin/TheaterCli.dll"
say "== CLI 全流程 =="
$CLI --flow 2>&1 | tee -a "$LOG"
FLOW=${PIPESTATUS[0]}
say "flow exit=$FLOW"

# 3) Python 流水线单测
say "== Python 单测 =="
( cd tools/pipeline && python3 -m unittest 2>&1 | tail -3 | tee -a "$LOG" )
PY=${PIPESTATUS[0]}

# 4) 生成物落盘（可用于对比不同赛季/不同玩家池）
say "== 产出 =="
$CLI --team-solve --difficulty all --out /tmp/team-plans-linux.json >>"$LOG" 2>&1 && say "  team-plans → /tmp/team-plans-linux.json"
$CLI --global-alloc --out /tmp/global-alloc-linux.json >>"$LOG" 2>&1 && say "  global-alloc → /tmp/global-alloc-linux.json"

say "== 结论 =="
if [ $FLOW -eq 0 ] && [ $PY -eq 0 ]; then say "PASS（CLI 全流程 + Python 单测）"; exit 0; fi
say "FAIL（flow=$FLOW python=$PY），详见 $LOG"; exit 1
