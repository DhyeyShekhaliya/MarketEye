namespace MarketEye.Infrastructure.MarketData.IndianApi;

/// <summary>
/// Derives a conservative <c>ReportedDate</c> for a fiscal period (PLAN.md §4.1).
///
/// **The provider does not supply one.** Its `financials` entries carry `EndDate` (the fiscal
/// period end) and a `StatementDate` field that is a constant in the data and therefore unusable.
/// §4.1 needs the date the market actually learned the figures, and without it the point-in-time
/// guarantee is broken.
///
/// Using `EndDate` directly would be lookahead bias of the worst kind: a screen run the day after
/// a fiscal year closes would "know" results published two months later. That produces backtests
/// which look excellent and are entirely fictional.
///
/// So the reported date is estimated from SEBI's filing deadlines, and estimated **late**:
///
///   Annual  : within 60 days of the financial year end
///   Quarterly: within 45 days of the quarter end
///
/// The asymmetry is deliberate. Erring late means a screen occasionally misses a company whose
/// results were already public — a missed opportunity, visible as a slightly conservative result.
/// Erring early means inventing knowledge nobody had, which manufactures alpha out of nothing.
/// Only one of those two errors is survivable in a backtest.
///
/// This is a documented approximation, not a fact. Every ingested row is marked
/// <see cref="Fundamentals.IsReportedDateEstimated"/> so no analysis can mistake it for a filing
/// date, and §12's reconciliation should sample real announcement dates against it.
/// </summary>
public static class ReportingLag
{
    /// <summary>SEBI LODR: annual results within 60 days of the financial year end.</summary>
    public const int AnnualFilingDays = 60;

    /// <summary>SEBI LODR: quarterly results within 45 days of the quarter end.</summary>
    public const int QuarterlyFilingDays = 45;

    /// <summary>
    /// Latest plausible publication date for a period ending <paramref name="periodEnd"/>.
    /// </summary>
    public static DateOnly EstimateReportedDate(DateOnly periodEnd, bool isAnnual) =>
        periodEnd.AddDays(isAnnual ? AnnualFilingDays : QuarterlyFilingDays);

    /// <summary>The provider labels statements "Annual" or "Interim".</summary>
    public static bool IsAnnual(string? type) =>
        string.Equals(type, "Annual", StringComparison.OrdinalIgnoreCase);
}
