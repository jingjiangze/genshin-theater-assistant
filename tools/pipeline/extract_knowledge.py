#!/usr/bin/env python3
"""Extract immutable ingest evidence into an append-only knowledge candidate registry.

No candidate is ever marked VERIFIED here. A later independent strategy gate owns promotion.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
import subprocess
from datetime import datetime, timezone
from pathlib import Path


def digest(value: str) -> str:
    return hashlib.sha256(value.encode("utf-8")).hexdigest()


def normalize(value: str) -> str:
    return re.sub(r"[\W_]+", "", (value or "").casefold(), flags=re.UNICODE)


def read_json(path: Path) -> dict:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
        return value if isinstance(value, dict) else {}
    except (OSError, ValueError):
        return {}


def evidence_from_batches(batches_root: Path) -> list[dict]:
    found = []
    for manifest_path in sorted(batches_root.glob("ingest/*/manifest.json")):
        manifest = read_json(manifest_path)
        if manifest.get("status") != "INGESTED":
            continue
        batch_id = manifest.get("batch_id") or manifest_path.parent.name
        files_root = manifest_path.parent / "files"
        for ops_path in sorted(files_root.glob("*-ops.json")):
            video_id = ops_path.name.removesuffix("-ops.json")
            meta = read_json(files_root / video_id / "meta.json")
            platform = str(meta.get("platform") or "unknown")
            raw_creator = meta.get("creator_id")
            meta_creator = str(raw_creator).strip() if raw_creator is not None else ""
            ops_doc = read_json(ops_path)
            for op in ops_doc.get("ops", []):
                if not isinstance(op, dict):
                    continue
                text = str(op.get("text") or "").strip()
                kind = str(op.get("kind") or "").strip()
                if len(text) < 4 or not kind:
                    continue
                act = op.get("act")
                if not isinstance(act, int) or not 1 <= act <= 12:
                    act = 0
                creator = str(op.get("creator_id") or meta_creator).strip()
                creator_key = f"{platform}:{creator}" if creator else ""
                source = str(op.get("source") or f"{platform}:{video_id}")
                channel = str(op.get("channel") or "unknown")
                evidence_id = digest("|".join((batch_id, video_id, source, channel, kind, normalize(text))))
                found.append({
                    "evidence_id": evidence_id,
                    "batch_id": batch_id,
                    "video_id": video_id,
                    "platform": platform,
                    "creator_id": creator_key,
                    "creator_name": str(op.get("creator_name") or meta.get("uploader") or ""),
                    "channel": channel,
                    "source": source,
                    "kind": kind,
                    "text": text,
                    "act": act,
                })
    return found


def merge_candidates(evidence: list[dict], existing: dict | None = None) -> dict:
    existing = existing or {}
    records = {
        item["candidate_id"]: item
        for item in existing.get("candidates", [])
        if isinstance(item, dict) and item.get("candidate_id")
    }
    for item in evidence:
        key = digest("|".join((item["kind"], str(item["act"]), normalize(item["text"]))))
        record = records.get(key)
        if record is None:
            record = {
                "candidate_id": key,
                "knowledge_type": "strategy",
                "kind": item["kind"],
                "text": item["text"],
                "act": item["act"] or None,
                "season": None,
                "version_range": None,
                "conditions": [],
                "steps": [],
                "evidence_ids": [],
                "evidence": [],
                "independent_creator_count": 0,
                "status": "CANDIDATE",
                "confidence": None,
                "counter_evidence": [],
                "status_reason": "version/condition and independent strategy evaluation are not yet available",
            }
            records[key] = record
        if item["evidence_id"] not in record["evidence_ids"]:
            record["evidence_ids"].append(item["evidence_id"])
            record["evidence"].append(item)
        # Extraction is append-only: never downgrade a terminal knowledge decision.
        if record.get("status") in {"VERIFIED", "CONFLICTED", "STALE", "REJECTED"}:
            continue
        creators = {x["creator_id"] for x in record["evidence"] if x.get("creator_id")}
        record["independent_creator_count"] = len(creators)
        if len(creators) >= 2 and len(record["evidence_ids"]) >= 3:
            record["status"] = "PROVISIONAL"
            record["status_reason"] = "at least two stable creator IDs and three evidence records; excluded from decisive runtime use"
        else:
            record["status"] = "CANDIDATE"
    return {
        "schema_version": 1,
        "updated_at": datetime.now(timezone.utc).isoformat(),
        "candidate_count": len(records),
        "verified_count": sum(x.get("status") == "VERIFIED" for x in records.values()),
        "candidates": sorted(records.values(), key=lambda x: x["candidate_id"]),
        "promotion_policy": {
            "verified_requires_independent_strategy_eval": True,
            "this_extractor_can_promote_to_verified": False,
        },
    }


def git(repo: Path, *args: str) -> str:
    p = subprocess.run(["git", "-C", str(repo), *args], check=True, text=True,
                       stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    return p.stdout.strip()


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--ledger-repo", type=Path, required=True)
    ap.add_argument("--batches", type=Path, required=True)
    ap.add_argument("--registry", type=Path, required=True)
    args = ap.parse_args()
    repo = args.ledger_repo.resolve()
    registry = args.registry.resolve()
    try:
        existing = read_json(registry)
        evidence = evidence_from_batches(args.batches.resolve())
        known = {
            evidence_id
            for candidate in existing.get("candidates", [])
            if isinstance(candidate, dict)
            for evidence_id in candidate.get("evidence_ids", [])
        }
        new_evidence = [item for item in evidence if item["evidence_id"] not in known]
        if not new_evidence and registry.exists():
            print(f"no new evidence; candidates={existing.get('candidate_count', 0)}")
            return 0
        result = merge_candidates(new_evidence, existing)
        serialized = json.dumps(result, ensure_ascii=False, indent=2) + "\n"
        if registry.exists() and registry.read_text(encoding="utf-8") == serialized:
            print(f"no registry change; candidates={result['candidate_count']}")
            return 0
        registry.parent.mkdir(parents=True, exist_ok=True)
        registry.write_text(serialized, encoding="utf-8")
        rel = registry.relative_to(repo)
        git(repo, "config", "user.name", "theater-automation[bot]")
        git(repo, "config", "user.email", "theater-automation[bot]@users.noreply.github.com")
        git(repo, "add", str(rel))
        git(repo, "commit", "-m", "knowledge: extract immutable candidate records")
        for attempt in range(1, 5):
            try:
                git(repo, "push", "origin", "HEAD:automation-data")
                break
            except subprocess.CalledProcessError:
                if attempt == 4:
                    raise
                git(repo, "fetch", "origin", "automation-data")
                git(repo, "rebase", "origin/automation-data")
        print(f"persisted candidates={result['candidate_count']} evidence={len(evidence)} verified=0")
        return 0
    except (OSError, ValueError, subprocess.CalledProcessError) as exc:
        print(f"extract_knowledge: {exc}")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
