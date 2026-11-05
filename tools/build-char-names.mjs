// 给 data/characters.json 补中文名（zhNames）：英文键 → 简体中文名。
// 数据源：theBowja/genshin-db（MIT），src/data/ChineseSimplified/characters/<slug>.json 的 .name 字段。
//
//   node tools/build-char-names.mjs            # 读 data/characters.json，补/刷新 zhNames
//   node tools/build-char-names.mjs --check    # 只检查覆盖率，不写盘
//
// 为什么要这一步：游戏 UI 与 OCR 都是中文，而 characters.json 的键是英文名（来自英文攻略项目），
// 不做映射就会出现"角色池键是中文、元素表键是英文"从而排序/资格计算全部空转。
import fs from 'node:fs';

const file = new URL('../data/characters.json', import.meta.url);
const doc = JSON.parse(fs.readFileSync(file, 'utf8'));
const eng = Object.keys(doc.characters || {});
if (eng.length === 0) { console.error('characters 为空'); process.exit(2); }

const checkOnly = process.argv.includes('--check');
const RAW = 'https://raw.githubusercontent.com/theBowja/genshin-db/main/src/data/ChineseSimplified/characters/';

const slugOf = (name) => name.toLowerCase().replace(/[^a-z0-9]/g, '');
// 别名：characters.json 用的是短名，genshin-db 用全名 slug
const overrides = {
  ayato: ['kamisatoayato'],
  kokomi: ['sangonomiyakokomi'],
  sara: ['kujousara'],
  ayaka: ['kamisatoayaka'],
  heizou: ['shikanoinheizou'],
  kazuha: ['kaedeharakazuha'],
  itto: ['aratakiitto'],
  mizuki: ['yumemizukimizuki'],
};
// 旅行者没有独立文件，游戏内统一显示「旅行者」
const manual = {
  'Traveler': '旅行者',
  'Pyro Traveler': '旅行者', 'Hydro Traveler': '旅行者', 'Electro Traveler': '旅行者',
  'Cryo Traveler': '旅行者', 'Dendro Traveler': '旅行者', 'Anemo Traveler': '旅行者', 'Geo Traveler': '旅行者',
};

async function fetchName(slug) {
  const res = await fetch(RAW + slug + '.json');
  if (!res.ok) return null;
  const j = await res.json();
  return typeof j.name === 'string' ? j.name : null;
}

const out = {};
const missing = [];
let i = 0;
const queue = [...eng];
const CONCURRENCY = 8;

async function worker() {
  while (queue.length) {
    const name = queue.shift();
    if (manual[name]) { out[name] = manual[name]; if (++i % 25 === 0) console.log(`  ${i}/${eng.length}`); continue; }
    const slugs = [slugOf(name), ...(overrides[slugOf(name)] || [])];
    let zh = null;
    for (const s of slugs) {
      if (!s) continue;
      zh = await fetchName(s);
      if (zh) break;
    }
    if (zh) out[name] = zh;
    else missing.push(name);
    if (++i % 25 === 0) console.log(`  ${i}/${eng.length}`);
  }
}
await Promise.all(Array.from({ length: CONCURRENCY }, worker));

console.log(`英文键 ${eng.length}，拿到中文名 ${Object.keys(out).length}，缺 ${missing.length}`);
if (missing.length) console.log('缺:', missing.join(', '));

// 冲突检查：两个英文名映射到同一中文名
const dup = {};
for (const [k, v] of Object.entries(out)) (dup[v] ||= []).push(k);
const dupList = Object.entries(dup).filter(([, v]) => v.length > 1);
if (dupList.length) console.log('中文名重复:', dupList.map(([k, v]) => `${k}<=${v.join('/')}`).join('; '));

if (checkOnly) {
  console.log('--check：不写盘');
  process.exit(0);
}

doc.zhNames = out;
doc.zhNamesSource = 'theBowja/genshin-db (MIT), src/data/ChineseSimplified/characters/*.json .name';
doc.zhNamesGeneratedAt = new Date().toISOString();
fs.writeFileSync(file, JSON.stringify(doc, null, 1), 'utf8');
console.log('已写:', file.pathname);
