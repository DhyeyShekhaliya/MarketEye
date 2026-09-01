# ADR-0005: Market data provider for Indian equities

**Status:** Proposed — awaiting a decision · **Date:** 2026-09-01 · **Phase:** 1

## Context

`PLAN.md` §12 names historical backfill "the real Phase 1 blocker" and requires it solved
*before* ingestion code is written. ADR-0004 sets the market to Indian equities, which changes
the candidate set entirely — the plan's original EODHD-or-FMP framing was US-shaped.

## The decisive requirement

Not price depth, and not price. It is **delisted-security history**.

§7 requires delisted securities to stay in the universe and exit at their last price (or zero for
bankruptcy). §8.2 mandates a guard that *throws* if a historical universe excludes them. §8.3
requires that known-bad strategies backtest badly — which cannot be demonstrated on a universe
that quietly drops its failures.

This is the one requirement that cannot be retrofitted. Price depth can be backfilled later and
fundamentals can be enriched, but a universe assembled from currently-listed instruments is
permanently survivorship-biased. Every backtest built on it is wrong in the same direction, and
the error is invisible because the results look good.

## Candidates

### indianapi.in — India-native, unified REST

Covers NSE and BSE with company fundamentals, quarterly results, balance sheets, cash flows,
ratios, corporate actions, shareholding patterns and historical price charts. Tiered from a free
plan through Hobby / Developer / Growth Analyst / Pro, each on its own subdomain.

Strong fit for §4.1 fundamentals and §5.2's concept vocabulary, and India-native means Indian
reporting conventions are native rather than mapped.

**Unresolved, and decisive:** published material does not state delisted/inactive company
coverage, historical EOD depth in years, or per-plan rate limits. Its corporate-actions
description names dividends, splits, mergers and acquisitions — **bonus and rights issues are not
mentioned**, and ADR-0004 explains why those two are not optional in India.

### EODHD — global, explicit survivorship-free product

Sells delisted-company data as a named feature for survivorship-bias-free backtesting: the
symbol-list endpoint takes `delisted=1` and returns inactive tickers with their EOD prices,
fundamentals, dividends and splits retained. EOD Historical Data is $19.99/mo, Fundamentals
$59.99/mo, All-in-One $99.99/mo — inside §12's ~$20-80 one-month backfill budget. Non-US
exchange history runs from January 2000, comfortably beyond the 5 years §9 benchmarks against.

**Caveats:** delisted data requires the All World Extended tier, not the base plan. Indian
corporate-action conventions are mapped through a global schema rather than native, so bonus and
rights handling needs verification rather than assumption.

### TrueData / FinEdge — authorised Indian vendors

TrueData is an authorised NSE/BSE/MCX vendor. FinEdge explicitly lists **bonus issues and rights
issues** alongside dividends and splits — the only candidate that names all four outright, which
directly addresses ADR-0004's first consequence.

Delisted-history coverage is again unstated. Authorised-vendor status is a licensing advantage
(§12), not a data-completeness one.

### Broker APIs — rejected

Angel One SmartAPI, Dhan and Upstox offer generous or free historical daily data — Dhan back to
each scrip's inception. Rejected for three reasons: they serve **currently tradeable
instruments**, so the survivorship requirement fails at the source; they carry no fundamentals,
which Phase 1 needs for the temporal table; and they require a demat account, which sits awkwardly
against §1's "not connected to a brokerage" boundary even for data-only use.

## Recommendation

**Verify delisted coverage before committing to anything.** One question decides this, and it
cannot be answered from public marketing pages — it needs a direct answer from the vendor or a
trial key:

> Do you provide historical daily prices and a listing of companies **delisted** from NSE/BSE over
> the last 5 years, and do you retain their price history after delisting?

If **indianapi.in answers yes**, take it: India-native, cheapest, best fundamentals fit — subject
to confirming bonus/rights are in the corporate-actions feed.

If **it answers no**, there are two honest paths, and the choice is a product decision rather than
a technical one:

1. **Pair it with EODHD** for the delisted universe. Two providers behind one
   `IMarketDataProvider` — more work, but §7 and §8.2 are satisfiable as written.
2. **Ship v1 survivorship-limited**, with the limitation stated on every backtest view next to the
   assumptions panel §7 already requires. This is defensible only if it is *disclosed*, and it
   means §8.2's universe guard is written as a known-failing test rather than quietly dropped.

What must not happen is adopting a live-only universe and leaving §8.2's guard unimplemented. That
converts a documented limitation into a silent one, which is precisely the failure mode §8 exists
to prevent.

## Not decided here

Whether to pay for one month of a higher tier for the initial backfill and then drop to a cheaper
daily-incremental plan — §12's suggested strategy. That depends on the provider chosen and on
whether the backfill endpoint is bulk or per-ticker.
