#!/usr/bin/env python3
"""Unit tests for the automatic promotion gate."""
import json
import tempfile
import unittest
from pathlib import Path

import verify_candidates


def holdout(n=22, acceptable="行秋", unacceptable=None):
    cases = []
    for i in range(n):
        cases.append({
            "case_id": f"c{i}", "act": 7,
            "acceptable": [acceptable], "unacceptable": unacceptable or [],
        })
    return {"id": "eval-test", "cases": cases, "split": "holdout"}


def candidate(role="行秋", status="PROVISIONAL", creators=2, evidences=3, counter=None):
    return {
        "candidate_id": "cand-" + role,
        "text": f"第 7 幕 推荐 {role}", "act": 7,
        "kind": "act_team", "season": 29,
        "version_range": {"min": "7.1", "max": "7.1"},
        "status": status,
        "evidence_ids": [f"ev{i}" for i in range(evidences)],
        "independent_creator_count": creators,
        "counter_evidence": counter or [],
    }


class VerifyCandidateTests(unittest.TestCase):
    def test_insufficient_holdout_blocks(self):
        reg = {"candidates": [candidate()], "verified_count": 0}
        updated, report = verify_candidates.verify(reg, holdout(n=5))
        self.assertEqual(report["gate"], "BLOCKED_INSUFFICIENT_HOLDOUT")
        self.assertEqual(report["promoted"], 0)
        self.assertEqual(updated["candidates"][0]["status"], "PROVISIONAL")

    def test_evidence_governance_required(self):
        reg = {"candidates": [candidate(status="CANDIDATE", creators=1, evidences=1)]}
        _, report = verify_candidates.verify(reg, holdout())
        self.assertEqual(report["evaluated"], 0)
        self.assertEqual(report["promoted"], 0)

    def test_qualified_candidate_is_verified(self):
        reg = {"candidates": [candidate()], "verified_count": 0}
        updated, report = verify_candidates.verify(reg, holdout(acceptable="行秋"))
        self.assertEqual(report["promoted"], 1)
        c = updated["candidates"][0]
        self.assertEqual(c["status"], "VERIFIED")
        self.assertTrue(c["verification"]["holdout_passed"])
        self.assertTrue(c["verification"]["strategy_gate_passed"])
        self.assertEqual(c["verification"]["holdout_samples"], 22)
        self.assertEqual(updated["verified_count"], 1)

    def test_unacceptable_role_is_not_promoted(self):
        reg = {"candidates": [candidate(role="五郎")], "verified_count": 0}
        h = holdout(); h["cases"] = [dict(c, unacceptable=["五郎"]) for c in h["cases"]]
        updated, report = verify_candidates.verify(reg, h)
        self.assertEqual(report["promoted"], 0)
        self.assertEqual(updated["candidates"][0]["status"], "CANDIDATE")

    def test_counter_evidence_blocks(self):
        reg = {"candidates": [candidate(counter=["ev-bad"])], "verified_count": 0}
        _, report = verify_candidates.verify(reg, holdout())
        self.assertEqual(report["promoted"], 0)

    def test_cli_writes_report(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            reg = {"candidates": [candidate()], "verified_count": 0}
            (root / "registry.json").write_text(json.dumps(reg), encoding="utf-8")
            (root / "holdout.json").write_text(json.dumps(holdout()), encoding="utf-8")
            rc = verify_candidates.main() if False else None
            # call main via subprocess-equivalent argv
            import sys
            old = sys.argv
            sys.argv = ["verify_candidates.py", "--registry", str(root / "registry.json"),
                        "--holdout", str(root / "holdout.json"), "--report", str(root / "report.json")]
            try:
                self.assertEqual(verify_candidates.main(), 0)
            finally:
                sys.argv = old
            self.assertTrue((root / "report.json").exists())
            self.assertEqual(json.loads((root / "registry.json").read_text())["verified_count"], 1)


if __name__ == "__main__":
    unittest.main()
