// 抓原神 BWIKI 的「幻想真境剧诗」页面，清洗 wikitext → 中文语料条目。
// 数据源：wiki.biligame.com MediaWiki API（action=parse&prop=wikitext），只读、只用公开页面 API。
//
//   node tools/fetch-wiki-corpus.mjs                 # 主页 + 最近 3 期
//   node tools/fetch-wiki-corpus.mjs --out data/guide-corpus-wiki.json
//
// 输出格式与 data/guide-corpus.json 兼容（entries[{id,act,tags,title,text,source}]），
// 运行时由 GuideIndex.LoadWithExtra 一起加载。
import fs from 'node:fs';

const args = process.argv.slice(2);
const outIdx = args.indexOf('--out');
const outPath = outIdx >= 0 ? args[outIdx + 1] : 'data/guide-corpus-wiki.json';
const pagesIdx = args.indexOf('--pages');
const extraPages = pagesIdx >= 0 ? String(args[pagesIdx + 1] || '').split(/[,，]/).map(s => s.trim()).filter(Boolean) : [];
const API = 'https://wiki.biligame.com/ys/api.php';
const HEADERS = { 'User-Agent': 'Mozilla/5.0 (compatible; theater-assistant/1.0; +https://github.com/jingjiangze/genshin-theater-assistant)' };

async function wikitext(page) {
  const u = `${API}?action=parse&page=${encodeURIComponent(page)}&format=json&prop=wikitext&formatversion=2&redirects=1`;
  const r = await fetch(u, { headers: HEADERS });
  if (!r.ok) throw new Error('HTTP ' + r.status);
  const j = await r.json();
  if (j.error) throw new Error(`${j.error.code}: ${j.error.info}`);
  return j.parse?.wikitext || '';
}

/** 模板参数里常藏着机制说明（例如 {{怪物技能|描述=...}}）：先把"参数值"抽出来，再交给人眼/语料 */
function templateValues(text) {
  const out = [];
  const re = /\{\{([^{}]{2,80})\|([^{}]{10,4000})\}\}/g;
  let m;
  while ((m = re.exec(text))) {
    const body = m[2];
    for (const part of body.split('|')) {
      const eq = part.indexOf('=');
      const val = (eq >= 0 ? part.slice(eq + 1) : part).trim();
      if (cjkLen(val) >= 16 && !/^\d+$/.test(val)) out.push(val);
    }
  }
  return out;
}

/** 清洗 wikitext：去模板/标签/表格/文件/引用，保留可读中文句子 */
function clean(text) {
  let s = text;
  s = s.replace(/<!--[\s\S]*?-->/g, ' ');
  s = s.replace(/\{\{[\s\S]*?\}\}/g, ' ');          // 模板（含嵌套的近似处理）
  s = s.replace(/\[\[(?:[^\]|]*\|)?([^\]]+)\]\]/g, '$1');
  s = s.replace(/\[https?:\/\/[^\s\]]+\s*([^\]]*)\]/g, '$1');
  s = s.replace(/<[^>]+>/g, ' ');
  s = s.replace(/\{\||[\s\S]*?\|\}/g, ' ');          // 表格
  s = s.replace(/^[|!].*$/gm, ' ');
  s = s.replace(/\[\[File:[^\]]*\]\]/gi, ' ');
  s = s.replace(/'''?/g, '');
  s = s.replace(/&nbsp;|&emsp;|&amp;/g, ' ');
  s = s.replace(/[ \t\u3000]+/g, ' ');
  s = s.replace(/\n{2,}/g, '\n');
  return s.trim();
}

const cjkLen = (s) => (s.match(/[\u4e00-\u9fff]/g) || []).length;
const ACT_RE = /第\s*(\d{1,2})\s*幕/;

function splitSections(wt) {
  const lines = wt.split('\n');
  const out = [];
  let cur = { title: '（导语）', body: [] };
  for (const line of lines) {
    const m = /^=+\s*(.+?)\s*=+\s*$/.exec(line);
    if (m) { out.push(cur); cur = { title: m[1], body: [] }; }
    else cur.body.push(line);
  }
  out.push(cur);
  return out;
}

// 用 allpages 枚举真实子页面（模板拼出来的名字不一定存在），按中文数字排序取最近 3 期
const CN = { 一: 1, 二: 2, 三: 3, 四: 4, 五: 5, 六: 6, 七: 7, 八: 8, 九: 9, 十: 10 };
function cnNum(s) {
  if (/^\d+$/.test(s)) return parseInt(s, 10);
  let total = 0, cur = 0;
  for (const ch of s) {
    if (ch === '十') { total += (cur || 1) * 10; cur = 0; }
    else if (CN[ch]) cur = CN[ch];
  }
  return total + cur;
}
const pages = ['幻想真境剧诗', ...extraPages];
try {
  const u = `${API}?action=query&list=allpages&apprefix=${encodeURIComponent('幻想真境剧诗/')}&aplimit=100&format=json&formatversion=2`;
  const j = await (await fetch(u, { headers: HEADERS })).json();
  const periods = (j.query?.allpages || [])
    .map(p => p.title)
    .map(t => ({ t, m: /^(幻想真境剧诗\/(第[一二三四五六七八九十\d]+期))$/.exec(t) }))
    .filter(x => x.m)
    .map(x => ({ page: x.m[1], n: cnNum(/第(.+)期/.exec(x.m[1])[1]) }))
    .sort((a, b) => a.n - b.n);
  console.log('枚举到真实期数页面:', periods.length, periods.slice(-5).map(p => p.page).join(', '));
  for (const p of periods.slice(-3)) pages.push(p.page);
} catch (e) {
  console.warn('子页面枚举失败，仅抓主页:', e.message);
}

const entries = [];
let seq = 0;
for (const page of pages) {
  let wt;
  try { wt = await wikitext(page); } catch (e) { console.log('跳过', page, e.message); continue; }
  console.log(`${page}: wikitext ${wt.length} 字符`);
  for (const sec of splitSections(wt)) {
    const body = clean(sec.body.join('\n'));
    if (cjkLen(body) < 40) continue;                        // 太短的多半是导航/表格残渣
    // 一句话一条：按中文句号/换行切，保留 20~160 字的句子
    const sents = body.split(/[。！？\n]/).map(s => s.trim()).filter(s => cjkLen(s) >= 16 && s.length <= 180);
    const keep = sents.slice(0, 6);
    if (keep.length === 0) continue;
    const text = keep.join('。') + '。';
    const m = ACT_RE.exec(text);
    entries.push({
      id: `wiki-${++seq}`,
      act: m ? parseInt(m[1], 10) : 0,
      tags: ['wiki', sec.title, m ? `第${m[1]}幕` : '通用'],
      title: `${page}#${sec.title}`.slice(0, 40),
      text,
      source: `bwiki:${page}#${sec.title}`,
    });
  }
}

const payload = {
  note: '由 tools/fetch-wiki-corpus.mjs 从 BWIKI（wiki.biligame.com）抓取并清洗，只保留可读中文句子；引用请注明 BWIKI。',
  generatedAt: new Date().toISOString(),
  pages,
  entries,
};
fs.writeFileSync(outPath, JSON.stringify(payload, null, 1), 'utf8');
console.log(`写出 ${entries.length} 条 → ${outPath}`);
const acts = entries.filter(e => e.act > 0).length;
console.log(`其中带幕次 ${acts} 条；平均长度 ${(entries.reduce((a, e) => a + e.text.length, 0) / Math.max(1, entries.length)).toFixed(0)} 字`);
console.log('样例:', entries.slice(0, 2).map(e => `${e.title} :: ${e.text.slice(0, 60)}…`).join('\n      '));
