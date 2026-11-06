import json
import tempfile
import unittest
from pathlib import Path

import persist_batch


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


if __name__ == "__main__":
    unittest.main()
