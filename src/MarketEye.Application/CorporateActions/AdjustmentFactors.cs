using MarketEye.Domain.Entities;

namespace MarketEye.Application.CorporateActions;

/// <summary>
/// Converts corporate actions into price adjustment factors (PLAN.md §4.4, `docs/adr/0004`).
///
/// A factor is the multiplier applied to every price BEFORE the ex-date to make the series
/// continuous. Factors below 1 mean the raw price stepped down for a reason that was not a loss.
///
/// The three share-count actions use three different conventions, and mixing them up produces a
/// series that is wrong by exactly a factor of two — large enough to ruin a backtest, small enough
/// to look like a real move.
/// </summary>
public static class AdjustmentFactors
{
    /// <summary>
    /// Split, quoted new-for-old: "2-for-1" means each old share becomes 2, so
    /// <paramref name="newShares"/> = 2, <paramref name="oldShares"/> = 1, factor 0.5.
    /// </summary>
    public static decimal ForSplit(decimal newShares, decimal oldShares)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(newShares);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(oldShares);
        return oldShares / newShares;
    }

    /// <summary>
    /// Bonus issue, quoted as free-for-held: "1:1" means one FREE share for each share held, so
    /// the holder ends up with two. That is the same economics as a 2-for-1 split but the opposite
    /// numbers, which is precisely why bonuses cannot be fed through <see cref="ForSplit"/>.
    ///
    /// factor = held / (free + held)
    /// </summary>
    public static decimal ForBonus(decimal freeShares, decimal heldShares)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(freeShares);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(heldShares);
        return heldShares / (freeShares + heldShares);
    }

    /// <summary>
    /// Rights issue: <paramref name="offered"/> new shares per <paramref name="held"/> held, at
    /// <paramref name="subscriptionPrice"/>, against the cum-rights close.
    ///
    /// This is dilution, not a split. The theoretical ex-rights price is the weighted average of
    /// existing shares at market and new shares at the subscription price:
    ///
    ///     TERP   = (held × cum + offered × subscription) / (held + offered)
    ///     factor = TERP / cum
    ///
    /// A rights issue priced AT the market causes no dilution and yields a factor of 1 — a useful
    /// sanity check on any implementation.
    /// </summary>
    public static decimal ForRights(
        decimal offered, decimal held, decimal subscriptionPrice, decimal cumRightsPrice)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(offered);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(held);
        ArgumentOutOfRangeException.ThrowIfNegative(subscriptionPrice);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cumRightsPrice);

        var terp = ((held * cumRightsPrice) + (offered * subscriptionPrice)) / (held + offered);
        return terp / cumRightsPrice;
    }

    /// <summary>
    /// Cash dividend. §4.4: dividends adjust RETURNS, not raw prices. The factor exists so
    /// AdjClose captures total return; raw Close is left alone so execution still uses what
    /// actually traded.
    ///
    /// factor = (cum-dividend close − dividend) / cum-dividend close
    /// </summary>
    public static decimal ForDividend(decimal dividendPerShare, decimal cumDividendClose)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(dividendPerShare);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cumDividendClose);

        // A dividend at or above the whole share price is a data error -- a special dividend that
        // large is not impossible, but a factor <= 0 would drive adjusted prices negative and
        // corrupt every return computed from them, so it fails loudly instead.
        if (dividendPerShare >= cumDividendClose)
        {
            throw new ArgumentException(
                $"Dividend {dividendPerShare} is not less than the cum-dividend close " +
                $"{cumDividendClose}. This is a data error, not a valid adjustment.",
                nameof(dividendPerShare));
        }
        return (cumDividendClose - dividendPerShare) / cumDividendClose;
    }
}
