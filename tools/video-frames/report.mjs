#!/usr/bin/env node
// 把优选清单渲染成 GitHub Step Summary（独立脚本，避免在 YAML 里写多行 node -e）。
//
// 为什么单独成文件：此前把这段逻辑内联在 workflow 的 `node -e "..."` 里，
// 多行双引号字符串既难排查，又反过来撑破了 YAML（workflow 名退回路径、无法触发）。
import fs from 'node:fs';

const path = process.argv[2] || 'data/pure-sources.json';
const summaryFile = process.env.GITHUB_STEP_SUMMARY;
if (!fs.existsSync(path)) { console.log('无清单文件: ' + path); process.exit(0); }

const j = JSON.parse(fs.readFileSync(path, 'utf8').replace(/^\uFEFF/, ''));
console.log(`清单共 ${j.total} 条（本轮新增 ${j.thisRun?.added ?? '?'}，更新 ${j.thisRun?.updated ?? '?'}）`);

const L = [];
L.push('### 优选纯净源清单');
L.push(`共 **${j.total}** 条；本轮新增 ${j.thisRun?.added ?? 0}（周期 ${j.period || '-'}）`);
L.push('');
L.push('| 分 | 纯净度 | 难度 | 时长 | BV | 标题 |');
L.push('|---|---|---|---:|---|---|');
for (const s of (j.sources || []).slice(0, 15)) {
  const t = (s.title || '').slice(0, 44).replace(/\|/g, '\\|');
  const min = Math.floor((s.duration || 0) / 60);
  L.push(`| ${s.score} | ${s.purity} | ${s.difficulty || '-'} | ${min}分 | ${s.bv} | ${t} |`);
}
const text = L.join('\n') + '\n';
if (summaryFile) fs.appendFileSync(summaryFile, text);
else console.log(text);
