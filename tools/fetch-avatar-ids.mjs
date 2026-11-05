// 生成 data/characters-id.json：角色数字 ID → 中文名 / 图标名。
// 数据源：gi.yatta.moe 的 /v2/chs/avatar（公开静态元数据，**不是账号数据**）。
// 用途：本地游戏日志里通常只有 avatarId（数字），靠这张表才能还原成角色名写入 roster.json。
//
//   node tools/fetch-avatar-ids.mjs
import fs from 'node:fs';

const url = 'https://gi.yatta.moe/api/v2/chs/avatar';
const res = await fetch(url, { headers: { 'User-Agent': 'Mozilla/5.0' } });
if (!res.ok) { console.error('拉取失败', res.status); process.exit(2); }
const j = await res.json();
const items = j?.data?.items || {};
const out = {};
for (const [id, v] of Object.entries(items)) {
  if (!v?.name) continue;
  out[id] = { name: v.name, icon: v.icon || '', rank: v.rank ?? 0 };
}
const payload = {
  note: '角色 ID → 中文名（静态元数据，来自 gi.yatta.moe /v2/chs/avatar）。与账号数据无关，可公开分发。',
  generatedAt: new Date().toISOString(),
  count: Object.keys(out).length,
  characters: out,
};
fs.writeFileSync('data/characters-id.json', JSON.stringify(payload, null, 1), 'utf8');
console.log(`写出 ${payload.count} 条 → data/characters-id.json`);
const sample = ['10000002', '10000046', '10000060'].map(id => `${id}=${out[id]?.name ?? '?'}`);
console.log('样例:', sample.join(', '));
