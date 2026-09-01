namespace MarketEye.Domain.Entities;

/// <summary>
/// Pre-computed technical indicators for one security on one date (PLAN.md §4.3).
///
/// Computed at ingest and stored, never at query time — screening must stay a flat indexable
/// WHERE. This trades write amplification for read latency, argued in `docs/adr/0003`'s sibling
/// ADR and §4.3.
///
/// All values derive from <see cref="PriceBar.AdjClose"/>, not raw Close: an unadjusted series has
/// a discontinuity at every split and bonus, which would put a false spike into every indicator.
/// </summary>
public class IndicatorSet
{
    public int SecurityId { get; set; }
    public DateOnly Date { get; set; }

    public decimal? Sma50 { get; set; }
    public decimal? Sma200 { get; set; }
    public decimal? Rsi14 { get; set; }
    public decimal? Macd { get; set; }
    public decimal? MacdSignal { get; set; }
    public decimal? Atr14 { get; set; }

    /// <summary>Annualised 30-day realised volatility.</summary>
    public decimal? Vol30 { get; set; }
}
