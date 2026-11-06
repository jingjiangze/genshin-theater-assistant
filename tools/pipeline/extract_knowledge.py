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

from jsonio import read_json as jsonio_read_json, read_text


def digest(value: str) -> str:
    return hashlib.sha256(value.encode("utf-8")).hexdigest()


def normalize(value: str) -> str:
    return re.sub(r"[\W_]+", "", (value or "").casefold(), flags=re.UNICODE)


def read_json(path: Path) -> dict:
    try:
        value = jsonio_read_json(path)
        return value if isinstance(value, dict) else {}
    except (OSError, ValueError):
        return {}


def extract_context(meta: dict) -> tuple[int | None, str | None]:
    text = " ".join((str(meta.get("title") or ""), str(meta.get("description") or "")))
    season_match = re.search(r"(?:第\s*(\d+)\s*(?:期|赛季)|season\s*#?\s*(\d+))", text, re.IGNORECASE)
    season = int(next(x for x in season_match.groups() if x)) if season_match else None
    version_match = re.search(r"(?<!\d)(\d+\.\d+)(?!\d)", text)
    version = version_match.group(1) if version_match else None
    return season, version


def evidence_key(item: dict) -> str:
    video_id = str(item.get("video_id") or "")
    platform = str(item.get("platform") or "")
    channel = str(item.get("channel") or "")
    kind = str(item.get("kind") or "")
    text = normalize(str(item.get("text") or ""))
    if not video_id or not platform or not channel or not kind or not text:
        return str(item.get("evidence_id") or "")
    # One video/channel/claim is one evidence record across repeated batches and frames.
    return digest("|".join((platform, video_id, channel, kind, text)))

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
            season, version = extract_context(meta)
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
                evidence_id = digest("|".join((platform, video_id, channel, kind, normalize(text))))
                found.append({
                    "evidence_id": evidence_id,
                    "batch_id": batch_id,
                    "observed_batches": [batch_id],
                    "video_id": video_id,
                    "platform": platform,
                    "season": season,
                    "version": version,
                    "creator_id": creator_key,
                    "creator_name": str(op.get("creator_name") or meta.get("uploader") or ""),
                    "channel": channel,
                    "source": source,
                    "source_refs": [source],
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
    # Migrate older registries by collapsing repeated ingests of the same video/channel claim.
    for record in records.values():
        unique = {}
        for old_item in record.get("evidence", []):
            key = evidence_key(old_item)
            if not key:
                continue
            batches = set(old_item.get("observed_batches", []))
            if old_item.get("batch_id"):
                batches.add(old_item["batch_id"])
            if key in unique:
                unique[key]["observed_batches"] = sorted(set(unique[key].get("observed_batches", [])) | batches)
                refs = set(unique[key].get("source_refs", [])) | set(old_item.get("source_refs", []))
                if old_item.get("source"):
                    refs.add(old_item["source"])
                unique[key]["source_refs"] = sorted(refs)
            else:
                old_item["evidence_id"] = key
                old_item["observed_batches"] = sorted(batches)
                refs = set(old_item.get("source_refs", []))
                if old_item.get("source"):
                    refs.add(old_item["source"])
                old_item["source_refs"] = sorted(refs)
                unique[key] = old_item
        record["evidence"] = list(unique.values())
        record["evidence_ids"] = list(unique.keys())

    for item in evidence:
        context_scope = "" if item.get("season") is not None else str(item.get("video_id") or item.get("batch_id") or item["evidence_id"])
        key = digest("|".join((
            item["kind"], str(item["act"]), str(item.get("season") or ""),
            str(item.get("version") or ""), context_scope, normalize(item["text"])
        )))
        record = records.get(key)
        if record is None:
            record = {
                "candidate_id": key,
                "knowledge_type": "strategy",
                "kind": item["kind"],
                "text": item["text"],
                "act": item["act"] or None,
                "season": item.get("season"),
                "version_range": ({"min": item["version"], "max": item["version"]} if item.get("version") else None),
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
        existing_evidence = next((x for x in record["evidence"] if x.get("evidence_id") == item["evidence_id"]), None)
        if existing_evidence is None:
            record["evidence_ids"].append(item["evidence_id"])
            batches = set(item.get("observed_batches", []))
            if item.get("batch_id"):
                batches.add(item["batch_id"])
            item["observed_batches"] = sorted(batches)
            refs = set(item.get("source_refs", []))
            if item.get("source"):
                refs.add(item["source"])
            item["source_refs"] = sorted(refs)
            record["evidence"].append(item)
        else:
            batches = set(existing_evidence.get("observed_batches", []))
            batches.update(item.get("observed_batches", []))
            if item.get("batch_id"):
                batches.add(item["batch_id"])
            existing_evidence["observed_batches"] = sorted(batches)
            refs = set(existing_evidence.get("source_refs", []))
            refs.update(item.get("source_refs", []))
            if item.get("source"):
                refs.add(item["source"])
            existing_evidence["source_refs"] = sorted(refs)
        # Extraction is append-only: never downgrade a terminal knowledge decision.
        if record.get("status") in {"VERIFIED", "CONFLICTED", "STALE", "REJECTED"}:
            continue
    # Recompute derived counts after deduplication, including records untouched by new evidence.
    for record in records.values():
        creators = {x["creator_id"] for x in record["evidence"] if x.get("creator_id")}
        record["independent_creator_count"] = len(creators)
        record["unique_video_count"] = len({x.get("video_id") for x in record["evidence"] if x.get("video_id")})
        if record.get("status") in {"VERIFIED", "CONFLICTED", "STALE", "REJECTED"}:
            continue
        has_version_context = record.get("season") is not None and record.get("version_range") is not None
        if (has_version_context and len(creators) >= 2 and record["unique_video_count"] >= 3
                and len(record["evidence_ids"]) >= 3):
            record["status"] = "PROVISIONAL"
            record["status_reason"] = "season/version context, at least two stable creator IDs, three distinct videos and three unique evidence records; excluded from decisive runtime use"
        else:
            record["status"] = "CANDIDATE"
            record["status_reason"] = "insufficient versioned independent video/creator evidence"
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
        known_batches = {}
        for candidate in existing.get("candidates", []):
            if not isinstance(candidate, dict):
                continue
            for stored in candidate.get("evidence", []):
                evidence_id = stored.get("evidence_id")
                if not evidence_id:
                    continue
                batches = set(stored.get("observed_batches", []))
                if stored.get("batch_id"):
                    batches.add(stored["batch_id"])
                known_batches[evidence_id] = batches
        new_evidence = [
            item for item in evidence
            if item["evidence_id"] not in known_batches
            or item.get("batch_id") not in known_batches[item["evidence_id"]]
        ]
        needs_migration = any(
            set(candidate.get("evidence_ids", [])) != {
                evidence_key(item) for item in candidate.get("evidence", [])
            }
            or any(not item.get("source_refs") for item in candidate.get("evidence", []))
            for candidate in existing.get("candidates", [])
            if isinstance(candidate, dict)
        )
        if not new_evidence and not needs_migration and registry.exists():
            print(f"no new evidence; candidates={existing.get('candidate_count', 0)}")
            return 0
        result = merge_candidates(new_evidence, existing)
        serialized = json.dumps(result, ensure_ascii=False, indent=2) + "\n"
        if registry.exists() and read_text(registry) == serialized:
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
