namespace MarketEye.Domain.Entities;

/// <summary>
/// One rebalance event within a <see cref="BacktestRun"/> (PLAN.md §7 rebalance loop).
///
/// <see cref="SignalDate"/> (T) and <see cref="ExecutionDate"/> (T+1) are kept as two separate
/// columns rather than one, so a query or an audit can see the T+1 rule was actually honoured
/// without deserialising <see cref="HoldingsJson"/>.
/// </summary>
public class BacktestRebalance
{
    public long Id { get; set; }

    public long BacktestRunId { get; set; }
    public BacktestRun? BacktestRun { get; set; }

    /// <summary>T — the date the screen ran and target weights were computed.</summary>
    public DateOnly SignalDate { get; set; }

    /// <summary>T+1 — the date trades actually filled (§7: never T's close).</summary>
    public DateOnly ExecutionDate { get; set; }

    public decimal CashAfter { get; set; }
    public decimal PortfolioValueAfter { get; set; }
    public decimal CostsPaid { get; set; }

    /// <summary>Traded notional as a fraction of portfolio value, this rebalance only.</summary>
    public decimal TurnoverPct { get; set; }

    /// <summary>JSON array of {SecurityId, Ticker, Weight, Shares, Price}.</summary>
    public required string HoldingsJson { get; set; }
}
