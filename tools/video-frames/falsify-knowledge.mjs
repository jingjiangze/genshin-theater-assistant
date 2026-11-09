#!/usr/bin/env node
// 知识证伪检验：用「字幕里每幕的真实编队」验证已验证知识，而不是只检查"实现符合我写的规格"。
//
// 背景（docs/AUDIT-OVERALL.md P1）：此前 140 条断言全是自证。唯一的外部真值现在有了：
//   `data/act-teams.json` —— 从 107 份字幕提取的每幕实际出场角色（54 份含明确编队）。
//
// 判定：
//   SUPPORTED   该角色在该幕出场 ≥ 2 个视频，或进入该幕 Top3
//   WEAK        出场 1 个视频
//   UNSUPPORTED 0 次出场 —— **可疑知识**，应降级而不是继续当"已验证"
//
// 用法：
//   node tools/video-frames/falsify-knowledge.mjs [--teams ../../data/act-teams.json]
//                                                [--knowledge ../../data/knowledge-release.json]
//                                                [--out ../../data/knowledge-evidence.json]
import fs from 'node:fs';
import path from 'node:path';

const args = process.argv.slice(2);
const getArg = (k, d) => { const i = args.indexOf(k); return i > 0 ? args[i + 1] : d; };
let REPO_ROOT = process.cwd();
try {
  const { execFileSync } = await import('node:child_process');
  REPO_ROOT = execFileSync('git', ['rev-parse', '--show-toplevel'], { encoding: 'utf8' }).trim();
} catch {}

const TEAMS = path.resolve(REPO_ROOT, getArg('--teams', 'data/act-teams.json'));
const KNOW = path.resolve(REPO_ROOT, getArg('--knowledge', 'data/knowledge-release.json'));
const OUT = path.resolve(REPO_ROOT, getArg('--out', 'data/knowledge-evidence.json'));

const readJson = (p) => JSON.parse(fs.readFileSync(p, 'utf8').replace(/^\uFEFF/, ''));

if (!fs.existsSync(TEAMS)) { console.error('缺 ' + TEAMS + '（先跑 act-teams.mjs）'); process.exit(2); }
if (!fs.existsSync(KNOW)) { console.error('缺 ' + KNOW); process.exit(2); }

const teams = readJson(TEAMS);
const know = readJson(KNOW);
const byAct = new Map((teams.acts || []).map(a => [a.act, a]));
const baseVideos = teams.videosWithAct || 0;

const rows = [];
for (const c of (know.candidates || [])) {
  const text = c.text || '';
  const m = text.match(/^第\s*(\d{1,2})\s*幕\s*推荐\s*(.+)$/);
  if (!m) continue;
  const act = Number(m[1]);
  const who = m[2].trim();
  const actData = byAct.get(act);
  const entry = actData?.top?.find(x => x.name === who);
  const videos = entry?.videos || 0;
  const rank = actData?.top ? actData.top.findIndex(x => x.name === who) + 1 : 0;

  let verdict;
  if (videos >= 2 || (rank > 0 && rank <= 3)) verdict = 'SUPPORTED';
  else if (videos === 1) verdict = 'WEAK';
  else verdict = 'UNSUPPORTED';

  rows.push({
    text, act, who, status: c.status || 'UNKNOWN',
    externalVideos: videos, rank, verdict,
    actTotal: actData?.distinct ?? 0,
    top3: actData?.top ? actData.top.slice(0, 3).map(x => `${x.name}(${x.videos})`) : [],
  });
}

const summary = {
  SUPPORTED: rows.filter(r => r.verdict === 'SUPPORTED').length,
  WEAK: rows.filter(r => r.verdict === 'WEAK').length,
  UNSUPPORTED: rows.filter(r => r.verdict === 'UNSUPPORTED').length,
};

console.log(`外部证据: ${teams.videosWithAct}/${teams.videosTotal} 份字幕含明确编队（基准 ${baseVideos}）`);
for (const r of rows) {
  console.log(`  [${r.verdict}] ${r.text}｜出场 ${r.externalVideos} 次｜该幕排名 ${r.rank || '-'}/${r.actTotal}` +
    (r.top3.length ? `｜Top3: ${r.top3.join(' ')}` : ''));
}
console.log(`汇总: SUPPORTED ${summary.SUPPORTED} / WEAK ${summary.WEAK} / UNSUPPORTED ${summary.UNSUPPORTED}`);

const out = {
  generatedAt: new Date().toISOString(),
  basis: { videosTotal: teams.videosTotal, videosWithAct: teams.videosWithAct },
  _note: '用字幕里的真实编队验证已验证知识——这是本仓库唯一的外部真值检验。UNSUPPORTED 的知识应降级。',
  summary, items: rows,
};
fs.writeFileSync(OUT, JSON.stringify(out, null, 2) + '\n', 'utf8');
console.log('已写 ' + OUT);

// 退出码：存在 UNSUPPORTED 且原状态为 VERIFIED → 1（供 CI 当门槛用）
const bad = rows.filter(r => r.verdict === 'UNSUPPORTED' && r.status === 'VERIFIED');
process.exit(bad.length > 0 ? 1 : 0);
