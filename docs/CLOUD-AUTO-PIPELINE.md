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

Current ledger statuses are intentionally conservative: ingest = `INGESTED`, replay = `EVALUATED_EXTERNAL` / `INSUFFICIENT_EXTERNAL_LABELS` / `EVALUATED_UNVERIFIED`, corpus = `CANDIDATE`. The current replay truth is coupled to visual OCR, so it cannot promote knowledge.

## Phases

| Phase | Scope | State |
|---|---|---|
| P0 | Durable batch ledger, hashes, source/run traceability, isolate corpus refresh from main | IMPLEMENTED; cross-branch smoke PASS; latest code regression checks pending |
| P1 | External evaluation labels, fixed holdout set, regression gates, automatic quarantine | IN PROGRESS: subtitle + chapter agreement evaluator implemented; calibration/holdout gates pending |
| P2 | Knowledge extraction, provenance/creator independence, deduplication, contradiction and version states | IN PROGRESS: creator IDs, candidate registry and scheduled extractor implemented; conflict/version lifecycle and strategy-backed VERIFIED gate pending |
| P3 | Single-act constraints and multi-act planner with baseline comparisons | IN PROGRESS: Beam Search planner and --plan-theater CLI implemented; endurance-aware assignment and replay comparison pending |
| P4 | Retrieval experiments (BM25 baseline, then optional vector/RRF/reranker) | IN PROGRESS: BM25 17/17 hit@1 and hit@3 baseline enforced in cloud validation; alternative retrievers deferred until larger holdout |
| P5 | Automated parameter optimization, promotion and rollback | IN PROGRESS: verified-only immutable release/active pointer/automatic rollback workflow implemented; no candidates can currently pass until strategy-eval produces holdout evidence; parameter search pending |
| P6 | Reassess model fine-tuning only after enough independent labels and measured benefit | DEFERRED |

## Multi-act planner v1

`app/ActPlanner.cs` composes the existing single-act `PlanRanker` with a bounded Beam Search across available act encounter variants. It applies a soft penalty when the same recommended character is reused across acts and exposes `--plan-theater --difficulty <name> --roster <path> --out <file>`. The GOOD roster does not contain a reliable live endurance ledger, so this is a planning heuristic rather than a proof that every character is available. It does not infer hidden stamina state. CI self-tests use a synthetic two-act case to verify that the planner chooses a non-repeated owned recommendation when scores tie.

## External evaluation v1

`tools/pipeline/build_external_eval.py` aligns replay frame timestamps with separately collected subtitle timestamps and video chapter metadata. A frame is counted only when both channels agree on the same act within the configured time window. These are separate data channels, not guaranteed independent authors: the same uploader may control both subtitles and chapter metadata. It ignores `ReplayRunner.TruthAct`, which is derived from OCR on the prediction frame. Conflicts and single-channel labels are excluded. The report records coverage, accuracy, evidence snippets, and excluded-sample reasons. Fewer than 10 externally labeled frames remains `INSUFFICIENT_EXTERNAL_LABELS`. This is an external signal, not a fully independent gold set: subtitle/chapter agreement can still be wrong or sparse.

## Provenance and creator independence v1

`ingest.py` now attaches `creator_id` and creator display name to operation candidates when the platform exposes a stable uploader/channel ID. `OpsConsensus` groups normalized candidate text but counts independent creators separately from video IDs. Multiple videos from one creator and records without a stable creator ID cannot satisfy the two-creator consensus threshold. Such records remain automatically quarantined candidates. `tools/pipeline/extract_knowledge.py` merges these immutable evidence records into an append-only knowledge registry. Existing `rotations.json` records sourced from video/community counts are now displayed as historical unverified candidates; only in-game mechanism sources remain baseline labels. This prevents old video-count consensus from being presented as independently verified strategy. It assigns `CANDIDATE` by default and `PROVISIONAL` only after explicit season and game-version context, at least two stable creator IDs, and three evidence records. When season context is missing, candidates are isolated per video instead of being merged across unknown seasons. It never assigns `VERIFIED`. This does not yet solve cross-post attribution when the platform omits stable IDs; those records receive no independence credit.

## Verified-only release and rollback v1

`tools/pipeline/release_knowledge.py` and `.github/workflows/auto-release.yml` implement an automatic, append-only release path. A candidate must already be `VERIFIED`, have explicit season/version, at least three evidence IDs and two stable creator IDs, and carry a strategy evaluation ID, at least 20 holdout samples, a passing holdout, a passing baseline-regression gate, at least 20 holdout samples, resolved counter-evidence, and a measured strategy success rate no worse than the baseline. The release snapshot is immutable; `active.json` is switched atomically. A matching failed post-release health report restores the previous release pointer. The current extractor never creates `VERIFIED`, so the release workflow safely no-ops until a future strategy-evaluation stage can supply those measurements.

## Automatic promotion contract

A batch being persisted, parsed, or successfully collected is not enough to promote it. Promotion must be based on:
1. labels from channels separate from the model input (not the same OCR output); creator/source independence must be checked separately;
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
