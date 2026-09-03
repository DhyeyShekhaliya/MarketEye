# ADR-0010: Benchmark as a config string

**Status:** Accepted · **Date:** 2026-09-02 · **Phase:** 3

## Context

`PLAN.md` §7 requires every backtest to compare against a benchmark — originally SPY total
return, restated to NIFTY 50 total return by `docs/adr/0004` — and §7 is explicit about the shape:
"Keep the benchmark a config value — a `TickerSymbol` string, not an `IBenchmarkProvider`
interface. Adding NIFTY 500 later is a row in a table." §14 records this as already considered and
rejected once: *"`IBenchmarkProvider` abstraction. Premature. One benchmark exists; a config
string covers it, and adding QQQ later is a table row. Contradicts the project's own 'don't
abstract until it breaks' rule."* This ADR does not re-argue that decision — it records what
implementing it actually required, since Phase 3 investigation found that **no ingestion path in
this codebase touches index-level data at all**: the NSE bhavcopy archive (`docs/adr/0005`) is
equity-only, and there is no bulk index-history API behind any existing provider client.

## Decision

### `BenchmarkTicker` stays a plain string field, all the way through

`BacktestDefinition.BenchmarkTicker` (`src/MarketEye.Domain/Backtesting/BacktestDefinition.cs`) is
`string?`, defaulting to `"NIFTY50TR"`. `BacktestEngine` reads it, looks up matching rows in a new
`BenchmarkPrices` table, and does nothing more abstract than that — no interface, no provider
registry. Adding a second benchmark later is inserting rows under a different `Ticker` value, one
new default in a UI dropdown, and nothing else, exactly as §7 and §14 already argued.

### `BenchmarkPrices` is a new, minimal table — not a repurposed `PriceBars`

A new entity, `BenchmarkPrice` (`src/MarketEye.Domain/Entities/BenchmarkPrice.cs`): `Ticker`,
`Date`, `TotalReturnIndexValue`, composite-keyed on `(Ticker, Date)`. Deliberately not stored in
`PriceBars` alongside individual securities — an index has no `Open`/`High`/`Low`/`Volume`, no
`SecurityId`, and is never a member of a screened universe. Overloading `PriceBars` to carry it
would mean every query against that table needs to remember to exclude index rows, forever.

### Total-return, not price index — the same distinction §4.4 already makes

NIFTY publishes price and total-return indices separately. `BenchmarkPrice.TotalReturnIndexValue`
is named to make the requirement unmissable at the call site: comparing a backtest's `AdjClose`-
based returns (which already include dividends, per §4.4) against the *price* index would
understate the benchmark the same way conflating `Close` and `AdjClose` would understate an
individual security's return. `docs/adr/0004` already flagged this distinction when NIFTY replaced
SPY; this ADR is what actually enforces it in the schema, by only ever naming the total-return
series.

### Sourcing: a manual CSV import, not a scraper — decided with the user, not silently

Three sourcing options were considered: scraping niftyindices.com the way `NseBhavcopyClient`
scrapes NSE, checking whether `indianapi.in` (already integrated for fundamentals) exposes an
index endpoint, or a one-time manual CSV download. The user chose the manual CSV path directly,
given the low update frequency this data actually needs (quarterly at most, versus a nightly
price/fundamentals cadence) does not justify building and maintaining a new scraper or spending
time confirming a second provider's coverage.

`NiftyTotalReturnLoader` (`src/MarketEye.Infrastructure/MarketData/Benchmark/NiftyTotalReturnLoader.cs`)
parses a locally-provided CSV (the shape niftyindices.com's own historical-data export already
uses: `Date,Close` with the TR series' close column) and MERGEs rows into `BenchmarkPrices`,
mirroring `LocalArchiveBhavcopySource`'s "read a local file" shape rather than
`NseBhavcopyClient`'s "scrape live" shape. It is a one-off admin operation, not wired into
`DailyIngestionJob` — this is reference data that changes slowly, not a nightly ingest concern.

**The CSV itself is not shipped by this ADR.** No fabricated or placeholder benchmark data belongs
in this repository — `CLAUDE.md`'s non-negotiables require performance numbers to be measured, not
estimated, and the same standard applies to backtest benchmark comparisons: a benchmark curve
computed from data that was never actually downloaded from niftyindices.com would be exactly the
kind of "a measured number taken on an invalid surface is an estimate wearing a lab coat" problem
§9 already forbids for a different metric. Populating `BenchmarkPrices` with the real NIFTY 50 TR
history is a manual follow-up step, run once against a real download.

### Missing benchmark data degrades gracefully, never fails the run

If `BenchmarkPrices` has fewer than two rows for the requested ticker and date range —
including the common case where nobody has run the loader yet — `BacktestEngine` sets
`BenchmarkCagr` and `BenchmarkCurveJson` to `null` and the backtest still completes and persists
normally. A missing benchmark is a UI message ("no data"), never a 500 or a blocked run. Forcing
every backtest to fail without benchmark data would make the loader a hard prerequisite for a
feature §7 treats as a comparison, not a gate.

## Consequences

- No `IBenchmarkProvider` exists anywhere in the codebase, consistent with §14's already-recorded
  decision. This ADR implements that decision; it does not reopen it.
- A second benchmark (NIFTY 500, say) is a new set of `BenchmarkPrices` rows under a new ticker and
  a new default option in the `/backtest` UI — no code change to `BacktestEngine` is needed.
- Until someone runs `NiftyTotalReturnLoader` against a real downloaded CSV, every backtest's
  benchmark comparison is silently absent rather than wrong — the schema and the engine are ready,
  the data is not yet populated, and that gap is recorded here rather than papered over with a
  fabricated series.
