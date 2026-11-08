import fs from "node:fs";
const src = fs.readFileSync(process.argv[2] + "/src/characters.ts", "utf8");
const block = src.slice(src.indexOf("const roster: Record<Element,string> = {"));
const map = {};
const re = /(\w+):'([^']+)'/g;
let m;
while ((m = re.exec(block.slice(0, block.indexOf("};"))))) {
  for (const name of m[2].split(",")) map[name.trim()] = m[1];
}
const out = { source: "Watchfuls/imaginarium-theater-planner src/characters.ts (starter roster)", generatedAt: new Date().toISOString(), characters: map };
fs.writeFileSync(process.argv[3], JSON.stringify(out, null, 1), "utf8");
console.log("characters:", Object.keys(map).length, "->", process.argv[3]);
console.log("sample:", ["Yelan","Zhongli","Fischl","Kachina","Aino","Flins","Qiqi","Odette","Vesna","Jahoda"].map(n => n + "=" + (map[n] ?? "?")).join(", "));
