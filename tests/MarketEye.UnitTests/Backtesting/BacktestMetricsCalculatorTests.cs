using FluentAssertions;
using MarketEye.Application.Backtesting;
using MarketEye.Domain.Backtesting;
using Xunit;

namespace MarketEye.UnitTests.Backtesting;

/// <summary>
/// PLAN.md §8.4's rigor applied to backtest metrics: every expected value here is computed
/// independently of <c>BacktestMetricsCalculator</c> — either a closed-form hand calculation
/// documented inline, or cross-checked with an independent script — never asserted against the
/// function's own prior output. Where the exact arithmetic is irrational (Sharpe/Sortino's
/// standard deviation), the expected value is quoted to enough decimal places that
/// <c>BeApproximately</c>'s tolerance only absorbs rounding, not a wrong formula.
/// </summary>
public class BacktestMetricsCalculatorTests
{
    [Fact]
    public void Cagr_of_a_value_that_exactly_doubles_over_two_years()
    {
        // (200/100)^(1/2) - 1 = sqrt(2) - 1 = 0.41421356...
        var cagr = BacktestMetricsCalculator.Cagr(beginningValue: 100m, endingValue: 200m, days: 730);

        cagr.Should().BeApproximately(0.41421356m, 0.00001m);
    }

    [Fact]
    public void Cagr_is_zero_for_a_zero_or_negative_beginning_value()
    {
        BacktestMetricsCalculator.Cagr(0m, 100m, 365).Should().Be(0m);
    }

    [Fact]
    public void Cagr_is_minus_one_hundred_percent_on_a_total_loss()
    {
        BacktestMetricsCalculator.Cagr(100m, 0m, 365).Should().Be(-1m);
    }

    [Fact]
    public void MaxDrawdown_finds_the_largest_peak_to_trough_decline()
    {
        // Peak reaches 150 at index 1, troughs at 90 (index 2): (90-150)/150 = -0.40 exactly.
        // The recovery to 120 does not create a worse drawdown, so -0.40 stays the answer.
        var equityCurve = new List<decimal> { 100m, 150m, 90m, 120m };

        BacktestMetricsCalculator.MaxDrawdown(equityCurve).Should().Be(-0.4m);
    }

    [Fact]
    public void MaxDrawdown_is_zero_for_a_monotonically_rising_curve()
    {
        var equityCurve = new List<decimal> { 100m, 110m, 120m, 130m };
        BacktestMetricsCalculator.MaxDrawdown(equityCurve).Should().Be(0m);
    }

    [Fact]
    public void DailyReturns_computes_simple_day_over_day_returns()
    {
        // (110-100)/100 = 0.10; (99-110)/110 = -0.10 exactly (11/110 = 0.1).
        var equityCurve = new List<decimal> { 100m, 110m, 99m };

        var returns = BacktestMetricsCalculator.DailyReturns(equityCurve);

        returns.Should().Equal(0.10m, -0.10m);
    }

    [Fact]
    public void Sharpe_with_a_clean_annualisation_factor_matches_hand_computed_value()
    {
        // Two points, [0.01, 0.03]: mean = 0.02, population stdev = sqrt(((0.01)^2+(0.01)^2)/2)
        // = sqrt(0.0001) = 0.01 exactly. tradingDays=4 makes sqrt(4)=2 exact, so the whole
        // computation is rational: Sharpe = (0.02/0.01) * 2 = 4.0 exactly. Chosen deliberately so
        // no BeApproximately tolerance is needed -- this is the cleanest possible check that the
        // mean/stdev/annualisation wiring is correct before the irrational cases below.
        var returns = new decimal[] { 0.01m, 0.03m };

        var sharpe = BacktestMetricsCalculator.Sharpe(returns, riskFreeAnnual: 0m, tradingDays: 4);

        sharpe.Should().Be(4.0m);
    }

    [Fact]
    public void Sharpe_matches_an_independently_computed_value()
    {
        // Returns [0.01, -0.02, 0.03, -0.01, 0.02]: mean = 0.006, population stdev ≈
        // 0.0185472370, annualised over 252 trading days: (0.006/0.0185472370) * sqrt(252) ≈
        // 5.13537662. Cross-checked with an independent Python computation of the same formula,
        // not derived from this implementation.
        var returns = new decimal[] { 0.01m, -0.02m, 0.03m, -0.01m, 0.02m };

        var sharpe = BacktestMetricsCalculator.Sharpe(returns);

        sharpe.Should().BeApproximately(5.13538m, 0.001m);
    }

    [Fact]
    public void Sortino_matches_an_independently_computed_value()
    {
        // Same series as the Sharpe test above. Downside is excess < 0 (the risk-free rate, here
        // zero) -- two of the five returns qualify: -0.02 and -0.01. downside variance =
        // (0.02^2 + 0.01^2) / 5 = 0.0001, downside deviation = 0.01 exactly. Sortino =
        // (0.006 / 0.01) * sqrt(252) ≈ 9.52470472. Cross-checked independently, not derived from
        // this implementation.
        var returns = new decimal[] { 0.01m, -0.02m, 0.03m, -0.01m, 0.02m };

        var sortino = BacktestMetricsCalculator.Sortino(returns);

        sortino.Should().BeApproximately(9.52470m, 0.001m);
    }

    [Fact]
    public void Sortino_is_zero_when_there_is_no_downside()
    {
        // An upside-only series has no downside deviation to divide by -- the formula's
        // convention (matching this implementation) is 0, not an undefined or infinite value.
        var returns = new decimal[] { 0.01m, 0.03m };

        BacktestMetricsCalculator.Sortino(returns).Should().Be(0m);
    }

    [Fact]
    public void WinRate_counts_strictly_positive_periods()
    {
        // Two positives (0.1, 0.05) out of five; a flat 0.0 period is not a win.
        var periodReturns = new decimal[] { 0.1m, -0.1m, 0.05m, -0.02m, 0.0m };

        BacktestMetricsCalculator.WinRate(periodReturns).Should().Be(0.4m);
    }

    [Fact]
    public void AnnualTurnover_scales_monthly_rebalances_by_twelve()
    {
        // Average per-rebalance turnover (0.1+0.2+0.15)/3 = 0.15, times 12 rebalances/year = 1.8.
        var turnoverPerRebalance = new decimal[] { 0.1m, 0.2m, 0.15m };

        var annual = BacktestMetricsCalculator.AnnualTurnover(turnoverPerRebalance, RebalanceFrequency.Monthly);

        annual.Should().Be(1.8m);
    }

    [Fact]
    public void AnnualTurnover_scales_quarterly_rebalances_by_four_not_twelve()
    {
        // Same average (0.15) as the monthly test, but annualised by 4 rebalances/year = 0.6 --
        // asserts the frequency actually changes the multiplier rather than always assuming 12.
        var turnoverPerRebalance = new decimal[] { 0.1m, 0.2m, 0.15m };

        var annual = BacktestMetricsCalculator.AnnualTurnover(turnoverPerRebalance, RebalanceFrequency.Quarterly);

        annual.Should().Be(0.6m);
    }
}
