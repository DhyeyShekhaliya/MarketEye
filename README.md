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

> The figures above are **illustrative**, showing the shape of the output — not measured results,
> and no specific strategy was run to produce them. See *Benchmarks* below for why no performance
> numbers are published, and the `Getting started` section for how to run a real screen or backtest.

**Educational purposes only. Not investment advice.**

## Architecture

The model sits at the edge of the system, not the middle. It reads prose and emits *concept names*
plus any number the user stated themselves. It never emits SQL, and it never invents a financial
threshold — "cheap" resolves from a Strategy Vocabulary table you can inspect and edit at
`/vocabulary`, not from the model's opinion. A concept the model returns that is not in that table
is a hard validation failure, never a silent fallback. Two tables sit behind this — `MetricConcepts`
(system-owned, sealed) and `StrategyConcepts` (user-editable) — `docs/adr/0007` argues why they're
split rather than one.

Everything downstream of the validator is deterministic. The model can be swapped, removed, or fail
entirely and the screening and backtesting engines still work — with `Ai:ApiKey` unset, parsing
falls back to a deterministic keyword parser and the rest of the app is unaffected.

Every screen and backtest resolves against a sealed `DataSnapshot`, never a live table. Ingestion
writes, then seals; a query only ever sees a snapshot that finished successfully. This is what makes
a `ScreenRun`/`BacktestRun` reproducible forever, gives the result cache a free invalidation key (a
new snapshot id is automatically a cache miss for every criteria that ran against the old one), and
means a night that dies partway through leaves an unsealed row nothing ever reads (`docs/adr/0012`).

## Features

### Intent translation and vocabulary

- A prompt turns into concept names, never SQL. The model emits names like `cheap`, `profitable`,
  `small_cap`, plus a numeric filter only where the user stated the number themselves. "Cheap"
  resolving to `P/E < 25 AND P/B < 3` comes from a table anyone can read and edit, not from the
  model's opinion; a concept the model names that isn't in that table is a hard failure, never a
  substitution.
- **Strategy Vocabulary** (`/vocabulary`) — 18 seeded concepts, India-calibrated, each editable or
  disableable without a deploy.
- **Interpretation panel** (`/screen`) — confirm-before-run: every resolved concept, its
  definition, and any place a user-supplied number overrode the vocabulary's default, rendered
  before anything executes. Nothing runs from an unconfirmed parse.
- Fails closed rather than guessing: an unknown or disabled concept is rejected, never a nearest
  match; a vague prompt ("good stocks") returns a clarifying question instead of screening the
  whole universe.
- Two-level caching and rate limiting — 10 parses/min and 100/day per caller, repeated prompts and
  repeated screens both served from cache, with a database-backed daily call cap as protection
  against burning through a finite LLM credit allotment.
- `MarketEye.AiEvals` — a 50-case prompt-to-expected-criteria suite, gated in CI at ≥85% on both
  concept-set match and explicit-filter match (`.github/workflows/ai-evals.yml`, weekly and on
  manual dispatch). One caveat worth knowing before trusting the model's judgment on an adversarial
  prompt: a "list every concept" injection attempt has been observed getting the model to actually
  comply on some repeat calls, not others — `IntentResolver` bounds the blast radius regardless,
  since it only ever resolves a named concept to its human-vetted definition and never reaches an
  invented threshold, but the guarantee is enforced by the system, not reliably by the model.

### Saved strategies and sharing

- **Saved strategies** (`/strategies`, `/api/strategies`) — stores the resolved criteria, not the
  prompt, so a saved strategy reproduces exactly even if the vocabulary changes later.
- **Read-only share links** — a saved strategy can be given an unguessable share token
  (`POST /api/strategies/{name}/share`); anyone holding the resulting `/shared/{token}` link sees
  the interpreted criteria and the strategy's last backtest, rendered read-only. The public route
  has no mutating verb reachable from it at all — no edit, delete, or re-run with different
  criteria — and there is no login system behind any of this; a token is the entire trust model.

### Screening DSL

`ScreenCriteria` is a tree-shaped DSL (`Group`/`Comparison`, PLAN.md §6) with a fail-closed
validator (field whitelist, per-field operator whitelist, per-field ranges, max tree depth, max
comparison count) and a compiler that emits parameterised SQL — the model never emits SQL, so
injection is structurally impossible rather than filtered. The type and validator already walk a
full tree today; the v1 compiler accepts only a flat `AND` (`docs/adr/0013`), so a criteria naming
`OR`/`NOT` fails at compile time with a message naming the unsupported operator, not silently.

### Backtesting

- The full rebalance loop (`BacktestEngine`) — point-in-time universe resolution (reusing the
  screening engine's delisted-inclusive join), T+1 execution, equal-weight target allocation,
  dividend accrual, and delisting exits at last price (or zero for bankruptcy).
- Circuit-lock handling — a locked bar cannot be filled; the engine skips it and searches forward
  within the same 5-day window missing prices use, dropping and logging the trade if nothing
  fillable turns up.
- Split/bonus/rights adjustment factors are applied to held share counts during the day-walk, in
  step with the price they affect — marking positions at raw `Close` without adjusting the held
  share count on a split's effective date would show an artificial value drop that never happened
  economically.
- Gross vs. net costs are computed from a single simulation, not two independent ones: the engine
  runs once at the configured costs and derives the gross curve as `netNav + cumulativeCostsPaid`
  at every point on the same trading path. This guarantees `CagrNet <= CagrGross` by construction,
  since non-negative costs are the only thing separating the two curves at any point.
- `CAGR`, max drawdown, Sharpe, Sortino, win rate, and annualised turnover
  (`BacktestMetricsCalculator`), hand-rolled `decimal`/`double` arithmetic with no new dependency.
- Benchmark comparison against a total-return index series, sourced from a manually-loaded CSV
  rather than a scraper (`NiftyTotalReturnLoader`, `docs/adr/0010`) — a config string
  (`BacktestDefinition.BenchmarkTicker`), not an `IBenchmarkProvider` interface. The loader takes
  the ticker as a parameter, so loading a second benchmark (e.g. NIFTY 500 TR) from the same
  manual-CSV mechanism is a second call with a different ticker, not new code. Missing benchmark
  data degrades to "no data" rather than failing the run; `/backtest`'s benchmark field is a
  dropdown of whatever is actually loaded into `BenchmarkPrices`, falling back to a free-text input
  when nothing has been loaded yet.
- `/backtest` — pick a saved strategy and a date range, run it, and see the equity curve next to an
  assumptions panel sourced directly from what actually ran, never a hand-typed copy.

### Alerts

A scheduled job (`AlertCheckJob`, invoked by a shared-secret-protected endpoint on the same cron
pattern as nightly ingestion) replays every saved strategy against the newest sealed snapshot and
diffs its matched securities against the immediately preceding run. Every entry and exit becomes an
`AlertEvent`, visible on `/alerts`. A strategy's first-ever check writes no events — there is
nothing yet to compare against, so a newly saved strategy's first night is silent rather than a
flood of "everything just entered." The diff itself (`AlertSetDiffer`, `MarketEye.Application`) is
pure set arithmetic, kept separate from the database orchestration around it (`AlertDiffer`,
`MarketEye.Infrastructure`) so it is unit-testable without a database.

### Home page ticker

The homepage shows a live-delayed NIFTY 50 quote and a one-month sparkline, read from Yahoo
Finance's public chart endpoint on page load and on manual refresh — never polled on a timer. This
is isolated to the Web project and to this one page: no screen, backtest, or alert reads from it,
and every correctness guarantee elsewhere in the system still depends only on MarketEye's own
sealed data snapshots and NSE ingestion.

## Data pipeline

- **Prices and universe** from the NSE bhavcopy archive — survivorship-free by construction, since
  a company that delisted still appears in every file up to its last trading day.
- **Indicators** (SMA, EMA, RSI, MACD, ATR, realised volatility) computed at ingest and stored,
  never at query time, and tested against published reference values rather than their own output
  (`docs/adr/0011`).
- **Corporate actions** — splits, bonus issues, rights issues, and dividends, each with its own
  adjustment convention, reconciled against the price step the market actually made on the ex-date.
- **Fundamentals** in a SQL Server temporal table (`FOR SYSTEM_TIME` plus a reporting-date filter,
  so a restatement or reporting lag can never leak into a historical read), with derived valuation
  ratios.
- **Bias guards** (`PointInTimeGuard`) throw in the repository layer rather than relying on
  convention or code review.

## Known limitations

Each of these is a property of the data source or the current deployment, not a design flaw:

- **Reporting dates are estimated.** The fundamentals provider supplies no filing date, so it is
  derived from SEBI deadlines and deliberately errs late. Fundamentals screening is therefore
  point-in-time correct to within the filing window, not to the day.
- **Fundamentals are annual only**, so they can be up to ~15 months stale. A screen for
  "profitable" answers *was profitable in the last reported financial year*.
- **Roughly half the securities carry synthetic identifiers** where no ISIN was recoverable from
  the archive. Ticker-change reconciliation cannot work for those.
- **The DSL cannot express field-to-field comparisons** (`Close > Sma200`), so a concept like
  "uptrend" is not in the seeded vocabulary — see `docs/adr/0013` and `PLAN.md` §14.
- **The Azure-deployed nightly ingestion cron currently fails**, most likely because NSE refuses a
  datacentre IP. It runs unattended and reliably against a local database.
- **The Azure-deployed dataset currently holds about one year of history**; the full five-year
  dataset (3,481 securities, ~2.5M bars) exists in the local database.

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
dotnet test tests/MarketEye.UnitTests   # no Docker required
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

# Nightly alert check -- diffs every saved strategy against the latest sealed snapshot
curl -X POST -H "X-Ingest-Secret: $SECRET" "localhost:5199/api/alerts/check"
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
`/vocabulary` to read or edit what a word like "cheap" means, `/strategies` to save, re-run, share,
and delete named screens, `/backtest` to run a saved strategy through the full rebalance loop and
see its equity curve, assumptions, and gross/net metrics, `/alerts` for entry/exit history per
saved strategy, and `/shared/{token}` for a strategy someone else shared with you.

## Testing

```bash
dotnet test tests/MarketEye.UnitTests                                  # no Docker required
dotnet test tests/MarketEye.BacktestTests                              # no Docker required
MARKETEYE_INTEGRATION=1 dotnet test tests/MarketEye.BacktestTests      # adds the synthetic-market
                                                                        # and known-bad-strategy suites
MARKETEYE_INTEGRATION=1 dotnet test tests/MarketEye.IntegrationTests   # needs Docker running
```

`MarketEye.IntegrationTests`' container-backed tests are skipped unless `MARKETEYE_INTEGRATION=1`
is set — a bare `dotnet test` never needs Docker. When set, they start their own throwaway SQL
Server containers through Testcontainers (roughly 10–12s to first query, emulated on Apple
Silicon — see `DEPENDENCIES.md`) and cover the vocabulary, screening pipeline, saved strategies,
strategy sharing, alert checks, benchmark loading, and `IntentTranslationService`'s cache/budget
behaviour against a real database. On a resource-constrained host, running every test class's own
container in parallel can exhaust available memory and produce spurious SQL Server startup
failures unrelated to the tests themselves — run sequentially if so:

```bash
dotnet exec tests/MarketEye.IntegrationTests/bin/Debug/net10.0/MarketEye.IntegrationTests.dll -parallelMode none
```

`MarketEye.BacktestTests` follows the same pattern: bias-guard tests run with no Docker;
`MARKETEYE_INTEGRATION=1` additionally runs synthetic-market tests (three securities, a
hand-computed split/dividend/bankruptcy delisting, asserting `BacktestEngine`'s output exactly)
and known-bad-strategy tests (bad fundamentals lose money, worst momentum loses money, a no-edge
basket lands at exactly its blended average) against a real SQL Server.

```bash
dotnet test tests/MarketEye.AiEvals   # offline tier, no key required
```

`MarketEye.AiEvals` replays 50 recorded model responses through the real parser, resolver and
validator — no key, no network, and it runs in the default loop above. The live tier that produces
those recordings is gated behind `MARKETEYE_AI_EVALS=1` plus `AI_API_KEY` and does not run here;
see `.github/workflows/ai-evals.yml`, which runs it against the real provider and asserts the
≥85% gate.

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

**Gross-vs-net cost reporting cannot invert.** Both curves are derived from one simulation, so
`CagrNet <= CagrGross` holds by construction rather than by two independent runs happening to agree.

## Benchmarks

**No performance numbers are published.** `PLAN.md` §9 defines the target precisely — p95 under
500ms for a 10-comparison screen over a 500-security universe against a sealed snapshot of five
years of daily bars. The dataset to run it against exists locally: 3,481 securities and ~2.5M bars.

What does not exist is a valid place to measure. The local SQL Server runs emulated on Apple
Silicon (no arm64 image); the deployed App Service F1 tier is shared infrastructure with a
60 CPU-minute daily cap and cold starts; and the free Azure SQL tier auto-pauses. Numbers from any
of those would describe the hardware, not the system. Performance claims here are measured or
absent, never estimated — see `docs/adr/0006`.

## Deployment

Both apps deploy independently to Azure App Service (F1 free tier) via GitHub Actions on push to
`main` — see `docs/azure-deployment-runbook.md` for the API and `docs/azure-deployment-web-runbook.md`
for the Blazor frontend, including the one-time Azure/GitHub setup each requires and the specific
failure modes (SQL firewall, serverless auto-pause, forwarded-headers/HTTPS redirect loops, stale
deployment artifacts) already worked through for this app.

## Layout

```
src/
  MarketEye.Domain/           entities, ScreenCriteria DSL, BacktestDefinition — zero dependencies
  MarketEye.Application/      criteria compiler, indicator math, pure backtest math (Backtesting/),
                               pure alert-diff set arithmetic
  MarketEye.Infrastructure/   EF Core, Dapper, SqlBulkCopy, provider clients, screening engine,
                               backtest engine (Backtesting/) — anything that touches the database
  MarketEye.Ai/                LLM client, concept resolution, parse cache, rate limiter
  MarketEye.Ingestion/        scheduled jobs, corporate actions, snapshot sealing, alert checks
  MarketEye.Api/              ASP.NET Core Web API
  MarketEye.Web/              Blazor Server UI, incl. MarketData/ (homepage ticker client)
tests/
  MarketEye.UnitTests/        indicator math, compiler, validator, architecture guards
  MarketEye.IntegrationTests/ Testcontainers + real SQL Server
  MarketEye.BacktestTests/    bias guards + synthetic-market/known-bad-strategy suites
  MarketEye.AiEvals/          prompt → expected-criteria suite, CI gate
```

Design rationale lives in `PLAN.md`; decisions with trade-offs worth revisiting are in `docs/adr/`.

---

**Educational purposes only. Not investment advice.** MarketEye screens and backtests historical data.
Past results do not predict future returns.
