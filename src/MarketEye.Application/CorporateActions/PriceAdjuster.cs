using MarketEye.Domain.Entities;

namespace MarketEye.Application.CorporateActions;

/// <summary>
/// Computes <see cref="PriceBar.AdjClose"/> from raw closes and corporate actions (§4.4).
///
/// The rule that makes multi-year backtests correct: raw Close is what traded and is never
/// rewritten; AdjClose is derived and is recomputed from scratch whenever actions change.
/// Recomputing rather than mutating in place is what keeps a re-ingested action from adjusting
/// the same bar twice.
/// </summary>
public static class PriceAdjuster
{
    /// <summary>
    /// Returns adjusted closes aligned to <paramref name="bars"/>.
    ///
    /// Walks backwards from the most recent bar, carrying a cumulative factor. Each action's
    /// factor applies to every bar strictly BEFORE its ex-date, which is what makes the series
    /// continuous across the event.
    /// </summary>
    public static decimal[] AdjustedCloses(
        IReadOnlyList<PriceBar> bars, IReadOnlyList<CorporateAction> actions)
    {
        var adjusted = new decimal[bars.Count];
        if (bars.Count == 0) return adjusted;

        // Only price-affecting actions carry a factor. Ticker changes and mergers do not move the
        // price by themselves, and a delisting ends the series rather than rescaling it.
        var priceActions = actions
            .Where(a => a.AdjustmentFactor is > 0)
            .OrderByDescending(a => a.EffectiveDate)
            .ToList();

        var cumulative = 1m;
        var actionIndex = 0;

        for (var i = bars.Count - 1; i >= 0; i--)
        {
            // Apply every action whose ex-date is after this bar. Applied in descending date
            // order so the factors compound in the right sequence when several land close
            // together -- a split and a dividend in the same week is ordinary, not exotic.
            while (actionIndex < priceActions.Count &&
                   priceActions[actionIndex].EffectiveDate > bars[i].Date)
            {
                cumulative *= priceActions[actionIndex].AdjustmentFactor!.Value;
                actionIndex++;
            }

            adjusted[i] = bars[i].Close * cumulative;
        }
        return adjusted;
    }
}
