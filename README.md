# MarketEye

> **MarketEye converts natural-language stock-screening ideas into a validated screening DSL, executes
> them against point-in-time market data, and backtests the same strategy without lookahead or
> survivorship bias.**

```
"profitable small caps with low debt and RSI below 40"
                    ↓
       ┌──────────────────────────┐
       │ Market cap  < $2B        │
       │ Net income  > $0         │
       │ Debt/Equity < 0.5        │
       │ RSI(14)     < 40         │
       └──────────────────────────┘
                    ↓
              37 securities
                    ↓
               [ Backtest ]
                    ↓
       CAGR (net)   14.2%     Turnover  38%/yr
       Max DD      -21.7%     Costs      0.6%/yr
       Sharpe        1.03     vs SPY TR  +2.1%
```

> The figures above are **illustrative**, showing the shape of the output. They are not
> measured results and no strategy has been run. Measured §9 benchmark numbers replace this
> block once Phase 1 is complete — see *Benchmarks* below.

**Educational purposes only. Not investment advice.**

## The idea

The model sits at the edge of the system, not the middle. It reads prose and emits *concept names*
plus any number the user stated themselves. It never emits SQL, and it never invents a financial
threshold — "cheap" resolves from a Strategy Vocabulary table you can inspect and edit at
`/vocabulary`, not from the model's opinion. A concept the model returns that is not in that table
is a hard validation failure, never a silent fallback. (Two tables sit behind this, one system-owned
and one yours to edit — `docs/adr/0007` argues why.)

Everything downstream of the validator is deterministic. The model can be swapped, removed, or fail
entirely and the screening and backtesting engines still work.

## Status

**Phase 2 complete. Phase 3 done and verified against real data.** The prompt → criteria → results
flow described above genuinely works: type a strategy in plain English, confirm the interpreted
criteria against your own Strategy Vocabulary, and run it. All three of §10's Phase 2 exit criteria
are met, including the `MarketEye.AiEvals` ≥85% gate — measured at 100.0% on both axes (below).
Backtesting runs end to end — `/backtest` builds a `BacktestDefinition` from a saved strategy, runs
the full §7 rebalance loop (T+1 execution, India-calibrated costs and slippage, circuit-lock and
delisting handling, dividend accrual) and renders the equity curve, assumptions panel, and
gross/net metrics. §8.1's synthetic-market suite and §8.3's known-bad-strategy suite both pass
against a real SQL Server, and caught a real split-adjustment bug during development (below). The
backfill-snapshot gap that used to block real historical backtests is fixed and the local 5-year
dataset is fully sealed — a real 3-year, quarterly-rebalanced backtest against actual NSE data ran
end to end (below), and that same live run caught a second real bug in gross-vs-net reporting,
also now fixed (below). The CAGR/Sharpe/drawdown figures in the block above are still illustrative
placeholder numbers, not results from a specific published run — but the engine producing real
numbers like them is no longer hypothetical.

| Phase | State |
|---|---|
| 0 — Foundation | Complete |
| 1 — Data pipeline + screener | Complete, with three qualified exit criteria (below) |
| 2 — Intent translation | Complete, with three exit criteria measured and met (below) |
| 3 — Backtesting | Complete — §10 exit criteria met and verified against a real multi-year backtest (below) |
| 4 — Polish | Not started |

`PLAN.md` §10 builds the foundation before the flashy part on purpose, because the foundation is
what makes the rest credible — Phase 2 is where that argument gets tested for real.

### What Phase 2 delivers

- **A prompt turns into criteria, never SQL.** The model (§5.1) emits *concept names* — `cheap`,
  `profitable`, `small_cap` — plus a numeric filter only where you stated the number yourself.
  "Cheap" resolving to `P/E < 25 AND P/B < 3` comes from a table you can read and edit, not from
  the model's opinion, and a concept the model names that isn't in that table is a hard failure,
  never a substitution
- **Strategy Vocabulary** (`/vocabulary`) — 18 seeded concepts, India-calibrated, each editable or
  disableable without a deploy. Two tables under the hood, `MetricConcepts` (system, sealed) and
  `StrategyConcepts` (yours to edit) — `docs/adr/0007` argues why they're split rather than one
- **Interpretation panel** (`/screen`) — confirm-before-run: every resolved concept, its
  definition, and any place your own number overrode the vocabulary's default, rendered before
  anything executes. Nothing runs from an unconfirmed parse
- **Fails closed, asks rather than guesses.** An unknown or disabled concept is rejected, never a
  nearest match. A vague prompt ("good stocks") returns a clarifying question instead of a screen
  over the whole universe
- **Saved strategies** (`/strategies`, `/api/strategies`) — stores the resolved criteria, not the
  prompt, so a saved strategy reproduces exactly even if the vocabulary changes later
- **Two-level caching and rate limiting** (§5.4, §5.5) — 10 parses/min and 100/day per caller,
  repeated prompts and repeated screens both served from cache, with a database-backed daily call
  cap as the real protection against burning through a finite LLM credit allotment
- **Runs with no AI configured at all.** No `Ai:ApiKey` set means natural-language parsing falls
  back to a deterministic keyword parser — the manual filter builder and the whole rest of the app
  are unaffected, proving §2's claim that the model can be removed and the system below still works

### What Phase 3 delivers so far

- **The full §7 rebalance loop** (`BacktestEngine`) — point-in-time universe resolution (reusing
  the screening engine's existing delisted-inclusive join, not a second implementation), T+1
  execution, equal-weight target allocation, dividend accrual, and delisting exits at last price
  (or zero for bankruptcy)
- **Circuit-lock handling** (§7 revision 3) — a locked bar cannot be filled; the engine skips it
  and searches forward within the same 5-day window missing prices use, dropping and logging the
  trade if nothing fillable turns up. `PointInTimeGuard.RequireNotCircuitLocked` closes the last
  gap in §8.2's guard table — all six guards now exist and throw rather than silently correct
  (`docs/adr/0008`)
- **Gross vs. net, computed honestly, not approximated.** Costs compound into share counts rather
  than being a simple end-of-period subtraction, so the engine runs the whole simulation twice —
  once at the configured India-calibrated 23bps + 5bps, once at zero — to report `CagrGross` and
  `CagrNet` side by side, exactly as §7 requires (`docs/adr/0009`)
- **CAGR, max drawdown, Sharpe, Sortino, win rate, and annualised turnover** (`BacktestMetricsCalculator`),
  hand-rolled `decimal`/`double` arithmetic with no new dependency, consistent with the existing
  indicator math's style
- **NIFTY 50 total-return benchmark comparison**, sourced from a manually-loaded CSV rather than a
  scraper (`NiftyTotalReturnLoader`, `docs/adr/0010`) — a config string, not an `IBenchmarkProvider`
  interface, per §14's already-recorded decision. Missing benchmark data degrades to "no data"
  rather than failing the run
- **`/backtest`** — pick a saved strategy and a date range, run it, and see the equity curve next
  to an assumptions panel sourced directly from what actually ran, never a hand-typed copy

### Verified — §8.1 and §8.3, both against a real SQL Server

The §8.1 synthetic-market suite (`MarketEye.BacktestTests/SyntheticMarket`, Docker-gated) — three
securities, a 2-for-1 split, a dividend, and a bankruptcy delisting, hand-computed against a real
SQL Server. Building it caught a genuine bug before any real backtest could hit it: the engine
marks positions at raw `Close` (§4.4, §7) as it should, but a split or bonus issue changes both the
share count and the price together — without adjusting the held share count on the action's
effective date, a split would have shown as an artificial ~50% value drop that never happened
economically. `BacktestPriceRepository.GetShareAdjustingActionsAsync` now applies Split/Bonus/
Rights `AdjustmentFactor`s to held positions during the day-walk, and
`SyntheticMarketEngineTests.A_stock_split_does_not_create_a_fake_value_drop` pins the fix. This is
exactly the failure mode §8.1 exists to catch, caught by writing the suite rather than in production.

The §8.3 known-bad-strategy suite (`KnownBadStrategyTests`, same folder) runs three scenarios: a
negative-earnings + high-leverage + high-price screen loses money; buying the worst momentum
(deeply oversold names that keep falling, not bouncing) loses money; and an indiscriminate,
no-edge four-security basket (two winners, two losers, equal-weighted) lands at exactly its
hand-computed blended average — 204,000 from 200,000, neither inflated nor deflated. "If
everything you test looks profitable, that's a bug" (§8.3) now has a suite that would catch it.

Both suites, plus the pre-existing bias guards, run green: `MARKETEYE_INTEGRATION=1 dotnet test
tests/MarketEye.BacktestTests` passes all 20.

### Two real bugs, found by actually running the thing — both fixed

**1. The backfill-snapshot gap is fixed.** The historical backfill (§10 Phase 1) used to seal
exactly one `DataSnapshot` for its whole date range rather than one per day, so
`SnapshotLifecycle.LatestSealedAsync` — which `BacktestEngine` correctly reuses from the screening
path — could only resolve the single date that backfill sealed. `/api/backtest` against an earlier
historical window degraded gracefully (warned, skipped every rebalance, flat curve, no crash) but
couldn't exercise a real multi-year strategy against the ~2.5M bars already sitting in `PriceBars`.
Fixed with `SnapshotLifecycle.SealHistoricalSnapshotsAsync` — `BackfillService` now seals a
snapshot per ingested day going forward, and the same method is exposed as
`POST /api/ingest/seal-historical-snapshots` to repair an already-backfilled range without
re-fetching anything. Run once locally over 2021-09-01–2026-09-01: **1,169 snapshots sealed in ~7
seconds.**

**2. A live multi-year backtest then caught a second bug: gross could come back higher than net.**
Running `/api/backtest` for real — a 10-position, quarterly-rebalanced NSE screen over
2022-01-03–2024-12-31 — surfaced `CagrNet` (7.5%) higher than `CagrGross` (3.5%), which is
impossible with non-negative costs. The cause: the original design ran gross and net as two
independent simulations, but weight-based rebalancing resizes every trade against the *current*
portfolio value, so the lower net-of-costs value produced a genuinely different share count than
the zero-cost run from the second rebalance onward — the two paths diverged, not just by a cash
offset. All 20 `MarketEye.BacktestTests` stayed green throughout, because the §8.1/§8.3 fixtures
are deliberately single-rebalance and never had a second rebalance for the paths to diverge across.
Fixed by running the simulation once and deriving gross as `netNav + cumulativeCostsPaid` at every
point on the same trading path — `CagrNet <= CagrGross` now holds by construction. Re-verified live
after the fix: `CagrGross 5.57% >= CagrNet 5.11%` on the same 3-year backtest. Full account in
`docs/adr/0009`.

### What Phase 1 actually delivered

- **Prices and universe** from the NSE bhavcopy archive — survivorship-free by construction, since
  a company that delisted in 2022 still appears in every file up to its last trading day
- **Indicators** (SMA, EMA, RSI, MACD, ATR, realised volatility) computed at ingest and tested
  against published reference values, not against their own output
- **Corporate actions** — splits, bonus issues, rights issues and dividends, each with its own
  adjustment convention, plus a reconciliation that checks stored factors against the price step
  the market actually made
- **Fundamentals** in a SQL Server temporal table, with derived valuation ratios
- **`ScreenCriteria`** — a tree-shaped DSL with a fail-closed validator and a compiler that emits
  parameterised SQL, so injection is structurally impossible rather than filtered
- **Bias guards** that throw in the repository layer rather than relying on convention

### Qualified exit criteria — stated, not hidden

Three of `PLAN.md` §10's Phase 1 criteria are not fully met, and pretending otherwise would
undermine the point of the rest:

| Criterion | Status |
|---|---|
| Nightly job unattended for a week | Ran locally, ~250 sessions, no intervention. **Not met on Azure** — the deployed cron fails, most likely NSE refusing a datacentre IP |
| §9 performance benchmarks | **Dropped by decision.** No valid measurement surface exists — see Benchmarks below |
| Splits/dividends verified across 20 securities | Method built and proven; it found four real defects. But the deployed one-year window contains too few splits and bonuses to reach 20 securities. Satisfiable against the local five-year dataset |

### Known limitations

Each of these is a property of the data source rather than a bug, and each is argued in
`docs/adr/0005`:

- **Reporting dates are estimated.** The fundamentals provider supplies no filing date, so it is
  derived from SEBI deadlines and deliberately errs late. Fundamentals screening is therefore
  point-in-time correct to within the filing window, not to the day.
- **Fundamentals are annual only**, so they can be up to ~15 months stale. A screen for
  "profitable" answers *was profitable in the last reported financial year*.
- **Roughly half the securities carry synthetic identifiers** where no ISIN was recoverable from
  the archive. Ticker-change reconciliation cannot work for those.

### Phase 2 — exit criteria, measured

All three of `PLAN.md` §10's Phase 2 criteria are met:

| Criterion | Status |
|---|---|
| Unknown concepts fail closed | **Met** — verified by unit tests and by construction: the model's output schema only enumerates concepts that currently exist in the vocabulary |
| A failed parse asks a question | **Met** — a vague or ambiguous prompt returns a clarifying question, never a guessed screen |
| `MarketEye.AiEvals` ≥85% eval, gated in CI | **Met, measured 2026-09-02.** All 50 cases against NVIDIA NIM (`openai/gpt-oss-20b`): **100.0%** concept-set match, **100.0%** explicit-filter match. `.github/workflows/ai-evals.yml` gates this weekly and on manual dispatch |

That 100% came from correcting the eval, not from favourable recordings — see `PLAN.md` §10's
Phase 2 exit-status note for the full account. Two findings from that pass are worth knowing before
trusting the model's judgment on an adversarial prompt: a "list every concept" injection attempt got
the model to actually comply 3 of 4 repeat calls (a more vaguely-worded attempt refused cleanly
every time), and one plain concept-only prompt split roughly 50/50 between resolving correctly and
hedging with an unneeded clarification. Neither reaches an invented threshold — `IntentResolver`
only ever resolves a concept to its human-vetted definition — but §5.1's guarantee is enforced by
the system, not reliably by the model's own judgment, and that distinction is the whole reason the
guarantee is where it is.

Also **the DSL cannot express field-to-field comparisons** (`Close > Sma200`), so a concept like
"uptrend" is not in the seeded vocabulary. See `PLAN.md` §14 and `docs/adr/0007`.

## Getting started

Requires the .NET 10 SDK and a container runtime. See `DEPENDENCIES.md` for exact versions and
machine setup.

```bash
cp .env.example .env          # then change the password
docker compose up -d          # SQL Server 2022, Developer Edition
dotnet build MarketEye.sln
dotnet run --project src/MarketEye.Api
```

The API applies EF migrations on startup **in Development only**, seeds both vocabularies on every
start, and exposes `/health`.

**No API key is required to run the app.** Natural-language parsing falls back to a deterministic
keyword parser when `Ai:ApiKey` is unset — the manual filter builder, the Strategy Vocabulary, and
saved strategies all work with no key at all. To use the real model (NVIDIA NIM, free signup
credits — see `PLAN.md` §5.4):

```bash
dotnet user-secrets set "Ai:ApiKey" "nvapi-..." --project src/MarketEye.Api
```

Then, separately, `dotnet run --project src/MarketEye.Web` and open `http://localhost:5015/screen`.

```bash
curl localhost:5199/health              # Healthy
dotnet test tests/MarketEye.UnitTests   # 263 tests, no Docker required
```

Ingestion and screening:

```bash
# One trading day from the NSE bhavcopy
curl -X POST -H "X-Ingest-Secret: $SECRET" "localhost:5199/api/ingest/run?date=2026-09-01"

# Historical backfill from a cloned archive mirror (see docs/backfill-runbook.md)
curl -X POST -H "X-Ingest-Secret: $SECRET" "localhost:5199/api/ingest/backfill?from=2021-09-01&to=2026-09-01"

# Fundamentals and corporate actions (rate limited to 500 calls/day)
curl -X POST -H "X-Ingest-Secret: $SECRET" "localhost:5199/api/ingest/fundamentals?max=25"

# Verify adjustment factors against the market's own repricing
curl "localhost:5199/api/reconcile/corporate-actions?securities=25"
```

Natural-language screening, end to end — works with or without an `Ai:ApiKey` (the keyword fallback
answers this one without a model call):

```bash
curl -X POST localhost:5199/api/parse -H 'Content-Type: application/json' \
  -d '{"prompt":"cheap profitable small caps that arent overbought"}'
# → { "criteria": {...}, "concepts": [...], "explicitFilters": [], "disclaimer": "..." }

# Confirm and run the returned "criteria" object:
curl -X POST localhost:5199/api/screen -H 'Content-Type: application/json' \
  -d '{"criteria": <the "criteria" object from the response above>}'
```

Or in the browser: `/screen` for the interpretation panel and the manual filter builder,
`/vocabulary` to read or edit what a word like "cheap" means, `/strategies` to save, re-run, and
delete named screens, `/backtest` to run a saved strategy through the full §7 rebalance loop and
see its equity curve, assumptions, and gross/net metrics.

```bash
dotnet test tests/MarketEye.BacktestTests             # 13 tests, no Docker required
MARKETEYE_INTEGRATION=1 dotnet test tests/MarketEye.BacktestTests    # 20 tests (+4 synthetic-market, +3 known-bad), needs Docker
MARKETEYE_INTEGRATION=1 dotnet test tests/MarketEye.IntegrationTests   # 25 tests, needs Docker running
```

`MarketEye.IntegrationTests`' container-backed tests are skipped unless `MARKETEYE_INTEGRATION=1`
is set — a bare `dotnet test` never needs Docker. When set, they start their own throwaway SQL
Server containers through Testcontainers (roughly 10–12s to first query, emulated on Apple
Silicon — see `DEPENDENCIES.md`) and cover the vocabulary, screening pipeline, saved strategies,
and `IntentTranslationService`'s cache/budget behaviour against a real database.

`MarketEye.BacktestTests` follows the same pattern for its §8.1/§8.3 suites: 13 bias-guard tests
run with no Docker; `MARKETEYE_INTEGRATION=1` additionally runs 4 §8.1 synthetic-market tests
(three securities, a hand-computed split/dividend/bankruptcy delisting, asserting `BacktestEngine`'s
output exactly — including that a stock split does not create a fake value drop, the specific
regression this suite caught) and 3 §8.3 known-bad-strategy tests (bad fundamentals lose money,
worst momentum loses money, a no-edge basket lands at exactly its blended average) against a real
SQL Server.

```bash
dotnet test tests/MarketEye.AiEvals                    # 4 tests, offline tier, no key required
```

`MarketEye.AiEvals` (§5.6's ≥85% eval gate) replays 50 recorded model responses through the real
parser, resolver and validator — no key, no network, and it runs in the default loop above. The
live tier that produces those recordings is gated behind `MARKETEYE_AI_EVALS=1` plus `AI_API_KEY`
and does not run here; see `.github/workflows/ai-evals.yml`, which runs it weekly against the real
provider and asserts the ≥85% gate (currently measuring 100.0% on both axes).

## Correctness

These properties are enforced structurally rather than by convention, because each one silently
invalidates every result downstream if it slips.

**Point-in-time reads need both conditions.** `FOR SYSTEM_TIME AS OF @date` handles restatements;
`ReportedDate <= @date` handles reporting lag. Either alone is lookahead bias. Both are covered by
integration tests that apply the real migrations to a real SQL Server and assert that a filing is
invisible before its reporting date.

**`Close` and `AdjClose` are never interchangeable.** Trades execute at raw `Close`/`Open`; returns
compute from `AdjClose`. Conflating them makes every multi-year backtest systematically wrong.

**Delisted securities stay in the universe.** They exit at their last traded price, or at zero for
bankruptcy. Removing them is survivorship bias, so `Security` rows are never deleted — and a guard
throws if an assembled universe omits a security that was trading on the as-of date.

**Ratios refuse rather than mislead.** A loss-making company gets no P/E rather than a negative
one. A negative multiple sorts as "cheapest" in an ascending screen, so the worst businesses would
top a value screen and a deliberately bad strategy would accidentally look good.

**Adjustment factors are verified against the market.** On an ex-date the price steps by the
action's economics, which implies a factor that can be compared against the one parsed from the
provider's text. Disagreement means one of them is wrong — usually the text, because a bonus quoted
"1:1" and a split quoted "2-for-1" are identical economics with inverted numbers.

**Unparseable actions are left visible.** When a ratio cannot be extracted confidently, no
adjustment is applied. That leaves a real step in the price series, which someone will notice — far
better than a smooth series computed from a guessed factor.

## Benchmarks

**Outstanding.** No performance numbers are published, by design.

`PLAN.md` §9 defines the target precisely — p95 under 500ms for a 10-comparison screen over a
500-security universe against a sealed snapshot of five years of daily bars. The dataset to run it
against exists: 3,481 securities and ~2.5M bars are ingested.

What does not exist is a valid place to measure. The local SQL Server runs emulated on Apple
Silicon (no arm64 image); App Service F1 is shared infrastructure with a 60 CPU-minute daily cap
and cold starts; and the free Azure SQL tier auto-pauses. Numbers from any of those would describe
the hardware, not the system.

This project's rule is that performance claims are measured or absent — never estimated. So this
section stays empty until there is a surface worth measuring on. See `docs/adr/0006`.

## Layout

```
src/
  MarketEye.Domain/           entities, ScreenCriteria DSL, BacktestDefinition — zero dependencies
  MarketEye.Application/      criteria compiler, indicator math, pure backtest math (Backtesting/)
  MarketEye.Infrastructure/   EF Core, Dapper, SqlBulkCopy, provider clients, screening engine,
                               backtest engine (Backtesting/) — anything that touches the database
  MarketEye.Ai/                LLM client, concept resolution, parse cache, rate limiter
  MarketEye.Ingestion/        scheduled jobs, corporate actions, snapshot sealing
  MarketEye.Api/              ASP.NET Core Web API
  MarketEye.Web/              Blazor Server UI
tests/
  MarketEye.UnitTests/        indicator math, compiler, validator, architecture guards
  MarketEye.IntegrationTests/ Testcontainers + real SQL Server
  MarketEye.BacktestTests/    bias guards + §8.1/§8.3 suites (13 tests + 7 Docker-gated)
  MarketEye.AiEvals/          prompt → expected-criteria suite, CI gate
```

Design rationale lives in `PLAN.md`; decisions with trade-offs worth revisiting are in `docs/adr/`.

---

**Educational purposes only. Not investment advice.** MarketEye screens and backtests historical data.
Past results do not predict future returns.
