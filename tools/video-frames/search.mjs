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

const all = [];
const seen = new Set();
let emptyPages = 0;

for (let p = 1; p <= maxPages && all.length < want; p++) {
  const url = `https://search.bilibili.com/video?keyword=${encodeURIComponent(keyword)}&order=click&page=${p}`;
  try {
    await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 60000 });
    await page.waitForTimeout(3500);
  } catch (e) {
    console.error(`page ${p} goto 失败: ${e.message}`);
    break;
  }

  const items = await page.evaluate(() => {
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
      out.push({ bv: m[1], title: title.slice(0, 90) });
    });
    return out;
  });

  let added = 0;
  for (const it of items) {
    if (seen.has(it.bv)) continue;
    seen.add(it.bv);
    all.push(it);
    added++;
  }
  console.error(`page ${p}: 抓到 ${items.length} 条，新增 ${added}，累计 ${all.length}/${want}`);
  if (added === 0) { emptyPages++; if (emptyPages >= 2) break; } else emptyPages = 0;
}

console.log(JSON.stringify({ keyword, pages: maxPages, count: all.length, items: all.slice(0, want) }, null, 2));
await browser.close();
