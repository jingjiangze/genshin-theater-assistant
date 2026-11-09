#!/usr/bin/env node
// 批量抓 BWIKI 的剧诗子页面：各期详情（第一期…第三十期）+ 专题页（首领怪物/待选事件/规则）。
//
// 合规：
//   · 只走 MediaWiki 公开 api.php（robots 只禁 index.php?）；
//   · 每次请求间隔 350ms（礼貌抓取，不给社区服务器添负担）；
//   · 每条语料标注来源页，保留许可说明。
//
// 用法：node tools/video-frames/bwiki-periods.mjs [--max-period 30] [--out ../../data/bwiki-periods.json]
import fs from 'node:fs';
import path from 'node:path';

const args = process.argv.slice(2);
const getArg = (k, d) => { const i = args.indexOf(k); return i > 0 ? args[i + 1] : d; };
let ROOT = process.cwd();
try {
  const { execFileSync } = await import('node:child_process');
  ROOT = execFileSync('git', ['rev-parse', '--show-toplevel'], { encoding: 'utf8' }).trim();
} catch {}

const API = 'https://wiki.biligame.com/ys/api.php';
const OUT = path.resolve(ROOT, getArg('--out', 'data/bwiki-periods.json'));
const MAX_PERIOD = Number(getArg('--max-period', '30'));
const DELAY_MS = 350;

const CN = ['零', '一', '二', '三', '四', '五', '六', '七', '八', '九', '十'];
/** 1..99 → 中文数字（第一期的写法） */
function cnNum(n) {
  if (n <= 10) return CN[n];
  if (n < 20) return '十' + CN[n - 10];
  const t = Math.floor(n / 10), u = n % 10;
  return CN[t] + '十' + (u === 0 ? '' : CN[u]);
}

const sleep = (ms) => new Promise(r => setTimeout(r, ms));

async function fetchWikitext(page) {
  const url = `${API}?action=parse&page=${encodeURIComponent(page)}&format=json&prop=wikitext&redirects=1`;
  const res = await fetch(url, { headers: { 'User-Agent': 'theater-assistant/0.72 (read-only corpus builder)' } });
  if (!res.ok) return null;
  const j = await res.json();
  if (j?.error) return null;
  return j?.parse?.wikitext?.['*'] || null;
}

function clean(s) {
  if (!s) return '';
  let t = s;
  t = t.replace(/\{\{效果\|([^}|]+)(\|[^}]*)?\}\}/g, '$1');
  t = t.replace(/\{\{卡牌图标\|[^}]*\|([^}|]+)(\|[^}]*)?\}\}/g, '$1');
  t = t.replace(/\{\{颜色\|([^}|]+)\|([^}|]+)\}\}/g, '$2');
  t = t.replace(/\{\{[^{}]*\}\}/g, '');
  t = t.replace(/\[\[([^\]|]+)\|([^\]]+)\]\]/g, '$2');
  t = t.replace(/\[\[([^\]]+)\]\]/g, '$1');
  t = t.replace(/<[^>]+>/g, ' ');
  t = t.replace(/'''?/g, '');
  t = t.replace(/&[a-z]+;/g, ' ');
  t = t.replace(/\|[^|\n]{0,20}\|/g, ' ');
  t = t.replace(/[ \t]+/g, ' ');
  t = t.replace(/\n{3,}/g, '\n\n');
  return t.trim();
}

function isJunk(text) {
  if (!text) return true;
  if (/@media|grid-template|display:\s*none|color:\s*#|border-radius/.test(text)) return true;
  if (/\{|\}/.test(text)) return true;
  const cjk = (text.match(/[\u4e00-\u9fff]/g) || []).length;
  return cjk < 14;
}

function chunk(text, max = 240) {
  const parts = text.split(/(?<=[。；！？])/).map(x => x.trim()).filter(Boolean);
  const out = [];
  let cur = '';
  for (const s of parts) {
    if ((cur + s).length > max && cur.length > 0) { out.push(cur); cur = s; }
    else cur += s;
  }
  if (cur.length > 0) out.push(cur);
  return out;
}

const entries = [];
let seq = 0;
const add = (e) => {
  if (!e.text || e.text.length < 25) return;
  entries.push({
    id: `bwiki-p-${++seq}`,
    act: e.act ?? 0,
    tags: e.tags || [],
    title: e.title,
    text: e.text.slice(0, 340),
    source: e.source,
  });
};

// ── ① 各期详情 ────────────────────────────────────────────────
let okPeriods = 0;
for (let i = 1; i <= MAX_PERIOD; i++) {
  const page = `幻想真境剧诗/第${cnNum(i)}期`;
  let w = null;
  try { w = await fetchWikitext(page); } catch { }
  await sleep(DELAY_MS);
  if (!w) { console.log(`  第${cnNum(i)}期：无页面`); continue; }
  okPeriods++;
  const body = clean(w);
  const date = (w.match(/(20\d{2})[.年](\d{1,2})/) || [])[0] || '';
  // 元素组合：从 w 里找「火水雷」这类三元素并列
  const elemHits = [...w.matchAll(/\{\{卡牌图标\|UI\|元素\|([火水雷冰风岩草])/g)].map(m => m[1]);
  const elems = [...new Set(elemHits)].slice(0, 6);
  const boss = (body.match(/首领[：:]\s*([^\n。]{2,40})/) || [])[1] || '';

  add({
    act: 0,
    tags: ['每期详情', `第${i}期`],
    title: `第${i}期（${date || '日期未知'}）`,
    text: `幻境真境剧诗第${i}期${date ? `（${date}）` : ''}${elems.length ? `，开放元素：${elems.join('/')}` : ''}${boss ? `；首领：${boss}` : ''}。来源页：${page}`,
    source: `BWIKI ${page}`,
  });

  // 正文按句切块（过滤垃圾）
  for (const blk of chunk(body)) {
    const text = blk.replace(/\n/g, ' ').trim();
    if (isJunk(text)) continue;
    add({
      act: 0,
      tags: ['每期详情', `第${i}期`],
      title: `第${i}期 详情`,
      text,
      source: `BWIKI ${page}`,
    });
  }
  console.log(`  第${cnNum(i)}期：${w.length} 字符，元素=${elems.join('/') || '?'}`);
}

// ── ①b 年份页（19 期以后写在 幻想真境剧诗/2026#第N期 这种形式里）──
for (const year of [2024, 2025, 2026]) {
  const page = `幻想真境剧诗/${year}`;
  let w = null;
  try { w = await fetchWikitext(page); } catch { }
  await sleep(DELAY_MS);
  if (!w) { console.log(`  ${page}：无页面`); continue; }
  const body = clean(w);
  // 按"第N期"切分，每期一块
  const marks = [...body.matchAll(/第\s*(\d{1,2})\s*期/g)];
  let n = 0;
  for (let k = 0; k < marks.length; k++) {
    const start = marks[k].index;
    const end = k + 1 < marks.length ? marks[k + 1].index : body.length;
    const seg = body.slice(start, Math.min(end, start + 700)).replace(/\n/g, " ").trim();
    if (isJunk(seg)) continue;
    add({
      act: 0,
      tags: ["每期详情", `第${marks[k][1]}期`, "年份页"],
      title: `第${marks[k][1]}期 详情（${year}）`,
      text: seg.slice(0, 340),
      source: `BWIKI ${page}`,
    });
    n++;
  }
  console.log(`  ${page}：${w.length} 字符 → ${n} 条`);
}

// ── ② 专题页 ──────────────────────────────────────────────────
for (const sub of ['首领怪物', '待选事件', '规则']) {
  const page = `幻想真境剧诗/${sub}`;
  let w = null;
  try { w = await fetchWikitext(page); } catch { }
  await sleep(DELAY_MS);
  if (!w) { console.log(`  专题 ${sub}：无页面`); continue; }
  const body = clean(w);
  let n = 0;
  for (const blk of chunk(body)) {
    const text = blk.replace(/\n/g, ' ').trim();
    if (isJunk(text)) continue;
    add({
      act: 0,
      tags: ['专题', sub],
      title: `幻境真境剧诗 · ${sub}`,
      text,
      source: `BWIKI ${page}`,
    });
    n++;
  }
  console.log(`  专题 ${sub}：${w.length} 字符 → ${n} 条`);
}

console.log(`\n合计 ${entries.length} 条（${okPeriods} 期 + 专题）`);

fs.writeFileSync(OUT, JSON.stringify({
  generatedAt: new Date().toISOString(),
  source: {
    api: API,
    license: 'BWIKI 社区内容（CC BY-NC-SA 系）；仅作本项目内部语料',
    periods: okPeriods,
  },
  entries,
}, null, 2) + '\n', 'utf8');
console.log('已写 ' + OUT);
