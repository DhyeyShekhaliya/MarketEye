# ADR-0009: Gross vs. net cost and turnover reporting

**Status:** Accepted, design revised after a live bug · **Date:** 2026-09-02, revised 2026-09-03 · **Phase:** 3

## Context

`PLAN.md` §7 states costs are not optional: "a monthly-rebalanced screen can turn over 40%+ per
year, and at 15bps round-trip that is a meaningful annual drag, and it falls hardest on exactly
the high-turnover strategies that look best without it. Report gross and net side by side." §7
revision 3 (`docs/adr/0004`) raises the default cost from a US-shaped 15bps to an India-calibrated
23bps (transaction) + 5bps (slippage), since Indian delivery trades pay Securities Transaction Tax
on both legs plus stamp duty, exchange charges, SEBI fees and GST. This ADR records how the
backtest engine actually computes and reports these numbers, since "report gross and net" is easy
to get subtly wrong if costs are treated as a simple end-of-period subtraction rather than
something that compounds into the portfolio's own share counts.

## Decision

### Costs are charged on traded notional, never on portfolio value

`TransactionCostModel.Cost(notionalTraded, transactionCostBps, slippageBps)`
(`src/MarketEye.Application/Backtesting/TransactionCostModel.cs`) takes the absolute dollar amount
actually bought or sold — `TradeListBuilder.Diff`'s `Trade.Notional`, computed as
`|targetWeight - currentWeight| × portfolioValue` — not the whole portfolio's value. A rebalance
that only reshuffles 10% of the portfolio pays costs on that 10%, never on the other 90% that
didn't trade. Charging the full portfolio value would overstate drag for every strategy that
doesn't fully turn over at each rebalance, which in practice is nearly all of them.

### Gross is derived from net's own trading path, not from a second independent simulation

**This was implemented differently at first, and the first design was wrong in a way only a real
multi-rebalance backtest exposed.** The original decision here ran `SimulateAsync` twice — once at
the definition's configured `TransactionCostBps`/`SlippageBps` (net), once at zero for both (gross)
— reasoning that costs compound into share counts and so "cannot be cleanly subtracted from a
single equity curve after the fact." That reasoning was correct about *why* a naive subtraction is
wrong, but the fix was itself wrong: weight-based rebalancing resizes every trade against the
portfolio's *current* value, so the net run's lower post-cost value at rebalance N produces a
genuinely different share count than the zero-cost run from rebalance N onward — the two
simulations follow different trading paths, not the same path with a cash offset. Over several
rebalances those paths can diverge enough to flip the sign of the gap entirely: a real 3-year,
quarterly-rebalanced backtest against actual NSE data showed `CagrNet` (7.5%) HIGHER than
`CagrGross` (3.5%) — impossible with non-negative costs, and undetected by every §8.1/§8.3 test
because those fixtures use a single rebalance, where the two paths cannot yet have diverged.

The fix: run the simulation **once**, at real costs, tracking cumulative costs paid alongside every
equity-curve point. The gross curve is then `grossNav[i] = netNav[i] + cumulativeCostsAtPoint[i]`
— the same trading path (same share counts, same fills), just with the costs added back in. This
guarantees `CagrNet <= CagrGross` by construction, because the only thing separating the two curves
at any point is the non-negative costs paid up to that point, not an independently-computed
position size. It is also strictly cheaper: one simulation pass instead of two.

### The persisted equity curve, and every other metric, is the net one

`MaxDrawdown`, `Sharpe`, `Sortino`, `WinRate`, and `BacktestRun.EquityCurveJson` are all computed
from the net (real-cost) simulation only — the gross simulation exists solely to produce
`CagrGross` for the side-by-side comparison §7 asks for. Reporting risk metrics against a
hypothetical zero-cost curve would answer a question nobody asked; the risk an investor actually
bears is the net one.

### Turnover is annualised from the configured rebalance frequency, not assumed

`BacktestMetricsCalculator.AnnualTurnover` (`src/MarketEye.Application/Backtesting/BacktestMetricsCalculator.cs`)
takes the average per-rebalance turnover percentage and multiplies by how many rebalances the
`RebalanceFrequency` implies per year (12 for Monthly, 4 for Quarterly, 1 for Annual), rather than
hardcoding an annualisation factor. A quarterly-rebalanced strategy and a monthly one with the same
per-rebalance turnover should not report the same annual figure, and this keeps that distinction
correct without a separate code path per frequency.

## Consequences

- Every `BacktestRun` carries `CagrGross`, `CagrNet`, and `TotalCostsPaid` side by side, matching
  §7's requirement verbatim, and `CagrNet <= CagrGross` holds by construction rather than merely by
  observation — there is no trading-path divergence left for it to fail on.
- A strategy with zero turnover (buys once, never rebalances again) reports `CagrGross == CagrNet`
  by construction — a useful sanity check that the cost model is not leaking drag onto trades that
  never happened. Verified directly: `SyntheticMarketEngineTests.Gross_and_net_returns_are_equal_when_costs_are_zero`.
- One simulation pass instead of two, which is both correct and faster than the design this
  replaced.
- **Lesson for future engine changes:** the §8.1/§8.3 fixtures are single-rebalance by design (to
  isolate universe/execution/corporate-action mechanics from reweighting arithmetic — see their own
  doc comments) and did not, and structurally cannot, catch a bug that only manifests across
  multiple rebalances. A real multi-year, multi-rebalance `/api/backtest` run against actual data
  is what caught this one. Treat that as part of validating any future change to the rebalance
  loop, not just the synthetic suite.
