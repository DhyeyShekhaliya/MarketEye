using MarketEye.Domain.Backtesting;

namespace MarketEye.Application.Backtesting;

/// <summary>
/// One point on an equity curve (portfolio or benchmark, rebased to the same starting capital).
/// </summary>
public readonly record struct EquityPoint(DateOnly Date, decimal Nav);

/// <summary>The full metrics set §7 requires, computed once per backtest run.</summary>
public sealed record BacktestMetricsResult
{
    public required decimal CagrGross { get; init; }
    public required decimal CagrNet { get; init; }
    public required decimal MaxDrawdown { get; init; }
    public required decimal Sharpe { get; init; }
    public required decimal Sortino { get; init; }
    public required decimal WinRate { get; init; }
    public required decimal AnnualTurnover { get; init; }
}

/// <summary>
/// Pure backtest metrics math (PLAN.md §7, §8.4-equivalent rigor). Hand-rolled `decimal`/`double`
/// arithmetic, consistent with <c>TechnicalIndicators.cs</c>'s existing style — no stats package
/// exists anywhere in this repo, and none of these formulas need one.
/// </summary>
public static class BacktestMetricsCalculator
{
    private const int TradingDaysPerYear = 252;

    /// <summary>
    /// Compound annual growth rate. <paramref name="days"/> is calendar days between the first and
    /// last equity-curve point, not trading days — CAGR is defined against elapsed time, not
    /// sessions.
    /// </summary>
    public static decimal Cagr(decimal beginningValue, decimal endingValue, int days)
    {
        if (beginningValue <= 0 || days <= 0) return 0m;

        var years = days / 365.0;
        var ratio = (double)(endingValue / beginningValue);

        // A total loss (ending value <= 0) has no real-valued CAGR under Math.Pow; report -100%
        // rather than NaN, since that is the economically honest answer.
        if (ratio <= 0) return -1m;

        return (decimal)(Math.Pow(ratio, 1.0 / years) - 1.0);
    }

    /// <summary>
    /// Largest peak-to-trough decline over the curve, expressed as a negative fraction
    /// (e.g. -0.40 for a 40% drawdown). Needs the full daily series, not just rebalance-date
    /// snapshots, or a fast intra-period crash-and-recovery would be invisible.
    /// </summary>
    public static decimal MaxDrawdown(IReadOnlyList<decimal> equityCurve)
    {
        if (equityCurve.Count == 0) return 0m;

        var peak = equityCurve[0];
        var worst = 0m;
        foreach (var value in equityCurve)
        {
            if (value > peak) peak = value;
            if (peak <= 0) continue;

            var drawdown = (value - peak) / peak;
            if (drawdown < worst) worst = drawdown;
        }
        return worst;
    }

    /// <summary>Day-over-day simple returns from an equity curve.</summary>
    public static decimal[] DailyReturns(IReadOnlyList<decimal> equityCurve)
    {
        if (equityCurve.Count < 2) return [];

        var returns = new decimal[equityCurve.Count - 1];
        for (var i = 1; i < equityCurve.Count; i++)
        {
            returns[i - 1] = equityCurve[i - 1] == 0
                ? 0m
                : (equityCurve[i] - equityCurve[i - 1]) / equityCurve[i - 1];
        }
        return returns;
    }

    /// <summary>
    /// Annualised Sharpe ratio: mean daily excess return over its population standard deviation,
    /// scaled by sqrt(252).
    /// </summary>
    public static decimal Sharpe(
        IReadOnlyList<decimal> dailyReturns, decimal riskFreeAnnual = 0m, int tradingDays = TradingDaysPerYear)
    {
        if (dailyReturns.Count == 0) return 0m;

        var dailyRiskFree = riskFreeAnnual / tradingDays;
        var excess = dailyReturns.Select(r => r - dailyRiskFree).ToArray();
        var mean = excess.Average();
        var variance = excess.Select(x => (x - mean) * (x - mean)).Sum() / excess.Length;
        var stdev = (decimal)Math.Sqrt((double)variance);

        return stdev == 0 ? 0m : mean / stdev * (decimal)Math.Sqrt(tradingDays);
    }

    /// <summary>
    /// Annualised Sortino ratio: like Sharpe, but the denominator is downside deviation
    /// (only return periods below the risk-free rate contribute to the variance sum), so an
    /// upside-only strategy is not penalised for volatility that was all gains.
    /// </summary>
    public static decimal Sortino(
        IReadOnlyList<decimal> dailyReturns, decimal riskFreeAnnual = 0m, int tradingDays = TradingDaysPerYear)
    {
        if (dailyReturns.Count == 0) return 0m;

        var dailyRiskFree = riskFreeAnnual / tradingDays;
        var excess = dailyReturns.Select(r => r - dailyRiskFree).ToArray();
        var mean = excess.Average();

        var downsideSquaredSum = excess.Where(x => x < 0).Sum(x => x * x);
        var downsideDeviation = (decimal)Math.Sqrt((double)(downsideSquaredSum / excess.Length));

        return downsideDeviation == 0 ? 0m : mean / downsideDeviation * (decimal)Math.Sqrt(tradingDays);
    }

    /// <summary>Fraction of periods (e.g. rebalance-to-rebalance, or daily) with a positive return.</summary>
    public static decimal WinRate(IReadOnlyList<decimal> periodReturns) =>
        periodReturns.Count == 0 ? 0m : (decimal)periodReturns.Count(r => r > 0) / periodReturns.Count;

    /// <summary>
    /// Per-rebalance turnover (traded notional / portfolio value), annualised by how many
    /// rebalances happen per year under the configured frequency.
    /// </summary>
    public static decimal AnnualTurnover(
        IReadOnlyList<decimal> turnoverPctPerRebalance, RebalanceFrequency frequency)
    {
        if (turnoverPctPerRebalance.Count == 0) return 0m;

        var rebalancesPerYear = frequency switch
        {
            RebalanceFrequency.Monthly => 12,
            RebalanceFrequency.Quarterly => 4,
            RebalanceFrequency.Annual => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(frequency), frequency, "Unknown rebalance frequency."),
        };

        return turnoverPctPerRebalance.Average() * rebalancesPerYear;
    }
}
