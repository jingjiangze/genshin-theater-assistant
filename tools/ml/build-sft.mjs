// 剧诗专用微调数据集构建器（离线、纯 Node，无依赖）。
//
// 目的：把 data/ 里已有的"真实结论"（视频字幕统计出的每幕编队、社区共识、播放手册、
// 攻略语料）转成指令微调样本，给 ◇模型层训一个只会说"剧诗怎么打"的小模型。
//
// 三类任务（都只用已有数据，不臆造答案）：
//   T1 决策/现在做  输入：幕 + 难度 + 首领标记 → 输出：可照做的一句 + 依据
//   T2 配队         输入：幕 + 难度 + 可用元素 → 输出：4 人（按视频出场统计）+ 缺谁补谁
//   T3 攻略问答     输入：语料标题（或幕+主题） → 输出：条目正文 + 出处
//
// 产出：
//   data/ml/theater-sft-train.jsonl / -val.jsonl   （messages 格式，Qwen/LLaMA 通用）
//   data/ml/manifest.json                          （计数、抽样校验、源文件哈希）
//
// 用法：node tools/ml/build-sft.mjs [--out data/ml] [--val 0.1] [--seed 20261110]

import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';

const args = process.argv.slice(2);
const opt = (name, def) => {
  const i = args.indexOf(name);
  return i >= 0 && args[i + 1] ? args[i + 1] : def;
};
const OUT = opt('--out', 'data/ml');
const VAL_RATIO = Number(opt('--val', '0.1'));
const SEED = Number(opt('--seed', '20261110'));

const readJson = (p) => JSON.parse(fs.readFileSync(p, 'utf8').replace(/^\uFEFF/, ''));
const ROOT = process.cwd();
const D = (f) => path.join(ROOT, 'data', f);

// ── 确定性伪随机（同一 seed 产出同一份数据集，CI 可复现）──────────────
let seedState = SEED >>> 0;
const rnd = () => {
  seedState = (seedState * 1664525 + 1013904223) >>> 0;
  return seedState / 0x100000000;
};
const pick = (arr) => arr[Math.floor(rnd() * arr.length)];

const SYSTEM = '你是原神「幻想真境剧诗」的随身助手。只根据给定的幕次与难度，给出一句可照做的中文指令；'
  + '不确定就说「不确定」，不要编造角色或机制。';

const samples = [];
const add = (task, user, assistant, meta = {}) => {
  if (!user || !assistant) return;
  samples.push({
    task,
    messages: [
      { role: 'system', content: SYSTEM },
      { role: 'user', content: user },
      { role: 'assistant', content: assistant.trim() },
    ],
    meta,
  });
};

const zhDifficulty = { Easy: '轻简', Normal: '普通', Hard: '困难', Visionary: '卓越', Lunar: '月谕' };

// ── T1/T2 数据源 ──────────────────────────────────────────────────
const playbook = readJson(D('act-playbook.json'));
const teams = readJson(D('act-teams.json'));
const rotations = readJson(D('rotations.json'));
const season = readJson(D('season29.json'));

const acts = [];
for (const k of Object.keys(playbook.acts || {})) {
  const n = Number(k);
  if (Number.isFinite(n) && n >= 1 && n <= 12) acts.push(n);
}
acts.sort((a, b) => a - b);

const diffKeys = Object.keys(season.acts || {});           // Easy…Lunar
const topTeam = (act) => {
  const t = teams.acts?.[act] ?? teams.acts?.[String(act)];
  if (!t || !Array.isArray(t.top)) return [];
  return t.top.filter((x) => x.share >= 0.02).slice(0, 4).map((x) => `${x.name}（${Math.round(x.share * 100)}%）`);
};

const rotationFor = (act) => {
  const r = rotations.acts?.[act] ?? rotations.acts?.[String(act)];
  if (!r || !Array.isArray(r.steps)) return [];
  return r.steps.slice(0, 3);
};

// T1：现在做（每个 幕×难度 一条 × 几种问法；难度只影响难度名，不臆造新机制）
const T1_PHRASINGS = [
  (act, d, boss, lv) => `当前：第${act}幕（${d}·${boss}）${lv}。给我一句现在该做的事。`,
  (act, d, boss, lv) => `第${act}幕 ${d} ${lv}。现在做什么？`,
  (act, d, boss, lv) => `我在剧诗第${act}幕，难度${d}${boss === '首领关' ? '，是首领关' : ''}${lv}。一句话概括该干嘛。`,
  (act, d, boss, lv) => `${d}·第${act}幕${lv}｜下一步？`,
];
let t1 = 0;
for (const act of acts) {
  const pb = playbook.acts[act] ?? playbook.acts[String(act)];
  if (!pb) continue;
  const rotations1 = rotationFor(act);
  for (const dk of diffKeys) {
    const enemy = season.acts?.[dk]?.[act] ?? season.acts?.[dk]?.[String(act)];
    const boss = enemy?.boss ? '首领关' : '普通关';
    const lv = enemy?.level ? `，敌人等级 ${enemy.level}` : '';
    const lines = [];
    if (pb.next_action) lines.push(pb.next_action.replace(/^现在做：/, '现在做：'));
    if (rotations1.length) {
      const s = rotations1[0];
      if (s?.do) lines.push(`依据：${s.do}${s.why ? `（${s.why}）` : ''}`);
    }
    if (!lines.length) continue;

    const answer = lines.join('\n');
    for (const phrasing of T1_PHRASINGS) {
      add('t1_now', phrasing(act, zhDifficulty[dk] ?? dk, boss, lv), answer,
        { act, difficulty: dk, boss: !!enemy?.boss });
      t1++;
    }
  }
}

// T2：配队（按视频统计的真实出场率，不排序臆断；问法同样多样化）
const T2_PHRASINGS = [
  (act) => `第${act}幕要怎么配队？（我有全角色）`,
  (act) => `第${act}幕带谁？`,
  (act) => `剧诗第${act}幕，队伍怎么排？`,
];
let t2 = 0;
for (const act of acts) {
  const top = topTeam(act);
  if (top.length < 2) continue;
  const answer = `第${act}幕优先：${top.join('、')}。\n依据：54 份实战字幕统计的出场率，缺某位就按同职能替换。`;
  for (const phrasing of T2_PHRASINGS) {
    add('t2_team', phrasing(act), answer, { act, count: top.length });
    t2++;
  }
}

// T3：攻略问答（语料条目直接当答案，问题用其标题构造）
// 语料有 786 条，全量会把数据集压成"问答模型"；按确定性抽样截到 240 条，
// 让"决策/配队"两类指令占到四成以上（模型的职责是补位决策层，不是背百科）。
const T3_CAP = 240;
const corpus = readJson(D('guide-corpus.json'));
const t3pool = [];
for (const e of corpus.entries || []) {
  const title = (e.title || '').trim();
  const text = (e.text || '').trim();
  if (title.length < 4 || text.length < 20) continue;
  if (text.length > 600) continue;                     // 过长条目切掉，避免训练序列爆
  const user = title.endsWith('？') || title.endsWith('?') ? title : `${title}？`;
  const answer = `${text}\n（出处：${e.source || '本项目整理'}）`;
  t3pool.push({ user, answer, act: e.act ?? null, tags: e.tags || [] });
}

for (let i = t3pool.length - 1; i > 0; i--) {
  const j = Math.floor(rnd() * (i + 1));
  [t3pool[i], t3pool[j]] = [t3pool[j], t3pool[i]];
}

let t3 = 0;
for (const item of t3pool.slice(0, T3_CAP)) {
  add('t3_qa', item.user, item.answer, { act: item.act, tags: item.tags });
  t3++;
}

// ── 切分（确定性洗牌）────────────────────────────────────────────
for (let i = samples.length - 1; i > 0; i--) {
  const j = Math.floor(rnd() * (i + 1));
  [samples[i], samples[j]] = [samples[j], samples[i]];
}
const valCount = Math.max(1, Math.round(samples.length * VAL_RATIO));
const val = samples.slice(0, valCount);
const train = samples.slice(valCount);

// ── 校验（结构 + 去重 + 长度）────────────────────────────────────
const problems = [];
const seen = new Set();
for (const s of samples) {
  const key = s.messages[1].content + '||' + s.messages[2].content;
  if (seen.has(key)) problems.push('重复样本：' + key.slice(0, 60));
  seen.add(key);
  for (const m of s.messages) {
    if (!m.content || !m.content.trim()) problems.push('空内容：' + s.task);
    if (m.content.length > 2000) problems.push('超长内容：' + s.task + ' ' + m.content.length);
  }
  if (s.messages.length !== 3) problems.push('消息数不是 3：' + s.task);
}
if (!train.length) problems.push('训练集为空');

// ── 落盘 ─────────────────────────────────────────────────────────
fs.mkdirSync(path.join(ROOT, OUT), { recursive: true });
const writeJsonl = (file, rows) => {
  fs.writeFileSync(path.join(ROOT, OUT, file), rows.map((r) => JSON.stringify(r)).join('\n') + '\n', 'utf8');
};
writeJsonl('theater-sft-train.jsonl', train);
writeJsonl('theater-sft-val.jsonl', val);

const sha = (f) => crypto.createHash('sha256').update(fs.readFileSync(D(f))).digest('hex').slice(0, 16);
const manifest = {
  builtAt: new Date().toISOString(),
  seed: SEED,
  valRatio: VAL_RATIO,
  total: samples.length,
  train: train.length,
  val: val.length,
  byTask: {
    t1_now: t1,
    t2_team: t2,
    t3_qa: t3,
  },
  sources: {
    'act-playbook.json': sha('act-playbook.json'),
    'act-teams.json': sha('act-teams.json'),
    'rotations.json': sha('rotations.json'),
    'season29.json': sha('season29.json'),
    'guide-corpus.json': sha('guide-corpus.json'),
  },
  problems: problems.slice(0, 20),
};
fs.writeFileSync(path.join(ROOT, OUT, 'manifest.json'), JSON.stringify(manifest, null, 2) + '\n', 'utf8');

console.log(`样本 ${samples.length}（T1 ${t1} / T2 ${t2} / T3 ${t3}）→ train ${train.length} / val ${val.length}`);
if (problems.length) {
  console.log(`校验问题 ${problems.length} 条（前 5）：`);
  for (const p of problems.slice(0, 5)) console.log('  - ' + p);
  process.exitCode = 1;
} else {
  console.log('校验通过：无空内容/无重复/长度在上限内');
}
