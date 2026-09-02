using Microsoft.EntityFrameworkCore;
using MarketEye.Domain.Entities;
using MarketEye.Domain.Screening;

namespace MarketEye.Infrastructure.Persistence;

/// <summary>
/// Seeds the metric whitelist — the compiler's controlled vocabulary (PLAN.md §5.2, §6).
///
/// These rows are the only strings that ever become SQL identifiers, so the table is system-owned
/// and has no edit screen. Thresholds are deliberately absent: what "cheap" means lives in
/// <see cref="StrategyConceptSeed"/>, where a human can see and change it (docs/adr/0007).
///
/// Ranges are not decoration. A P/E of 10,000 is a data error or an injection attempt, not a
/// screen, and rejecting it here keeps nonsense out of the query planner (§5.2).
/// </summary>
public static class MetricConceptSeed
{
    private const string Numeric = "LessThan,LessThanOrEqual,GreaterThan,GreaterThanOrEqual";

    /// <summary>
    /// Upserts by name. Unlike the strategy vocabulary this is safe to overwrite on every start:
    /// these rows are system-owned by design, so there is no user edit here to clobber, and an
    /// existing database picks up newly added metrics without a manual step.
    /// </summary>
    public static async Task SeedAsync(MarketEyeDbContext db, CancellationToken ct)
    {
        var existing = await db.MetricConcepts.ToDictionaryAsync(c => c.Name, StringComparer.Ordinal, ct);

        foreach (var wanted in SeedRows())
        {
            if (existing.TryGetValue(wanted.Name, out var row))
            {
                row.DisplayName = wanted.DisplayName;
                row.Description = wanted.Description;
                row.ColumnName = wanted.ColumnName;
                row.Source = wanted.Source;
                row.AllowedOperatorsCsv = wanted.AllowedOperatorsCsv;
                row.MinValue = wanted.MinValue;
                row.MaxValue = wanted.MaxValue;
                row.Unit = wanted.Unit;
            }
            else
            {
                db.MetricConcepts.Add(wanted);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>The seed rows, buildable without a database so tests can assert over them.</summary>
    public static IEnumerable<MetricConceptEntity> SeedRows()
    {
        // --- Valuation and returns, from FundamentalRatios -------------------------------------
        yield return new MetricConceptEntity
        {
            Name = "PeRatio", DisplayName = "P/E ratio", ColumnName = "Pe",
            Source = MetricSource.FundamentalRatio, AllowedOperatorsCsv = Numeric,
            MinValue = 0m, MaxValue = 1000m,
            Description = "Price divided by trailing earnings per share.",
        };
        yield return new MetricConceptEntity
        {
            Name = "PbRatio", DisplayName = "P/B ratio", ColumnName = "Pb",
            Source = MetricSource.FundamentalRatio, AllowedOperatorsCsv = Numeric,
            MinValue = 0m, MaxValue = 100m,
            Description = "Price divided by book value per share.",
        };
        yield return new MetricConceptEntity
        {
            Name = "PsRatio", DisplayName = "P/S ratio", ColumnName = "Ps",
            Source = MetricSource.FundamentalRatio, AllowedOperatorsCsv = Numeric,
            MinValue = 0m, MaxValue = 100m,
            Description = "Price divided by revenue per share.",
        };
        yield return new MetricConceptEntity
        {
            Name = "ReturnOnEquity", DisplayName = "Return on equity", ColumnName = "Roe",
            Source = MetricSource.FundamentalRatio, AllowedOperatorsCsv = Numeric,
            MinValue = -100m, MaxValue = 200m, Unit = "%",
            Description = "Net income as a percentage of shareholders' equity.",
        };
        yield return new MetricConceptEntity
        {
            Name = "ReturnOnCapital", DisplayName = "Return on invested capital", ColumnName = "Roic",
            Source = MetricSource.FundamentalRatio, AllowedOperatorsCsv = Numeric,
            MinValue = -100m, MaxValue = 200m, Unit = "%",
            Description = "Operating return on debt plus equity.",
        };
        yield return new MetricConceptEntity
        {
            Name = "DebtToEquity", DisplayName = "Debt to equity", ColumnName = "DebtToEquity",
            Source = MetricSource.FundamentalRatio, AllowedOperatorsCsv = Numeric,
            MinValue = 0m, MaxValue = 50m,
            Description = "Total debt divided by shareholders' equity.",
        };
        yield return new MetricConceptEntity
        {
            Name = "GrossMargin", DisplayName = "Gross margin", ColumnName = "GrossMargin",
            Source = MetricSource.FundamentalRatio, AllowedOperatorsCsv = Numeric,
            MinValue = -100m, MaxValue = 100m, Unit = "%",
            Description = "Gross profit as a percentage of revenue.",
        };
        yield return new MetricConceptEntity
        {
            Name = "FcfYield", DisplayName = "Free cash flow yield", ColumnName = "FcfYield",
            Source = MetricSource.FundamentalRatio, AllowedOperatorsCsv = Numeric,
            MinValue = -100m, MaxValue = 100m, Unit = "%",
            Description = "Free cash flow as a percentage of market capitalisation.",
        };
        yield return new MetricConceptEntity
        {
            Name = "MarketCap", DisplayName = "Market capitalisation", ColumnName = "MarketCap",
            Source = MetricSource.FundamentalRatio, AllowedOperatorsCsv = Numeric,
            // Stored value is already in CRORE, not raw rupees -- see RatioCalculator.MarketCap's
            // doc-comment. 100,000,000 (crore) comfortably exceeds Reliance's real market cap,
            // which has been the largest in India at roughly 20-22 lakh (2,000,000-2,200,000) crore.
            MinValue = 0m, MaxValue = 100_000_000m, Unit = "INR_CR",
            Description = "Shares outstanding multiplied by price, in crore.",
        };

        // --- Technicals, from Indicators --------------------------------------------------------
        yield return new MetricConceptEntity
        {
            Name = "Rsi14", DisplayName = "RSI (14)", ColumnName = "Rsi14",
            Source = MetricSource.Indicator, AllowedOperatorsCsv = Numeric,
            MinValue = 0m, MaxValue = 100m,
            Description = "Wilder's 14-period relative strength index.",
        };
        yield return new MetricConceptEntity
        {
            Name = "Sma50", DisplayName = "50-day moving average", ColumnName = "Sma50",
            Source = MetricSource.Indicator, AllowedOperatorsCsv = Numeric,
            MinValue = 0m, MaxValue = 1_000_000m, Unit = "INR",
        };
        yield return new MetricConceptEntity
        {
            Name = "Sma200", DisplayName = "200-day moving average", ColumnName = "Sma200",
            Source = MetricSource.Indicator, AllowedOperatorsCsv = Numeric,
            MinValue = 0m, MaxValue = 1_000_000m, Unit = "INR",
        };
        yield return new MetricConceptEntity
        {
            Name = "Macd", DisplayName = "MACD", ColumnName = "Macd",
            Source = MetricSource.Indicator, AllowedOperatorsCsv = Numeric,
            MinValue = -100_000m, MaxValue = 100_000m,
            Description = "12/26 exponential moving average convergence-divergence.",
        };
        yield return new MetricConceptEntity
        {
            Name = "MacdSignal", DisplayName = "MACD signal line", ColumnName = "MacdSignal",
            Source = MetricSource.Indicator, AllowedOperatorsCsv = Numeric,
            MinValue = -100_000m, MaxValue = 100_000m,
            Description = "9-period exponential moving average of MACD.",
        };
        yield return new MetricConceptEntity
        {
            Name = "Atr14", DisplayName = "ATR (14)", ColumnName = "Atr14",
            Source = MetricSource.Indicator, AllowedOperatorsCsv = Numeric,
            MinValue = 0m, MaxValue = 1_000_000m, Unit = "INR",
            Description = "14-period average true range.",
        };
        yield return new MetricConceptEntity
        {
            Name = "Volatility30", DisplayName = "30-day volatility", ColumnName = "Vol30",
            Source = MetricSource.Indicator, AllowedOperatorsCsv = Numeric,
            MinValue = 0m, MaxValue = 10m,
            Description = "Annualised realised volatility over 30 trading days.",
        };

        // --- Price and liquidity, from PriceBars ------------------------------------------------
        yield return new MetricConceptEntity
        {
            Name = "ClosePrice", DisplayName = "Close price", ColumnName = "Close",
            Source = MetricSource.PriceBar, AllowedOperatorsCsv = Numeric,
            MinValue = 0m, MaxValue = 1_000_000m, Unit = "INR",
            Description = "Last traded price. Execution price, not the return series (§4.4).",
        };
        yield return new MetricConceptEntity
        {
            Name = "Volume", DisplayName = "Volume", ColumnName = "Volume",
            Source = MetricSource.PriceBar, AllowedOperatorsCsv = Numeric,
            MinValue = 0m, MaxValue = 1_000_000_000_000m, Unit = "shares",
            Description = "Shares traded on the snapshot date. A liquidity floor, not a signal.",
        };
    }
}
