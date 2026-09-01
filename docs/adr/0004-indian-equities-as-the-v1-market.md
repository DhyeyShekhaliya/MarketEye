# ADR-0004: Indian equities (NSE/BSE) as the v1 market

**Status:** Accepted · **Date:** 2026-09-01 · **Phase:** 1

## Context

`PLAN.md` §14 listed market coverage as still open and recommended US-only for v1. That
recommendation is now overridden: v1 targets Indian equities.

This is a scope decision, not a re-litigation — §14 explicitly left it open.

## Decision

NSE/BSE-listed Indian equities. INR. Daily bars, as §1 already specifies.

## Consequences the plan did not anticipate

The plan was written against US market structure. Five assumptions break, and each one is a
correctness issue rather than a cosmetic swap.

### 1. Corporate actions: bonus and rights issues (§4.3, §4.4)

`CorporateActionType` currently models `Split / Dividend / TickerChange / Merger / Delisting`.
Indian markets add two that are common and materially different:

- **Bonus issue** — free additional shares. Economically close to a split, but reported
  separately and with its own ratio convention (e.g. 1:1 means one free share per share held,
  which is a 2-for-1 split; getting the convention backwards halves or doubles every price).
- **Rights issue** — shares offered to existing holders below market price. This is *not* a
  split: it dilutes and requires its own adjustment factor. Treating it as a split is wrong;
  ignoring it leaves a price discontinuity that looks like a real return.

Both must be added to the enum and to the adjustment logic before any backfill is trusted.

### 2. Transaction costs are roughly 50% higher than the §7 defaults

§7 defaults to `TransactionCostBps 10` + `SlippageBps 5` — a US-calibrated 15bps round trip.
Indian delivery trades carry Securities Transaction Tax on **both** legs (~10bps each way),
plus stamp duty on the buy, exchange charges, SEBI fees and GST. Round trip lands nearer
**22-25bps** even with a zero-brokerage discount broker.

§7 says costs are not optional and a 40%-turnover strategy is meaningfully affected by them.
The defaults must be re-derived for India, not inherited. Leaving them at 15bps would flatter
every backtest by a consistent margin.

### 3. Circuit limits break the T+1 execution assumption (§7)

§7 fills at T+1's open. Indian equities have daily price bands (upper/lower circuits); a stock
locked at its upper circuit **cannot be bought** at that price, and an illiquid one can be
circuit-locked for consecutive sessions. A backtest that fills anyway is claiming trades that
could not have happened — a subtler cousin of the lookahead bias §8.2 already guards against.

This needs an explicit rule and a §8.2-style guard. It has no US equivalent.

### 4. The benchmark is not SPY (§7)

§7 names SPY total return and — correctly — keeps the benchmark a config string rather than an
interface. That design holds; the value changes to a NIFTY 50 total-return series. Worth noting
that NIFTY *price* and *total-return* indices are published separately, and using the price index
would understate the benchmark exactly the way §4.4 warns about for individual securities.

### 5. Dual listing extends the identity rule (§4.4)

§4.4 requires that a ticker change not create a second `Security` row, reconciled on a
provider-stable id. In India the same company routinely lists on **both** NSE and BSE with
different tickers and slightly different prices. The identity rule now has to deduplicate across
exchanges, and the ingestion must choose a primary venue per security rather than storing both
as independent securities.

### 6. Fundamentals: standalone vs consolidated (§4.1)

Indian companies report both standalone and consolidated financials. They differ materially for
any company with subsidiaries. §4.1's temporal table has no column for this distinction, and
mixing the two across securities — or across periods for one security — produces ratios that are
not comparable. One must be chosen and recorded.

### 7. Licensing is sharper than §12 assumed (§12)

NSE's data policy prohibits redistribution without an agreement, and licences apply to
applications that display market data. §12 already flagged licensing as a risk; for India it is
firmer. Two things reduce exposure: §1 already rules out real-time and intraday data, and
end-of-day historical data is treated far less restrictively than live feeds. A public
deployment still needs the vendor's terms read before launch, and §12's mitigation — gate behind
login, or show derived metrics only — becomes the likely path rather than a fallback.

## Cost accepted

The plan's US framing has to be revisited in Phase 1 rather than inherited. None of the above
changes the architecture — the DSL, snapshot model, temporal reads and bias guards are all
market-agnostic — but the *parameters* and the corporate-action model are not, and shipping with
US defaults would produce backtests that are quietly wrong rather than obviously broken.
