# ADR-0011: Pre-computed indicators, never computed at query time

**Status:** Accepted · **Date:** 2026-09-03 · **Phase:** 1 (retroactively documented in Phase 4)

## Context

`PLAN.md` §4.3 states the rule plainly: "Indicators are computed at ingest and stored, never at
query time. Screening must stay a flat indexable `WHERE`." This was implemented in Phase 1 —
`IndicatorSet` (`src/MarketEye.Domain/Entities/IndicatorSet.cs`) and `DailyIngestionJob`'s
`RecomputeDerivedAsync` step (`src/MarketEye.Ingestion/Jobs/DailyIngestionJob.cs`) have existed
since then — but no ADR recorded the trade-off it makes, and `IndicatorSet`'s own doc comment
points at "`docs/adr/0003`'s sibling ADR" for the argument, which does not exist (0003 is the
unrelated .NET 10 decision). §11 lists this as one of six ADRs the plan calls for; this closes that
specific gap, retroactively, for a decision already shipped rather than one still being designed.

## Decision

### Compute SMA/RSI/MACD/ATR/realised-volatility once per affected security, at ingest

`DailyIngestionJob.RecomputeDerivedAsync` walks every security touched by that night's bars,
recomputes `IndicatorSet.Sma50`, `Sma200`, `Rsi14`, `Macd`, `MacdSignal`, `Atr14`, and `Vol30` from
its full adjusted-close history (`TechnicalIndicators`, `src/MarketEye.Application/Indicators/`),
and writes one row per `(SecurityId, Date)` — the same grain as `PriceBars`. A screen or backtest
then reads these columns directly; nothing downstream of ingestion ever calls
`TechnicalIndicators` itself.

### The trade this makes explicit: write amplification for read latency

Every night's ingestion pays the cost of recomputing a full indicator history for each touched
security (§4.3's "write amplification"), so that a screen's `WHERE Rsi14 < 30 AND Close > Sma200`
is a comparison against pre-materialised columns — the same flat, indexable shape CLAUDE.md
requires screening to stay in ("Screening must stay a flat indexable `WHERE`"). The alternative —
computing SMA/RSI/MACD from raw price history inside the screening query itself — would make that
promise impossible to keep for anything beyond raw price and fundamental columns: an indicator
needs an ordered window of prior bars, which is not expressible as a single-row predicate the
`CriteriaCompiler` can push into a `WHERE` clause, and recomputing a 200-day SMA per row for every
security in the universe on every screen would turn an indexed lookup into a per-request numerical
simulation.

### Incremental, not full-universe, to fit the hosting budget

`docs/adr/0006` (free-tier Azure hosting) already established that App Service F1's daily CPU
budget is the binding constraint on nightly work. `RecomputeDerivedAsync`'s own doc comment records
the consequence for indicators specifically: only securities touched by that night's ingestion are
recomputed, never the whole universe — a full nightly recompute across every security is exactly
the workload that would exhaust the quota. This is the same "amortise the cost, but keep it
bounded" reasoning §4.5's snapshot-per-day design applies to sealing.

### Derived from `AdjClose`, never raw `Close`

`IndicatorSet`'s own doc comment states this directly: an indicator computed from raw `Close` would
carry a false spike at every split or bonus issue, since `AdjClose` is what removes that
discontinuity (§4.4). This is not a separate decision from the write-amplification trade above —
it is the reason indicators cannot simply be a SQL computed column over `PriceBars.Close`, since
`AdjClose` itself already depends on corporate-action data that changes independently of the price
series it adjusts.

## Consequences

- A screen or backtest never pays indicator-computation cost at request time; the cost is paid once,
  incrementally, at ingest. This is what keeps `ScreeningEngine`'s query a flat parameterised `SELECT`
  (`src/MarketEye.Infrastructure/Screening/ScreeningEngine.cs`) rather than a stored procedure doing
  numerical work per row.
- Backfilling a large historical range recomputes indicators for the full affected date range in one
  pass (see `BackfillService`'s two-pass design, §10 Phase 1 exit notes) rather than day-by-day, for
  the same O(days²) reason `SnapshotLifecycle.SealHistoricalSnapshotsAsync`'s own doc comment warns
  against for snapshot sealing.
- Adding a new indicator later (say, Bollinger Bands) means a new `IndicatorSet` column, a new
  `TechnicalIndicators` function, and a recompute pass over existing history — an additive schema
  change, not a rearchitecture, since the "compute at ingest, read as a column" shape already exists.
- `IndicatorSet`'s doc comment's dangling reference to "`docs/adr/0003`'s sibling ADR" should be read
  as this ADR going forward; a future edit to that file may correct the citation in passing.
