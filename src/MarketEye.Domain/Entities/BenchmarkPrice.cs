namespace MarketEye.Domain.Entities;

/// <summary>
/// One day's value of a benchmark total-return index (PLAN.md §7, `docs/adr/0010`).
///
/// A config-string ticker (e.g. "NIFTY50TR"), not a table of benchmark metadata or an
/// IBenchmarkProvider abstraction — §14 already rejected the interface. Adding NIFTY 500 later is
/// rows with a different Ticker, not a schema change.
///
/// Total-return, not the price index: NIFTY publishes both, and comparing a backtest's AdjClose-
/// based returns against the price index would understate the benchmark the same way §4.4 warns
/// about for individual securities.
/// </summary>
public class BenchmarkPrice
{
    public required string Ticker { get; set; }
    public DateOnly Date { get; set; }
    public decimal TotalReturnIndexValue { get; set; }
}
