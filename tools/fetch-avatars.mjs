// 拉角色头像素材（给「头像模板认角色池」用）。
// 名字→图标：gi.yatta.moe 的 /avatar 索引（中文名 → UI_AvatarIcon_xxx）；
// 图标本体：enka.network 的 /ui/<icon>.png（公开 CDN，128×128 左右）。
//
//   node tools/fetch-avatars.mjs                 # 全部（约 138 个）
//   node tools/fetch-avatars.mjs 夜兰 凯亚 芭芭拉  # 只拉指定中文名
//   node tools/fetch-avatars.mjs --out assets/avatars
import fs from 'node:fs';
import path from 'node:path';

const args = process.argv.slice(2);
const outArgIdx = args.indexOf('--out');
const outDir = outArgIdx >= 0 ? args[outArgIdx + 1] : 'assets/avatars';
const only = args.filter((a, i) => !a.startsWith('--') && i !== outArgIdx + 1);
fs.mkdirSync(outDir, { recursive: true });

const idxRes = await fetch('https://gi.yatta.moe/api/v2/chs/avatar', { headers: { 'User-Agent': 'Mozilla/5.0' } });
if (!idxRes.ok) { console.error('取头像索引失败', idxRes.status); process.exit(2); }
const idx = await idxRes.json();
const items = Object.entries(idx.data.items || {}).map(([id, v]) => ({ id, name: v.name, icon: v.icon, rank: v.rank }));
console.log('索引角色数:', items.length);

const want = only.length ? items.filter(it => only.includes(it.name)) : items;
if (only.length) console.log('指定拉取:', want.map(w => w.name).join(', '));

const manifest = [];
let ok = 0, fail = 0;
const CONC = 8;
const queue = [...want];

async function worker() {
  while (queue.length) {
    const it = queue.shift();
    const file = path.join(outDir, `${it.icon}.png`);           // 用内部图标名做文件名，避免中文路径问题
    if (fs.existsSync(file) && fs.statSync(file).size > 1000) {
      manifest.push({ name: it.name, icon: it.icon, file: path.basename(file) });
      ok++; continue;
    }
    try {
      const r = await fetch(`https://enka.network/ui/${it.icon}.png`, { headers: { 'User-Agent': 'Mozilla/5.0' } });
      if (!r.ok) throw new Error('HTTP ' + r.status);
      const buf = Buffer.from(await r.arrayBuffer());
      if (buf.length < 1000) throw new Error('too small ' + buf.length);
      fs.writeFileSync(file, buf);
      manifest.push({ name: it.name, icon: it.icon, file: path.basename(file) });
      ok++;
    } catch (e) {
      fail++;
      console.log('  跳过', it.name, it.icon, String(e.message || e).slice(0, 60));
    }
  }
}
await Promise.all(Array.from({ length: CONC }, worker));

manifest.sort((a, b) => a.name.localeCompare(b.name, 'zh'));
fs.writeFileSync(path.join(outDir, 'index.json'), JSON.stringify({ source: 'gi.yatta.moe + enka.network/ui', count: manifest.length, avatars: manifest }, null, 1), 'utf8');
console.log(`成功 ${ok} / 失败 ${fail} → ${outDir}（index.json 已写）`);
