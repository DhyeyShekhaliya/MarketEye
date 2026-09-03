# ADR-0008: T+1 execution and circuit-lock handling

**Status:** Accepted · **Date:** 2026-09-02 · **Phase:** 3

## Context

`PLAN.md` §7 fixes the rebalance loop's execution rule: a screen run with data as of T fills at
T+1's open, never at T's close. §7 revision 3 (`docs/adr/0004`) adds a complication with no US
equivalent: Indian equities carry daily circuit limits, and a stock locked at its band cannot be
bought or sold at that price. Filling anyway claims a trade that could not have happened — the
same class of error as lookahead, just one step removed from it. §7 revision 3 names this as an
open gap: "the rebalance loop must skip circuit-locked fills and carry the intended trade
forward, and §8.2 needs a guard for it." Phase 3 closes that gap; this ADR records how.

## Decision

### T+1 is enforced by a guard, not by convention

`PointInTimeGuard.RequireExecutionAfterSignal(signalDate, executionDate)`
(`src/MarketEye.Infrastructure/Screening/PointInTimeGuard.cs`) already existed before Phase 3,
built alongside the other §8.2 guards and unit-tested in
`tests/MarketEye.BacktestTests/BiasGuards/PointInTimeGuardTests.cs`. `BacktestEngine.SimulateAsync`
(`src/MarketEye.Infrastructure/Backtesting/BacktestEngine.cs`) calls it immediately after resolving
the next trading date and before any fill is attempted — the same "guard first, act second"
ordering `ScreeningEngine.RunAsync` already uses for `RequireSealed`.

### A circuit-locked bar gets its own guard, symmetrical with the others

`PointInTimeGuard.RequireNotCircuitLocked(PriceBar bar)` throws if `bar.IsCircuitLocked` is true.
It is deliberately a **backstop**, not the control flow: `FillExecutor.TryFillAsync`
(`src/MarketEye.Infrastructure/Backtesting/FillExecutor.cs`) searches forward for the first
non-locked bar and only ever calls the guard on the bar it already decided to fill. The guard
exists to catch a future refactor that constructs a fill without checking first — the identical
relationship `RequireSealed` has to `ScreeningEngine`, which already re-checks a condition its own
caller is expected to have satisfied.

### Skip-and-carry-forward, capped at the same window as missing prices

§7 step 10 already caps a missing-price carry-forward at 5 trading days before forcing an exit
(`MissingPriceCarryForward.MaxCarryForwardDays`,
`src/MarketEye.Application/Backtesting/MissingPriceCarryForward.cs`). §7 does not separately specify
a cap for circuit-lock retries, so `FillExecutor` reuses the same constant rather than inventing a
second, undocumented number: searching forward for a fillable bar considers at most
`MaxCarryForwardDays` candidate trading days, then the trade is dropped and logged. Reusing one
constant for two related "how long do we wait before giving up" decisions is simpler to reason
about than two independently-tunable windows with no stated relationship between them, and nothing
in §7 argues they should differ.

### A dropped trade does not fail the backtest

A circuit-locked security that never becomes fillable within the window is logged
(`ILogger<BacktestEngine>.LogWarning`) and the position simply stays at its prior weight for that
rebalance — the portfolio behaves exactly as it would in reality if the trade genuinely could not
be executed. Throwing here would make an illiquid, realistic security a reason the whole backtest
fails, which is a worse answer than the strategy under-performing because a trade it wanted to
make was not actually possible.

## Consequences

- Every fill in a backtest either respects T+1 and an unlocked price, or the trade did not happen
  — there is no code path that constructs a fill without both guards standing between the decision
  and the execution.
- The circuit-lock guard closes the exact gap `docs/adr/0004` named as open. §8.2's guard table is
  now fully implemented, not five of six items.
- Reusing `MissingPriceCarryForward.MaxCarryForwardDays` for circuit-lock retries is a scope
  decision, not a measured one — if real backtests show securities routinely locked for longer
  than 5 sessions, revisit the constant with data rather than widening it speculatively here.
