import fs from 'node:fs';
// 搜同类视频：用 cookie 打开 B站站内搜索（按播放量排序），抽出 BV 号 + 标题。
//   node search.mjs "幻想真境剧诗 全程" 50
// 多页抓取：单页约 30-40 条，凑不够 want 时自动翻页（&page=N），最多 6 页。
// 只在页面 DOM 里取 href/标题，不调私有接口、不做签名。
// 进度信息写 stderr；stdout 只输出一个 JSON（供 CI 用 ConvertFrom-Json 解析）。
import { chromium } from 'playwright-core';

const exe = process.env.BROWSER_PATH;   // 存在才用（本机 Thorium），否则用 playwright 自带 chromium
const rawCookie = process.env.BILI_COOKIE || '';
const keyword = process.argv[2] || '幻想真境剧诗 全程';
const want = parseInt(process.argv[3] || '8', 10);
const maxPages = parseInt(process.argv[4] || '6', 10);
// 第 5 个参数：额外关键词，用 | 分隔。按播放量排序时前页常被同一 UP 主占满，
// 换关键词才能拿到真正独立的作者（实测单一关键词前 8 全是 mid=431073645）。
const extraKeywords = (process.argv[5] || '').split('|').map(s => s.trim()).filter(Boolean);
const queries = [keyword, ...extraKeywords];
// 第 6 个参数：排除表文件路径（账本里累积的「无文本证据」BV，Tab 分隔第一列）
const excludePath = process.argv[6] || '';
// 第 7 个参数：排序方式轮换（逗号分隔）。B站默认 order=click 按播放量，
// 头部会长期被同一个大 UP 主占据；轮换 pubdate/dm/stow 才能触达长尾作者。
const orderArg = process.argv[7] || 'click';
const orders = orderArg.split(',').map(s => s.trim()).filter(Boolean);
if (orders.length === 0) orders.push('click');
const excludedBv = new Set();
if (excludePath && fs.existsSync(excludePath)) {
  // Node fs 不认 'utf-8-sig'（Python 编码名），用 utf8 读取后手动去 BOM
  const raw = fs.readFileSync(excludePath, 'utf8').replace(/^\uFEFF/, '');
  for (const line of raw.split(/\r?\n/)) {
    const bv = line.trim().split('\t')[0];
    if (/^BV[0-9A-Za-z]+$/.test(bv)) excludedBv.add(bv);
  }
}

const cookies = rawCookie.split(';').map(s => s.trim()).filter(Boolean).map(kv => {
  const i = kv.indexOf('=');
  return { name: kv.slice(0, i), value: kv.slice(i + 1), domain: '.bilibili.com', path: '/' };
}).filter(c => c.name && c.value !== undefined);

const launchOpts = { headless: true, args: ['--disable-gpu', '--lang=zh-CN'] };
if (exe && fs.existsSync(exe)) launchOpts.executablePath = exe;
const browser = await chromium.launch(launchOpts);
const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 }, locale: 'zh-CN' });
if (cookies.length) await ctx.addCookies(cookies);
const page = await ctx.newPage();

// 搜索页 DOM 与内嵌状态常拿不到 UP 主 mid；拦截官方搜索接口响应建立 bvid->mid 映射。
// 这是公开搜索页自身发出的请求，不新增任何私有接口调用。
const apiAuthors = new Map();
page.on('response', async (resp) => {
  const u = resp.url();
  if (!/api\.bilibili\.com\/x\/web-interface\/(wbi\/)?search\/type/.test(u)) return;
  try {
    const j = await resp.json();
    const rows = j?.data?.result || [];
    for (const r of rows) {
      const bv = r.bvid || r.bv;
      const mid = r.mid ?? r.author?.mid ?? r.owner?.mid;
      if (bv && mid !== undefined) apiAuthors.set(String(bv), {
        id: String(mid), name: String(r.author || r.uname || ''),
      });
    }
  } catch {}
});

const all = [];              // 已知作者（author_id 或 author 名）的条目
const fallback = [];         // 作者未知、只能按 BV 去重的条目
const seen = new Set();
const seenAuthors = new Set();
let emptyPages = 0;

for (const [qi, q] of queries.entries()) {
  if (all.length >= want) break;
  const order = orders[qi % orders.length];
  for (let p = 1; p <= maxPages && all.length < want; p++) {
  const url = `https://search.bilibili.com/video?keyword=${encodeURIComponent(q)}&order=${encodeURIComponent(order)}&page=${p}`;
  try {
    await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 60000 });
    await page.waitForTimeout(3500);
  } catch (e) {
    console.error(`[${q}] page ${p} goto 失败: ${e.message}`);
    break;
  }

  const items = await page.evaluate((authorRows) => {
    const apiAuthors = new Map(authorRows);
    // 先从页面内嵌状态里建立 BV -> mid 映射（B站搜索页是 SPA，DOM 里的作者名常为空）。
    const byAuthor = new Map();
    const collect = (obj, depth) => {
      if (!obj || typeof obj !== 'object' || depth > 8) return;
      if (Array.isArray(obj)) { for (const x of obj) collect(x, depth + 1); return; }
      const bv = obj.bvid || obj.bv_id || obj.bv;
      const mid = obj.mid ?? obj.owner?.mid ?? obj.author?.mid ?? obj.upper?.mid;
      const name = obj.author || obj.owner?.name || obj.upper?.name || obj.uname;
      if (typeof bv === 'string' && /^BV[0-9A-Za-z]+$/.test(bv) && mid !== undefined)
        byAuthor.set(bv, { id: String(mid), name: String(name || '') });
      for (const v of Object.values(obj)) collect(v, depth + 1);
    };
    for (const key of ['__INITIAL_STATE__', '__NEXT_DATA__', '__NUXT__']) {
      if (window[key]) collect(window[key], 0);
    }
    // 兜底：有些版本把数据放在 script[type=application/json] 里
    document.querySelectorAll('script[type="application/json"]').forEach(s => {
      try { collect(JSON.parse(s.textContent || 'null'), 0); } catch {}
    });

    const out = [];
    document.querySelectorAll('a[href*="/video/BV"]').forEach(a => {
      const m = /\/video\/(BV[0-9A-Za-z]+)/.exec(a.getAttribute('href') || '');
      if (!m) return;
      const box = a.closest('.bili-video-card') || a.parentElement;
      let title = (a.getAttribute('title') || '').trim();
      if (!title) {
        const titEl = box?.querySelector('.bili-video-card__info--tit, h3');
        title = (titEl?.getAttribute('title') || titEl?.innerText || box?.innerText || '').trim().split('\n')[0];
      }
      const domNameEl = box?.querySelector('.bili-video-card__info--author, .bili-video-card__info--by, [class*="author"]');
      const domName = (domNameEl?.innerText || domNameEl?.getAttribute('title') || '').trim().slice(0, 60);
      const state = byAuthor.get(m[1]) || apiAuthors.get(m[1]);
      out.push({
        bv: m[1], title: title.slice(0, 90),
        author: (state?.name || domName || ''),
        author_id: state?.id || '',
      });
    });
    return out;
  }, [...apiAuthors.entries()]);

  let added = 0;
  for (const it of items) {
    if (seen.has(it.bv) || excludedBv.has(it.bv)) continue;
    // 独立作者优先：只有拿到稳定作者键（mid / 作者名）才计入达标；
    // 作者未知的条目先放 fallback，绝不因为「BV 不同」就冒充独立作者。
    const authorKey = it.author_id || it.author || '';
    if (!authorKey) {
      // 记录来源关键词，最终按关键词轮转取，避免全被第一个查询的前几条占满
      if (!seen.has(it.bv)) { seen.add(it.bv); fallback.push({ ...it, query: q }); added++; }
      continue;
    }
    if (seenAuthors.has(authorKey) || seen.has(it.bv)) continue;
    seen.add(it.bv);
    seenAuthors.add(authorKey);
    all.push(it);
    added++;
    if (all.length >= want) break;
  }
  console.error(`[${q}] page ${p}: 抓到 ${items.length} 条，新增 ${added}，已知作者条目 ${all.length}/${want}，独立作者 ${seenAuthors.size}，待补 ${fallback.length}`);
  if (added === 0) { emptyPages++; if (emptyPages >= 2) break; } else emptyPages = 0;
  }
}

// 「相关纯净源」打分：优先无解说/无剪辑的完整实录，且赛季要对得上。
// 分数只用于排序，不删除任何来源——排序依据会写进输出，可审计。
// 「纯净流」判定（标题层）：硬排除 + 初步分级。
// 审计背景：原实现只有加减分，没有硬排除、也没有时长（搜索阶段拿不到时长），
// 且"攻略/低配/满花"与"纯净/无解说"同属加分项 → 攻略型混剪可能排序高于纯净流。
// 这里分两步：本函数做**标题硬排除**，时长相关的最终分级在 ingest.py（有 duration）。
const EXCLUDE_RE = /(解说|实况|吐槽|整活|切片|混剪|合集|锐评|测评|分析|讲解|反应|弹幕版|二创)/;
const PURE_RE = /(纯净|无解说|无剪辑|原声|全程|完整|一条过|无删减)/;

function titlePurity(title) {
  const t = title || '';
  if (EXCLUDE_RE.test(t)) return 'excluded';   // 明确是解说/剪辑类，直接不要
  if (PURE_RE.test(t)) return 'pure-title';    // 标题自称纯净，待时长确认
  return 'unknown';
}

function relevanceScore(title) {
  const t = title || '';
  let s = 0;
  const has = (re) => re.test(t);
  // 纯净源特征
  if (has(/纯净|无解说|无剪辑|原声|全程/)) s += 4;
  if (EXCLUDE_RE.test(t)) s -= 99;   // 硬排除：不参与排序竞争
  // 与目标强相关（本玩法/难度）
  if (has(/幻想真境剧诗|剧诗/)) s += 2;
  // 高难度加权：用户明确要"高难录制相关"，因此月谕/卓越必须显著高于一般攻略内容。
  // 审计背景：原实现里"月谕|卓越"只 +2，与"满花|低配|平民"同分 →
  // 低配向的普通难度视频会与高难录制打平甚至更高（本轮修正）。
  if (has(/月谕|Lunar/i)) s += 8;
  else if (has(/卓越|Visionary/i)) s += 6;
  else if (has(/困难(?!难度?以上)|\bHard\b/i)) s += 3;
  if (has(/普通|Easy/)) s -= 2;
  // 可执行性：满花/满星/低配是攻略型内容
  if (has(/满花|满星|低配|平民|试用/)) s += 2;
  // 低配/平民的"可参考性"加分降为 +1：它们是易上手，不等于高难录制
  if (has(/低配|平民/)) s += 1;
  // 赛季相关性：当前赛季周期（可从环境变量覆盖）优先
  const period = process.env.TARGET_PERIOD || '2026-11';
  const [yy, mm] = period.split('-');
  if (new RegExp(`${yy}\s*年\s*0?${Number(mm)}\s*月`).test(t)) s += 5;
  else if (new RegExp(`${yy}\s*年`).test(t)) s += 1;
  // 明显是往年赛季的降权（仍保留，供跨赛季对比，但不优先）
  if (has(/202[0-5]s*年/)) s -= 2;
  return s;
}

// 已知作者不足时按「关键词轮转」从未知作者条目补足：
// 同一关键词按播放量排序常被一个 UP 主占满，轮转能采到不同作者（真实 mid 由 yt-dlp 阶段写）。
for (const it of all) { it.relevance = relevanceScore(it.title); it.title_purity = titlePurity(it.title); }
all.sort((x, y) => (y.relevance || 0) - (x.relevance || 0));
const usable = all.filter(x => x.title_purity !== 'excluded');
const excludedCount = all.length - usable.length;
const chosen = usable.slice(0, want);
const byQuery = new Map();
for (const it of fallback) {
  const k = it.query || keyword;
  if (!byQuery.has(k)) byQuery.set(k, []);
  byQuery.get(k).push({ ...it, relevance: relevanceScore(it.title) });
}
const queues = [...byQuery.values()];
let round = 0;
while (chosen.length < want && queues.some(q => q.length > 0)) {
  for (const q of queues) {
    if (chosen.length >= want) break;
    q.sort((x, y) => (y.relevance || 0) - (x.relevance || 0));
    const it = q.shift();
    if (!it || chosen.some(x => x.bv === it.bv)) continue;
    chosen.push({ ...it, creator_unknown: true });
  }
  round++;
  if (round > want * 4) break;
}
console.log(JSON.stringify({
  keyword, queries, pages: maxPages,
  count: chosen.length,
  relevance_scored: true,
  target_period: process.env.TARGET_PERIOD || '2026-11',
  independent_creators: seenAuthors.size,
  creator_unknown_count: chosen.filter(x => x.creator_unknown).length,
  title_excluded_count: excludedCount,
  title_pure_count: chosen.filter(x => x.title_purity === 'pure-title').length,
  items: chosen,
}, null, 2));
await browser.close();
