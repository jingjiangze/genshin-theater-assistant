#!/usr/bin/env python3
"""Create immutable knowledge releases from VERIFIED candidates or roll back a bad release.

No manual approval is used. Only candidates carrying machine-verifiable strategy and holdout
results can be activated. Candidate extraction itself cannot set VERIFIED.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import subprocess
import tempfile
from datetime import datetime, timezone
from pathlib import Path


def read_json(path: Path) -> dict:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
        return value if isinstance(value, dict) else {}
    except (OSError, ValueError):
        return {}


def stable_json(value: object) -> bytes:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")


def eligible(candidate: dict) -> bool:
    verification = candidate.get("verification") or {}
    return (
        candidate.get("status") == "VERIFIED"
        and candidate.get("season") is not None
        and candidate.get("version_range") is not None
        and len(candidate.get("evidence_ids") or []) >= 3
        and int(candidate.get("independent_creator_count") or 0) >= 2
        and bool(verification.get("evaluation_id"))
        and verification.get("holdout_passed") is True
        and verification.get("baseline_regression_passed") is True
        and verification.get("strategy_gate_passed") is True
        and int(verification.get("holdout_samples") or 0) >= 20
        and isinstance(verification.get("strategy_success_rate"), (int, float))
        and isinstance(verification.get("baseline_strategy_success_rate"), (int, float))
        and 0.0 <= verification["strategy_success_rate"] <= 1.0
        and 0.0 <= verification["baseline_strategy_success_rate"] <= 1.0
        and verification["strategy_success_rate"] >= verification["baseline_strategy_success_rate"]
        and verification.get("counter_evidence_resolved") is True
    )


def atomic_json(path: Path, value: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, temp_name = tempfile.mkstemp(prefix=path.name + ".", suffix=".tmp", dir=path.parent)
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as stream:
            json.dump(value, stream, ensure_ascii=False, indent=2)
            stream.write("\n")
        os.replace(temp_name, path)
    finally:
        if os.path.exists(temp_name):
            os.unlink(temp_name)


def release_or_rollback(registry_path: Path, releases_dir: Path, active_path: Path,
                        health_path: Path | None = None) -> dict:
    registry = read_json(registry_path)
    active = read_json(active_path)
    health = read_json(health_path) if health_path else {}
    active_id = active.get("release_id")

    if health and health.get("release_id") == active_id and health.get("passed") is False:
        previous = active.get("previous_release")
        pointer = {
            "schema_version": 1,
            "release_id": previous,
            "previous_release": None,
            "status": "ROLLED_BACK" if previous else "BASELINE",
            "rolled_back_from": active_id,
            "rollback_reason": str(health.get("reason") or "post-release regression failed"),
            "updated_at": datetime.now(timezone.utc).isoformat(),
        }
        atomic_json(active_path, pointer)
        return {"action": "ROLLBACK", "from": active_id, "to": previous}

    candidates = [x for x in registry.get("candidates", []) if isinstance(x, dict) and eligible(x)]
    if not candidates:
        return {"action": "NOOP", "reason": "no VERIFIED candidate passed strategy and holdout gates"}

    candidates.sort(key=lambda x: x["candidate_id"])
    registry_hash = hashlib.sha256(registry_path.read_bytes()).hexdigest()
    release_basis = {
        "schema_version": 1,
        "registry_sha256": registry_hash,
        "candidate_ids": [x["candidate_id"] for x in candidates],
        "evaluation_ids": [x["verification"]["evaluation_id"] for x in candidates],
    }
    release_id = "rel-" + hashlib.sha256(stable_json(release_basis)).hexdigest()[:16]
    release_path = releases_dir / release_id / "knowledge.json"
    if not release_path.exists():
        release = {
            "schema_version": 1,
            "release_id": release_id,
            "created_at": datetime.now(timezone.utc).isoformat(),
            "registry_sha256": registry_hash,
            "candidates": candidates,
        }
        atomic_json(release_path, release)

    if active_id == release_id:
        return {"action": "NOOP", "reason": "active release is already current", "release_id": release_id}

    pointer = {
        "schema_version": 1,
        "release_id": release_id,
        "previous_release": active_id,
        "status": "ACTIVE",
        "activated_at": datetime.now(timezone.utc).isoformat(),
        "source_registry_sha256": registry_hash,
    }
    atomic_json(active_path, pointer)
    return {"action": "PROMOTE", "release_id": release_id, "previous_release": active_id,
            "candidate_count": len(candidates)}


def git(repo: Path, *args: str) -> str:
    p = subprocess.run(["git", "-C", str(repo), *args], check=True, text=True,
                       stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    return p.stdout.strip()


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--ledger-repo", type=Path, required=True)
    ap.add_argument("--registry", type=Path, required=True)
    ap.add_argument("--releases-dir", type=Path, required=True)
    ap.add_argument("--active-pointer", type=Path, required=True)
    ap.add_argument("--health-report", type=Path)
    args = ap.parse_args()
    repo = args.ledger_repo.resolve()
    registry = args.registry.resolve()
    releases_dir = args.releases_dir.resolve()
    active_pointer = args.active_pointer.resolve()
    health = args.health_report.resolve() if args.health_report else None
    before = {
        registry: registry.read_bytes() if registry.exists() else None,
        active_pointer: active_pointer.read_bytes() if active_pointer.exists() else None,
    }
    release_files_before = set(releases_dir.rglob("knowledge.json")) if releases_dir.exists() else set()
    try:
        result = release_or_rollback(registry, releases_dir, active_pointer, health)
        changed = [p for p in before if (p.read_bytes() if p.exists() else None) != before[p]]
        if releases_dir.exists():
            changed.extend(p for p in releases_dir.rglob("knowledge.json") if p not in release_files_before)
        print(json.dumps(result, ensure_ascii=False))
        if not changed:
            return 0
        rels = [str(p.relative_to(repo)) for p in changed if p.is_relative_to(repo)]
        if not rels:
            raise ValueError("release outputs must remain inside ledger repository")
        git(repo, "config", "user.name", "theater-automation[bot]")
        git(repo, "config", "user.email", "theater-automation[bot]@users.noreply.github.com")
        git(repo, "add", *rels)
        git(repo, "commit", "-m", f"knowledge: {result['action'].lower()} release")
        for attempt in range(1, 5):
            try:
                git(repo, "push", "origin", "HEAD:automation-data")
                break
            except subprocess.CalledProcessError:
                if attempt == 4:
                    raise
                git(repo, "fetch", "origin", "automation-data")
                git(repo, "rebase", "origin/automation-data")
        return 0
    except (OSError, ValueError, subprocess.CalledProcessError) as exc:
        print(f"release_knowledge: {exc}")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
