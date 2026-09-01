# MarketEye — AI-Assisted Stock Screener

> Named **MarketEye** in revision 3, matching the repository. Previously the working name "Sift".
> **Revision 2.** Changes from v1 are summarised in §14. Previous version: `PLAN.md.v1.bak`.

**One line:** A .NET platform that converts natural-language screening ideas into a validated internal
DSL, executes them against point-in-time market data, and backtests the same strategy without
lookahead or survivorship bias.

---

## 1. Scope

### What this is
- A **data pipeline** ingesting **Indian (NSE) equities** fundamentals, prices, and corporate actions
  into SQL Server. Market decided in revision 3 — see `docs/adr/0004`.
- A **screening DSL** (`ScreenCriteria`) — a constrained intermediate representation between human
  intent and SQL.
- An **intent translator** that maps prose onto that DSL. It selects concepts; it does not invent numbers.
- A **backtester** with explicit execution semantics, transaction costs, and point-in-time correctness.

### What this is NOT (keep these written down)
- Not investment advice. Disclaimers on every results view and in every system prompt.
- Not real-time or intraday. Daily bars, refreshed nightly. Deliberate, not a limitation.
- Not connected to a brokerage. No orders, no portfolio sync, no money.
- The AI does not decide, score, rank, or produce financial thresholds. See §5.

### Success criteria
A stranger visits the deployed URL, types *"cheap profitable small caps that aren't overbought"*,
sees the interpreted concepts and their **user-editable definitions**, runs the screen against 5 years
of real data, and views a backtest equity curve **with its cost and execution assumptions displayed
on the same screen**. The README states measured benchmark numbers, not estimates.

---

## 2. Architecture

```
              ┌──────────────────────┐
              │   Market Data APIs   │
              └──────────┬───────────┘
                         ▼
              ┌──────────────────────┐
              │    Ingestion Job     │  validate → normalise →
              │  corporate actions   │  compute indicators →
              │  → DataSnapshot      │  SqlBulkCopy → seal snapshot
              └──────────┬───────────┘
                         ▼
              ┌──────────────────────┐
              │      SQL Server      │  Prices · Fundamentals (temporal)
              │                      │  Indicators · CorporateActions
              │                      │  MetricConcepts · DataSnapshots
              └──────────┬───────────┘
                         │
         ┌───────────────┴───────────────┐
         ▼                               ▼
┌──────────────────┐            ┌──────────────────┐
│ Screening Engine │            │ Backtest Engine  │
└────────┬─────────┘            └────────┬─────────┘
         └───────────────┬───────────────┘
                         ▼
              ┌──────────────────────┐
              │    ScreenCriteria    │   the internal DSL / IR
              └──────────▲───────────┘
                         │
                  ┌──────┴──────┐
                  │  Validator  │   whitelist, ranges, depth limit
                  └──────▲──────┘
                         │
                  ┌──────┴──────┐
                  │     AI      │   prose → concepts + explicit user numbers
                  └─────────────┘
```

**The governing property: AI is at the edge.** Everything load-bearing happens *after* it —
validation, deterministic domain logic, deterministic SQL, reproducible results. The model can be
swapped, removed, or fail entirely and the system below it still works.

### Solution layout

```
MarketEye.sln
├── src/
│   ├── MarketEye.Domain/           entities, ScreenCriteria DSL, BacktestDefinition — zero dependencies
│   ├── MarketEye.Application/      criteria compiler, screening engine, backtest engine
│   ├── MarketEye.Infrastructure/   EF Core, Dapper, SqlBulkCopy, provider clients
│   ├── MarketEye.Ai/               LLM client, concept resolution, parse cache, rate limiter
│   ├── MarketEye.Ingestion/        scheduled jobs, corporate actions, snapshot sealing
│   ├── MarketEye.Api/              ASP.NET Core Web API
│   └── MarketEye.Web/              Blazor Server UI
└── tests/
    ├── MarketEye.UnitTests/          indicator math, compiler, validator
    ├── MarketEye.IntegrationTests/   Testcontainers + real SQL Server
    ├── MarketEye.BacktestTests/      synthetic market + bias guards — see §8
    └── MarketEye.AiEvals/            prompt → expected-criteria suite, CI gate
```

---

## 3. Tech stack (decided)

| Layer | Choice | Why |
|---|---|---|
| Backend | ASP.NET Core Web API (**.NET 10 LTS**) | Amended in revision 3 — was .NET 9. See `docs/adr/0003` |
| Frontend | Blazor Server | Fast to build. Know the tradeoff: a persistent circuit per visitor, which is a real hosting cost at scale |
| Database | **SQL Server** (Developer Edition locally) | Temporal tables + columnstore — see §4 |
| ORM | EF Core for CRUD/config; **Dapper + SqlBulkCopy for the hot path** | EF is too slow for millions of daily bars |
| Jobs | Scheduled GitHub Actions cron calling a protected ingestion endpoint | Amended: App Service F1 has no Always On, so an in-process `BackgroundService` timer never fires. Adds no Azure resource. See `docs/adr/0006` |
| AI | Azure OpenAI / OpenAI with **strict structured outputs** | Small/cheap tier is plenty — extraction, not reasoning |
| Cache | `HybridCache` in-memory, two levels — see §5.5 | **No Redis in v1.** Add only when you can name what it fixed |
| Data source | One provider behind `IMarketDataProvider` (EODHD or FMP) | Alpha Vantage free tier ≈ 25 req/day — unusable for backfill |
| Deploy | Azure App Service **F1 (free)** + Azure SQL **free offer** | Free tiers chosen for budget. Consequences in `docs/adr/0006`, including §9 losing its measurement surface |
| Observability | Application Insights + Serilog | — |

**Do not add** until something concrete breaks: Redis, message queues, microservices, vector DB,
Kubernetes, RAG, sentiment analysis, ML prediction, options, portfolio optimisation.

---

## 4. Data model

### 4.1 Point-in-time correctness (temporal tables)

Fundamentals get restated. Backtesting against today's restated numbers is lookahead bias and
silently invalidates every result.

```sql
CREATE TABLE Fundamentals (
    SecurityId         INT  NOT NULL,
    FiscalPeriodEnd    DATE NOT NULL,
    ReportedDate       DATE NOT NULL,     -- when the market actually learned this
    Revenue            DECIMAL(18,2),
    NetIncome          DECIMAL(18,2),
    TotalDebt          DECIMAL(18,2),
    ShareholdersEquity DECIMAL(18,2),
    ValidFrom DATETIME2 GENERATED ALWAYS AS ROW START,
    ValidTo   DATETIME2 GENERATED ALWAYS AS ROW END,
    PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo),
    CONSTRAINT PK_Fundamentals PRIMARY KEY (SecurityId, FiscalPeriodEnd)
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.FundamentalsHistory));

SELECT * FROM Fundamentals FOR SYSTEM_TIME AS OF @asOfDate WHERE ReportedDate <= @asOfDate;
```

Both conditions are required. `FOR SYSTEM_TIME` handles restatements; `ReportedDate <= @asOfDate`
handles reporting lag. EF Core supports this via `.IsTemporal()` / `.TemporalAsOf()`.

### 4.2 Storage layout — to be benchmarked, not assumed

Apply a clustered columnstore index to `PriceBars` and `Indicators`. Columnstore is *expected* to
improve compression and analytical scan performance for this access pattern.

**Do not quote a speedup figure until §9's benchmark has been run.** Record rowstore vs. columnstore
on the same representative queries and put the measured numbers in the README.

### 4.3 Tables

| Table | Notes |
|---|---|
| `Securities` | Ticker, Name, Exchange, Sector, Industry, **IsActive, DelistedDate, DelistingReason** |
| `PriceBars` | (SecurityId, Date) — OHLCV, **`Close` and `AdjClose` kept separate**. Clustered columnstore |
| `CorporateActions` | SecurityId, EffectiveDate, ActionType (Split / **Bonus** / **Rights** / Dividend / TickerChange / Merger / Delisting), AdjustmentFactor, DividendAmount, NewTicker |

> **Revision 3 — Bonus and Rights are not optional in India (`docs/adr/0004`).** A *bonus issue* is
> split-like but uses the opposite ratio convention (1:1 means one free share per share held, i.e. a
> 2-for-1 split); reading it as a split ratio halves or doubles every historical price. A *rights
> issue* is not a split at all — it dilutes, and needs its own adjustment factor. Ignoring it leaves
> a price discontinuity that reads as a real return. Store a computed `AdjustmentFactor` per action
> rather than a raw `SplitRatio`, so all four price-affecting action types share one code path.
| `Indicators` | (SecurityId, Date) — Sma50, Sma200, Rsi14, Macd, MacdSignal, Atr14, Vol30. Pre-computed at ingest. Clustered columnstore |
| `Fundamentals` | **Temporal.** Raw reported figures |
| `FundamentalRatios` | Derived: PE, PB, PS, ROE, ROIC, DebtToEquity, GrossMargin, FcfYield |
| `MetricConcepts` | The controlled vocabulary — see §5.2 |
| `DataSnapshots` | See §4.5 |
| `Strategies` | UserId, Name, CriteriaJson, CreatedAt |
| `ScreenRuns` | StrategyId, **SnapshotId**, RunAt, ResultCount, DurationMs |
| `BacktestRuns` | DefinitionJson, **SnapshotId**, RunAt, MetricsJson, DurationMs |
| `IngestionRuns` | Source, StartedAt, CompletedAt, Status, RecordsWritten, Error |
| `ParseCache` | PromptHash (unique), NormalizedPrompt, CriteriaJson, Model, CreatedAt |
| `ScreenResultCache` | ResultKeyHash (unique), SnapshotId, SecurityIds, CreatedAt — see §5.5 |
| `Alerts` | Phase 4 |

### 4.4 Price vs. total return — get this right or the backtest is wrong

Over 5–10 years dividends are a large fraction of equity return; a price-return backtest understates
performance systematically, and inconsistently across sectors. Rules:

- Store **raw `Close`** (what actually traded — use for display and execution) and **`AdjClose`**
  (split- and dividend-adjusted — use for return calculation) as separate columns. Never conflate them.
- Splits adjust historical prices *and* share counts. Dividends adjust returns, not prices, unless
  using adjusted series.
- The backtester computes returns from `AdjClose` and executes at raw `Close`/`Open`.
- Ticker changes must not create a second `Security` row. Reconcile on a provider-stable security ID.

### 4.5 `DataSnapshot` — reproducibility

Every screen and backtest resolves against a sealed snapshot, never "whatever is in the table now".

```
DataSnapshots
    SnapshotId      -- monotonic
    AsOfDate
    CreatedAt
    SealedAt        -- null until ingestion completed successfully
    ProviderVersion
    PriceRowCount
    FundamentalRowCount
```

Ingestion writes, then seals. Queries only ever read sealed snapshots. This gives you:

- **Reproducibility** — re-running a stored `ScreenRun` returns identical results, forever.
- **Debuggability** — "why did this return 183 companies yesterday and 177 today?" becomes a diff
  of two snapshots rather than a guess.
- **A clean cache key** — §5.5 depends on it.
- **Atomic failure** — a half-finished nightly job leaves an unsealed snapshot that nothing reads.

---

## 5. AI design

### 5.1 The rule

> **The AI may infer *concepts*. It may not invent *financial thresholds*.**

This is stricter than validating a generated number after the fact, and it is the single most
important design constraint in the project.

"Cheap" does not inherently mean `P/E < 15`. If the model picks that number, the system's answers
become unstable across runs and untestable. So the model emits **concept names**, and explicit
numeric filters **only where the user supplied the number themselves**:

```jsonc
// "cheap profitable small caps that aren't overbought"
{ "concepts": ["cheap", "profitable", "small_cap", "not_overbought"],
  "explicit_filters": [] }

// "profitable small caps with P/E below 12"
{ "concepts": ["profitable", "small_cap"],
  "explicit_filters": [ { "field": "PeRatio", "op": "lt", "value": 12 } ] }
```

The application then resolves concepts to filters from `MetricConcepts`. Any concept the model emits
that does not exist in the table is a hard validation failure, not a fallback.

### 5.2 The vocabulary is a first-class feature

`MetricConcepts` is not prompt scaffolding — it is a **user-editable Strategy Vocabulary** with its
own screen:

| Concept | Definition | Enabled |
|---|---|---|
| Cheap | P/E < 15 AND FCF Yield > 4% | ✓ |
| Profitable | Net Income > 0 | ✓ |
| Small cap | Market cap < $2B | ✓ |
| Quality | ROE > 15% AND D/E < 0.5 | ✓ |
| Oversold | RSI(14) < 30 | ✓ |
| Overbought | RSI(14) > 70 | ✓ |

Schema: `Name`, `Aliases[]`, `Description`, `DefinitionJson` (a `ScreenCriteria` fragment), `IsEnabled`,
`IsSystem`, `OwnerUserId`.

This is what the project actually is:

> **An LLM-powered natural-language interface to a controlled financial DSL.**

### 5.3 Interpretation panel — confirm before run

Never run a screen from an unconfirmed parse. Show:

```
"cheap profitable small caps that aren't overbought"

INTERPRETED STRATEGY                                   Universe: US equities

  Cheap          → P/E < 15 AND FCF Yield > 4%        [edit definition] [×]
  Profitable     → Net income > $0                    [edit definition] [×]
  Small cap      → Market cap < $2B                   [edit definition] [×]
  Not overbought → RSI(14) < 70                       [edit definition] [×]

  Definitions come from your Strategy Vocabulary — not from the AI.   [ Run screen ]
```

Every "edit definition" click writes to `MetricConcepts` and is a free eval case. This panel *is* the
architecture made visible: the model chose four words, you own what all four mean.

### 5.4 Request pipeline

```
prompt → auth → rate limiter → parse cache → LLM (strict schema)
       → schema validation → concept resolution → domain validation
       → ScreenCriteria → result cache → SQL
```

**Rate limits are a Phase 2 requirement, not a risk-register line:** 10 parses/min/user,
100/day/user, enforced before the cache lookup so a hot loop cannot bypass it.

### 5.5 Two-level cache

| Cache | Key | Why |
|---|---|---|
| `ParseCache` | `hash(normalised_prompt)` | Repeat phrasings cost zero tokens |
| `ScreenResultCache` | `hash(criteria + universe + sort + limit) + SnapshotId` | 100 users running the same screen execute one query |

Data changes exactly once per day, so invalidation is trivial: a new `SnapshotId` invalidates every
result-cache entry by construction. No TTL guessing, no stale reads.

### 5.6 Eval suite

`MarketEye.AiEvals` — ~50 `prompt → expected ScreenCriteria` pairs, scored on concept-set match and
explicit-filter match separately. **Runs in CI as a gate at ≥85%.** A failed or low-confidence parse
must degrade to a clarifying question, never to a guessed screen.

---

## 6. The screening DSL

`ScreenCriteria` is a small internal language, not a bag of if-statements. Interview framing:
*"a constrained intermediate representation between natural language and SQL."*

```
ScreenCriteria
    Universe        exchange / index / sector constraints
    Root            FilterNode
    Sort            field + direction
    Limit           int

FilterNode  =  Group { Op: AND | OR | NOT, Children: FilterNode[] }
            |  Comparison { Field, Operator, Value }
```

**Model the tree from day one; implement only `AND` in v1.** The type is a tree, the JSON schema is a
tree, and the validator walks a tree — but the v1 compiler and UI handle a single flat `AND` group.
Adding `OR`/`NOT` later becomes additive rather than a rewrite.

Be honest about the cost: full boolean support means recursive validation, recursive SQL generation,
nested-group UI, and a harder parsing target for the model. That work belongs in Phase 3+, once the
foundation is proven — not in Phase 1.

Validator: field whitelist, per-field operator whitelist, per-field sane ranges, **max tree depth 4**,
max 20 comparisons. Compiles to a parameterised query via LINQ expression trees. The model never
emits SQL, so injection is structurally impossible rather than defended against.

---

## 7. Backtest semantics

**Define this before writing a line of the engine.** Without it, two implementations of the "same"
strategy produce different results and neither is wrong.

```
BacktestDefinition
    Universe               index / exchange / sector at each rebalance
    Criteria               ScreenCriteria
    StartDate, EndDate
    RebalanceFrequency     Monthly | Quarterly | Annual
    WeightingMethod        EqualWeight (v1) | MarketCapWeight
    InitialCapital         decimal
    ExecutionPrice         NextOpen (v1) | NextClose
    TransactionCostBps     default 23   (India; see below)
    SlippageBps            default 5
    MaxPositions           int?
```

> **Revision 3 — the cost defaults are India-calibrated (`docs/adr/0004`).** The original 10bps was
> a US number. Indian delivery trades pay Securities Transaction Tax on **both** legs (~10bps each
> way) plus stamp duty on the buy, exchange charges, SEBI fees and GST — roughly **22-25bps round
> trip** even with a zero-brokerage discount broker. §7 already argues costs are not optional; the
> point is stronger here, because the Indian figure is over 50% higher than the US one.
>
> **Circuit limits (no US equivalent).** Indian equities have daily price bands. A stock locked at
> its upper circuit cannot be bought at that price, and an illiquid one can stay locked for
> consecutive sessions. Filling at T+1's open regardless claims a trade that could not have
> happened — the same class of error as lookahead. The rebalance loop must skip circuit-locked
> fills and carry the intended trade forward, and §8.2 needs a guard for it.

### The rebalance loop — exact order of operations

At each rebalance date **T**:

1. Resolve the universe **as it existed at T** — include securities later delisted, exclude those not
   yet listed. Never use today's index membership.
2. Run the screen with `FOR SYSTEM_TIME AS OF T` and `ReportedDate <= T`.
3. Select target holdings; apply `MaxPositions` by the configured sort.
4. Compute target weights.
5. Compute the trade list as the diff from current holdings.
6. **Execute at T+1's open** (never at T's close — the screen used T's data).
7. Deduct `TransactionCostBps + SlippageBps` on the **traded notional**, not on portfolio value.
8. Hold to the next rebalance; accrue dividends into cash.
9. **Delisting mid-period:** exit at the last available price on the delisting date. If the reason is
   bankruptcy with no final price, mark to zero. This is the difference between an honest and a
   flattering backtest.
10. **Missing price mid-period:** carry the position forward at the last known price for up to 5
    trading days; beyond that, force-exit and log it.
11. Record full portfolio state — holdings, weights, cash, costs paid — to `BacktestRuns`.

### Costs are not optional

A monthly-rebalanced screen can turn over 40%+ per year. At 15bps round-trip that is a meaningful
annual drag, and it falls hardest on exactly the high-turnover strategies that look best without it.
Report **gross and net** side by side.

### Assumptions must be visible in the UI

Print them next to every equity curve. A backtest without displayed assumptions is a marketing claim:

```
Rebalance: Monthly   Weighting: Equal   Execution: Next open
Transaction cost: 23 bps   Slippage: 5 bps   Benchmark: NIFTY 50 (total return)
Universe includes delisted securities.
```

Metrics: CAGR, max drawdown, Sharpe, Sortino, win rate, **annual turnover**, **total costs paid**,
and gross-vs-net CAGR. Benchmark against **NIFTY 50 total return**. Keep the benchmark a config
value — a `TickerSymbol` string, not an `IBenchmarkProvider` interface. Adding NIFTY 500 later is a
row in a table. Note NIFTY publishes *price* and *total-return* indices separately; using the price
index understates the benchmark exactly the way §4.4 warns about for individual securities.

---

## 8. Testing and correctness

The backtester is the component most able to be confidently wrong. Test it accordingly.

### 8.1 Synthetic market — the primary defence

Hand-build a tiny deterministic dataset: ~5 securities, ~24 months, hand-computed prices, one split,
one dividend, one delisting. You know the correct answer by construction, so assert exact values:

```
Expected final equity     Expected holdings at each rebalance
Expected CAGR             Expected turnover
Expected max drawdown     Expected total costs paid
```

Testing a backtester only against real market data gives you no ground truth. This is the fix.

### 8.2 Bias guards — must fail loudly

| Guard | Expectation |
|---|---|
| Any read of `ReportedDate > asOfDate` | Throws. Enforced in the repository layer, not by convention |
| Today's index membership in a historical rebalance | Throws |
| A filter referencing future returns | Rejected by the validator |
| Universe at T excludes securities delisted after T | Assert they ARE included |
| Execution at T's close using T's screen | Throws |

### 8.3 Known-bad strategies

Deliberately poor strategies must backtest poorly — negative earnings + high leverage + high price,
random selection, buy-the-worst-momentum. **If everything you test looks profitable, you have a bug,
not alpha.** This is the single best sanity check in the project.

### 8.4 Indicator math

Unit-test SMA/EMA/RSI/MACD/ATR against published reference values, not against your own output.

---

## 9. Performance benchmarks

The v1 target `<500ms p95` was unqualified and therefore unreproducible. Define it:

> **p95 < 500ms** for a screening query of 10 comparisons over a 500-security universe against a
> sealed snapshot containing 5 years of daily bars, warm cache excluded from the measurement,
> AI parse time excluded, measured server-side over 200 runs.

> **Revision 3 — status: OUTSTANDING. No benchmark number may be published until this is
> resolved (`docs/adr/0006`).**
>
> **The data is not the problem.** The bhavcopy backfill loaded the whole NSE board — 3,481
> securities, ~2.5M bars, 2021-09 to 2026-09 — so a 500-security universe over five years is fully
> available. (An earlier note here claimed the NIFTY 50 universe made §9 unmeasurable. That was
> wrong: NIFTY 50 is the *screening* universe, not the ingested dataset.)
>
> **The measurement surface is the problem.** All three candidates are invalid:
>
> | Surface | Why it fails |
> |---|---|
> | Local Docker | SQL Server runs emulated on Apple Silicon — no arm64 image exists |
> | App Service F1 | Shared infrastructure, 60 CPU-min/day, cold starts; results are scheduling noise |
> | Azure SQL free | Serverless auto-pause and a vCore-second budget make repeated timed runs unrepresentative |
>
> **Therefore the README states benchmarks as outstanding**, and no figure appears anywhere until
> a valid surface exists. The non-negotiables forbid estimates, and a measured number taken on an
> invalid surface is an estimate wearing a lab coat.
>
> **To close this later**, one of: rent a paid Azure SQL tier for a single recorded run and note the
> exact SKU beside the numbers; run against a native x86-64 host; or restate the definition for a
> surface that is actually available and measure honestly against that.

Benchmark suite to record in the README:

```
Universe: 500 securities × 5 years (≈630k bars) × {5, 10, 20} comparisons
Storage:  rowstore vs. clustered columnstore
Measure:  p50 / p95 / p99, cold cache and warm cache, compression ratio
Ingest:   full-universe nightly job wall time, rows/sec via SqlBulkCopy
Backtest: 5-year monthly rebalance, wall time
```

Measured numbers in a README beat any feature list. Estimated numbers are worse than none.

---

## 10. Phases

Each phase ends **deployed, tested, and green in CI** before the next begins.

> **Timeline realism.** The elapsed estimates below assume steady part-time work. End to end this is
> a **3–5 month** project, not a 6-week one. Under-scoping the calendar is how projects get abandoned
> at 70%. If time is constrained, cut Phase 3 or Phase 4 entirely — do not compress Phase 1.

### Phase 0 — Foundation (~1 week)
- [ ] Solution scaffold per §2
- [ ] `docker-compose` with SQL Server; EF migrations on startup in dev
- [ ] GitHub Actions: build → test
- [ ] Health check, Serilog, App Insights
- [ ] `IMarketDataProvider` + fixture-backed stub

**Exit:** `docker compose up` gives a running API on a live DB, green CI.

> **Status: complete except CI.** The scaffold, compose stack, temporal schema slice, health check,
> Serilog, provider stub and test projects are done and verified. GitHub Actions was deliberately
> deferred (revision 3), so "green CI" remains outstanding and should be closed before Phase 1
> ships rather than at the end of it.

### Phase 1 — Data pipeline + screener (~4–6 weeks) ← the part most people never finish
- [ ] **Historical backfill strategy solved first** (see §12) — this blocks everything
- [ ] Provider client: rate limiting, exponential backoff, idempotent re-runs
- [ ] `SqlBulkCopy` ingestion; `IngestionRuns` history with failure capture
- [ ] `CorporateActions` ingestion; split/dividend adjustment; ticker-change reconciliation
- [ ] `DataSnapshot` write-then-seal lifecycle
- [ ] Indicators computed at ingest, unit-tested against reference values
- [ ] Fundamentals into the temporal table
- [ ] `ScreenCriteria` tree type + validator + compiler (flat `AND` only)
- [ ] Blazor UI: manual filter controls, results grid
- [ ] Deployed to Azure

**Exit:** ~~500+ tickers~~ **the full NIFTY 50 universe plus delisted members** (ADR-0005), 5 years
of bars, nightly job unattended for a week, §9 benchmark run and recorded. Splits and dividends
verified against a hand-checked sample of 20 securities.

> **Amended.** "500+ tickers" was written for the US S&P 500 universe. The Indian universe decided
> in ADR-0005 is ~60-80 securities. The §9 benchmark clause is also affected — see `docs/adr/0006`,
> which records that no valid measurement surface currently exists.

### Phase 2 — Intent translation (~2–3 weeks)
- [ ] `MetricConcepts` seeded with ~20 concepts + Strategy Vocabulary screen
- [ ] LLM parse to concepts + explicit filters, strict structured output
- [ ] Concept resolution + domain validation
- [ ] Interpretation panel with edit-definition (§5.3)
- [ ] Rate limiter (§5.4), `ParseCache`, `ScreenResultCache`
- [ ] **Saved strategies** — core workflow, not polish
- [ ] `MarketEye.AiEvals` 50 cases wired into CI as a gate

**Exit:** ≥85% eval, unknown concepts fail closed, a failed parse asks a question.

### Phase 3 — Backtesting (~4–6 weeks) ← the differentiator
- [ ] `BacktestDefinition` (§7) implemented exactly as specified
- [ ] Point-in-time universe reconstruction; delisted securities included
- [ ] Rebalance loop with T+1 execution, costs, slippage, dividend accrual
- [ ] Metrics incl. turnover, costs paid, gross vs. net
- [ ] Equity curve vs. SPY total return, **assumptions panel rendered alongside**
- [ ] `MarketEye.BacktestTests`: synthetic market, bias guards, known-bad strategies (§8)
- [ ] Optional: `OR` / `NOT` in the DSL compiler and UI

**Exit:** Synthetic market matches hand-computed values exactly. Every §8.2 guard throws. Bad
strategies backtest badly.

### Phase 4 — Polish (~2–3 weeks)
- [ ] Alerts: notify when a security enters/exits a saved strategy
- [ ] Strategy sharing
- [ ] Additional benchmarks (config rows)
- [ ] README as product pitch (§13), 5 ADRs

---

## 11. ADRs to write (`docs/adr/`)

1. **SQL Server over PostgreSQL** — temporal tables and columnstore, weighed against losing pgvector.
2. **Dapper + SqlBulkCopy on the ingest path, EF Core elsewhere** — knowing when not to use your ORM.
3. **Pre-computed indicators** — write amplification traded for read latency.
4. **AI emits concepts, never thresholds or SQL** — the core correctness and security argument.
5. **Sealed data snapshots** — reproducibility, cache invalidation, atomic ingestion failure.
6. **Tree-shaped DSL, flat implementation in v1** — deferring cost without a later rewrite.

---

## 12. Risks

| Risk | Mitigation |
|---|---|
| **Historical backfill vs. free-tier limits** — the real Phase 1 blocker | Per-ticker calls cannot backfill 5 years under a free tier. Use a bulk/EOD historical endpoint, or budget **one month of a paid tier (~$20–80)** for the initial backfill, then run daily incrementals (one bulk call/day) on the free tier. Solve this before writing ingestion code |
| **Data licensing** | Most providers restrict redistribution. A public deployment serving their data may breach ToS. Read the terms; if unclear, gate the deployment behind login or show derived metrics only |
| Data quality: splits, dividends, ticker changes | Now an explicit data-model requirement (§4.3, §4.4), not a hope. Reconcile a 20-ticker sample against a second source |
| Backtest silently wrong | §8 exists specifically for this. The synthetic market is non-negotiable |
| Blazor Server hosting cost | One persistent circuit per visitor. Fine at portfolio scale; know the number and the WASM alternative when asked |
| Scope creep | Phases are sequential; none begins with the previous unshipped. §1 non-goals are binding |
| Timeline optimism | §10 states 3–5 months. Cut whole phases, never compress Phase 1 |
| LLM cost | Two-level cache, small model tier, hard per-user rate limit |
| Legal | "Educational purposes only, not investment advice" on every results view and in the system prompt |

---

## 13. README opening (write this first, it clarifies the product)

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

Follow immediately with the §9 measured benchmark table.

---

## 14. Revision log & rejected changes

### Adopted in revision 3
- **Renamed Sift → MarketEye**, matching the GitHub repository. Applied across namespaces,
  projects, the solution, the DbContext, the compose service, the dev database and all docs.
  `PLAN.md.v1.bak` is left untouched: `CLAUDE.md` keeps it as a diffing baseline, and rewriting
  it would defeat that.
- **.NET 10 LTS replaces .NET 9 in §3.** Not a version-chasing change: `OpenAI` 2.13.0 →
  `System.ClientModel` 1.14.0 requires `Microsoft.Extensions.Logging.Abstractions >= 10.0.3`,
  which made `MarketEye.Ai` unresolvable on the 9.x line (hard `NU1605`, not a warning). .NET 9 is also
  EOL 2026-11-10, inside this project's own 3–5 month timeline. See `docs/adr/0003`.
- **Central Package Management adopted** so a future framework-line move is one edit, not eleven.
- **Phase 0 CI deferred.** Phase 0 was completed with `git init` and a GitHub remote but *without*
  the GitHub Actions workflow, so its "green CI" exit criterion is unmet by decision, not oversight.
  Tracked in §10.

### Adopted in revision 2
Explicit backtest semantics (§7) · transaction costs and slippage with visible assumptions (§7) ·
concepts-not-thresholds rule (§5.1) · vocabulary as a first-class feature (§5.2) · formal tree DSL
(§6) · columnstore claim downgraded to a hypothesis (§4.2) · qualified benchmark definition (§9) ·
screen-result cache (§5.5) · `DataSnapshot` (§4.5) · corporate actions + price-vs-total-return (§4.3,
§4.4) · sanity/synthetic test suite (§8) · rate limiting as a requirement (§5.4) · saved strategies
moved to Phase 2 · realistic timeline (§10) · backfill and licensing risks (§12).

### Considered and rejected — do not re-litigate
- **`IBenchmarkProvider` abstraction.** Premature. One benchmark exists; a config string covers it,
  and adding QQQ later is a table row. Contradicts the project's own "don't abstract until it breaks" rule.
- **A separate explainability panel.** Folded into §5.3 instead — one confirm-before-run surface, not two.
- **Reordering the phases to build AI first.** Reviewed and explicitly kept as-is. The flashy part
  goes last because the foundation is what makes it credible.
- **Numeric quality scores for this plan.** Not a measurement. Ignored.

### Still open
- [x] Market coverage — **RESOLVED: Indian equities (NSE/BSE)**, overriding the US-only
      recommendation. See `docs/adr/0004`, which records seven consequences the US-shaped plan did
      not anticipate — bonus/rights issues, circuit limits vs. T+1 fills, India-calibrated costs,
      the NIFTY benchmark, cross-exchange identity, standalone vs. consolidated fundamentals, and
      firmer licensing.
- [x] Universe for v1 — **RESOLVED: NIFTY 50 plus its delisted historical members**, sourced from
      the NSE bhavcopy archive (free, survivorship-free by construction). Fundamentals from
      indianapi.in. See `docs/adr/0005`.
      **Note:** §9's benchmark is defined at 500 securities; a NIFTY 50 universe is ~10% of that,
      so §9 is not measurable as written and must be restated before any number is published.
- [ ] Auth — ASP.NET Core Identity (simpler, sufficient) vs. Entra ID.
- [ ] Multi-user from Phase 2, or single-user until Phase 4?
