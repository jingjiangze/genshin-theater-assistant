#!/usr/bin/env node
// 从已有结构化数据批量生成攻略语料，扩充 GuideIndex（23 条 → 500+）。
//
// 为什么这样做（不是"网上爬更多文案"）：
//   `act-teams.json` 已经从 107 份字幕里提取了「每幕真实编队」（54 份含明确编队），
//   这是**真实通关者留下的记录**，比二手攻略文案可信；把它转成可检索语料是零成本高收益。
//
// 生成策略（多角度表述，提升关键词检索命中）：
//   ① 幕-角色：每幕每个登场角色 × 2 种表述
//   ② 幕-组合：每幕的高频组合（前 3 名角色拼成）
//   ③ 角色属性：元素 + 职能（用于"这角色能干嘛"类提问）
//   ④ 替代关系：substitutes.json 的等价角色（用于"没有 X 用什么"）
//   ⑤ 幕内机制：act-playbook.json
//   ⑥ 保留原有手工整理的 23 条
//
// 用法：node tools/video-frames/build-corpus.mjs [--min-videos 1]
import fs from 'node:fs';
import path from 'node:path';

const args = process.argv.slice(2);
const getArg = (k, d) => { const i = args.indexOf(k); return i > 0 ? args[i + 1] : d; };
let ROOT = process.cwd();
try {
  const { execFileSync } = await import('node:child_process');
  ROOT = execFileSync('git', ['rev-parse', '--show-toplevel'], { encoding: 'utf8' }).trim();
} catch {}

const MIN_VIDEOS = Number(getArg('--min-videos', '1'));
const rd = (p) => JSON.parse(fs.readFileSync(path.join(ROOT, 'data', p), 'utf8').replace(/^\uFEFF/, ''));

const teams = rd('act-teams.json');
const chars = rd('characters.json');
const subs = fs.existsSync(path.join(ROOT, 'data', 'substitutes.json')) ? rd('substitutes.json') : {};
const playbook = fs.existsSync(path.join(ROOT, 'data', 'act-playbook.json')) ? rd('act-playbook.json') : {};
const oldCorpus = fs.existsSync(path.join(ROOT, 'data', 'guide-corpus.json')) ? rd('guide-corpus.json') : { entries: [], sources: {} };

const zhNames = chars.zhNames || {};
const reverse = {};   // 中文名 → 英文 key
for (const [k, v] of Object.entries(zhNames)) reverse[v] = k;

const out = [];
const seen = new Set();
let seq = 0;

function add(e) {
  const key = (e.title || '') + '|' + (e.text || '').slice(0, 60);
  if (seen.has(key)) return;
  seen.add(key);
  out.push({ id: e.id || `gen-${++seq}`, act: e.act ?? 0, tags: e.tags || [], title: e.title, text: e.text, source: e.source || 'generated' });
}

// ① 幕-角色（每幕每个角色 × 2 表述）
for (const a of teams.acts || []) {
  const act = a.act;
  for (const r of (a.top || [])) {
    if (r.videos < MIN_VIDEOS) continue;
    add({
      act,
      tags: ['编队', '角色', `第${act}幕`],
      title: `第${act}幕 使用 ${r.name}`,
      text: `第${act}幕共 ${a.distinct} 个不同角色被提及，其中 ${r.name} 出场 ${r.videos} 个视频（占比 ${(r.share * 100).toFixed(0)}%）。若你的池子里有 ${r.name}，第${act}幕优先考虑。`,
      source: `act-teams（${teams.videosWithAct} 份字幕统计）`,
    });
    add({
      act,
      tags: ['角色适配', r.name],
      title: `${r.name} 适合第${act}幕吗`,
      text: `据 ${teams.videosWithAct} 份实战字幕统计，${r.name} 在第${act}幕出场 ${r.videos} 次。该幕共 ${a.distinct} 个角色被使用，可替代选择较多。`,
      source: 'act-teams',
    });
  }
}

// ② 幕-组合（每幕前 3 名拼成一条"主流搭配"）
for (const a of teams.acts || []) {
  const top = (a.top || []).filter(r => r.videos >= MIN_VIDEOS).slice(0, 3);
  if (top.length < 2) continue;
  add({
    act: a.act,
    tags: ['搭配', '组合'],
    title: `第${a.act}幕 主流搭配`,
    text: `第${a.act}幕被提及最多的角色依次是：${top.map(r => `${r.name}（${r.videos} 次）`).join('、')}。可据此组队，或从各角色下位替代里挑选。`,
    source: 'act-teams',
  });
}

// ③ 角色属性（元素 + 职能）
// 注意：characters.json 的元素在 chars.characters（{EnglishName: Element}），不在顶层；
// 职能在 substitutes.characters（{中文名: {element, roles[], tier, note}}）。
const elemMap = chars.characters || {};
const roleInfo = subs.characters || {};
for (const [key, element] of Object.entries(elemMap)) {
  const cn = zhNames[key];
  if (!cn || typeof element !== 'string') continue;
  const info = roleInfo[cn] || {};
  const roles = Array.isArray(info.roles) ? info.roles.join('/') : '';
  const tier = info.tier ? `，评级 ${info.tier}` : '';
  const note = info.note ? `（${info.note}）` : '';
  add({
    act: 0,
    tags: ['角色', '元素', cn, element],
    title: `${cn} 的元素与职能`,
    text: `${cn} 是 ${element} 元素角色${roles ? `，职能：${roles}` : ''}${tier}${note}。元素需求满足型的幕次可直接使用。`,
    source: 'characters',
  });
}

// ③b 职能定义（每个职能一条：它要什么）
for (const [rk, rv] of Object.entries(subs.roles || {})) {
  if (!rv || typeof rv !== 'object') continue;
  add({
    act: 0,
    tags: ['职能', rk],
    title: `${rv.label || rk} 职能要求`,
    text: `${rv.label || rk}：${rv.need || ''}。可用角色见替代关系。`,
    source: 'substitutes',
  });
}

// ④ 替代关系：substitutionRules 是 {role, prefer[], why} 的数组
for (const rule of (subs.substitutionRules || [])) {
  const list = Array.isArray(rule.prefer) ? rule.prefer : [];
  const label = (subs.roles || {})[rule.role]?.label || rule.role;
  if (list.length < 2) continue;
  add({
    act: 0,
    tags: ['替代', rule.role],
    title: `${label} 职能有哪些可选角色`,
    text: `${label}：${rule.why || ''}。可选：${list.join('、')}。缺其中某个时按顺序换下一个。`,
    source: 'substitutes',
  });
  for (const name of list.slice(0, 6)) {
    add({
      act: 0,
      tags: ['替代', rule.role, name],
      title: `没有 ${name} 时用什么（${label}）`,
      text: `${label} 职能的等价角色：${list.filter(x => x !== name).join('、')}。缺 ${name} 时可从中挑。`,
      source: 'substitutes',
    });
  }
}

// ⑤ 幕内机制
for (const [actStr, v] of Object.entries(playbook.acts || {})) {
  const act = Number(actStr);
  if (!Number.isFinite(act)) continue;
  const boss = v.boss || v.Boss || '';
  const need = Array.isArray(v.need) ? v.need.join('/') : (v.need || '');
  const what = v.what || v.text || '';
  if (!boss && !need && !what) continue;
  add({
    act,
    tags: ['机制', `第${act}幕`],
    title: `第${act}幕 首领与要点`,
    text: [boss ? `首领：${boss}` : '', need ? `元素需求：${need}` : '', what ? `要点：${what}` : '']
      .filter(Boolean).join('；'),
    source: 'act-playbook',
  });
}

// ⑤b 首领抗性（从 season29 的 encounters[].res 提取，真实数据）
const season = fs.existsSync(path.join(ROOT, "data", "season29.json")) ? rd("season29.json") : null;
if (season && season.acts) {
  const diffLabel = { Easy: "普通", Normal: "困难", Hard: "卓越", Visionary: "月谕", Lunar: "月谕+" };
  for (const [diff, actMap] of Object.entries(season.acts)) {
    if (!actMap || typeof actMap !== "object") continue;
    for (const [actStr, v] of Object.entries(actMap)) {
      const act = Number(actStr);
      if (!Number.isFinite(act) || !v || typeof v !== "object") continue;
      const enc = (v.encounters || [])[0];
      if (!enc || !enc.res) continue;
      const nm = String(enc.name || "").replace(/^Climactic Showdown · /, "");
      add({
        act,
        tags: ["抗性", "首领", `第${act}幕`, diff],
        title: `第${act}幕 首领抗性（${diffLabel[diff] || diff}）`,
        text: `${nm}：${String(enc.res).slice(0, 220)}${enc.req ? `；要求：${String(enc.req).slice(0, 120)}` : ""}`,
        source: `season29.json（${diff}）`,
      });
    }
  }
}

// ⑤c 幕-职能需求（每幕需要哪些职能，来源：act-teams 的实际出场角色 → 反查职能）
// 逻辑：某幕里出场过的角色，若带某职能，则该幕"需要"该职能——这是从实战反推的，不是猜。
const cnRoles = {};   // 中文名 → [roles]
for (const [cn, info] of Object.entries(subs.characters || {})) {
  if (info && Array.isArray(info.roles)) cnRoles[cn] = info.roles;
}
const roleLabel = {};
for (const [rk, rv] of Object.entries(subs.roles || {})) roleLabel[rk] = (rv && rv.label) || rk;

for (const a of teams.acts || []) {
  const needCount = {};   // role → 出现次数
  for (const r of (a.top || [])) {
    for (const role of (cnRoles[r.name] || [])) {
      needCount[role] = (needCount[role] || 0) + r.videos;
    }
  }
  for (const [role, cnt] of Object.entries(needCount).sort((x, y) => y[1] - x[1]).slice(0, 3)) {
    if (cnt < 2) continue;
    const prefer = (subs.substitutionRules || []).find(x => x.role === role)?.prefer || [];
    add({
      act: a.act,
      tags: ["职能", role, `第${a.act}幕`],
      title: `第${a.act}幕 需要 ${roleLabel[role] || role}`,
      text: `第${a.act}幕出场角色中，${roleLabel[role] || role} 职能累计出现 ${cnt} 次，说明这一幕对它有实际需求。可选：${prefer.slice(0, 6).join("、") || "见替代关系"}。`,
      source: "act-teams × substitutes（实战反推）",
    });
  }
}

// ⑤d 抗性弱点解读（从 res 文本里抽 Weak / 高抗元素）
for (const [diff, actMap] of Object.entries((season && season.acts) || {})) {
  if (!actMap || typeof actMap !== "object") continue;
  for (const [actStr, v] of Object.entries(actMap)) {
    const act = Number(actStr);
    const enc = v && (v.encounters || [])[0];
    if (!enc || !enc.res) continue;
    const res = String(enc.res);
    const weak = (res.match(/Weak:\s*([^.;]+)/i) || [])[1];
    const high = [...res.matchAll(/([A-Z][a-z]+)\s+(\d{2,3})%/g)]
      .filter(m => Number(m[2]) >= 50).map(m => `${m[1]} ${m[2]}%`);
    if (!weak && high.length === 0) continue;
    add({
      act,
      tags: ["抗性", "弱点", `第${act}幕`],
      title: `第${act}幕 该打什么元素（${diff}）`,
      text: [weak ? `弱点：${weak.trim()}` : "", high.length ? `高抗（≥50%）：${high.join("、")}` : ""]
        .filter(Boolean).join("；") + "。",
      source: `season29.json（${diff}）`,
    });
  }
}

// ⑤e 角色在某幕的定位（把职能并入幕次语境，比前两种表述多出"能干嘛"）
for (const a of teams.acts || []) {
  for (const r of (a.top || []).slice(0, 6)) {
    const roles = cnRoles[r.name] || [];
    if (roles.length === 0) continue;
    const labels = roles.map(x => roleLabel[x] || x);
    add({
      act: a.act,
      tags: ["定位", r.name, `第${a.act}幕`].concat(roles),
      title: `${r.name} 在第${a.act}幕的定位`,
      text: `${r.name} 在第${a.act}幕出场 ${r.videos} 次，职能为 ${labels.join("/")}。该幕共有 ${a.distinct} 个角色被使用，若缺 ${r.name}，可从同职能替代里换。`,
      source: "act-teams × substitutes",
    });
  }
}

// ⑤f 首领幕标记（来源：season29 的 acts[].boss 布尔值）
{
  const lunar = (season && season.acts && (season.acts.Lunar || season.acts.Hard)) || {};
  for (const [actStr, v] of Object.entries(lunar)) {
    const act = Number(actStr);
    if (!Number.isFinite(act) || !v || typeof v !== "object") continue;
    const isBoss = v.boss === true;
    if (!isBoss) continue;
    const lv = v.level || 0;
    add({
      act,
      tags: ["首领幕", "节奏", `第${act}幕`],
      title: `第${act}幕 是首领幕`,
      text: `第${act}幕为首领幕（角色等级 ${lv || "?"}）。首领幕抗性与机制都会上一个台阶，应保留耐力未耗尽的强力角色；前序普通幕尽量用替补角色过渡。`,
      source: "season29.json（boss 标记）",
    });
  }
}

// ⑤g 难度概览（来源：season29 各难度的幕数与等级范围）
for (const [diff, actMap] of Object.entries((season && season.acts) || {})) {
  if (!actMap || typeof actMap !== "object") continue;
  const acts = Object.keys(actMap).map(Number).filter(Number.isFinite).sort((a, b) => a - b);
  if (acts.length === 0) continue;
  const levels = acts.map(a => actMap[String(a)]?.level).filter(x => typeof x === "number");
  const minLv = levels.length ? Math.min(...levels) : 0;
  const maxLv = levels.length ? Math.max(...levels) : 0;
  const bosses = acts.filter(a => actMap[String(a)]?.boss === true);
  add({
    act: 0,
    tags: ["难度", diff],
    title: `${diff} 难度概览`,
    text: `${diff} 共 ${acts.length} 幕，角色等级 ${minLv}–${maxLv}${bosses.length ? `，首领幕：第 ${bosses.join("/")} 幕` : ""}。`,
    source: "season29.json",
  });
}

// ⑥ 保留原有手工语料
for (const e of (oldCorpus.entries || [])) add(e);

// 输出（合并 sources）
const sources = { ...(oldCorpus.sources || {}) };
sources['act-teams'] = `从 ${teams.videosWithAct} 份 B 站字幕提取的每幕真实编队统计`;
sources['characters'] = '角色元素/职能表（data/characters.json）';
sources['substitutes'] = '等价角色/职能分组（data/substitutes.json）';
sources['act-playbook'] = '幕内首领与机制（data/act-playbook.json）';

const result = {
  season: oldCorpus.season || 29,
  note: `自动生成语料：${teams.videosWithAct} 份字幕 + 角色表 + 替代关系 + 幕内机制。手工条目已保留。`,
  sources,
  entries: out,
};
fs.writeFileSync(path.join(ROOT, 'data', 'guide-corpus.json'), JSON.stringify(result, null, 2) + '\n', 'utf8');

const byTag = {};
for (const e of out) for (const t of e.tags) byTag[t] = (byTag[t] || 0) + 1;
console.log(`生成完成：${out.length} 条（原 ${(oldCorpus.entries || []).length} 条已并入）`);
console.log(`按标签 Top8：${Object.entries(byTag).sort((a, b) => b[1] - a[1]).slice(0, 8).map(([k, v]) => `${k}=${v}`).join(' ')}`);
