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
threshold — "cheap" resolves from a `MetricConcepts` table you can inspect and edit, not from the
model's opinion. A concept the model returns that is not in that table is a hard validation
failure, never a silent fallback.

Everything downstream of the validator is deterministic. The model can be swapped, removed, or fail
entirely and the screening and backtesting engines still work.

## Status

**Phase 0 (Foundation) complete.** The solution scaffold, local SQL Server stack, point-in-time
schema slice, and health-checked API are in place and tested.

| Phase | State |
|---|---|
| 0 — Foundation | Complete, except CI (deferred) |
| 1 — Data pipeline + screener | Not started |
| 2 — Intent translation | Not started |
| 3 — Backtesting | Not started |
| 4 — Polish | Not started |

There is no market data and no AI yet, by design: `PLAN.md` §10 builds the foundation before the
flashy part, because the foundation is what makes the rest credible.

## Getting started

Requires the .NET 10 SDK and a container runtime. See `DEPENDENCIES.md` for exact versions and
machine setup.

```bash
cp .env.example .env          # then change the password
docker compose up -d          # SQL Server 2022, Developer Edition
dotnet build MarketEye.sln
dotnet run --project src/MarketEye.Api
```

The API applies EF migrations on startup **in Development only** and exposes `/health`.

```bash
curl localhost:5199/health    # Healthy
dotnet test                   # unit + integration
```

`MarketEye.IntegrationTests` and `MarketEye.BacktestTests` start their own SQL Server containers through
Testcontainers, so the container runtime must be running. `MarketEye.AiEvals` calls a live LLM and is
excluded from the default loop.

## Correctness

Three properties are enforced structurally rather than by convention, because each one silently
invalidates every result downstream if it slips:

**Point-in-time reads need both conditions.** `FOR SYSTEM_TIME AS OF @date` handles restatements;
`ReportedDate <= @date` handles reporting lag. Either alone is lookahead bias. Both are covered by
integration tests that apply the real migrations to a real SQL Server.

**`Close` and `AdjClose` are never interchangeable.** Trades execute at raw `Close`/`Open`; returns
compute from `AdjClose`. Conflating them makes every multi-year backtest systematically wrong.

**Delisted securities stay in the universe.** They exit at their last traded price, or at zero for
bankruptcy. Removing them is survivorship bias, so `Security` rows are never deleted.

## Benchmarks

Not yet measured. `PLAN.md` §9 defines the benchmark precisely, and the project's own rule is that
performance numbers in docs are measured or stated as hypotheses — never estimated.

One constraint worth recording now: there is no arm64 SQL Server image, so the local container runs
emulated on Apple Silicon. That is fine for correctness work but is **not a valid measurement
surface**. Benchmark numbers must come off the Azure SQL target.

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
