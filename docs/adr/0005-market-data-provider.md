# ADR-0005: Market data provider for Indian equities

**Status:** Accepted · **Date:** 2026-09-01 · **Phase:** 1

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

**Resolved against it, on two counts.** The published endpoint list settles what the marketing
page left open:

- **No delisted/inactive endpoint exists.** The survivorship requirement fails outright.
- **No endpoint returns the full NSE/BSE roster.** `/stock` is a by-name lookup and
  `/industry_search` returns filtered subsets. This is the more disqualifying of the two: nightly
  ingestion needs "give me every security" as its first call, and §4.5's snapshot cannot be built
  from an API you can only query if you already know what to ask for.

`/historical_data` does offer 1m / 6m / 1yr / 3yr / 5yr / 10yr / max, so price depth is adequate.
Corporate actions appear only as a `stockCorporateActionData` field inside `/stock`, with no
confirmation that bonus and rights issues are included.

**Verdict:** usable as a *fundamentals* source, not as the price or universe source.

### NSE bhavcopy archive — free, official, survivorship-free by construction

The bhavcopy is NSE's official end-of-day file listing **every security that traded that day**
with its OHLCV. Archives are freely downloadable and public mirrors carry two decades of history.

This solves the binding constraint almost by accident: a company delisted in 2022 still appears in
every bhavcopy up to its last trading day. Reconstructing the universe as-of any past date is then
a matter of reading that date's file — which is exactly the point-in-time universe §7 requires,
obtained without paying for a "survivorship-free" product, because the raw daily record was never
survivor-filtered in the first place.

It is also the same shape as §4.5's snapshot model: one immutable file per trading day.

**Costs:** more ingestion work than a REST API — download, parse, normalise, handle NSE's format
changes over the years and the holiday calendar. No fundamentals. Corporate actions must come from
NSE's separate corporate-actions reports or a vendor. Ticker-change reconciliation (§4.4) is
manual, since bhavcopy keys on symbol rather than a stable id.

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

**Bhavcopy archive for prices and universe; a REST vendor for fundamentals.**

No single Indian source covers both well. The split follows the requirement that cannot be
retrofitted: prices and universe membership must be survivorship-free from day one, and the
bhavcopy archive is that, for free. Fundamentals can be added, corrected and re-ingested later
without invalidating anything already stored, so that side is a cost/convenience choice rather
than a correctness one.

This deviates from §3's "one provider behind `IMarketDataProvider`". The interface still holds —
it becomes two implementations composed behind it rather than one — and §3's underlying point,
that vendor choice stays swappable, is preserved.

If the extra ingestion work is unattractive, **EODHD alone is the coherent paid alternative**: it
sells delisted history explicitly and carries fundamentals, at $19.99-99.99/mo, inside §12's
budget. It trades money for the bhavcopy parsing work and accepts a global schema over native
Indian conventions.

What must not happen is adopting a live-only universe and leaving §8.2's guard unimplemented.
That converts a documented limitation into a silent one — precisely the failure mode §8 exists to
prevent.

## Not decided here

Whether to pay for one month of a higher tier for the initial backfill and then drop to a cheaper
daily-incremental plan — §12's suggested strategy. That depends on the provider chosen and on
whether the backfill endpoint is bulk or per-ticker.


## Decision taken

- **Prices and universe:** NSE bhavcopy archive. Free, official, survivorship-free by construction.
- **Fundamentals:** indianapi.in. India-native, and retrofittable if it disappoints.
- **Universe:** NIFTY 50 plus its delisted historical members.

Two `IMarketDataProvider` implementations composed behind the interface, per the recommendation
above.

### Resolved: bonus and rights are included

The open question — whether indianapi.in's `stockCorporateActionData` covers **bonus and rights
issues** as well as splits and dividends — was confirmed **yes** with the vendor on 2026-09-02.

That settles the corporate-actions source: indianapi.in supplies all four price-affecting action
types, so NSE's separate corporate-actions reports are not needed as a second integration. ADR-0004
explains why the two Indian-specific types were the deciding factor.

Ingestion must still **verify the ratio convention per action type** rather than trusting a shared
field. A bonus quoted "1:1" and a split quoted "2-for-1" are the same economics with inverted
numbers, and `AdjustmentFactors` has separate functions for exactly that reason. A sample of known
actions should be checked by hand before the adjustment output is trusted — which is what §12's
20-security reconciliation is for.

### Consequence for §9

§9's benchmark is defined at "500 securities × 5 years (≈630k bars)". A NIFTY 50 universe is
~50 securities (plus delisted members), roughly a tenth of that. The §9 figure is therefore **not
measurable as written** on this universe. Either the benchmark definition is restated for the
chosen universe, or the benchmark is run against a wider bhavcopy-derived set than the screening
universe. This must be settled before any performance number is published, since the project's
non-negotiables require measured numbers under a stated definition.
