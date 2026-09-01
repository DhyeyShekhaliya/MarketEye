using MarketEye.Domain.Entities;

// Namespace is 'Ratios', not 'Fundamentals': a namespace matching the entity type name shadows it
// and the compiler resolves 'Fundamentals' to the namespace instead.
namespace MarketEye.Application.Ratios;

/// <summary>
/// Derives <see cref="FundamentalRatios"/> from reported figures and a price (PLAN.md §4.3).
///
/// Derived rather than read from the provider's own `keyMetrics`, deliberately: we control the
/// basis and the as-of date. The provider's ratios carry neither, and §4.1 makes both load-bearing
/// — a P/E computed against today's price is meaningless for a screen run as of 2023.
///
/// Every method returns null when an input is missing or the result would be meaningless. A null
/// ratio removes a security from a screen on that metric; a fabricated one puts it in the results
/// wearing a number nobody computed.
/// </summary>
public static class RatioCalculator
{
    /// <summary>
    /// Builds the ratio row for one reported period.
    /// </summary>
    /// <param name="price">
    /// Close on or before <see cref="Fundamentals.ReportedDate"/>. Must NOT be the latest price:
    /// using today's close for a 2023 filing is lookahead, and it would quietly rewrite history
    /// every time the job re-ran.
    /// </param>
    public static FundamentalRatios From(Fundamentals f, decimal? price, ReportingBasis basis)
    {
        var marketCap = MarketCap(price, f.SharesOutstanding);

        return new FundamentalRatios
        {
            SecurityId = f.SecurityId,
            ReportedDate = f.ReportedDate,
            Basis = basis,
            MarketCap = marketCap,
            Pe = Divide(marketCap, f.NetIncome, allowNegativeDenominator: false),
            Pb = Divide(marketCap, f.ShareholdersEquity, allowNegativeDenominator: false),
            Ps = Divide(marketCap, f.Revenue, allowNegativeDenominator: false),
            Roe = Percent(f.NetIncome, f.ShareholdersEquity),
            DebtToEquity = Divide(f.TotalDebt, f.ShareholdersEquity, allowNegativeDenominator: false),
            GrossMargin = GrossMarginPercent(f.Revenue, f.CostOfRevenue),

            // Not derivable from what the provider returns. Left null rather than approximated:
            // ROIC needs invested capital and NOPAT, FCF yield needs operating cash flow and
            // capex. A plausible-looking wrong number is worse than an absent one, because the
            // screen would silently rank on it.
            Roic = null,
            FcfYield = null,
        };
    }

    /// <summary>Shares are reported in the same unit as the financials, so no scaling is applied.</summary>
    public static decimal? MarketCap(decimal? price, decimal? shares) =>
        price is > 0 && shares is > 0 ? price * shares : null;

    /// <summary>
    /// Ratio of two figures.
    ///
    /// A negative denominator is refused for valuation multiples. A company with negative earnings
    /// has no meaningful P/E — reporting one produces a NEGATIVE multiple that sorts as "cheapest"
    /// in an ascending screen, so the worst businesses come top. §8.3 expects known-bad strategies
    /// to backtest badly; this is one way they would accidentally look good.
    /// </summary>
    public static decimal? Divide(decimal? numerator, decimal? denominator, bool allowNegativeDenominator)
    {
        if (numerator is null || denominator is null) return null;
        if (denominator == 0) return null;
        if (!allowNegativeDenominator && denominator < 0) return null;
        return numerator / denominator;
    }

    /// <summary>Return on equity as a percentage. Negative equity makes it meaningless.</summary>
    public static decimal? Percent(decimal? numerator, decimal? denominator)
    {
        var ratio = Divide(numerator, denominator, allowNegativeDenominator: false);
        return ratio is null ? null : ratio * 100m;
    }

    /// <summary>(Revenue − cost of revenue) / revenue, as a percentage.</summary>
    public static decimal? GrossMarginPercent(decimal? revenue, decimal? costOfRevenue)
    {
        if (revenue is null || costOfRevenue is null) return null;
        if (revenue <= 0) return null;
        return (revenue.Value - costOfRevenue.Value) / revenue.Value * 100m;
    }
}
