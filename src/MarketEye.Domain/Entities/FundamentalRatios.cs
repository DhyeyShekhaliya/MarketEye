namespace MarketEye.Domain.Entities;

/// <summary>
/// Ratios derived from <see cref="Fundamentals"/> and price (PLAN.md §4.3).
///
/// Derived at ingest for the same reason indicators are: screening must stay a flat indexable
/// WHERE. Keyed by ReportedDate rather than FiscalPeriodEnd so a point-in-time read stays a
/// simple range predicate (§4.1).
/// </summary>
public class FundamentalRatios
{
    public int SecurityId { get; set; }
    public DateOnly ReportedDate { get; set; }

    public decimal? Pe { get; set; }
    public decimal? Pb { get; set; }
    public decimal? Ps { get; set; }
    public decimal? Roe { get; set; }
    public decimal? Roic { get; set; }
    public decimal? DebtToEquity { get; set; }
    public decimal? GrossMargin { get; set; }
    public decimal? FcfYield { get; set; }

    /// <summary>Market capitalisation in INR at ReportedDate.</summary>
    public decimal? MarketCap { get; set; }

    /// <summary>
    /// Indian companies file both standalone and consolidated accounts and they differ materially
    /// for any group with subsidiaries. Mixing the two across securities makes ratios
    /// incomparable, so the basis is recorded rather than assumed (`docs/adr/0004`).
    /// </summary>
    public ReportingBasis Basis { get; set; }
}

public enum ReportingBasis
{
    Consolidated = 0,
    Standalone = 1,
}
