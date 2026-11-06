#!/usr/bin/env python3
"""Build an independent replay evaluation from subtitle + chapter evidence.

The production OCR result is never used to create labels. A frame is scored only when
subtitle and chapter channels independently state the same act near its timestamp.
"""
from __future__ import annotations

import argparse
import json
import re
from collections import Counter
from pathlib import Path

from jsonio import read_json, read_text

ACT_RE = re.compile(r"第\s*(\d{1,2})\s*幕")
TIME_RE = re.compile(r"-(\d+(?:\.\d+)?)s-")


def acts(text: str) -> set[int]:
    return {int(m.group(1)) for m in ACT_RE.finditer(text or "") if 1 <= int(m.group(1)) <= 12}


def clock_seconds(value: str) -> float | None:
    m = re.fullmatch(r"(\d{1,2}):(\d{2}):(\d{2})", value.strip())
    if not m:
        return None
    h, minute, sec = map(int, m.groups())
    return h * 3600 + minute * 60 + sec


def collect_events(video_dir: Path) -> list[dict]:
    events = []
    subs = video_dir / "subs.txt"
    if subs.exists():
        for line in read_text(subs).splitlines():
            m = re.match(r"^\[([^|\]]+)\|([^\]]*)\]\s*(.*)$", line)
            if not m or m.group(1).strip().lower() != "subtitle":
                continue
            timestamp = clock_seconds(m.group(2))
            labels = acts(m.group(3))
            if timestamp is not None and len(labels) == 1:
                events.append({"time": timestamp, "act": next(iter(labels)),
                               "source": "subtitle", "evidence": m.group(3)[:240]})
    meta = video_dir / "meta.json"
    if meta.exists():
        try:
            obj = read_json(meta)
            for chapter in obj.get("chapters", []):
                start = chapter.get("start")
                labels = acts(chapter.get("title", ""))
                if isinstance(start, (int, float)) and len(labels) == 1:
                    events.append({"time": float(start), "act": next(iter(labels)),
                                   "source": "chapter", "evidence": str(chapter.get("title", ""))[:240]})
        except (OSError, ValueError, TypeError):
            pass
    return events


def evaluate(replay_path: Path, ingest_root: Path, tolerance: float = 2.5,
             min_labels: int = 10) -> dict:
    replay = read_json(replay_path)
    video_id = replay_path.name.removesuffix("-replay.json")
    events = collect_events(ingest_root / video_id)
    steps = replay.get("steps", [])
    samples = []
    excluded = Counter()
    for step in steps:
        frame = str(step.get("Frame", ""))
        match = TIME_RE.search(frame)
        if not match:
            excluded["frame_timestamp_missing"] += 1
            continue
        stamp = float(match.group(1))
        near = [e for e in events if abs(e["time"] - stamp) <= tolerance]
        by_source: dict[str, set[int]] = {}
        evidence = []
        for event in near:
            by_source.setdefault(event["source"], set()).add(event["act"])
            evidence.append(event)
        if len(by_source) < 2:
            excluded["fewer_than_two_independent_channels"] += 1
            continue
        source_labels = []
        conflict = False
        for source, values in by_source.items():
            if len(values) != 1:
                conflict = True
                break
            source_labels.append(next(iter(values)))
        if conflict or len(set(source_labels)) != 1:
            excluded["conflicting_external_labels"] += 1
            continue
        truth = source_labels[0]
        predicted = int(step.get("Act") or 0)
        raw = int(step.get("RawAct") or 0)
        samples.append({
            "frame": frame, "timestamp_seconds": stamp, "truth_act": truth,
            "predicted_act": predicted, "raw_act": raw,
            "match": predicted == truth, "raw_match": raw == truth,
            "independent_channels": sorted(by_source),
            "evidence": [{"source": e["source"], "text": e["evidence"]} for e in near],
        })
    count = len(samples)
    correct = sum(1 for x in samples if x["match"])
    raw_correct = sum(1 for x in samples if x["raw_match"])
    status = "EVALUATED_EXTERNAL" if count >= min_labels else "INSUFFICIENT_EXTERNAL_LABELS"
    return {
        "schema_version": 1, "video_id": video_id, "status": status,
        "label_method": "subtitle_chapter_agreement",
        "prediction_input": "video_frame_ocr_and_templates",
        "label_sources": ["subtitle", "chapter"],
        "independence_scope": "separate text/metadata channels; same-video uploader dependence may remain",
        "label_count": count, "frame_count": len(steps),
        "coverage": (count / len(steps)) if steps else 0.0,
        "accuracy": (correct / count) if count else None,
        "raw_accuracy": (raw_correct / count) if count else None,
        "min_labels_required": min_labels,
        "promotion_eligible": False,
        "promotion_reason": "evaluation output alone cannot promote knowledge; holdout and source diversity gates are not yet complete",
        "excluded": dict(excluded),
        "samples": samples,
    }


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--replay-dir", type=Path, required=True)
    ap.add_argument("--ingest-dir", type=Path, required=True)
    ap.add_argument("--out", type=Path, required=True)
    ap.add_argument("--tolerance", type=float, default=2.5)
    ap.add_argument("--min-labels", type=int, default=10)
    args = ap.parse_args()
    reports = []
    for replay in sorted(args.replay_dir.glob("*-replay.json")):
        reports.append(evaluate(replay, args.ingest_dir, args.tolerance, args.min_labels))
    if not reports:
        raise SystemExit("no replay JSON files found")
    args.out.parent.mkdir(parents=True, exist_ok=True)
    result = {
        "schema_version": 1, "label_method": "subtitle_chapter_agreement",
        "reports": reports,
        "total_labels": sum(x["label_count"] for x in reports),
        "externally_evaluated_videos": sum(x["status"] == "EVALUATED_EXTERNAL" for x in reports),
        "promotion_eligible": False,
    }
    args.out.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    for report in reports:
        print(f"{report['video_id']}: {report['status']} labels={report['label_count']} "
              f"coverage={report['coverage']:.1%} accuracy={report['accuracy']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
