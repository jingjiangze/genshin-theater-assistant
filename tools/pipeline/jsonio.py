"""Shared JSON I/O for the cloud pipeline.

CI run 421773... on automation/cloud-auto-evolution failed with:
  json.decoder.JSONDecodeError: Unexpected UTF-8 BOM (decode using utf-8-sig)

Reason: some batch artifacts are produced on Windows with PowerShell 5.1
`Set-Content -Encoding UTF8`, which writes a BOM; readers used plain `utf-8`.
All pipeline readers must tolerate both BOM and BOM-less UTF-8, and all writers
must emit BOM-less UTF-8 with LF newlines so hashes stay stable across runners.
"""
from __future__ import annotations

import json
from pathlib import Path


def read_text(path: Path) -> str:
    return Path(path).read_text(encoding="utf-8-sig", errors="replace")


def read_json(path: Path):
    return json.loads(read_text(path))


def write_json(path: Path, value) -> None:
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(value, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
        newline="\n",
    )
