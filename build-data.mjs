import fs from "node:fs";
const repo = process.argv[2], out = process.argv[3];
const ad = JSON.parse(fs.readFileSync(repo + "/src/data/theater-act-details.json", "utf8")).seasons["29"];
const lu = JSON.parse(fs.readFileSync(repo + "/src/data/theater-lineups.json", "utf8")).current;
const acts = {};
for (const d of ["Easy", "Normal", "Hard", "Visionary", "Lunar"]) {
  acts[d] = {};
  for (const [n, st] of Object.entries(ad[d])) {
    acts[d][n] = {
      level: st.level ?? null,
      boss: [3,6,8,10].includes(Number(n)),
      lunar: Number(n) >= 11,
      encounters: (st.encounters ?? []).map(e => ({
        name: e.name,
        enemies: e.enemies || "",
        res: e.resistances || "",
        req: (e.requirements || "").replace(/\n/g, " | "),
        mech: (e.mechanics || "").replace(/\n/g, " "),
        rec: (e.recommendedElements || []).join("/"),
        key: (e.keyRecommendations || []).map(k => (k.avoid ? "avoid:" : "") + k.label).join(";"),
        chars: (e.notableCharacters || []).map(c => c.name).join(",")
      }))
    };
  }
}
const meta = {
  generatedAt: new Date().toISOString(),
  season: lu.season, elements: lu.elements, startsAt: lu.startsAt,
  opening: lu.openingCharacters, guests: lu.specialGuests,
  bossStages: lu.bossStages,
  acts
};
fs.writeFileSync(out, JSON.stringify(meta, null, 1), "utf8");
console.log("wrote", out, fs.statSync(out).size, "bytes");

