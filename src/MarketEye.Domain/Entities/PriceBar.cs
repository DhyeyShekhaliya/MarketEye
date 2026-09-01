namespace MarketEye.Domain.Entities;

/// <summary>
/// One daily bar (PLAN.md §4.3). Clustered columnstore — wide, append-only, scanned analytically.
///
/// <see cref="Close"/> and <see cref="AdjClose"/> are separate columns and must never be conflated
/// (§4.4). Over five years dividends and bonus issues are a large fraction of Indian equity return;
/// computing returns from raw Close understates performance systematically, and inconsistently
/// across sectors.
/// </summary>
public class PriceBar
{
    public int SecurityId { get; set; }
    public DateOnly Date { get; set; }

    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }

    /// <summary>What actually traded. Use for display and for backtest execution (§4.4, §7).</summary>
    public decimal Close { get; set; }

    /// <summary>
    /// Adjusted for splits, bonus issues, rights issues and dividends. Use for return
    /// calculation only (§4.4). Recomputed when a corporate action is ingested.
    /// </summary>
    public decimal AdjClose { get; set; }

    public long Volume { get; set; }

    /// <summary>
    /// True when the security was locked at a price band on this date. Indian equities have daily
    /// circuit limits and a locked stock cannot be traded at that price, so the backtester must
    /// refuse the fill rather than assume one (§7, revision 3).
    /// </summary>
    public bool IsCircuitLocked { get; set; }
}
