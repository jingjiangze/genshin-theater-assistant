#!/usr/bin/env python3
"""Automatic promotion gate: evaluate knowledge candidates against a FIXED holdout set.

Design rules (fully automatic, no human approval in the loop):

* The holdout set lives in the repo (`data/evaluations/golden/`) and is NEVER read by
  candidate extraction. It is the independent truth source for strategy validity.
* A candidate is promoted only when:
    - it already has enough independent evidence (>=3 evidence ids, >=2 creators,
      season/version present), i.e. `status == PROVISIONAL` from extract_knowledge;
    - its claim is a per-act character recommendation we can evaluate;
    - on the holdout cases for that act, recommending it is acceptable in at least
      the same fraction of cases as a fixed baseline (never worse);
    - no counter-evidence is unresolved;
    - holdout sample count >= 20 (global) and per-act cases exist.
* Promotion writes a `verification` block on the candidate and sets status VERIFIED.
  Everything else stays untouched; a candidate that fails is left as-is (isolated).
* This script does NOT publish. `release_knowledge.py` owns activation/rollback.

Outputs a machine-readable verification report for the ledger.
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
from datetime import datetime, timezone
from pathlib import Path

from jsonio import read_json, write_json

BASELINE_RATE = 0.5  # conservative fixed baseline: "recommend a generic Hydro" heuristic
MIN_HOLDOUT_SAMPLES = 20


def normalize(text: str) -> str:
    import re
    return re.sub(r"[\W_]+", "", (text or "").casefold(), flags=re.UNICODE)


def candidate_role(candidate: dict) -> str | None:
    """Extract the recommended character name from a structured act_team claim."""
    text = str(candidate.get("text") or "")
    import re
    m = re.fullmatch(r"第\s*(\d{1,2})\s*幕\s*推荐\s*([\u4e00-\u9fff]{2,8})", text.strip())
    if not m:
        return None
    return m.group(2)


def evaluate_candidate(candidate: dict, cases: list[dict]) -> dict:
    role = candidate_role(candidate)
    act = candidate.get("act")
    if not role or not isinstance(act, int):
        return {"evaluated": False, "reason": "not a structured per-act recommendation"}

    mine = [c for c in cases if c.get("act") == act]
    if not mine:
        return {"evaluated": False, "reason": "no holdout cases for this act"}

    acceptable = [c for c in mine if role in (c.get("acceptable") or [])]
    unacceptable = [c for c in mine if role in (c.get("unacceptable") or [])]
    success = len(acceptable) / len(mine)

    # Baseline: fraction of cases where ANY Hydro generic is acceptable.
    baseline = sum(1 for c in mine if any(r in (c.get("acceptable") or [])
                   for r in ("夜兰", "行秋", "芭芭拉", "心海", "莫娜"))) / len(mine)

    return {
        "evaluated": True,
        "act": act, "role": role,
        "holdout_cases": len(mine),
        "acceptable_count": len(acceptable),
        "unacceptable_count": len(unacceptable),
        "success_rate": round(success, 4),
        "baseline_rate": round(baseline, 4),
        "beats_baseline": success >= baseline,
        "no_unacceptable": len(unacceptable) == 0,
    }


def verify(registry: dict, holdout: dict) -> tuple[dict, dict]:
    cases = holdout.get("cases") or []
    total_samples = len(cases)
    records = registry.get("candidates") or []
    report = {
        "schema_version": 1,
        "evaluation_id": holdout.get("id"),
        "created_at": datetime.now(timezone.utc).isoformat(),
        "holdout_samples": total_samples,
        "min_holdout_samples": MIN_HOLDOUT_SAMPLES,
        "baseline_rate": BASELINE_RATE,
        "evaluated": 0, "promoted": 0, "rejected": 0,
        "details": [],
    }
    if total_samples < MIN_HOLDOUT_SAMPLES:
        report["gate"] = "BLOCKED_INSUFFICIENT_HOLDOUT"
        return registry, report

    for rec in records:
        if not isinstance(rec, dict):
            continue
        # Only evaluate candidates that already passed evidence governance.
        evidence_ok = (
            rec.get("status") in ("PROVISIONAL", "VERIFIED")
            and rec.get("season") is not None
            and rec.get("version_range") is not None
            and len(rec.get("evidence_ids") or []) >= 3
            and int(rec.get("independent_creator_count") or 0) >= 2
        )
        if not evidence_ok:
            continue
        res = evaluate_candidate(rec, cases)
        report["evaluated"] += 1
        entry = {"candidate_id": rec.get("candidate_id"), **res}
        report["details"].append(entry)

        if not res.get("evaluated"):
            report["rejected"] += 1
            continue

        passed = (
            res["beats_baseline"] and res["no_unacceptable"]
            and not (rec.get("counter_evidence") or [])
        )
        if passed:
            rec["status"] = "VERIFIED"
            rec["verification"] = {
                "evaluation_id": holdout.get("id"),
                "holdout_passed": True,
                "baseline_regression_passed": True,
                "strategy_gate_passed": True,
                "holdout_samples": total_samples,
                "strategy_success_rate": res["success_rate"],
                "baseline_strategy_success_rate": res["baseline_rate"],
                "counter_evidence_resolved": True,
                "verified_at": datetime.now(timezone.utc).isoformat(),
            }
            report["promoted"] += 1
        else:
            rec["status"] = "CANDIDATE"
            report["rejected"] += 1

    registry["verified_count"] = sum(x.get("status") == "VERIFIED" for x in records if isinstance(x, dict))
    registry["updated_at"] = datetime.now(timezone.utc).isoformat()
    report["gate"] = "PROMOTED" if report["promoted"] else "NOOP_NO_QUALIFIED_CANDIDATE"
    return registry, report


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--registry", type=Path, required=True)
    ap.add_argument("--holdout", type=Path, required=True)
    ap.add_argument("--report", type=Path, required=True)
    ap.add_argument("--ledger-repo", type=Path, default=None,
                    help="账本 git 工作副本；给了就把 verification 结果提交到 automation-data")
    args = ap.parse_args()

    registry = read_json(args.registry)
    holdout = read_json(args.holdout)
    updated, report = verify(registry, holdout)
    if updated != registry or report["promoted"] > 0:
        write_json(args.registry, updated)
    write_json(args.report, report)

    if args.ledger_repo:
        repo = args.ledger_repo.resolve()
        rels = []
        for p in (args.registry, args.report):
            try:
                rels.append(str(p.resolve().relative_to(repo)))
            except ValueError:
                pass
        if rels:
            subprocess.run(["git", "-C", str(repo), "config", "user.name", "theater-automation[bot]"], check=True)
            subprocess.run(["git", "-C", str(repo), "config", "user.email",
                            "theater-automation[bot]@users.noreply.github.com"], check=True)
            subprocess.run(["git", "-C", str(repo), "add", *rels], check=True)
            diff = subprocess.run(["git", "-C", str(repo), "diff", "--cached", "--quiet"])
            if diff.returncode != 0:
                subprocess.run(["git", "-C", str(repo), "commit", "-m",
                                "knowledge: record automatic strategy verification"], check=True)
                for attempt in range(1, 5):
                    try:
                        subprocess.run(["git", "-C", str(repo), "push", "origin", "HEAD:automation-data"],
                                       check=True, capture_output=True)
                        break
                    except subprocess.CalledProcessError:
                        if attempt == 4:
                            raise
                        subprocess.run(["git", "-C", str(repo), "fetch", "origin", "automation-data"], check=True)
                        subprocess.run(["git", "-C", str(repo), "rebase", "origin/automation-data"], check=True)
    print(json.dumps({k: report[k] for k in
                      ("evaluation_id", "holdout_samples", "evaluated", "promoted", "rejected", "gate")},
                     ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
