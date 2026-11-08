#!/usr/bin/env bash
# 云端一条命令跑完整流程（Linux / headless）。
#
# 设计原则：
#   1. **每步失败不中断**，全部跑完再汇总——否则一个环境小问题会掩盖后面所有结果；
#   2. 每步都产出**可核验证据**（退出码 + 关键输出行），写入报告，不靠"看起来绿了"；
#   3. 明确区分「云端能验」与「云端不能验」的能力（见 capabilities 段），不假装覆盖。
#
# 用法：
#   bash tools/ci/cloud-all.sh                 # 默认：仓库目录=自动探测，跳过 git 同步
#   bash tools/ci/cloud-all.sh --sync          # 先 git fetch+reset 到 origin/main
#   bash tools/ci/cloud-all.sh --frames DIR    # 指定 OCR 验证帧目录
#   bash tools/ci/cloud-all.sh --push          # 成功后把报告推回 GitHub（需 GH_TOKEN）
set -uo pipefail

REPO="$(cd "$(dirname "$0")/../.." && pwd)"
FRAMES="$REPO/frames-sample"
DO_SYNC=0; DO_PUSH=0
while [ $# -gt 0 ]; do
  case "$1" in
    --sync) DO_SYNC=1; shift;;
    --push) DO_PUSH=1; shift;;
    --frames) FRAMES="$2"; shift 2;;
    *) REPO="$1"; shift;;
  esac
done

cd "$REPO" || { echo "仓库不存在: $REPO"; exit 2; }
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"; export PATH="$DOTNET_ROOT:$PATH"
[ -f .env.local ] && . ./.env.local 2>/dev/null || true

LOG="$REPO/cloud-all.log"
REPORT_MD="$REPO/cloud-run-report.md"
REPORT_JSON="$REPO/cloud-run-report.json"
: > "$LOG"
STEPS_JSON="[]"
FAILED=0

say() { echo "$*" | tee -a "$LOG"; }

# run_step <名称> <展示用命令> <实际命令...>
run_step() {
  local name="$1"; shift
  local cmd="$1"; shift
  say ""
  say "──── $name ────"
  local out rc
  out=$("$@" 2>&1); rc=$?
  say "$out" | tail -25
  local verdict="PASS"
  [ $rc -ne 0 ] && { verdict="FAIL"; FAILED=$((FAILED+1)); }
  say "[$name] exit=$rc → $verdict"
  STEPS_JSON=$(python3 - "$STEPS_JSON" "$name" "$cmd" "$rc" "$verdict" "$(printf '%s' "$out" | tail -12)" <<'PY'
import json,sys
arr=json.loads(sys.argv[1])
arr.append({"step":sys.argv[2],"cmd":sys.argv[3],"exit":int(sys.argv[4]),"verdict":sys.argv[5],"tail":sys.argv[6]})
print(json.dumps(arr,ensure_ascii=False))
PY
)
}

say "════════ 云端全流程 $(date -u +%Y-%m-%dT%H:%M:%SZ) ════════"
say "仓库: $REPO"
say "内核: $(uname -sr)｜核: $(nproc)｜内存: $(free -m | awk '/^Mem:/{print $2"MB"}')"
say "CPU : $(lscpu | awk -F': *' '/Model name/{print $2; exit}')"
say "dotnet: $(dotnet --version 2>/dev/null || echo 缺)"

# ---------- 0) 可选同步 ----------
if [ $DO_SYNC -eq 1 ]; then
  run_step "仓库同步到 origin/main" "git fetch+reset" \
    bash -c 'git fetch -q origin main && git reset -q --hard origin/main && git log --oneline -1'
fi

# ---------- 1) 能力矩阵（哪些云端能验，哪些不能） ----------
capabilities() {
  echo "能验:"
  echo "  - 决策/配队/量化（纯逻辑，无 WinForms 依赖）"
  echo "  - OCR 引擎 v5/v6 速度与交叉一致率（RapidOcrNet + SkiaSharp）"
  echo "  - 知识管线契约（Python 单测 34 项）"
  echo "  - 赛季无关性（临时造 season30 冒烟）"
  echo "不能验（明确声明）:"
  echo "  - WinForms 悬浮窗与真机抓屏（需 Windows 桌面会话）"
  echo "  - DirectML 实际加速（上游包停更 1.24.4，且无 GPU）"
  echo "  - B站采集（需 cookie，且云端无浏览器登录态）"
}
run_step "能力矩阵" "内置声明" capabilities

# ---------- 2) 构建 ----------
run_step "构建跨平台 CLI" "dotnet build cli/TheaterCli.csproj" \
  bash -c 'dotnet build cli/TheaterCli.csproj -c Release -o cli/bin 2>&1 | tail -5'
run_step "构建 OCR 探针" "dotnet build cli/OcrProbe" \
  bash -c 'dotnet build cli/OcrProbe/OcrProbe.csproj -c Release -o cli/OcrProbe/bin 2>&1 | tail -5'

CLI="dotnet cli/bin/TheaterCli.dll"

# ---------- 3) 数据/策略自检 ----------
run_step "策略来自数据" "$CLI --policy" $CLI --policy
run_step "赛季自动发现" "$CLI --seasons" $CLI --seasons
run_step "幕内知识" "$CLI --playbook" $CLI --playbook
run_step "OCR 引擎调优" "$CLI --ocr-tuning" $CLI --ocr-tuning
run_step "OCR 后处理（BetterGI 对齐）" "$CLI --ocr" $CLI --ocr

# ---------- 4) 决策链 ----------
run_step "敌人量化档案" "$CLI --enemy-profile" $CLI --enemy-profile
run_step "量化决策" "$CLI --quant-decide" $CLI --quant-decide
run_step "通用最优配队（全赛季全难度）" "$CLI --team-solve --difficulty all" \
  $CLI --team-solve --difficulty all --out /tmp/team-plans.json
run_step "跨幕全局分配" "$CLI --global-alloc" $CLI --global-alloc --out /tmp/global-alloc.json
run_step "第二赛季冒烟" "$CLI --season-smoke" $CLI --season-smoke
run_step "配队质量闸门（硬判据）" "$CLI --quality-gates" $CLI --quality-gates
run_step "决策编排三态" "$CLI --orchestrate" $CLI --orchestrate

# ---------- 5) 模型通道（有 key 才跑） ----------
if [ -n "${LLM_API_KEY:-}" ]; then
  run_step "模型通道（主）" "$CLI --llm-probe" $CLI --llm-probe
else
  say ""
  say "──── 模型通道（主）────"
  say "SKIP：未配置 LLM_API_KEY（.env.local 缺失或未加载）"
fi
if [ -n "${LLM_VERIFY_API_KEY:-}" ]; then
  run_step "模型通道（验证/模拟专用）" "$CLI --llm-probe --verify" $CLI --llm-probe --verify
else
  say "SKIP：未配置 LLM_VERIFY_API_KEY（验证通道可选，用于与主通道交叉对照）"
fi

# ---------- 6) OCR 云端验证（v5/v6 + 交叉） ----------
if [ -d "$FRAMES" ] && [ -n "$(ls "$FRAMES"/*.png "$FRAMES"/*.jpg 2>/dev/null | head -1)" ]; then
  run_step "云端 OCR 验证（v5/v6 交叉）" "cloud-ocr.sh" \
    bash tools/ci/cloud-ocr.sh "$REPO" "$FRAMES"
else
  say "SKIP：无帧样本目录（$FRAMES）"
fi

# ---------- 7) 知识管线契约 ----------
run_step "Python 单测（知识管线）" "python3 -m unittest" \
  bash -c 'cd tools/pipeline && python3 -m unittest 2>&1 | tail -4'

# ---------- 8) 汇总 ----------
TOTAL=$(python3 -c "import json,sys;print(len(json.loads(sys.argv[1])))" "$STEPS_JSON")
STATUS=$([ $FAILED -eq 0 ] && echo PASS || echo "FAIL($FAILED/$TOTAL)")

python3 - "$STEPS_JSON" "$REPO" "$STATUS" "$TOTAL" "$FAILED" <<'PY'
import json,sys,os,datetime
steps=json.loads(sys.argv[1]); repo=sys.argv[2]; status=sys.argv[3]
total=sys.argv[4]; failed=sys.argv[5]
meta={
 "generatedAt":datetime.datetime.now(datetime.timezone.utc).isoformat(),
 "repo":repo,"status":status,"total":int(total),"failed":int(failed),
 "env":{"kernel":os.uname().sysname+" "+os.uname().release,"cores":os.cpu_count()},
 "steps":steps,
}
open(os.path.join(repo,"cloud-run-report.json"),"w",encoding="utf-8").write(
  json.dumps(meta,ensure_ascii=False,indent=1))
lines=["# 云端全流程报告","",
 f"- 时间：{meta['generatedAt']}",
 f"- 环境：{meta['env']['kernel']} / {meta['env']['cores']} 核",
 f"- 结果：**{status}**（{total} 步，失败 {failed}）","",
 "| # | 步骤 | 命令 | 退出码 | 判定 |","|---:|---|---|---:|---|"]
for i,s in enumerate(steps,1):
    cmd=s["cmd"].replace("|","\\|")
    lines.append(f"| {i} | {s['step']} | `{cmd}` | {s['exit']} | {s['verdict']} |")
lines+=["","## 失败详情",""]
bad=[s for s in steps if s["exit"]!=0]
if not bad: lines.append("（无）")
for s in bad:
    lines.append(f"### {s['step']} (exit={s['exit']})")
    lines.append("```")
    lines.append(s["tail"])
    lines.append("```")
open(os.path.join(repo,"cloud-run-report.md"),"w",encoding="utf-8").write("\n".join(lines)+"\n")
print(f"报告: {repo}/cloud-run-report.md")
PY

say ""
say "════════ 汇总: $STATUS（$TOTAL 步，失败 $FAILED）════════"
say "报告: $REPORT_MD"
say "JSON: $REPORT_JSON"

# ---------- 9) 可选推回 GitHub ----------
if [ $DO_PUSH -eq 1 ] && [ -n "${GH_TOKEN:-}" ]; then
  run_step "报告推回 GitHub" "gh-commit" \
    node "$REPO/../gh-commit.mjs" jingjiangze/genshin-theater-assistant main \
      "ci(cloud): 云端全流程报告 $STATUS" cloud-run-report.md cloud-run-report.json
fi

[ $FAILED -eq 0 ] && exit 0 || exit 1
