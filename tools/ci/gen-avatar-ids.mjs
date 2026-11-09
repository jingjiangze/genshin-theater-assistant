#!/usr/bin/env node
// 生成 avatarId → 角色英文名 的映射表（data/avatar-ids.json）。
//
// 为什么需要：Enka 的 avatarInfoList 只给数字 avatarId，而我们内部用 GOOD 的英文 key
// （characters.json 的键，如 "KamisatoAyaka"）。没有这张表就解析不出角色，硬写等于假成功。
//
// 数据源：Yatta 的公开角色表（社区维护，含 id 与英文名）。
// 用法：node tools/ci/gen-avatar-ids.mjs [--out ../../data/avatar-ids.json]
import fs from 'node:fs';
import path from 'node:path';

const args = process.argv.slice(2);
const getArg = (k, d) => { const i = args.indexOf(k); return i > 0 ? args[i + 1] : d; };
let REPO_ROOT = process.cwd();
try {
  const { execFileSync } = await import('node:child_process');
  REPO_ROOT = execFileSync('git', ['rev-parse', '--show-toplevel'], { encoding: 'utf8' }).trim();
} catch {}

const OUT = path.resolve(REPO_ROOT, getArg('--out', 'data/avatar-ids.json'));
const SRC = 'https://gi.yatta.moe/api/v2/en/avatar';

async function main() {
  console.log('拉取 ' + SRC);
  const res = await fetch(SRC, { headers: { 'User-Agent': 'theater-assistant/gen-avatar-ids' } });
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  const j = await res.json();
  const items = j?.data?.items || {};
  const ids = {};
  let count = 0;
  for (const [k, v] of Object.entries(items)) {
    // 旅行者有 -geo/-electro 等变体，跳过带后缀的（保留基础形态）
    if (!/^\d+$/.test(String(v?.id ?? k))) continue;
    // 保留原始名（含空格）——characters.json 的 zhNames 键是 "Hu Tao" 这种形态，
    // 去空格会匹配不上（实测踩过：HuTao/YaeMiko 显示英文名）。
    const name = String(v?.name || '').trim();
    if (!name) continue;
    ids[String(v.id ?? k)] = name;
    count++;
  }
  if (count === 0) throw new Error('未解析到任何角色，数据源结构可能已变');

  const out = {
    generatedAt: new Date().toISOString(),
    source: SRC,
    _note: 'avatarId → 角色英文名（已去空格，对齐 characters.json 的键）。Enka 解析依赖此表。',
    total: count,
    ids,
  };
  fs.mkdirSync(path.dirname(OUT), { recursive: true });
  fs.writeFileSync(OUT, JSON.stringify(out, null, 2) + '\n', 'utf8');
  console.log(`已写 ${OUT}（${count} 个角色）`);
}

main().catch(e => { console.error('失败: ' + e.message); process.exit(1); });
