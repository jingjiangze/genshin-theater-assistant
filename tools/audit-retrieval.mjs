// 检索可行性审计：在 data/guide-corpus.json 上跑 BM25（中文字符 bigram + ASCII 词），
// 用一组真实提问检查命中排行。目的：在引入 95MB 向量模型之前，先看零依赖方案够不够用。
//   node tools/audit-retrieval.mjs
import fs from 'node:fs';

const corpusPath = new URL('../data/guide-corpus.json', import.meta.url);
const corpus = JSON.parse(fs.readFileSync(corpusPath, 'utf8'));
const docs = corpus.entries;

// 分词：ASCII 词 + 中文单字 + 中文 bigram（中文检索里 bigram 召回最稳）
function tokenize(s) {
  const text = String(s || '').toLowerCase();
  const tokens = [];
  for (const m of text.matchAll(/[a-z0-9]+/g)) tokens.push(m[0]);
  const cjk = text.replace(/[^\u4e00-\u9fff]/g, ' ');
  for (const seg of cjk.split(/\s+/).filter(Boolean)) {
    for (let i = 0; i < seg.length; i++) {
      tokens.push(seg[i]);
      if (i + 1 < seg.length) tokens.push(seg.slice(i, i + 2));
    }
  }
  return tokens;
}

const docTokens = docs.map(d => tokenize(`${d.title} ${d.text} ${(d.tags || []).join(' ')} 第${d.act}幕`));
const N = docs.length;
const df = new Map();
for (const toks of docTokens) {
  for (const t of new Set(toks)) df.set(t, (df.get(t) || 0) + 1);
}
const avgLen = docTokens.reduce((a, t) => a + t.length, 0) / N;
const k1 = 1.5, b = 0.75;

function bm25(query) {
  const q = tokenize(query);
  const qSet = [...new Set(q)];
  const scores = docTokens.map(toks => {
    const tf = new Map();
    for (const t of toks) tf.set(t, (tf.get(t) || 0) + 1);
    let s = 0;
    for (const t of qSet) {
      const f = tf.get(t) || 0;
      if (!f) continue;
      const dft = df.get(t) || 0;
      const idf = Math.log(1 + (N - dft + 0.5) / (dft + 0.5));
      s += idf * (f * (k1 + 1)) / (f + k1 * (1 - b + b * toks.length / avgLen));
    }
    return s;
  });
  return scores.map((s, i) => ({ s, i })).sort((a, b) => b.s - a.s);
}

// 结构化加权：query 里出现「第N幕」时，同幕条目加成（真实运行时我们知道当前幕次）
function withActBoost(query, ranked) {
  const m = /第\s*(\d{1,2})\s*幕/.exec(query);
  if (!m) return ranked;
  const act = parseInt(m[1], 10);
  return ranked.map(r => ({ ...r, s: r.s * (docs[r.i].act === act ? 2.2 : 1) })).sort((a, b) => b.s - a.s);
}

const cases = [
  ['第3幕 翠翎恐蕈 怎么打', 'act3-boss'],
  ['翠翎恐蕈 怒气条', 'act3-boss'],
  ['第6幕 护盾 破盾', 'act6-boss'],
  ['深黯魇语之主 钓客', 'act6-boss'],
  ['第8幕 丘丘萨满 锅', 'act8-boss'],
  ['丘丘萨满 砸锅', 'act8-boss'],
  ['第10幕 标记 奶', 'act10-boss'],
  ['月守宫 标记怎么解', 'act10-boss'],
  ['月谕模式 需要多少人', 'rule-difficulty'],
  ['月谕 编入 28 人', 'rule-difficulty'],
  ['三选一 选什么', 'rule-三选一'],
  ['耐力 用完了怎么办', 'rule-endurance'],
  ['带几个奶妈', 'prep-healer'],
  ['助演选谁', 'prep-friend'],
  ['圣牌挑战 怎么打', 'act11-12-圣牌'],
  ['首领在第几幕', 'boss-order'],
  ['本期元素限制', 'element-limit'],
];

let hit1 = 0, hit3 = 0;
const rows = [];
for (const [q, expect] of cases) {
  const ranked = withActBoost(q, bm25(q));
  const names = ranked.slice(0, 3).map(r => docs[r.i].id);
  const rank = names.indexOf(expect);
  if (rank === 0) hit1++;
  if (rank >= 0) hit3++;
  rows.push({ q, expect, top3: names.join(' > '), ok: rank === 0 ? 'HIT@1' : rank > 0 ? `HIT@${rank + 1}` : 'MISS' });
}
for (const r of rows) console.log(`${r.ok.padEnd(7)} ${r.q.padEnd(22)} → ${r.top3}`);
console.log(`\nhit@1 = ${hit1}/${cases.length}，hit@3 = ${hit3}/${cases.length}`);
console.log(`语料 ${N} 条，平均长度 ${avgLen.toFixed(1)} token`);
