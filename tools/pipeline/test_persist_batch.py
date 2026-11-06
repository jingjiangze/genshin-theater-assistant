import json
import tempfile
import unittest
from pathlib import Path

import persist_batch
import build_external_eval
import extract_knowledge


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

    def test_independent_eval_status_requires_external_label_report(self):
        self.write("external-eval.json", json.dumps({
            "independently_evaluated_videos": 1,
            "promotion_eligible": False
        }))
        manifest, _ = persist_batch.build_manifest(
            self.root, "evaluation", "105", "1", "sha", "video-eval"
        )
        self.assertEqual(manifest["status"], "EVALUATED_INDEPENDENT")
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
                "evidence_ids": ["old"], "evidence": [], "status": "VERIFIED", "season": 29,
                "version_range": {"min": "7.1", "max": "7.1"}, "independent_creator_count": 2
            }]
        }
        evidence = [{
            "evidence_id": "new", "kind": "action", "text": "先击败钓客", "act": 6,
            "season": 29, "version": "7.1", "video_id": "BV-new", "creator_id": "bilibili:creator-C", "source": "BV-new"
        }]
        result = extract_knowledge.merge_candidates(evidence, existing)
        self.assertEqual(result["candidates"][0]["status"], "VERIFIED")
        self.assertEqual(len(result["candidates"][0]["evidence_ids"]), 2)

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
        self.assertEqual(report["status"], "EVALUATED_INDEPENDENT")
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
