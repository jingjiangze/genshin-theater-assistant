# Cloud-first automatic knowledge pipeline

## Operating contract

The Windows client remains the local OCR/runtime application. GitHub Actions performs cloud collection, durable batch storage, knowledge extraction, evaluation, planning experiments, and controlled version selection.

No human approval is a normal pipeline dependency. Machine quality gates, immutable versions, isolation, and automatic rollback replace manual approval. Low-confidence candidates never silently become decision knowledge.

## Data lifecycle

`video-ingest`, `video-eval`, and `corpus-refresh` write immutable run snapshots to the separate `automation-data` branch under `.pipeline-state/batches/<kind>/<run>-attempt-<n>/`.

Every manifest records:
- schema version, batch identity, workflow and source commit;
- run ID and attempt;
- allow-listed file paths, byte sizes, and SHA-256 hashes;
- a machine-readable status and an explicit non-promotion reason.

The ledger stores only selected text/JSON reports. It does not store cookies, environment files, model weights, video frames, or arbitrary workspace files. Each file is limited to 20 MiB and each batch to 50 MiB. GitHub Actions Artifacts remain short-term diagnostics, not the system of record.

## Knowledge state machine

- `CANDIDATE`: newly extracted; isolated from live decisions.
- `PROVISIONAL`: structurally valid and supported by initial evidence; eligible only for low-weight retrieval after the later evidence gate exists.
- `VERIFIED`: passes independent evidence and fixed regression thresholds.
- `CONFLICTED`: material evidence disagrees; excluded from decisive recommendations until resolved automatically.
- `STALE`: version/condition no longer applies; retained historically, excluded from current recommendations.
- `REJECTED`: invalid, duplicate, or disproven.

Current ledger statuses are intentionally conservative: ingest = `INGESTED`, replay = `EVALUATED_UNVERIFIED`, corpus = `CANDIDATE`. The current replay truth is coupled to visual OCR, so it cannot promote knowledge.

## Phases

| Phase | Scope | State |
|---|---|---|
| P0 | Durable batch ledger, hashes, source/run traceability, isolate corpus refresh from main | IMPLEMENTED; smoke write and latest Actions checks pending |
| P1 | Independent evaluation labels, fixed holdout set, regression gates, automatic quarantine | IN PROGRESS: subtitle + chapter agreement evaluator implemented; calibration/holdout gates pending |
| P2 | Knowledge extraction, provenance/creator independence, deduplication, contradiction and version states | PENDING |
| P3 | Single-act constraints and multi-act planner with baseline comparisons | PENDING |
| P4 | Retrieval experiments (BM25 baseline, then optional vector/RRF/reranker) | PENDING |
| P5 | Automated parameter optimization, promotion and rollback | PENDING |
| P6 | Reassess model fine-tuning only after enough independent labels and measured benefit | DEFERRED |

## Independent evaluation v1\n\n`tools/pipeline/build_external_eval.py` aligns replay frame timestamps with independently collected subtitle timestamps and video chapter metadata. A frame is counted only when both channels agree on the same act within the configured time window. It ignores `ReplayRunner.TruthAct`, which is derived from OCR on the prediction frame. Conflicts and single-channel labels are excluded. The report records coverage, accuracy, evidence snippets, and excluded-sample reasons. Fewer than 10 independently labeled frames remains `INSUFFICIENT_INDEPENDENT_LABELS`. This is a first independent signal, not yet a calibrated gold set: subtitle/chapter agreement can still be wrong or sparse.\n\n## Automatic promotion contract

A batch being persisted, parsed, or successfully collected is not enough to promote it. Promotion must be based on:
1. independently sourced labels (not the same OCR output used as model input);
2. fixed train/validation/holdout separation;
3. minimum evidence and source-independence requirements;
4. no regression against the current stable baseline;
5. a versioned candidate artifact and an atomic pointer/version switch;
6. automatic rollback if post-promotion regression fails.

The pipeline must preserve the last known-good version when a gate fails. It must not turn a model-generated conclusion into independent evidence.

## Current boundaries

- `main` and the Windows client are not changed by this branch.
- Candidate data is stored on `automation-data`, not published into runtime corpus.
- No secrets are written to files or artifacts.
- Collection outages are isolated as failed batches; old outputs cannot masquerade as fresh output.
