#!/usr/bin/env node
// 从 BWIKI（幻想真境剧诗专页）提取结构化语料。
//
// 为什么用 BWIKI：
//   · 社区权威 wiki，内容结构清晰（每期详情/玩法规则/待选事件/首领/反应加强）；
//   · MediaWiki 公开 API（robots.txt 只禁 index.php?，未禁 api.php），合规；
//   · 覆盖我们缺的部分：待选事件（buff）、首领机制、元素反应加强历史。
//
// 用法：node tools/video-frames/bwiki-corpus.mjs [--out ../../data/bwiki-corpus.json]
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
const PAGE = '幻想真境剧诗';
const OUT = path.resolve(ROOT, getArg('--out', 'data/bwiki-corpus.json'));

async function fetchWikitext() {
  const url = `${API}?action=parse&page=${encodeURIComponent(PAGE)}&format=json&prop=wikitext`;
  const res = await fetch(url, { headers: { 'User-Agent': 'theater-assistant/0.71 (read-only corpus builder)' } });
  if (!res.ok) throw new Error('HTTP ' + res.status);
  const j = await res.json();
  const w = j?.parse?.wikitext?.['*'];
  if (!w) throw new Error('无 wikitext：' + JSON.stringify(j?.error || {}).slice(0, 120));
  return w;
}

/** wiki 标记清理 */
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

/** 垃圾判定：CSS / 纯链接列表 / 有效汉字太少 */
function isJunk(text) {
  if (!text) return true;
  if (/@media|grid-template|display:\s*none|color:\s*#|border-radius/.test(text)) return true;
  if (/\{|\}/.test(text)) return true;
  const linkHits = (text.match(/幻想真境剧诗\//g) || []).length;
  if (linkHits >= 2) return true;
  const cjk = (text.match(/[\u4e00-\u9fff]/g) || []).length;
  if (cjk < 14) return true;
  return false;
}

/** 按句切块，每块 ~240 字 */
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

/** 按标题切章节（含二级/三级） */
function splitSections(w) {
  const out = [];
  const re = /^(==+)\s*([^=\n]+?)\s*==+\s*$/gm;
  const marks = [...w.matchAll(re)];
  for (let i = 0; i < marks.length; i++) {
    const level = marks[i][1].length;
    const title = marks[i][2].trim();
    const start = marks[i].index + marks[i][0].length;
    const end = i + 1 < marks.length ? marks[i + 1].index : w.length;
    out.push({ level, title, body: w.slice(start, end) });
  }
  return out;
}

const wikitext = await fetchWikitext();
console.log(`BWIKI 页面拉取成功：${wikitext.length} 字符`);

const sections = splitSections(wikitext);
console.log(`章节数：${sections.length}（含三级）`);

const entries = [];
let seq = 0;
const add = (e) => {
  if (!e.text || e.text.length < 25) return;
  entries.push({
    id: `bwiki-${++seq}`,
    act: e.act ?? 0,
    tags: e.tags || [],
    title: e.title,
    text: e.text.slice(0, 340),
    source: 'BWIKI 幻想真境剧诗（wiki.biligame.com/ys）',
  });
};

// ① 章节正文：二级 + 三级，按句切块
for (const sec of sections) {
  if (sec.level > 3) continue;
  const body = clean(sec.body);
  if (body.length < 30) continue;
  for (const blk of chunk(body)) {
    const text = blk.replace(/\n/g, ' ').trim();
    if (isJunk(text)) continue;
    const m = text.match(/第\s*(\d{1,2})\s*幕/);
    add({ act: m ? Number(m[1]) : 0, tags: ['BWIKI', sec.title], title: sec.title, text });
  }
}

// ② 每期详情：从链接列表里抽「第N期（年月）」，每期一条
{
  const sec = sections.find(s => s.title.includes('每期详情'));
  if (sec) {
    const raw = sec.body;
    const periods = [...raw.matchAll(/幻想真境剧诗\/第([一二三四五六七八九十百\d]+)期[（(]([^）)]+)[）)]/g)];
    const seen = new Set();
    for (const p of periods) {
      const name = p[1];
      const when = p[2];
      if (seen.has(name)) continue;
      seen.add(name);
      add({
        act: 0,
        tags: ['每期详情', '期数'],
        title: `幻境真境剧诗 第${name}期`,
        text: `第${name}期于 ${when} 上线。每期会轮换开放元素组合与开幕/待命角色，具体组合见当期游戏内界面或本页对应子页面。`,
      });
    }
    console.log(`  每期详情：解析到 ${seen.size} 期`);
  }
}

// ③ 待选事件（buff 类，我们此前完全没有）
{
  const sec = sections.find(s => s.title.includes('待选事件'));
  if (sec) {
    const body = clean(sec.body);
    for (const kind of ['神秘收获', '伙伴', '惊喜', '助益', '礼物']) {
      const idx = body.indexOf(kind);
      if (idx < 0) continue;
      const slice = body.slice(idx, idx + 400).replace(/\n/g, ' ').trim();
      add({ act: 0, tags: ['待选事件', 'buff', kind], title: `待选事件：${kind}`, text: slice });
    }
  }
}

// ④ 元素反应加强（辉彩祝福 / 加强历史）—— 影响配队取向
for (const key of ['辉彩祝福', '加强历史']) {
  const secs = sections.filter(s => s.title.includes(key));
  for (const sec of secs) {
    const body = clean(sec.body).replace(/\n/g, ' ');
    for (const blk of chunk(body, 260)) {
      if (isJunk(blk)) continue;
      add({ act: 0, tags: ['元素反应', key], title: `元素反应强化：${key}`, text: blk });
    }
  }
}

// ⑤ 首领怪物
{
  const secs = sections.filter(s => s.title.includes('首领怪物'));
  for (const sec of secs) {
    const body = clean(sec.body).replace(/\n/g, ' ');
    for (const blk of chunk(body, 260)) {
      if (isJunk(blk)) continue;
      add({ act: 0, tags: ['首领', '机制'], title: '首领怪物与机制', text: blk });
    }
  }
}

console.log(`生成 ${entries.length} 条 BWIKI 语料`);

fs.writeFileSync(OUT, JSON.stringify({
  generatedAt: new Date().toISOString(),
  source: {
    page: PAGE,
    api: API,
    url: 'https://wiki.biligame.com/ys/幻想真境剧诗',
    license: 'BWIKI 社区内容（CC BY-NC-SA 系）；仅作本项目内部语料，转载需遵守原站协议',
    fetchedChars: wikitext.length,
  },
  entries,
}, null, 2) + '\n', 'utf8');
console.log('已写 ' + OUT);
