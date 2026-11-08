# Cloud automation branch validation

This branch is an isolated execution check for the cloud-first automation work.

## What this validates

- GitHub connector can read the repository and create a branch from the current main head.
- Pushes to `automation/**` run the existing Windows CI.
- The cloud validation workflow runs on the same branch and verifies the existing ingest, replay/evaluation, corpus, adaptation, and release entry points.
- Existing JSON datasets remain parseable.
- Each validation run emits a small manifest containing event, ref, commit SHA, run ID, and tracked-file count.

## Safety boundary

- No production data is rewritten by this validation workflow.
- No secret values are printed or copied into artifacts.
- The workflow uses `contents: read`; it cannot push to main or publish knowledge.
- Artifacts are diagnostic only and expire after seven days.
- This is a connectivity and baseline-contract test, not proof that external Bilibili/YouTube collection or semantic strategy quality is healthy.

## Follow-up implementation gate

After the branch CI is green, implement durable batch storage, candidate/verified knowledge states, independent evaluation sets, and automatic promotion/rollback as separate commits. Do not promote a candidate solely because collection or JSON parsing succeeded.
