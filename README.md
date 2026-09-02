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

**Phase 2 in progress.** The prompt → criteria → results flow described above genuinely works now:
type a strategy in plain English, confirm the interpreted criteria against your own Strategy
Vocabulary, and run it. Backtesting (Phase 3) has not started, so the CAGR/Sharpe/drawdown figures
in the block above remain illustrative.

| Phase | State |
|---|---|
| 0 — Foundation | Complete |
| 1 — Data pipeline + screener | Complete, with three qualified exit criteria (below) |
| 2 — Intent translation | In progress — screening flow complete; the `MarketEye.AiEvals` ≥85% CI gate is not yet wired up (below) |
| 3 — Backtesting | Not started |
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

### Phase 2 — what's not done yet

One of `PLAN.md` §10's Phase 2 criteria is not met:

| Criterion | Status |
|---|---|
| Unknown concepts fail closed | **Met** — verified by unit tests and by construction: the model's output schema only enumerates concepts that currently exist in the vocabulary |
| A failed parse asks a question | **Met** — a vague or ambiguous prompt returns a clarifying question, never a guessed screen |
| `MarketEye.AiEvals` ≥85% eval, gated in CI | **Not yet.** The two-tier offline/live suite structure exists but the 50 cases and recorded fixtures are not complete, so there is no CI gate yet |

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
dotnet test tests/MarketEye.UnitTests   # 231 tests, no Docker required
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
delete named screens.

```bash
dotnet test tests/MarketEye.BacktestTests             # 11 tests, no Docker required
MARKETEYE_INTEGRATION=1 dotnet test tests/MarketEye.IntegrationTests   # 25 tests, needs Docker running
```

`MarketEye.IntegrationTests`' container-backed tests are skipped unless `MARKETEYE_INTEGRATION=1`
is set — a bare `dotnet test` never needs Docker. When set, they start their own throwaway SQL
Server containers through Testcontainers (roughly 10–12s to first query, emulated on Apple
Silicon — see `DEPENDENCIES.md`) and cover the vocabulary, screening pipeline, saved strategies,
and `IntentTranslationService`'s cache/budget behaviour against a real database.

`MarketEye.AiEvals` (§5.6's ≥85% eval gate) is under active development and is not yet part of a
clean solution-wide `dotnet test MarketEye.sln` run — its two-tier offline/live structure exists,
but the 50 recorded cases it replays are not complete. Run the projects above individually until
that lands.

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
  MarketEye.Application/      criteria compiler, screening engine, backtest engine
  MarketEye.Infrastructure/   EF Core, Dapper, SqlBulkCopy, provider clients
  MarketEye.Ai/               LLM client, concept resolution, parse cache, rate limiter
  MarketEye.Ingestion/        scheduled jobs, corporate actions, snapshot sealing
  MarketEye.Api/              ASP.NET Core Web API
  MarketEye.Web/              Blazor Server UI
tests/
  MarketEye.UnitTests/        indicator math, compiler, validator, architecture guards
  MarketEye.IntegrationTests/ Testcontainers + real SQL Server
  MarketEye.BacktestTests/    synthetic market + bias guards
  MarketEye.AiEvals/          prompt → expected-criteria suite, CI gate
```

Design rationale lives in `PLAN.md`; decisions with trade-offs worth revisiting are in `docs/adr/`.

---

**Educational purposes only. Not investment advice.** MarketEye screens and backtests historical data.
Past results do not predict future returns.
