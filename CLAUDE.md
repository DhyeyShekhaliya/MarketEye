# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Current state

**No code exists yet.** Phase 0 has not started. The repository contains only planning documents and
is not yet a git repository.

- `PLAN.md` — the source of truth. Read it before proposing any design work.
- `PLAN.md.v1.bak` — superseded first draft, kept for diffing only. Do not treat as current.
- `.sf/` — stray Salesforce CLI org cache, unrelated to this project. Ignore.

When scaffolding, follow the solution layout in `PLAN.md` §2 and the phase order in §10. Phase 1
(ingestion + screener) contains no AI; do not pull Phase 2 work forward because it is more interesting.

## Commands

None exist yet. Once Phase 0 scaffolds the solution these apply (the test framework has not been
chosen, so adjust filter syntax to match):

```bash
dotnet build MarketEye.sln
dotnet test                                     # all tests
dotnet test tests/MarketEye.UnitTests           # one project
dotnet test --filter "FullyQualifiedName~Rsi"   # one test / class
dotnet run --project src/MarketEye.Api
docker compose up -d                            # SQL Server for local dev
dotnet ef migrations add <Name> --project src/MarketEye.Infrastructure --startup-project src/MarketEye.Api
```

`MarketEye.IntegrationTests` and `MarketEye.BacktestTests` need a running SQL Server (Testcontainers).
`MarketEye.AiEvals` is a CI gate at ≥85% and calls a live LLM — it is not part of the default local loop.

## Architecture invariants

These are the rules that make the design work. Violating any of them is a correctness bug, not a
style preference. Each is argued in `PLAN.md`; the section is cited.

**AI is at the edge (§2, §5.1).** The model emits *concept names* plus explicit numeric filters only
where the user supplied the number themselves. It never emits SQL, and never invents a financial
threshold — "cheap" resolves from the `MetricConcepts` table, not from the model. A concept the model
returns that is not in the table is a hard validation failure, never a fallback. Everything downstream
of the validator is deterministic.

**Point-in-time reads need both conditions (§4.1).** `FOR SYSTEM_TIME AS OF @date` handles
restatements; `ReportedDate <= @date` handles reporting lag. One without the other is lookahead bias.

**Queries read sealed snapshots, never live tables (§4.5).** Ingestion writes then seals a
`DataSnapshot`. Screens and backtests resolve against a `SnapshotId`. This is what makes results
reproducible and makes result-cache invalidation free.

**`Close` and `AdjClose` are not interchangeable (§4.4).** Execute trades at raw `Close`/`Open`;
compute returns from `AdjClose`. Conflating them makes every multi-year backtest systematically wrong.

**EF Core for CRUD/config, Dapper + `SqlBulkCopy` for the ingest path (§3).** Never bulk-insert price
bars through EF.

**Indicators are computed at ingest and stored, never at query time (§4.3).** Screening must stay a
flat indexable `WHERE`.

**Backtest execution is T+1 (§7).** The screen uses data as of T, so trades fill at T+1's open.
Filling at T's close is lookahead. Delisted securities stay in the universe and exit at their last
price (or zero for bankruptcy) — removing them is survivorship bias.

**`ScreenCriteria` is a tree, implemented flat (§6).** Model `Group`/`Comparison` as a tree from day
one — types, JSON schema, validator all walk a tree — but v1 compiles only a single flat `AND`.
`OR`/`NOT` is Phase 3+. Do not flatten the type to save effort now; that creates a rewrite later.
(Deliberately still out of scope after Phase 3 shipped — see `PLAN.md` §10's Phase 3 exit note.)

**A DB-touching backtest/screening component lives in `MarketEye.Infrastructure`, never
`MarketEye.Application` (§2).** `MarketEye.Application`'s `ProjectReference`s must equal exactly
`["MarketEye.Domain"]` — enforced by `tests/MarketEye.UnitTests/Architecture/RepositoryLayoutTests.cs`.
`BacktestEngine`, `BacktestPriceRepository`, and `FillExecutor` all need EF/Dapper/SQL and so live
in `src/MarketEye.Infrastructure/Backtesting/`, mirroring `ScreeningEngine`'s placement. Pure
backtest math with no database dependency (`RebalanceScheduler`, `PortfolioWeighting`,
`TradeListBuilder`, `TransactionCostModel`, `MissingPriceCarryForward`, `BacktestMetricsCalculator`)
lives in `src/MarketEye.Application/Backtesting/`, mirroring `TechnicalIndicators.cs`/
`CriteriaCompiler.cs`. `BacktestDefinition` itself (a value type, zero deps) lives in
`src/MarketEye.Domain/Backtesting/`, mirroring `ScreenCriteria`.

## Testing the backtester

The backtester is the component most able to be confidently wrong, so it has its own rules (§8):

- The **synthetic market** (§8.1) is the primary correctness test. Real market data has no ground
  truth. `tests/MarketEye.BacktestTests/SyntheticMarket/SyntheticMarketEngineTests.cs` (Docker-
  gated, `MARKETEYE_INTEGRATION=1`) seeds 3 securities, a split, a dividend, and a bankruptcy
  delisting into a real SQL Server and asserts `BacktestEngine`'s output against hand-derived
  values. Building it caught a real bug: positions are marked at raw `Close`, so a split/bonus
  needs its held share count adjusted in step or the portfolio shows a fake value drop — fixed by
  `BacktestPriceRepository.GetShareAdjustingActionsAsync`, pinned by
  `A_stock_split_does_not_create_a_fake_value_drop`.
- Bias guards must **throw in the repository layer**, not be enforced by convention or code review.
  All six §8.2 guards are implemented in `PointInTimeGuard`
  (`src/MarketEye.Infrastructure/Screening/PointInTimeGuard.cs`), including
  `RequireNotCircuitLocked` for §7 revision 3's circuit-limit rule — a locked bar cannot be filled,
  so the fill logic (`FillExecutor`) skips it and searches forward rather than trading through it.
- Known-bad strategies must backtest badly. If everything you test looks profitable, that is a bug.
  `tests/MarketEye.BacktestTests/SyntheticMarket/KnownBadStrategyTests.cs` (same Docker gate) covers
  three scenarios: negative-earnings + high-leverage + high-price loses money; buying the worst
  momentum (oversold names that keep falling, not bouncing) loses money; and an indiscriminate,
  no-edge basket lands at exactly its hand-computed blended average rather than an inflated one.
- Indicator math is tested against published reference values, never against its own output.
  `BacktestMetricsCalculatorTests` (`tests/MarketEye.UnitTests/Backtesting/`) applies the same rigor
  to CAGR/max-drawdown/Sharpe/Sortino — expected values are either exact closed-form (chosen
  deliberately so `sqrt` terms cancel) or cross-checked with an independent script, asserted with
  `BeApproximately`, never against the function's own prior output.

**Two real bugs were found by actually running a live backtest, not by inspection — both fixed.**
(1) `BackfillService` used to seal one `DataSnapshot` for its whole range rather than one per day,
so `SnapshotLifecycle.LatestSealedAsync` could only resolve the single date it sealed. Fixed by
`SnapshotLifecycle.SealHistoricalSnapshotsAsync` (seals per day going forward; also exposed as
`POST /api/ingest/seal-historical-snapshots` to repair an already-backfilled range with no
re-fetching). (2) A real multi-year, multi-rebalance backtest then showed `CagrNet` HIGHER than
`CagrGross` — impossible with non-negative costs, and invisible to every §8.1/§8.3 test because
those fixtures are deliberately single-rebalance. Cause: gross and net used to run as two
*independent* simulations, and weight-based rebalancing resizes trades against each simulation's
own current value, so the two ran genuinely different trading paths after the first rebalance, not
the same path with a cash offset. Fixed by running the simulation once and deriving
`grossNav = netNav + cumulativeCostsAtThatPoint` on the SAME path — `CagrNet <= CagrGross` now
holds by construction. Full account: `docs/adr/0009`. **Takeaway for future rebalance-loop
changes:** the synthetic fixtures cannot catch a bug that only manifests across multiple
rebalances — a real multi-year `/api/backtest` run is part of validating any such change, not
optional polish.

## Decisions already made — do not re-propose

Argued and rejected in `PLAN.md` §14 and §3. Re-raising these wastes a review cycle:

Redis · message queues · microservices · Kubernetes · vector DB · RAG · sentiment analysis · ML price
prediction · options · portfolio optimisation · brokerage integration · `IBenchmarkProvider` (one
benchmark exists; a config string covers it) · a separate explainability panel (folded into the
interpretation panel, §5.3) · building the AI phase before the data pipeline.

Infrastructure gets added when something concrete breaks without it, and the ADR must name what broke.

## Non-negotiables

- Performance numbers in docs must be **measured** under the §9 benchmark definition, never estimated.
  If it has not been benchmarked, state it as a hypothesis.
- Every results view and every system prompt carries "educational purposes only, not investment advice".
- Backtest output always displays its assumptions (costs, slippage, rebalance, weighting, execution
  price) next to the equity curve, and reports gross and net side by side.
