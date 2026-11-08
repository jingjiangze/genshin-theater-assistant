#!/usr/bin/env node
// 优选「纯净源」清单：从 ingest 的批次摘要里挑出高价值录制源，累积写入 data/pure-sources.json。
//
// 为什么需要：筛选结果此前用完即弃（每次 ingest 的 summary 只留在 artifact/账本），
// 没有"一批稳定可复用的纯净源"。用户要求"视频不要断"，因此清单要**持久且累积**：
//   每次筛选把新视频并入，保留历史（记录 firstSeen / lastSeen），不覆盖旧条目。
//
// 打分（越高越好）：
//   纯净度：pure 100 / likely 60 / mixed 20 / short·excluded 直接淘汰
//   难度  ：月谕 80 / 卓越 60 / 困难 30（用户明确要"高难录制相关"）
//   赛季  ：当前周期（TARGET_PERIOD，默认 2026-11）+40
//   文本  ：min(text_lines, 40) × 1.5（有字幕/章节=可核对操作）
//   时长  ：>=15min +30 / >=8min +15
//
// 用法：
//   node pick.mjs <ingest-summary.json> [--out ../../data/pure-sources.json] [--top 30]
import fs from 'node:fs';
import path from 'node:path';

const src = process.argv[2];
if (!src || !fs.existsSync(src)) { console.error('用法: pick.mjs <ingest-summary.json> [--out f] [--top N]'); process.exit(2); }
const getArg = (k, d) => { const i = process.argv.indexOf(k); return i > 0 ? process.argv[i + 1] : d; };
const outPath = path.resolve(getArg('--out', '../../data/pure-sources.json'));
const TOP = Number(getArg('--top', '30'));
const PERIOD = process.env.TARGET_PERIOD || '2026-11';

const summary = JSON.parse(fs.readFileSync(src, 'utf8').replace(/^\uFEFF/, ''));
const vids = summary.videos || [];

function difficultyOf(title) {
  const t = title || '';
  if (/月谕|Lunar/i.test(t)) return 'Lunar';
  if (/卓越|Visionary/i.test(t)) return 'Visionary';
  if (/困难|\bHard\b/i.test(t)) return 'Hard';
  if (/普通|Easy/.test(t)) return 'Easy';
  return '';
}
function diffScore(d) { return d === 'Lunar' ? 80 : d === 'Visionary' ? 60 : d === 'Hard' ? 30 : 0; }
function purityScore(p) { return p === 'pure' ? 100 : p === 'likely' ? 60 : p === 'mixed' ? 20 : 0; }

const now = new Date().toISOString();
const scored = [];
for (const v of vids) {
  const purity = v.purity || 'unknown';
  if (purity === 'short' || purity === 'excluded') continue;   // 切片/解说直接淘汰
  const title = v.title || '';
  const diff = difficultyOf(title);
  const dur = Number(v.duration || 0);
  const txt = Number(v.text_lines || 0);
  let score = purityScore(purity) + diffScore(diff);
  if (new RegExp(PERIOD.replace('-', '\\s*年\\s*0?').replace(/\\s\*年\\s\*0\?/, '\\s*年\\s*0?')) && title.includes(PERIOD.split('-')[0])) score += 40;
  if (new RegExp(PERIOD.split('-')[0] + '\\s*年\\s*0?' + Number(PERIOD.split('-')[1]) + '\\s*月').test(title)) score += 40;
  score += Math.min(txt, 40) * 1.5;
  if (dur >= 900) score += 30; else if (dur >= 480) score += 15;
  if (/纯净|无解说|一镜到底|全程|完整/.test(title)) score += 25;
  scored.push({
    bv: v.id, title, purity, difficulty: diff, duration: Math.round(dur),
    textLines: txt, score: Math.round(score * 10) / 10,
  });
}
scored.sort((a, b) => b.score - a.score);

// 累积：读旧清单，保留历史条目
let old = { sources: [] };
try { if (fs.existsSync(outPath)) old = JSON.parse(fs.readFileSync(outPath, 'utf8').replace(/^\uFEFF/, '')); } catch {}
const byBv = new Map((old.sources || []).map(s => [s.bv, s]));
let added = 0, updated = 0;
for (const s of scored.slice(0, TOP)) {
  const prev = byBv.get(s.bv);
  if (prev) { Object.assign(prev, s, { lastSeenAt: now }); updated++; }
  else { byBv.set(s.bv, { ...s, firstSeenAt: now, lastSeenAt: now }); added++; }
}
// 历史条目降权展示但不删（视频不要断：老源仍可用）
const all = [...byBv.values()].sort((a, b) => (b.score || 0) - (a.score || 0));

const out = {
  generatedAt: now,
  period: PERIOD,
  criteria: '纯净度(pure100/likely60/mixed20) + 高难(月谕80/卓越60/困难30) + 当期季节+40 + 文本量 + 时长 + 纯净特征+25；short/excluded 淘汰',
  total: all.length,
  thisRun: { candidates: scored.length, added, updated },
  sources: all,
};
fs.mkdirSync(path.dirname(outPath), { recursive: true });
fs.writeFileSync(outPath, JSON.stringify(out, null, 2) + '\n', 'utf8');

console.log(`[pick] 本轮候选 ${scored.length}，新增 ${added}，更新 ${updated}，清单共 ${all.length} 条 → ${outPath}`);
console.log('[pick] Top 8：');
for (const s of all.slice(0, 8))
  console.log(`  ${String(s.score).padStart(6)}  ${s.purity.padEnd(6)} ${(s.difficulty || '-').padEnd(10)} ${String(Math.round(s.duration / 60)).padStart(3)}分  ${s.bv}  ${(s.title || '').slice(0, 46)}`);
