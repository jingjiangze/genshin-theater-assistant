#!/usr/bin/env node
// 从米游社（HoYoLAB 国服）抓「幻想真境剧诗」攻略语料。
//
// 合规：
//   · 只用**公开** API（bbs-api.miyoushe.com/post/wapi/searchPosts 与 getPostFull），实测无需签名；
//   · 不登录、不绕过鉴权、不限速滥用（每请求 400ms）；
//   · 只取正文文本用于本地检索，每条标注原作者与 post_id，保留出处。
//
// 用法：node tools/video-frames/miyoushe-corpus.mjs [--max 40] [--out ../../data/miyoushe-corpus.json]
import fs from 'node:fs';
import path from 'node:path';

const args = process.argv.slice(2);
const getArg = (k, d) => { const i = args.indexOf(k); return i > 0 ? args[i + 1] : d; };
let ROOT = process.cwd();
try {
  const { execFileSync } = await import('node:child_process');
  ROOT = execFileSync('git', ['rev-parse', '--show-toplevel'], { encoding: 'utf8' }).trim();
} catch {}

const OUT = path.resolve(ROOT, getArg('--out', 'data/miyoushe-corpus.json'));
const MAX = Number(getArg('--max', '40'));
const DELAY = 400;
const H = {
  'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) theater-assistant/0.73',
  'Referer': 'https://www.miyoushe.com/ys/',
  'Accept': 'application/json',
};
const sleep = (ms) => new Promise(r => setTimeout(r, ms));

async function getJson(url) {
  try {
    const r = await fetch(url, { headers: H });
    if (!r.ok) return null;
    return await r.json();
  } catch { return null; }
}

/** 搜「幻想真境剧诗」相关帖子 */
async function search(keyword, size = 20, lastId = '') {
  const u = `https://bbs-api.miyoushe.com/post/wapi/searchPosts?keyword=${encodeURIComponent(keyword)}&size=${size}`
    + (lastId ? `&last_id=${encodeURIComponent(lastId)}` : '');
  const j = await getJson(u);
  if (!j || j.retcode !== 0) return { posts: [], next: '' };
  const posts = (j.data?.posts || []).map(x => x.post).filter(Boolean);
  return { posts, next: j.data?.last_id || '' };
}

/** 取帖子正文（HTML → 纯文本） */
async function fullPost(postId) {
  const j = await getJson(`https://bbs-api.miyoushe.com/post/wapi/getPostFull?post_id=${encodeURIComponent(postId)}`);
  if (!j || j.retcode !== 0) return null;
  const p = j.data?.post?.post;
  if (!p) return null;
  return { html: p.content || '', subject: p.subject || '', uid: p.uid || '', createdAt: p.created_at || 0 };
}

function htmlToText(html) {
  if (!html) return '';
  let t = html;
  t = t.replace(/<img[^>]*alt="([^"]*)"[^>]*>/gi, ' $1 ');
  t = t.replace(/<img[^>]*>/gi, ' ');
  t = t.replace(/<br\s*\/?>/gi, '\n');
  t = t.replace(/<\/(p|div|li|h[1-6])>/gi, '\n');
  t = t.replace(/<[^>]+>/g, '');
  t = t.replace(/&nbsp;/g, ' ').replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"');
  t = t.replace(/[ \t]+/g, ' ').replace(/\n{3,}/g, '\n\n');
  return t.trim();
}

function chunk(text, max = 260) {
  const parts = text.split(/(?<=[。；！？\n])/).map(x => x.trim()).filter(Boolean);
  const out = [];
  let cur = '';
  for (const s of parts) {
    if ((cur + s).length > max && cur.length > 0) { out.push(cur); cur = s; }
    else cur += (cur ? ' ' : '') + s;
  }
  if (cur.length > 0) out.push(cur);
  return out;
}

function isJunk(text) {
  if (!text) return true;
  const cjk = (text.match(/[\u4e00-\u9fff]/g) || []).length;
  if (cjk < 20) return true;
  if (/^(广告|本页|上一篇|下一篇)/.test(text)) return true;
  return false;
}

const KEYWORDS = ['幻想真境剧诗', '剧诗 阵容', '剧诗 月谕', '剧诗 攻略 满花'];
const seen = new Set();
const entries = [];
let seq = 0;
let fetched = 0;

for (const kw of KEYWORDS) {
  const { posts } = await search(kw, 20);
  await sleep(DELAY);
  console.log(`搜索「${kw}」：${posts.length} 条`);

  for (const p of posts) {
    if (entries.length >= MAX * 8) break;
    const id = p.post_id;
    if (!id || seen.has(id)) continue;
    seen.add(id);

    const full = await fullPost(id);
    await sleep(DELAY);
    if (!full) continue;
    fetched++;

    const text = htmlToText(full.html);
    if (text.length < 60) continue;

    let n = 0;
    for (const blk of chunk(text)) {
      if (isJunk(blk)) continue;
      const m = blk.match(/第\s*(\d{1,2})\s*幕/);
      entries.push({
        id: `mys-${++seq}`,
        act: m ? Number(m[1]) : 0,
        tags: ['米游社', kw],
        title: full.subject || '米游社攻略',
        text: blk.slice(0, 340),
        source: `米游社 post ${id}${full.uid ? `（uid ${full.uid}）` : ''}`,
      });
      n++;
      if (n >= 12) break;   // 单篇最多取 12 块，避免一篇占满
    }
    if (fetched >= MAX) break;
  }
  if (fetched >= MAX) break;
}

console.log(`\n抓取帖子 ${fetched} 篇 → 生成 ${entries.length} 条语料`);

fs.writeFileSync(OUT, JSON.stringify({
  generatedAt: new Date().toISOString(),
  source: {
    api: 'https://bbs-api.miyoushe.com/post/wapi',
    license: '米游社用户创作内容，版权归原作者；仅作本项目本地检索用，标注了 post_id 与 uid',
    postsFetched: fetched,
  },
  entries,
}, null, 2) + '\n', 'utf8');
console.log('已写 ' + OUT);
