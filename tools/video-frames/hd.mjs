// 用已登录 cookie 抓 B站视频的**高清原生帧**（1080p/更高），供 `--batch` 派生模板。
// 用法（cookie 只走环境变量，不落盘、不进仓库）：
//   $env:BILI_COOKIE = 'SESSDATA=...; bili_jct=...; ...'
//   node hd.mjs BV193xp6WErn 20 60 200 out=./hd
//
// 思路：页面内同源 fetch 调 playurl（自动带 cookie）→ 拿到 dash 的 AVC 流地址
// → 自建 <video> 播这个地址（referer 由页面提供）→ 逐时间点 canvas 抓原生分辨率帧。
import { chromium } from 'playwright-core';
import fs from 'node:fs';

const exe = process.env.BROWSER_PATH;   // 存在才用（本机 Thorium），否则用 playwright 自带 chromium
const rawCookie = process.env.BILI_COOKIE || '';
if (!rawCookie.trim()) {
  console.error('缺少 BILI_COOKIE 环境变量');
  process.exit(2);
}

const cookies = rawCookie
  .split(';')
  .map(s => s.trim())
  .filter(Boolean)
  .map(kv => {
    const i = kv.indexOf('=');
    return { name: kv.slice(0, i), value: kv.slice(i + 1), domain: '.bilibili.com', path: '/' };
  })
  .filter(c => c.name && c.value !== undefined);

const bvid = process.argv[2];
const args = process.argv.slice(3);
const outArg = args.find(a => a.startsWith('out='));
const spreadArg = args.find(a => a.startsWith('spread='));
const everyArg = args.find(a => a.startsWith('every='));
const rangeArg = args.find(a => a.startsWith('range='));
const maxArg = args.find(a => a.startsWith('max='));
const outDir = outArg ? outArg.slice(4) : './hd';
let stamps = args.filter(a => !a.startsWith('out=') && !a.startsWith('spread=') && !a.startsWith('every=') && !a.startsWith('range=') && !a.startsWith('max=')).map(Number).filter(n => Number.isFinite(n));
const spread = spreadArg ? parseInt(spreadArg.slice(7), 10) : 0;
fs.mkdirSync(outDir, { recursive: true });

const launchOpts = {
  headless: true,
  args: ['--disable-gpu', '--mute-audio', '--autoplay-policy=no-user-gesture-required', '--lang=zh-CN'],
};
if (exe && fs.existsSync(exe)) launchOpts.executablePath = exe;   // runner 上没有 Thorium → 用自带 chromium
const browser = await chromium.launch(launchOpts);
const ctx = await browser.newContext({
  viewport: { width: 1920, height: 1080 },
  userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36',
  locale: 'zh-CN',
});
await ctx.addCookies(cookies);

const page = await ctx.newPage();
page.on('console', m => { if (m.type() === 'error') console.log('  [page]', String(m.text()).slice(0, 160)); });

await page.goto(`https://www.bilibili.com/video/${bvid}/`, { waitUntil: 'domcontentloaded', timeout: 60000 });
await page.waitForTimeout(3500);

// 登录态自检（不打印任何 cookie 值）
const nav = await page.evaluate(() => {
  const s = window.__INITIAL_STATE__ || {};
  return { isLogin: !!(s.loginInfo || {}).isLogin, uname: (s.loginInfo || {}).uname || '', cid: ((s.videoData || {}).cid) || 0, title: ((s.videoData || {}).title) || '' };
});
console.log('page:', JSON.stringify(nav).slice(0, 200));

const pick = await page.evaluate(async (bv) => {
  const vres = await fetch(`https://api.bilibili.com/x/web-interface/view?bvid=${bv}`, { credentials: 'include' }).then(r => r.json());
  if (vres.code !== 0) return { err: 'view code ' + vres.code };
  const cid = vres.data.cid, dur = vres.data.duration;

  const purl = await fetch(`https://api.bilibili.com/x/player/playurl?bvid=${bv}&cid=${cid}&qn=127&fnval=4048&fnver=0&fourk=1`, { credentials: 'include' }).then(r => r.json());
  if (purl.code !== 0) return { err: 'playurl code ' + purl.code + ' ' + purl.message };

  const dash = purl.data.dash || {};
  const videos = dash.video || [];
  // 优先 AVC（codecid 7，Chromium 一定能解），再按清晰度 id 降序
  const withMeta = videos.map(v => ({ id: v.id, codecid: v.codecid, w: v.width, h: v.height, url: v.baseUrl, backups: v.backupUrl || [] }));
  const avc = withMeta.filter(v => v.codecid === 7).sort((a, b) => b.id - a.id);
  const any = withMeta.slice().sort((a, b) => b.id - a.id);
  const chosen = avc[0] || any[0] || null;
  return { cid, dur, accept: purl.data.accept_description || [], all: withMeta.map(v => `${v.id}:${v.codecid}:${v.w}x${v.h}`), chosen };
}, bvid);

console.log('quality candidates:', (pick.all || []).join(' | '));
console.log('chosen:', pick.chosen ? `${pick.chosen.id} codec=${pick.chosen.codecid} ${pick.chosen.w}x${pick.chosen.h}` : '<none>');
if (pick.err || !pick.chosen) {
  console.error('取流失败:', pick.err || 'no dash stream');
  await browser.close();
  process.exit(3);
}

// 在页面里建一个隐藏 video 播 dash 流（video-only，无音轨）
const ready = await page.evaluate(async ({ url, backups, w, h }) => {
  const v = document.createElement('video');
  v.id = 'hd-src';
  v.muted = true;
  v.preload = 'auto';
  v.playsInline = true;
  v.crossOrigin = 'anonymous';           // 先试 CORS，能过就能用 canvas 取像素
  v.style.cssText = `position:fixed;left:0;top:0;width:${w}px;height:${h}px;z-index:2147483647;background:#000`;
  document.body.appendChild(v);

  const tryLoad = (u) => new Promise(res => {
    const onOk = () => { cleanup(); res(true); };
    const onErr = () => { cleanup(); res(false); };
    const cleanup = () => { v.removeEventListener('loadedmetadata', onOk); v.removeEventListener('error', onErr); };
    v.addEventListener('loadedmetadata', onOk, { once: true });
    v.addEventListener('error', onErr, { once: true });
    v.src = u;
    v.load();
    setTimeout(() => { cleanup(); res(false); }, 15000);
  });

  for (const u of [url, ...backups]) {
    if (await tryLoad(u)) return { ok: true, w: v.videoWidth, h: v.videoHeight, dur: v.duration };
  }
  return { ok: false };
}, { url: pick.chosen.url, backups: pick.chosen.backups, w: pick.chosen.w, h: pick.chosen.h });

console.log('video ready:', JSON.stringify(ready));
if (!ready.ok) {
  console.error('video 无法加载 dash 流');
  await browser.close();
  process.exit(4);
}

// 取像素有两条路：canvas（精确，但要 CORS）→ 不合格时退回整页截图（合成面，1:1 不缩放）
await page.setViewportSize({ width: ready.w, height: ready.h });
let mode = 'canvas';

async function seek(tt) {
  await page.evaluate(async (t) => {
    const v = document.querySelector('#hd-src');
    if (!v) return;
    v.pause();
    await new Promise(r => {
      let done = false;
      const fin = () => { if (done) return; done = true; v.removeEventListener('seeked', fin); setTimeout(r, 220); };
      v.addEventListener('seeked', fin, { once: true });
      v.currentTime = t;
      setTimeout(fin, 4000);
    });
  }, tt);
}

async function grabCanvas() {
  const shot = await page.evaluate(() => {
    const v = document.querySelector('#hd-src');
    if (!v) return null;
    const c = document.createElement('canvas');
    c.width = v.videoWidth; c.height = v.videoHeight;
    c.getContext('2d').drawImage(v, 0, 0);
    return { url: c.toDataURL('image/png'), w: c.width, h: c.height };
  });
  if (!shot) return null;
  return { buf: Buffer.from(shot.url.split(',')[1], 'base64'), w: shot.w, h: shot.h };
}

async function grabScreenshot() {
  const buf = await page.screenshot({ clip: { x: 0, y: 0, width: ready.w, height: ready.h } });
  return { buf, w: ready.w, h: ready.h };
}

const realDur = ready.dur || pick.dur || 0;
if (everyArg || rangeArg) {
  const every = everyArg ? Math.max(0.5, parseFloat(everyArg.slice(6))) : 0;
  const cap = maxArg ? parseInt(maxArg.slice(4), 10) : 400;
  let from = 0, to = realDur * 0.98;
  if (rangeArg) {
    const [a, b] = rangeArg.slice(6).split('-').map(Number);
    if (Number.isFinite(a)) from = a;
    if (Number.isFinite(b)) to = b;
  }
  // 关键：每帧要**铺满全片**。若按 every 秒抽会超过 cap，不能截断到前 N 帧
  // （实测：every=2 max=60 只覆盖前 120s，全是队伍配置页，真值帧 0.1%）；
  // 而是自动放大步长，让 cap 个时间点均匀落在 [from,to] 上。
  const requestedStep = every > 0 ? every : Math.max(1, (to - from) / cap);
  const step = Math.max(requestedStep, (to - from) / Math.max(1, cap - 1));
  stamps = [];
  for (let t2 = from; t2 <= to && stamps.length < cap; t2 += step) stamps.push(Math.round(t2 * 10) / 10);
  console.log(`按秒抽帧：请求 ${requestedStep}s/帧 → 实际 ${Math.round(step * 10) / 10}s/帧（铺满全片），区间 ${from}-${Math.round(to)}s，共 ${stamps.length} 帧`);
}
if (spread > 0 && !everyArg && !rangeArg) {
  // 均匀铺开：跳过片头片尾各 5%，避免抓到黑屏/片尾
  stamps = [];
  const from = Math.min(15, realDur * 0.05), to = realDur * 0.95;
  for (let i = 0; i < spread; i++) stamps.push(Math.round(from + (to - from) * (i / Math.max(1, spread - 1))));
  console.log('spread 时间点:', stamps.join(','));
}
for (const t of stamps) {
  if (realDur && t > realDur - 2) { console.log('  skip(超出时长)', t); continue; }
  await seek(t);

  let shot = null;
  if (mode === 'canvas') {
    try { shot = await grabCanvas(); }
    catch (e) {
      if (String(e).includes('Tainted') || String(e).includes('SecurityError')) {
        console.log('  canvas 被 CORS 污染，改用整页截图');
        mode = 'screenshot';
        shot = await grabScreenshot();
      } else { console.log('  err', String(e).slice(0, 120)); }
    }
  } else {
    shot = await grabScreenshot();
  }

  if (!shot) { console.log('  no frame at', t); continue; }
  const file = `${outDir}/${bvid}-${t}s-${shot.w}x${shot.h}.png`;
  fs.writeFileSync(file, shot.buf);
  console.log('  saved', file, `(${mode})`);
}

await browser.close();
