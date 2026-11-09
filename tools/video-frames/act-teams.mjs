#!/usr/bin/env node
// 从采集到的字幕文本里提取「每幕实际编队」，聚合成跨 UP 主的真实共识。
//
// 为什么重要（docs/AUDIT-OVERALL.md 的 P1）：
//   我们所有断言都是「实现符合我写的规格」，没有任何外部真值。
//   而 B站 UP 主的字幕里直接写着每幕用了谁，例如：
//     [description|] 第3幕：宵宫+夜兰+云堇+挂件
//   这既是**可证伪的知识检验**（推荐的角色到底出没出场），
//   也是**比元素匹配更靠谱的配队依据**（真实通关用的编队）。
//
// 用法：
//   node tools/video-frames/act-teams.mjs [--ledger <dir>] [--out ../../data/act-teams.json]
//   未指定 --ledger 时，从 git 的 automation-data 分支读取 subs.txt。
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';

const args = process.argv.slice(2);
const getArg = (k, d) => { const i = args.indexOf(k); return i > 0 ? args[i + 1] : d; };
const OUT = path.resolve(getArg('--out', '../../data/act-teams.json'));
const LEDGER = getArg('--ledger', '');

function say(...a) { console.log(...a); }

// git 在子目录里 ls-tree 只列该子目录，必须显式用仓库根。
let REPO_ROOT = process.cwd();
try {
  REPO_ROOT = execFileSync("git", ["rev-parse", "--show-toplevel"], { encoding: "utf8" }).trim();
} catch {}

// ---- 角色词典（中文名），用于从自然文本里认出角色 ----
function loadCharacters() {
  const p = path.join(REPO_ROOT, 'data', 'characters.json');
  const set = new Set();
  try {
    const j = JSON.parse(fs.readFileSync(p, 'utf8').replace(/^\uFEFF/, ''));
    const zh = j.zhNames || {};
    for (const v of Object.values(zh)) if (typeof v === 'string' && v.length > 1) set.add(v);
  } catch (e) { say('读 characters.json 失败: ' + e.message); }
  return set;
}

// ---- 收集字幕文件 ----
function collectSubs() {
  const out = [];
  if (LEDGER) {
    const walk = (d) => {
      for (const e of fs.readdirSync(d, { withFileTypes: true })) {
        const p = path.join(d, e.name);
        if (e.isDirectory()) walk(p);
        else if (e.name === 'subs.txt') out.push({ file: p, read: () => fs.readFileSync(p, 'utf8') });
      }
    };
    walk(LEDGER);
    return out;
  }
  // 从 automation-data 分支读（不 checkout，避免污染工作树）
  const list = execFileSync('git', ['-C', REPO_ROOT, 'ls-tree', '-r', '--name-only', 'origin/automation-data'], {
    encoding: 'utf8', maxBuffer: 64 * 1024 * 1024,
  }).split('\n').filter(f => f.endsWith('subs.txt'));
  for (const f of list) {
    out.push({
      file: f,
      read: () => execFileSync('git', ['-C', REPO_ROOT, 'show', `origin/automation-data:${f}`], {
        encoding: 'utf8', maxBuffer: 16 * 1024 * 1024,
      }),
    });
  }
  return out;
}

const CHARS = loadCharacters();
say(`角色词典: ${CHARS.size} 个`);

const subs = collectSubs();
say(`字幕文件: ${subs.length} 份`);

// ---- 解析：第N幕：角色+角色+... ----
// 同一行里可能有多幕写法（"需要选出角色：4幕聚怪"），只认 `第N幕：` 这种明确的编队行。
const ACT_LINE = /第\s*(\d{1,2})\s*幕\s*[：:]\s*([^\n]+)/g;

const perAct = new Map();      // act -> Map(char -> Set(videoKey))
let videosWithAct = 0, videosTotal = 0;

for (const s of subs) {
  videosTotal++;
  let text;
  try { text = s.read(); } catch { continue; }
  const videoKey = s.file.split('/').filter(Boolean).slice(-2, -1)[0] || s.file;
  let hit = false;

  for (const m of text.matchAll(ACT_LINE)) {
    const act = Number(m[1]);
    const tail = m[2] || '';
    hit = true;
    for (const c of CHARS) {
      if (tail.includes(c)) {
        if (!perAct.has(act)) perAct.set(act, new Map());
        const map = perAct.get(act);
        if (!map.has(c)) map.set(c, new Set());
        map.get(c).add(videoKey);
      }
    }
  }
  if (hit) videosWithAct++;
}

say(`含「第N幕：编队」的字幕: ${videosWithAct}/${videosTotal}`);

// ---- 聚合输出 ----
const teams = [];
for (const act of [...perAct.keys()].sort((a, b) => a - b)) {
  const map = perAct.get(act);
  const rows = [...map.entries()]
    .map(([name, set]) => ({ name, videos: set.size, share: set.size / Math.max(1, videosWithAct) }))
    .sort((a, b) => b.videos - a.videos || a.name.localeCompare(b.name));
  teams.push({ act, distinct: rows.length, top: rows.slice(0, 8) });
  say(`  第${act}幕: ${rows.length} 个不同角色｜Top: ${rows.slice(0, 4).map(r => `${r.name}(${r.videos})`).join(' ')}`);
}

const out = {
  generatedAt: new Date().toISOString(),
  source: LEDGER ? `ledger:${LEDGER}` : 'git:origin/automation-data',
  videosTotal,
  videosWithAct,
  _note: '从字幕提取的「每幕实际编队」，是外部真值：既用于证伪已验证知识，也可作为配队依据。',
  acts: teams,
};
fs.mkdirSync(path.dirname(OUT), { recursive: true });
fs.writeFileSync(OUT, JSON.stringify(out, null, 2) + '\n', 'utf8');
say(`已写 ${OUT}（${teams.length} 幕）`);
