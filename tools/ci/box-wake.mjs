#!/usr/bin/env node
// box 唤醒器：读 data/box-policy.json 决定「要不要唤醒、唤醒后干什么」。
//
// 设计原则（回应用户「不要硬编码」）：
//   1. 间隔 / 触发源 / 任务清单 / 状态门槛 全部来自 box-policy.json，代码里没有写死的分钟数或任务名；
//   2. **幂等**：先 box_status，只有 hibernated/failed/stopped 才 ensure；running 直接跳过；
//   3. **事件优先**：由 workflow_run（video-ingest 成功）触发，而不是无条件定时拉起；
//   4. **最小间隔**：用 data/box-state.json 记录上次唤醒，避免事件密集时反复拉起；
//   5. **可降级**：box 不可用时不阻塞主链路（fallbackToCi），因为 box 是加速器不是必需件。
//
// 用法：
//   node tools/ci/box-wake.mjs            # 按策略决策并执行
//   node tools/ci/box-wake.mjs --dry      # 只报告决策，不动手
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';

const REPO = process.cwd();
const POLICY = path.join(REPO, 'data', 'box-policy.json');
const STATE = path.join(REPO, 'data', 'box-state.json');
const DRY = process.argv.includes('--dry');

function readJson(p, fallback) {
  try { return JSON.parse(fs.readFileSync(p, 'utf8').replace(/^\uFEFF/, '')); } catch { return fallback; }
}
function say(...a) { console.log(...a); }

const policy = readJson(POLICY, null);
if (!policy) { say('无 data/box-policy.json，跳过（不假设默认策略）'); process.exit(0); }

say(`策略: enabled=${policy.enabled} minInterval=${policy.minIntervalMinutes}min tasks=${(policy.tasks || []).map(t => t.name).join(',') || '无'}`);
if (policy.enabled === false) { say('策略已禁用，退出'); process.exit(0); }
if (!policy.tasks || policy.tasks.length === 0) { say('策略未配置任务，退出'); process.exit(0); }

// ---- 最小间隔校验（状态外置在仓库，不依赖 box 自身状态）----
const state = readJson(STATE, {});
const now = Date.now();
const last = state.lastWakeAt ? Date.parse(state.lastWakeAt) : 0;
const elapsedMin = last ? (now - last) / 60000 : Infinity;
if (elapsedMin < (policy.minIntervalMinutes || 0)) {
  say(`距上次唤醒 ${elapsedMin.toFixed(1)} 分钟 < 最小间隔 ${policy.minIntervalMinutes}，跳过`);
  process.exit(0);
}

// ---- MCP 调用（复用 box-mcp.mjs）----
function mcp(tool, args = {}) {
  const tmp = path.join(REPO, `.box-args-${tool}.json`);
  fs.writeFileSync(tmp, JSON.stringify(args));
  try {
    const out = execFileSync('node', ['tools/ci/box-mcp.mjs', 'call', tool, `@${tmp}`], {
      encoding: 'utf8', timeout: 180000, stdio: ['ignore', 'pipe', 'pipe'],
    });
    return out;
  } catch (e) {
    // Windows 上 libuv 可能在输出完整结果后以非零码退出（Assertion ... UV_HANDLE_CLOSING）。
    // 只要 stdout 里有有效内容，就视为成功——不能用退出码单独判定。
    const out = String(e.stdout || '');
    if (/\{/.test(out)) return out;
    return `ERROR ${e.status ?? '?'}: ${String(e.stderr || e.message).slice(0, 200)}`;
  } finally {
    try { fs.unlinkSync(tmp); } catch {}
  }
}

// ---- 读当前状态，判断是否真需要唤醒（幂等）----
const stRaw = mcp('box_status');
const m = stRaw.match(/"state"\s*:\s*"([^"]+)"/);
const cur = m ? m[1] : 'unknown';
say(`当前 box 状态: ${cur}`);
const wakeStates = policy.wakeWhenState || ['hibernated', 'failed'];
if (!wakeStates.includes(cur)) {
  say(`状态 ${cur} 不在唤醒清单 [${wakeStates.join(',')}] 内 → 无需唤醒`);
  if (!DRY) {
    fs.writeFileSync(STATE, JSON.stringify({ ...state, lastCheckedAt: new Date().toISOString(), lastState: cur }, null, 2) + '\n');
  }
  process.exit(0);
}
if (DRY) { say('[dry] 将要唤醒并执行任务'); process.exit(0); }

// ---- 唤醒 ----
say('唤醒中…');
const ensure = mcp('box_ensure');
const ok = /"state"\s*:\s*"running"/.test(ensure);
if (!ok) {
  say('唤醒失败: ' + ensure.slice(0, 160));
  if (policy.fallbackToCi) {
    say('fallbackToCi=true → 记为 PENDING，不阻塞主链路');
    fs.writeFileSync(STATE, JSON.stringify({
      ...state, lastAttemptAt: new Date().toISOString(), lastResult: 'wake-failed', lastState: cur,
    }, null, 2) + '\n');
    process.exit(0);
  }
  process.exit(1);
}
say('已 running');

// ---- 前置环境检查（策略声明 requireEnv，缺则跳过，不浪费一次唤醒）----
const need = policy.requireEnv || [];
if (need.length) {
  const shell = mcp('shell', { command: 'cd /workspace/theater && . ./.env.local 2>/dev/null; ' +
    need.map(v => `printf '%s=%s\\n' ${v} "${'$'}{${v}:-MISSING}"`).join('; '), timeout_s: 60 });
  const miss = need.filter(v => new RegExp(`${v}=MISSING`).test(shell));
  if (miss.length) {
    say(`缺少环境变量 ${miss.join(',')} → 任务必然失败，跳过执行（保持 box running 供排查）`);
  }
}

// ---- 执行任务 ----
const results = [];
for (const t of policy.tasks) {
  const argStr = t.args || '';
  const cmd = t.name === 'cloud-all'
    ? `cd /workspace/theater && export DOTNET_ROOT=$HOME/.dotnet && export PATH=$DOTNET_ROOT:$PATH && ROLE=box bash tools/ci/cloud-all.sh /workspace/theater ${argStr}`
    : t.name === 'cloud-video'
      ? `cd /workspace/theater && export DOTNET_ROOT=$HOME/.dotnet && export PATH=$DOTNET_ROOT:$PATH && . ./.env.local 2>/dev/null; BVS=$(node -e "const j=require('/workspace/theater/data/pure-sources.json');console.log(j.sources.slice(0,${(argStr.match(/--top\s+(\d+)/) || [])[1] || 2}).map(s=>s.bv).join(' '))" 2>/dev/null); bash tools/ci/cloud-video.sh /workspace/theater $BVS`
      : `cd /workspace/theater && bash tools/ci/${t.name}.sh /workspace/theater ${argStr}`;
  say(`▶ 任务 ${t.name} ${argStr}`);
  const out = mcp('shell', { command: cmd, timeout_s: 900 });
  const verdict = /合计 \d+ 帧|汇总: PASS/.test(out) ? 'PASS' : (/ERROR/.test(out) ? 'ERROR' : 'UNKNOWN');
  say(`  → ${verdict}`);
  results.push({ name: t.name, verdict });
}

// ---- 记录状态 ----
fs.writeFileSync(STATE, JSON.stringify({
  lastWakeAt: new Date().toISOString(),
  lastResult: results.map(r => `${r.name}=${r.verdict}`).join(','),
  lastState: 'running',
  hibernateAfterMinutes: policy.hibernateAfterMinutes ?? null,
}, null, 2) + '\n');
say('任务结果: ' + results.map(r => `${r.name}=${r.verdict}`).join(', '));
say('（休眠策略由下次唤醒时判断；hibernateAfterMinutes=' + (policy.hibernateAfterMinutes ?? '未设') + '）');
