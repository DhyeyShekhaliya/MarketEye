# ADR-0012: Sealed data snapshots

**Status:** Accepted · **Date:** 2026-09-03 · **Phase:** 1 (retroactively documented in Phase 4)

## Context

`PLAN.md` §4.5 requires every screen and backtest to resolve against a sealed snapshot, "never
whatever is in the table now." This was implemented in Phase 1 — `DataSnapshot`
(`src/MarketEye.Domain/Entities/DataSnapshot.cs`) and `SnapshotLifecycle`
(`src/MarketEye.Infrastructure/Ingestion/SnapshotLifecycle.cs`) have existed since then, and every
later phase depends on the guarantee — but the reasoning is currently scattered across §4.5 itself
and passing mentions inside `docs/adr/0007` and `docs/adr/0008`, with no ADR of its own. §11 lists
this as one of six ADRs the plan calls for; this closes that gap for a decision already shipped.

## Decision

### Write-then-seal, never write-and-immediately-visible

`SnapshotLifecycle.OpenAsync` creates a `DataSnapshot` row with `SealedAt = null`.
`DailyIngestionJob.RunAsync` writes price bars and recomputes derived data against that open
snapshot, and only calls `SnapshotLifecycle.SealAsync` — which sets `SealedAt` and refuses to seal
a snapshot with zero price rows — as its last step, after everything else succeeded.
`LatestSealedAsync` is the only read path any screen or backtest uses to resolve "the current
data," and it filters on `SealedAt != null` explicitly: an unsealed snapshot is invisible to every
query in the system, not merely discouraged.

### What this buys, all from one mechanism

- **Reproducibility.** A `ScreenRun` or `BacktestRun` records the `SnapshotId` it ran against
  (`ScreenRun.SnapshotId`, `BacktestRun`'s definition/backtest window resolve snapshots by date
  through the same `LatestSealedAsync` call). Since a sealed snapshot's underlying rows never
  change, re-running the same criteria against the same `SnapshotId` returns identical results
  forever — "why did this return 183 companies yesterday and 177 today" becomes a diff between two
  named snapshots rather than a guess about what changed underneath a live table.
- **Free cache invalidation.** `CachedScreeningEngine`'s cache key
  (`src/MarketEye.Infrastructure/Screening/CachedScreeningEngine.cs`, `BuildKey`) is
  `screen:{hash(criteria)}:{snapshotId}` — the snapshot id is part of the key, not a side channel
  invalidated by a separate mechanism. A new sealed snapshot is a new id, which is automatically a
  cache miss for every criteria that ran against the old one; there is no TTL to tune and no
  explicit invalidation call anywhere in the ingestion path, because a stale entry can only ever be
  keyed under an id nothing will ask for again.
- **Atomic ingest failure.** `DailyIngestionJob.RunAsync`'s catch block calls
  `SnapshotLifecycle.AbandonAsync` on any exception, and `SealAsync` itself refuses to seal a
  snapshot with zero price rows (distinguishing a market holiday, which should produce no snapshot
  at all, from a silently failed download, which must not look like one). A night that dies
  partway through — bars written, corporate actions half-applied, indicators not yet recomputed —
  leaves an unsealed row that `LatestSealedAsync` will never return, so nothing downstream ever
  observes a half-finished ingestion as if it were complete data.

### Sealing per historical date, not once per backfill range

`BackfillService` originally sealed exactly one snapshot for its whole range (bars bulk-load in one
pass to stay linear rather than quadratic). `SnapshotLifecycle.SealHistoricalSnapshotsAsync` fixed
the resulting gap — `LatestSealedAsync` can only resolve a date at or before a snapshot actually
sealed at that date, so a five-year backfill sealed under one date left every earlier date
unresolvable for a point-in-time screen or backtest. The fix reads only the dates and row counts
already sitting in `PriceBars` (no re-fetch, no re-parse), keeping the retroactive repair a cheap
third pass rather than reintroducing the O(days²) cost the original two-pass design exists to
avoid.

## Consequences

- Every correctness guarantee downstream — point-in-time reads (§4.1), the result cache (§5.5), and
  reproducible backtests (§7) — depends on this one write-then-seal mechanism rather than each
  building its own invalidation or point-in-time logic independently.
- A query that bypasses `LatestSealedAsync` and reads `PriceBars`/`Fundamentals` directly would
  silently reintroduce lookahead bias and cache incoherence at once; there is no guard enforcing
  this at the type level today beyond convention and code review, unlike the six explicit
  `PointInTimeGuard` throws §8.2 requires for the backtester specifically.
- Sealing is per calendar date, not per ingestion run: a retroactive repair (`SealHistoricalSnapshotsAsync`,
  exposed as `POST /api/ingest/seal-historical-snapshots`) can backfill missing seals for an
  already-loaded range without re-touching a single already-correct `PriceBars` row.
