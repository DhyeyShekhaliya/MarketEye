namespace MarketEye.Domain.Entities;

/// <summary>
/// A completed backtest execution (PLAN.md §7, §7 "record full portfolio state").
///
/// Stores the resolved <see cref="RebalanceFrequency"/>/definition as JSON (mirrors
/// <see cref="ScreenRun.CriteriaJson"/>/<see cref="SavedStrategy.CriteriaJson"/>) rather than a
/// normalised table of its own fields, and the equity curve as a JSON blob rather than a
/// per-day table — nothing today needs to query across many runs' daily NAV, and this matches the
/// existing JSON-blob convention for reproducible, replay-once records.
/// </summary>
public class BacktestRun
{
    public long Id { get; set; }

    /// <summary>
    /// Set when this run was launched against a saved strategy (from /backtest's strategy picker).
    /// Null on delete, mirroring ScreenRun.SavedStrategyId: a completed backtest is a historical
    /// record that outlives the strategy that produced it. Lets a shared strategy's public page
    /// (Phase 4 "Strategy sharing") show its most recent backtest without a second lookup table.
    /// </summary>
    public int? SavedStrategyId { get; set; }
    public SavedStrategy? SavedStrategy { get; set; }

    /// <summary>Serialised BacktestDefinition. The UI's assumptions panel renders this, never a
    /// hand-typed copy, so it cannot drift from what actually ran.</summary>
    public required string DefinitionJson { get; set; }

    public DateTimeOffset RunAt { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }

    public decimal InitialCapital { get; set; }
    public decimal FinalEquity { get; set; }

    public decimal CagrGross { get; set; }
    public decimal CagrNet { get; set; }
    public decimal MaxDrawdown { get; set; }
    public decimal Sharpe { get; set; }
    public decimal Sortino { get; set; }
    public decimal WinRate { get; set; }
    public decimal AnnualTurnover { get; set; }
    public decimal TotalCostsPaid { get; set; }

    public string? BenchmarkTicker { get; set; }
    public decimal? BenchmarkCagr { get; set; }

    /// <summary>JSON array of {Date, Nav}, one point per trading day in the backtest window.</summary>
    public required string EquityCurveJson { get; set; }

    /// <summary>JSON array of {Date, Nav}, rebased to InitialCapital. Null when no benchmark data
    /// was available for the requested window — a missing benchmark never fails the run.</summary>
    public string? BenchmarkCurveJson { get; set; }

    public int DurationMs { get; set; }

    public List<BacktestRebalance> Rebalances { get; set; } = [];
}
