using MarketEye.Infrastructure.Screening;

namespace MarketEye.Infrastructure.Backtesting;

/// <summary>A realised fill: what actually got traded, on which day, at what price.</summary>
public sealed record Fill(int SecurityId, DateOnly Date, decimal Price);

/// <summary>
/// Resolves a trade to a fill, or decides it cannot be filled (PLAN.md §7 revision 3).
///
/// A circuit-locked bar cannot be filled at that price, so this searches forward day-by-day for
/// the first tradeable bar, capped at the same carry-forward window §7 step 10 uses for missing
/// prices. <see cref="PointInTimeGuard.RequireNotCircuitLocked"/> is called immediately before
/// constructing the fill as a backstop, even though the search loop already filtered locked bars —
/// the same "re-check, don't trust the caller" pattern the guard's own doc comment describes.
/// </summary>
public sealed class FillExecutor(BacktestPriceRepository priceRepo)
{
    /// <summary>
    /// Finds the first fillable bar for <paramref name="securityId"/> at or after
    /// <paramref name="earliestDate"/>, considering at most <paramref name="maxRetryDays"/>
    /// candidate trading days. Returns null if every candidate in that window is circuit-locked or
    /// no bars exist at all — the caller drops the trade and logs it.
    /// </summary>
    public async Task<Fill?> TryFillAsync(
        int securityId, DateOnly earliestDate, DateOnly windowEnd, int maxRetryDays, CancellationToken ct)
    {
        var bars = await priceRepo.GetBarsAsync([securityId], earliestDate, windowEnd, ct);
        if (!bars.TryGetValue(securityId, out var series) || series.Count == 0) return null;

        var candidates = series.OrderBy(b => b.Date).Take(maxRetryDays).ToList();
        var fillable = candidates.FirstOrDefault(b => !b.IsCircuitLocked);
        if (fillable is null) return null;

        PointInTimeGuard.RequireNotCircuitLocked(fillable);
        return new Fill(fillable.SecurityId, fillable.Date, fillable.Close);
    }
}
