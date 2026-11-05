import fs from 'node:fs';
// 搜同类视频：用 cookie 打开 B站站内搜索（按播放量排序），抽出 BV 号 + 标题。
//   node search.mjs "幻想真境剧诗 全程" 8
// 只在页面 DOM 里取 href/标题，不调私有接口、不做签名。
import { chromium } from 'playwright-core';

const exe = process.env.BROWSER_PATH;   // 存在才用（本机 Thorium），否则用 playwright 自带 chromium
const rawCookie = process.env.BILI_COOKIE || '';
const keyword = process.argv[2] || '幻想真境剧诗 全程';
const want = parseInt(process.argv[3] || '8', 10);

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

const url = `https://search.bilibili.com/video?keyword=${encodeURIComponent(keyword)}&order=click`;
await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 60000 });
await page.waitForTimeout(4000);

const items = await page.evaluate(() => {
  const out = [];
  const seen = new Set();
  document.querySelectorAll('a[href*="/video/BV"]').forEach(a => {
    const m = /\/video\/(BV[0-9A-Za-z]+)/.exec(a.getAttribute('href') || '');
    if (!m) return;
    const bv = m[1];
    if (seen.has(bv)) return;
    seen.add(bv);
    const box = a.closest('.bili-video-card') || a.parentElement;
    let title = (a.getAttribute('title') || '').trim();
    if (!title) {
      const titEl = box?.querySelector('.bili-video-card__info--tit, h3');
      title = (titEl?.getAttribute('title') || titEl?.innerText || box?.innerText || '').trim().split('\n')[0];
    }
    out.push({ bv, title: title.slice(0, 90) });
  });
  return out;
});

console.log(JSON.stringify({ keyword, count: items.length, items: items.slice(0, want) }, null, 2));
await browser.close();
