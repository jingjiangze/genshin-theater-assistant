import json
import tempfile
import unittest
from pathlib import Path

import persist_batch
import build_external_eval
import extract_knowledge
import release_knowledge


class PersistBatchTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name) / "input"
        self.root.mkdir()

    def tearDown(self):
        self.tmp.cleanup()

    def write(self, name, value="{}"):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(value, encoding="utf-8")
        return path

    def test_ingest_manifest_hashes_only_allowlisted_files(self):
        self.write("ingest-summary.json", '{"videos":[]}')
        self.write("BV1/meta.json", '{"title":"sample"}')
        self.write("BV1/subs.txt", "字幕内容")
        self.write("BV1-ops.json", '{"ops":[]}')
        self.write(".yt-cookies.txt", "SESSDATA=must-not-persist")
        self.write("unrelated.txt", "must-not-persist")
        manifest, payload = persist_batch.build_manifest(
            self.root, "ingest", "100", "2", "abc123", "video-ingest"
        )
        self.assertEqual(manifest["status"], "INGESTED")
        self.assertEqual(manifest["file_count"], 4)
        self.assertFalse(any("cookie" in item["path"].lower() for item in manifest["files"]))
        self.assertFalse(any(item["path"] == "unrelated.txt" for item in manifest["files"]))
        for item in manifest["files"]:
            raw = next(data for _, rel, data in payload if rel == item["path"])
            self.assertEqual(item["sha256"], persist_batch.sha256(raw))

    def test_corpus_is_candidate_and_never_auto_promoted(self):
        self.write("guide-corpus-extra.json", '{"entries":[]}')
        manifest, _ = persist_batch.build_manifest(
            self.root, "corpus", "101", "1", "def456", "corpus-refresh"
        )
        self.assertEqual(manifest["status"], "CANDIDATE")
        self.assertFalse(manifest["promotion"]["eligible"])

    def test_evaluation_is_not_ground_truth(self):
        self.write("BV1-replay.json", '{"frames":[]}')
        manifest, _ = persist_batch.build_manifest(
            self.root, "evaluation", "102", "1", "ghi789", "video-eval"
        )
        self.assertEqual(manifest["status"], "EVALUATED_UNVERIFIED")
        self.assertFalse(manifest["promotion"]["eligible"])

    def test_empty_batch_fails_closed(self):
        with self.assertRaises(ValueError):
            persist_batch.build_manifest(
                self.root, "ingest", "103", "1", "sha", "video-ingest"
            )

    def test_oversized_file_fails_closed(self):
        self.write("ingest-summary.json", "x" * (persist_batch.MAX_FILE_BYTES + 1))
        with self.assertRaises(ValueError):
            persist_batch.build_manifest(
                self.root, "ingest", "104", "1", "sha", "video-ingest"
            )

    def test_external_eval_status_requires_external_label_report(self):
        self.write("external-eval.json", json.dumps({
            "externally_evaluated_videos": 1,
            "promotion_eligible": False
        }))
        manifest, _ = persist_batch.build_manifest(
            self.root, "evaluation", "105", "1", "sha", "video-eval"
        )
        self.assertEqual(manifest["status"], "EVALUATED_EXTERNAL")
        self.assertFalse(manifest["promotion"]["eligible"])

    def test_knowledge_context_is_versioned_and_seasons_do_not_merge(self):
        season, version = extract_knowledge.extract_context({
            "title": "第29期 幻想真境剧诗 7.1全程攻略"
        })
        self.assertEqual(season, 29)
        self.assertEqual(version, "7.1")
        evidence = [
            {"evidence_id": "s29", "kind": "action", "text": "先击败钓客", "act": 6,
             "season": 29, "version": "7.1", "video_id": "BV29a", "creator_id": "bilibili:A"},
            {"evidence_id": "s30", "kind": "action", "text": "先击败钓客", "act": 6,
             "season": 30, "version": "7.2", "video_id": "BV30a", "creator_id": "bilibili:A"},
        ]
        result = extract_knowledge.merge_candidates(evidence)
        self.assertEqual(len(result["candidates"]), 2)
        self.assertEqual({x["season"] for x in result["candidates"]}, {29, 30})

    def test_knowledge_requires_two_creators_and_three_evidence_for_provisional(self):
        evidence = [
            {"evidence_id": "e1", "kind": "action", "text": "先击败钓客", "act": 6,
             "season": 29, "version": "7.1", "video_id": "BV1", "creator_id": "bilibili:creator-A", "source": "BV1"},
            {"evidence_id": "e2", "kind": "action", "text": "先击败钓客", "act": 6,
             "season": 29, "version": "7.1", "video_id": "BV2", "creator_id": "bilibili:creator-A", "source": "BV2"},
            {"evidence_id": "e3", "kind": "action", "text": "先击败钓客", "act": 6,
             "season": 29, "version": "7.1", "video_id": "BV3", "creator_id": "bilibili:creator-B", "source": "BV3"},
        ]
        result = extract_knowledge.merge_candidates(evidence)
        candidate = result["candidates"][0]
        self.assertEqual(candidate["status"], "PROVISIONAL")
        self.assertEqual(candidate["independent_creator_count"], 2)
        self.assertEqual(result["verified_count"], 0)

    def test_extraction_never_downgrades_terminal_status(self):
        existing = {
            "candidates": [{
                "candidate_id": extract_knowledge.digest("|".join(("action", "6", "29", "7.1", "", extract_knowledge.normalize("先击败钓客")))), "kind": "action", "text": "先击败钓客", "act": 6,
                "evidence_ids": ["old"], "evidence": [{
                    "evidence_id": "old", "batch_id": "oldbatch", "observed_batches": ["oldbatch"],
                    "platform": "bilibili", "video_id": "BV-old", "channel": "subtitle",
                    "kind": "action", "text": "先击败钓客", "creator_id": "bilibili:creator-A"
                }], "status": "VERIFIED", "season": 29,
                "version_range": {"min": "7.1", "max": "7.1"}, "independent_creator_count": 2
            }]
        }
        evidence = [{
            "evidence_id": "new", "kind": "action", "text": "先击败钓客", "act": 6,
            "season": 29, "version": "7.1", "video_id": "BV-new", "platform": "bilibili", "channel": "subtitle", "creator_id": "bilibili:creator-C", "source": "BV-new"
        }]
        result = extract_knowledge.merge_candidates(evidence, existing)
        self.assertEqual(result["candidates"][0]["status"], "VERIFIED")
        self.assertEqual(len(result["candidates"][0]["evidence_ids"]), 2)

    def test_repeated_ingest_of_same_video_does_not_inflate_evidence_count(self):
        base = {
            "kind": "action", "text": "先击败钓客", "act": 6, "season": 29,
            "version": "7.1", "video_id": "BV-same", "platform": "bilibili",
            "channel": "subtitle", "creator_id": "bilibili:creator-A"
        }
        first = dict(base, evidence_id="stable-evidence", batch_id="run-1", observed_batches=["run-1"], source="BV-same@10s")
        repeated = dict(base, evidence_id="stable-evidence", batch_id="run-2", observed_batches=["run-2"], source="BV-same@15s")
        result = extract_knowledge.merge_candidates([first, repeated])
        candidate = result["candidates"][0]
        self.assertEqual(len(candidate["evidence_ids"]), 1)
        self.assertEqual(candidate["unique_video_count"], 1)
        self.assertEqual(candidate["evidence"][0]["observed_batches"], ["run-1", "run-2"])
        self.assertEqual(candidate["evidence"][0]["source_refs"], ["BV-same@10s", "BV-same@15s"])

    def test_registry_migration_collapses_old_duplicate_batches_and_keeps_source_refs(self):
        evidence = {
            "platform": "bilibili", "video_id": "BV-migrate", "channel": "subtitle",
            "kind": "action", "text": "先击败钓客", "creator_id": "bilibili:creator-A",
        }
        existing = {"candidates": [{
            "candidate_id": "legacy", "kind": "action", "text": "先击败钓客", "act": 6,
            "season": 29, "version_range": {"min": "7.1", "max": "7.1"},
            "status": "CANDIDATE", "evidence_ids": ["old-1", "old-2"],
            "evidence": [
                dict(evidence, evidence_id="old-1", batch_id="run-1", source="BV-migrate@10s"),
                dict(evidence, evidence_id="old-2", batch_id="run-2", source="BV-migrate@15s"),
            ]
        }]}
        result = extract_knowledge.merge_candidates([], existing)
        candidate = result["candidates"][0]
        self.assertEqual(len(candidate["evidence_ids"]), 1)
        self.assertEqual(candidate["unique_video_count"], 1)
        self.assertEqual(candidate["evidence"][0]["observed_batches"], ["run-1", "run-2"])
        self.assertEqual(candidate["evidence"][0]["source_refs"], ["BV-migrate@10s", "BV-migrate@15s"])

    def test_one_creator_many_videos_stays_candidate(self):
        evidence = [
            {"evidence_id": f"e{i}", "kind": "action", "text": "先击败钓客", "act": 6,
             "season": 29, "version": "7.1", "video_id": f"BV{i}", "creator_id": "bilibili:creator-A", "source": f"BV{i}"}
            for i in range(1, 5)
        ]
        result = extract_knowledge.merge_candidates(evidence)
        candidate = result["candidates"][0]
        self.assertEqual(candidate["status"], "CANDIDATE")
        self.assertEqual(candidate["independent_creator_count"], 1)
        self.assertEqual(result["verified_count"], 0)

    def test_release_requires_verified_strategy_and_holdout_gates(self):
        registry = self.root / "registry.json"
        releases = self.root / "releases"
        active = self.root / "active.json"
        registry.write_text(json.dumps({"candidates": [{
            "candidate_id": "candidate-1", "status": "PROVISIONAL"
        }]}), encoding="utf-8")
        active.write_text(json.dumps({"release_id": "rel-stable"}), encoding="utf-8")
        result = release_knowledge.release_or_rollback(registry, releases, active)
        self.assertEqual(result["action"], "NOOP")
        self.assertEqual(json.loads(active.read_text())["release_id"], "rel-stable")

    def test_release_promotes_only_verified_candidate(self):
        registry = self.root / "registry.json"
        releases = self.root / "releases"
        active = self.root / "active.json"
        candidate = {
            "candidate_id": "candidate-verified", "status": "VERIFIED",
            "season": 29, "version_range": {"min": "7.1", "max": "7.1"},
            "evidence_ids": ["a", "b", "c"], "independent_creator_count": 2,
            "verification": {
                "evaluation_id": "eval-29-7.1", "holdout_passed": True,
                "baseline_regression_passed": True, "strategy_gate_passed": True,
                "holdout_samples": 30, "strategy_success_rate": 0.90,
                "baseline_strategy_success_rate": 0.85,
                "counter_evidence_resolved": True
            }
        }
        registry.write_text(json.dumps({"candidates": [candidate]}), encoding="utf-8")
        active.write_text(json.dumps({"release_id": "rel-stable"}), encoding="utf-8")
        result = release_knowledge.release_or_rollback(registry, releases, active)
        pointer = json.loads(active.read_text(encoding="utf-8"))
        self.assertEqual(result["action"], "PROMOTE")
        self.assertEqual(pointer["previous_release"], "rel-stable")
        self.assertTrue((releases / pointer["release_id"] / "knowledge.json").exists())

    def test_release_rejects_insufficient_holdout_samples(self):
        registry = self.root / "registry.json"
        releases = self.root / "releases"
        active = self.root / "active.json"
        candidate = {
            "candidate_id": "candidate-small-holdout", "status": "VERIFIED",
            "season": 29, "version_range": {"min": "7.1", "max": "7.1"},
            "evidence_ids": ["a", "b", "c"], "independent_creator_count": 2,
            "verification": {
                "evaluation_id": "eval-small", "holdout_passed": True,
                "baseline_regression_passed": True, "strategy_gate_passed": True,
                "holdout_samples": 5, "strategy_success_rate": 0.95,
                "baseline_strategy_success_rate": 0.90, "counter_evidence_resolved": True
            }
        }
        registry.write_text(json.dumps({"candidates": [candidate]}), encoding="utf-8")
        result = release_knowledge.release_or_rollback(registry, releases, active)
        self.assertEqual(result["action"], "NOOP")
        self.assertFalse(active.exists())

    def test_failed_post_release_health_rolls_back_atomically(self):
        registry = self.root / "registry.json"
        releases = self.root / "releases"
        active = self.root / "active.json"
        health = self.root / "health.json"
        active.write_text(json.dumps({
            "release_id": "rel-new", "previous_release": "rel-old", "status": "ACTIVE"
        }), encoding="utf-8")
        health.write_text(json.dumps({
            "release_id": "rel-new", "passed": False, "reason": "strategy regression"
        }), encoding="utf-8")
        result = release_knowledge.release_or_rollback(registry, releases, active, health)
        pointer = json.loads(active.read_text(encoding="utf-8"))
        self.assertEqual(result["action"], "ROLLBACK")
        self.assertEqual(pointer["release_id"], "rel-old")
        self.assertEqual(pointer["status"], "ROLLED_BACK")

    def test_external_eval_uses_subtitle_and_chapter_not_visual_truth(self):
        ingest = self.root / "ingest"
        video = ingest / "BV1"
        video.mkdir(parents=True)
        (video / "subs.txt").write_text(
            "[subtitle|00:00:10] 现在进入第3幕，准备挑战首领\\n", encoding="utf-8"
        )
        (video / "meta.json").write_text(
            json.dumps({"chapters": [{"title": "第3幕", "start": 10.0}]}), encoding="utf-8"
        )
        replay_dir = self.root / "replay"
        replay_dir.mkdir()
        replay = replay_dir / "BV1-replay.json"
        replay.write_text(json.dumps({
            "steps": [{
                "Frame": "BV1-10s-1920x1080", "Act": 3, "RawAct": 3,
                "TruthAct": 9
            }]
        }), encoding="utf-8")
        report = build_external_eval.evaluate(replay, ingest, tolerance=2.5, min_labels=1)
        self.assertEqual(report["status"], "EVALUATED_EXTERNAL")
        self.assertEqual(report["label_count"], 1)
        self.assertEqual(report["samples"][0]["truth_act"], 3)
        self.assertTrue(report["samples"][0]["match"])
        self.assertFalse(report["promotion_eligible"])

    def test_external_eval_excludes_single_source_or_conflict(self):
        ingest = self.root / "ingest"
        video = ingest / "BV2"
        video.mkdir(parents=True)
        (video / "subs.txt").write_text(
            "[subtitle|00:00:10] 现在进入第3幕\\n", encoding="utf-8"
        )
        (video / "meta.json").write_text(
            json.dumps({"chapters": [{"title": "第4幕", "start": 10.0}]}), encoding="utf-8"
        )
        replay_dir = self.root / "replay"
        replay_dir.mkdir()
        replay = replay_dir / "BV2-replay.json"
        replay.write_text(json.dumps({
            "steps": [{"Frame": "BV2-10s-1920x1080", "Act": 3, "RawAct": 3}]
        }), encoding="utf-8")
        report = build_external_eval.evaluate(replay, ingest, tolerance=2.5, min_labels=1)
        self.assertEqual(report["label_count"], 0)
        self.assertIn("conflicting_external_labels", report["excluded"])


if __name__ == "__main__":
    unittest.main()
