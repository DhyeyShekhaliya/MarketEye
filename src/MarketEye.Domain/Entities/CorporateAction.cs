namespace MarketEye.Domain.Entities;

/// <summary>
/// A price- or share-count-affecting event (PLAN.md §4.3).
///
/// Indian markets add bonus and rights issues to the US set, and they are not cosmetic variants
/// of a split — see `docs/adr/0004`.
/// </summary>
public class CorporateAction
{
    public long Id { get; set; }

    public int SecurityId { get; set; }
    public Security? Security { get; set; }

    /// <summary>Ex-date: the first date on which the price reflects the action.</summary>
    public DateOnly EffectiveDate { get; set; }

    public CorporateActionType ActionType { get; set; }

    /// <summary>
    /// The multiplier applied to all prices BEFORE <see cref="EffectiveDate"/> to make the series
    /// continuous. Storing a computed factor rather than a raw ratio lets splits, bonuses and
    /// rights share one adjustment code path, which is the only way the three stay consistent.
    ///
    /// A 2-for-1 split and a 1:1 bonus both yield 0.5 despite opposite ratio conventions —
    /// converting at ingest is exactly where that trap gets defused.
    /// </summary>
    public decimal? AdjustmentFactor { get; set; }

    /// <summary>Per-share dividend in INR. Adjusts returns, not raw prices (§4.4).</summary>
    public decimal? DividendAmount { get; set; }

    /// <summary>Set on a ticker change. Must not create a second Security row (§4.4).</summary>
    public string? NewTicker { get; set; }

    /// <summary>The provider's own text, kept for auditing an adjustment that looks wrong.</summary>
    public string? RawDescription { get; set; }
}

public enum CorporateActionType
{
    Split = 0,
    Dividend = 1,
    TickerChange = 2,
    Merger = 3,
    Delisting = 4,

    /// <summary>
    /// Free additional shares. Ratio convention is the inverse of a split: "1:1" means one free
    /// share per share held, i.e. the same economics as a 2-for-1 split.
    /// </summary>
    Bonus = 5,

    /// <summary>
    /// Shares offered to existing holders below market. Dilutive — not a split. Needs its own
    /// factor derived from the offer price and ratio.
    /// </summary>
    Rights = 6,
}
