# Automation data ledger

Append-only batch snapshots. Each batch is isolated by workflow run and attempt.
Raw collection, candidate corpus, and evaluation reports are not promoted to live decision knowledge by this ledger. Each manifest records source commit, run identity, file sizes, SHA-256 hashes, and a non-promotion status.
