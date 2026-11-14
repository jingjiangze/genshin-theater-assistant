#!/usr/bin/env node
// ─────────────────────────────────────────────────────────────────────────────
// 把运行时记录库的导出（records-export.jsonl）转成 SFT 样本。
//
// 为什么要这一步：合成样本（act-playbook 派生）只能覆盖"剧本里有的幕"；
// 真实运行记录能补上两类东西——
//   · 界面类型的真实分布（大堂/队伍/选卡/战斗各占多少）；
//   · 人工标注过的对错（Ctrl+Alt+B 标错 → label=wrong_advice，label=good 才是正样本）。
//
// 输入：`TheaterAssistantWpf.exe --db-export out.jsonl` 的产物。
// 输出：data/ml/records-{train,val}.jsonl（messages 格式，与 build-sft 一致），
//       以及 manifest-records.json（计数 + 拒绝原因）。build-sft 会自动合并这两份。
//
// 过滤规则（宁可少、不可脏）：
//   · label=wrong_advice → 丢弃并计数（这些样本要么重写，要么作为反例另用）；
//   · 空 advice / kind=paused / 纯模板来源(ui) → 丢弃；
//   · 无标注的记录只保留「有明确来源标记」的（◆量化/★已验证/○语料/·规则）——
//     这些是决策层真的输出过、玩家真的看到过的句子。
// 切分：按 tick_id 的哈希做 20% val，确定性（同一份导出永远同一份切分）。
// ─────────────────────────────────────────────────────────────────────────────
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';

const args = process.argv.slice(2);
const opt = (name, def) => {
  const i = args.indexOf(name);
  return i >= 0 && i + 1 < args.length ? args[i + 1] : def;
};
const IN = opt('--in', path.join('data', 'ml', 'records-export.jsonl'));
const OUT_DIR = opt('--out', path.join('data', 'ml'));
const SYSTEM = '你是原神「幻想真境剧诗」的随身助手。只根据给定的幕次与难度，给出一句可照做的中文指令；'
  + '不确定就说「不确定」，不要编造角色或机制。';
const KIND_ZH = { act: '幕内', choice: '三选一', team: '队伍配置', theater: '大堂', detail: '演出详情', combat: '战斗中' };

if (!fs.existsSync(IN)) {
  console.error(`找不到导出文件：${IN}\n先运行：TheaterAssistantWpf.exe --db-export ${IN}`);
  process.exit(2);
}

const strip = (s) => (s || '').replace(/^[▶·◆◇★○\s]+/, '').trim();
const rows = fs.readFileSync(IN, 'utf8').split('\n').filter(Boolean).map((l) => {
  try { return JSON.parse(l); } catch { return null; }
}).filter(Boolean);

const stat = { total: rows.length, kept: 0, rejectedLabel: 0, empty: 0, paused: 0, weakSource: 0, dup: 0 };
const seen = new Set();
const train = [];
const val = [];

for (const r of rows) {
  if (r.label === 'wrong_advice') { stat.rejectedLabel++; continue; }
  if (r.kind === 'paused') { stat.paused++; continue; }
  const advice = strip(r.advice);
  if (!advice) { stat.empty++; continue; }
  const source = (r.source || '').trim();
  const strong = /^[▶·◆◇★○]/.test(source) || /^(◆|★|◇|○|·)/.test(advice);
  if (r.label !== 'good' && !strong) { stat.weakSource++; continue; }

  const actText = r.stable_act ? `第${r.stable_act}幕` : '剧诗界面（幕次未定）';
  const kindText = KIND_ZH[r.kind] || r.kind || '';
  const user = `${actText}${kindText ? `（${kindText}）` : ''}。现在做什么？`;
  const key = user + '\u0000' + advice;
  if (seen.has(key)) { stat.dup++; continue; }
  seen.add(key);

  const sample = {
    task: r.label === 'good' ? 'rec_good' : 'rec_weak',
    messages: [
      { role: 'system', content: SYSTEM },
      { role: 'user', content: user },
      { role: 'assistant', content: advice },
    ],
    meta: { tick_id: r.tick_id, session: r.session, source, label: r.label ?? null },
  };
  const h = crypto.createHash('sha1').update(String(r.tick_id) + user).digest();
  if (h[0] % 5 === 0) val.push(sample); else train.push(sample);
  stat.kept++;
}

fs.mkdirSync(OUT_DIR, { recursive: true });
const write = (name, arr) => {
  const p = path.join(OUT_DIR, name);
  fs.writeFileSync(p, arr.map((x) => JSON.stringify(x)).join('\n') + (arr.length ? '\n' : ''), 'utf8');
  return p;
};
const pt = write('records-train.jsonl', train);
const pv = write('records-val.jsonl', val);
const manifest = {
  source: path.basename(IN),
  builtAt: new Date().toISOString(),
  ...stat,
  train: train.length,
  val: val.length,
};
fs.writeFileSync(path.join(OUT_DIR, 'manifest-records.json'), JSON.stringify(manifest, null, 2) + '\n', 'utf8');

console.log(`读 ${stat.total} 条 → 保留 ${stat.kept}（train ${train.length} / val ${val.length}）`);
console.log(`丢弃：标错 ${stat.rejectedLabel} · 空建议 ${stat.empty} · 暂停 ${stat.paused} · 弱来源 ${stat.weakSource} · 重复 ${stat.dup}`);
console.log(`产物：${pt}｜${pv}`);
if (stat.kept === 0) process.exit(1);
