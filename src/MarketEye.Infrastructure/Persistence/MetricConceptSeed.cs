using Microsoft.EntityFrameworkCore;
using MarketEye.Domain.Entities;
using MarketEye.Domain.Screening;

namespace MarketEye.Infrastructure.Persistence;

/// <summary>
/// Seeds the controlled vocabulary (PLAN.md §5.2, Phase 2 expands it to ~20 concepts).
///
/// Every threshold here is a number a human chose and can edit in the UI. §5.1's rule depends on
/// that being true: when a user says "cheap", the number comes from this table, not from the model.
/// </summary>
public static class MetricConceptSeed
{
    private const string Numeric = "LessThan,LessThanOrEqual,GreaterThan,GreaterThanOrEqual";

    public static async Task SeedAsync(MarketEyeDbContext db, CancellationToken ct)
    {
        if (await db.MetricConcepts.AnyAsync(ct)) return;

        db.MetricConcepts.AddRange(
            new MetricConceptEntity
            {
                Name = "PeRatio", DisplayName = "P/E ratio", ColumnName = "Pe",
                Source = MetricSource.FundamentalRatio, AllowedOperatorsCsv = Numeric,
                MinValue = 0m, MaxValue = 1000m,
                Description = "Price divided by trailing earnings per share.",
                // "Cheap" resolves to this, not to the model's opinion (§5.1). Indian large caps
                // trade at structurally higher multiples than US ones, so a US-derived threshold
                // of 15 would screen out almost the entire NIFTY 50.
                DefaultThreshold = 25m, DefaultOperator = ComparisonOperator.LessThan,
            },
            new MetricConceptEntity
            {
                Name = "PbRatio", DisplayName = "P/B ratio", ColumnName = "Pb",
                Source = MetricSource.FundamentalRatio, AllowedOperatorsCsv = Numeric,
                MinValue = 0m, MaxValue = 100m,
                Description = "Price divided by book value per share.",
            },
            new MetricConceptEntity
            {
                Name = "ReturnOnEquity", DisplayName = "Return on equity", ColumnName = "Roe",
                Source = MetricSource.FundamentalRatio, AllowedOperatorsCsv = Numeric,
                MinValue = -100m, MaxValue = 200m, Unit = "%",
                Description = "Net income as a percentage of shareholders' equity.",
                DefaultThreshold = 15m, DefaultOperator = ComparisonOperator.GreaterThan,
            },
            new MetricConceptEntity
            {
                Name = "DebtToEquity", DisplayName = "Debt to equity", ColumnName = "DebtToEquity",
                Source = MetricSource.FundamentalRatio, AllowedOperatorsCsv = Numeric,
                MinValue = 0m, MaxValue = 50m,
                Description = "Total debt divided by shareholders' equity.",
                DefaultThreshold = 0.5m, DefaultOperator = ComparisonOperator.LessThan,
            },
            new MetricConceptEntity
            {
                Name = "MarketCap", DisplayName = "Market capitalisation", ColumnName = "MarketCap",
                Source = MetricSource.FundamentalRatio, AllowedOperatorsCsv = Numeric,
                MinValue = 0m, MaxValue = 100_000_000_000_000m, Unit = "INR",
                Description = "Shares outstanding multiplied by price, in rupees.",
            },
            new MetricConceptEntity
            {
                Name = "Rsi14", DisplayName = "RSI (14)", ColumnName = "Rsi14",
                Source = MetricSource.Indicator, AllowedOperatorsCsv = Numeric,
                MinValue = 0m, MaxValue = 100m,
                Description = "Wilder's 14-period relative strength index.",
                // "Overbought" is conventionally 70 and "oversold" 30. Conventional, and editable.
                DefaultThreshold = 70m, DefaultOperator = ComparisonOperator.GreaterThan,
            },
            new MetricConceptEntity
            {
                Name = "Sma50", DisplayName = "50-day moving average", ColumnName = "Sma50",
                Source = MetricSource.Indicator, AllowedOperatorsCsv = Numeric,
                MinValue = 0m, MaxValue = 1_000_000m, Unit = "INR",
            },
            new MetricConceptEntity
            {
                Name = "Sma200", DisplayName = "200-day moving average", ColumnName = "Sma200",
                Source = MetricSource.Indicator, AllowedOperatorsCsv = Numeric,
                MinValue = 0m, MaxValue = 1_000_000m, Unit = "INR",
            },
            new MetricConceptEntity
            {
                Name = "Volatility30", DisplayName = "30-day volatility", ColumnName = "Vol30",
                Source = MetricSource.Indicator, AllowedOperatorsCsv = Numeric,
                MinValue = 0m, MaxValue = 10m,
                Description = "Annualised realised volatility over 30 trading days.",
            },
            new MetricConceptEntity
            {
                Name = "ClosePrice", DisplayName = "Close price", ColumnName = "Close",
                Source = MetricSource.PriceBar, AllowedOperatorsCsv = Numeric,
                MinValue = 0m, MaxValue = 1_000_000m, Unit = "INR",
                Description = "Last traded price. Execution price, not the return series (§4.4).",
            });

        await db.SaveChangesAsync(ct);
    }
}
