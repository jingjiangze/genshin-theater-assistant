#!/usr/bin/env python3
"""Persist immutable, content-addressed pipeline batches to the automation-data branch.

Only explicitly allow-listed text outputs are copied. Cookies, video frames, model files,
runner temp files and arbitrary workspace paths are never persisted.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import subprocess
import sys
import time
from datetime import datetime, timezone

from jsonio import read_json
from pathlib import Path
from typing import Iterable

ALLOWLIST = {
    "ingest": ("ingest-summary.json", "*-ops.json", "*/meta.json", "*/subs.txt",
               "no_text_videos.txt"),
    "evaluation": (
        "*-replay.md", "*-replay.json", "external-eval.json", "*-ops.json",
        "*/meta.json",
        "experience/SUMMARY.md", "experience/rules.json",
        "experience/OPS-CONSENSUS.md",
    ),
    "corpus": ("guide-corpus-extra.json", "guide-corpus-wiki.json"),
    "adaptation": ("adapt-report.json", "quality-report.json", "*.manifest.json"),
}
MAX_FILE_BYTES = 20 * 1024 * 1024
MAX_BATCH_BYTES = 50 * 1024 * 1024
FORBIDDEN_PARTS = {
    ".yt-cookies", "cookie", "secret", ".env", "token", "credential",
    "node_modules", ".git", "models", "frames", "screenshots",
}


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def selected_files(root: Path, kind: str) -> list[Path]:
    patterns = ALLOWLIST[kind]
    found: set[Path] = set()
    for pattern in patterns:
        found.update(p for p in root.glob(pattern) if p.is_file())
    safe = []
    for path in sorted(found):
        rel = path.relative_to(root)
        parts = {part.lower() for part in rel.parts}
        if any(any(bad in part for bad in FORBIDDEN_PARTS) for part in parts):
            continue
        if path.is_symlink():
            continue
        safe.append(path)
    return safe


def build_manifest(root: Path, kind: str, run_id: str, attempt: str,
                   source_sha: str, workflow: str) -> tuple[dict, list[tuple[Path, str, bytes]]]:
    files = selected_files(root, kind)
    if not files:
        raise ValueError(f"no allow-listed output files found for kind={kind}")
    payload = []
    total = 0
    for path in files:
        data = path.read_bytes()
        if len(data) > MAX_FILE_BYTES:
            raise ValueError(f"file exceeds limit ({MAX_FILE_BYTES} bytes): {path.name}")
        total += len(data)
        if total > MAX_BATCH_BYTES:
            raise ValueError(f"batch exceeds limit ({MAX_BATCH_BYTES} bytes)")
        rel = path.relative_to(root).as_posix()
        payload.append((path, rel, data))
    batch_id = f"{run_id}-attempt-{attempt}"
    status = {
        "ingest": "INGESTED",
        "evaluation": "EVALUATED_UNVERIFIED",
        "corpus": "CANDIDATE",
        "adaptation": "CANDIDATE",
    }[kind]
    if kind == "evaluation":
        external = next((path for path in files if path.name == "external-eval.json"), None)
        if external is not None:
            try:
                result = read_json(external)
                status = (
                    "EVALUATED_EXTERNAL"
                    if int(result.get("externally_evaluated_videos", 0)) > 0
                    else "INSUFFICIENT_EXTERNAL_LABELS"
                )
            except (OSError, ValueError, TypeError):
                status = "EVALUATION_INVALID"
    manifest = {
        "schema_version": 1,
        "batch_id": batch_id,
        "kind": kind,
        "status": status,
        "workflow": workflow,
        "run_id": str(run_id),
        "run_attempt": str(attempt),
        "source_commit": source_sha,
        "created_at": datetime.now(timezone.utc).isoformat(),
        "file_count": len(payload),
        "total_bytes": total,
        "files": [
            {"path": rel, "size_bytes": len(data), "sha256": sha256(data)}
            for _, rel, data in payload
        ],
        "promotion": {
            "eligible": False,
            "reason": "durable ingestion is not semantic validation or independent ground truth",
        },
    }
    return manifest, payload


def git(repo: Path, *args: str) -> str:
    result = subprocess.run(
        ["git", "-C", str(repo), *args],
        check=True, text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    )
    return result.stdout.strip()


def persist(state_dir: Path, manifest: dict, payload: list[tuple[Path, str, bytes]]) -> str:
    batch_root = state_dir / ".pipeline-state" / "batches" / manifest["kind"] / manifest["batch_id"]
    batch_root.mkdir(parents=True, exist_ok=False)
    for _, rel, data in payload:
        dest = batch_root / "files" / rel
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_bytes(data)
    (batch_root / "manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    state_readme = state_dir / ".pipeline-state" / "README.md"
    state_readme.parent.mkdir(parents=True, exist_ok=True)
    if not state_readme.exists():
        state_readme.write_text(
            "# Automation data ledger\n\n"
            "Append-only batch snapshots. Each batch is isolated by workflow run and attempt.\n"
            "Raw collection, candidate corpus, and evaluation reports are not promoted to live\n"
            "decision knowledge by this ledger. See each manifest for file hashes and status.\n",
            encoding="utf-8",
        )
    git(state_dir, "config", "user.name", "theater-automation[bot]")
    git(state_dir, "config", "user.email", "theater-automation[bot]@users.noreply.github.com")
    git(state_dir, "add", ".pipeline-state/README.md", str(batch_root.relative_to(state_dir)))
    git(state_dir, "commit", "-m", f"data: persist {manifest['kind']} batch {manifest['batch_id']}")
    # Rebase boundedly if another workflow advanced the ledger branch.
    for attempt in range(1, 5):
        try:
            git(state_dir, "push", "origin", "HEAD:automation-data")
            return git(state_dir, "rev-parse", "HEAD")
        except subprocess.CalledProcessError:
            if attempt == 4:
                raise
            git(state_dir, "fetch", "origin", "automation-data")
            try:
                git(state_dir, "rebase", "origin/automation-data")
            except subprocess.CalledProcessError:
                git(state_dir, "rebase", "--abort")
                raise
    raise RuntimeError("unreachable push retry state")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--kind", choices=sorted(ALLOWLIST), required=True)
    parser.add_argument("--input-dir", type=Path, required=True)
    parser.add_argument("--state-dir", type=Path, required=True)
    parser.add_argument("--run-id", default=os.environ.get("GITHUB_RUN_ID", "local"))
    parser.add_argument("--attempt", default=os.environ.get("GITHUB_RUN_ATTEMPT", "1"))
    parser.add_argument("--source-sha", default=os.environ.get("GITHUB_SHA", "unknown"))
    parser.add_argument("--workflow", default=os.environ.get("GITHUB_WORKFLOW", "local"))
    args = parser.parse_args()
    try:
        manifest, payload = build_manifest(
            args.input_dir.resolve(), args.kind, args.run_id, args.attempt,
            args.source_sha, args.workflow,
        )
        commit = persist(args.state_dir.resolve(), manifest, payload)
        print(json.dumps({"batch_id": manifest["batch_id"], "kind": args.kind,
                          "status": manifest["status"], "file_count": manifest["file_count"],
                          "ledger_commit": commit}, ensure_ascii=False))
        return 0
    except (OSError, ValueError, subprocess.CalledProcessError, FileExistsError) as exc:
        print(f"persist_batch: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
