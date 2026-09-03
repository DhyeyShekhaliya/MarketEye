using MarketEye.Domain.Screening;

namespace MarketEye.Domain.Backtesting;

/// <summary>
/// The backtest configuration (PLAN.md §7). Every assumption a run makes is a field here, not a
/// hardcoded constant in the engine — §7 requires assumptions to be visible in the UI next to the
/// equity curve, and that is only possible if they all live on one object that gets serialised
/// and displayed verbatim.
///
/// Deliberately has NO separate Universe property. §7's pseudocode lists Universe and Criteria as
/// two fields, but <see cref="ScreenCriteria.Universe"/> already carries exchange/sector/index
/// constraints and is the only universe the compiler ever reads — a second field here would be a
/// second, unreconciled source of truth.
/// </summary>
public sealed record BacktestDefinition
{
    public required ScreenCriteria Criteria { get; init; }

    public required DateOnly StartDate { get; init; }
    public required DateOnly EndDate { get; init; }

    public required RebalanceFrequency RebalanceFrequency { get; init; }

    /// <summary>
    /// Only <see cref="Backtesting.WeightingMethod.EqualWeight"/> is implemented in v1.
    /// MarketCapWeight is modelled — mirroring §6's "model the tree, implement one path" precedent
    /// for OR/NOT — but throws <see cref="NotSupportedException"/> until a later phase builds it.
    /// </summary>
    public WeightingMethod WeightingMethod { get; init; } = WeightingMethod.EqualWeight;

    public required decimal InitialCapital { get; init; }

    /// <summary>§7: fills happen at T+1, never at T's close. NextOpen is the only sane v1 default.</summary>
    public ExecutionPriceRule ExecutionPrice { get; init; } = ExecutionPriceRule.NextOpen;

    /// <summary>
    /// Default 23bps: India's round-trip STT (both legs) + stamp duty + exchange charges + SEBI
    /// fees + GST, per PLAN.md §7 revision 3. Not the US-shaped 10bps the original plan assumed.
    /// </summary>
    public int TransactionCostBps { get; init; } = 23;

    public int SlippageBps { get; init; } = 5;

    public int? MaxPositions { get; init; }

    /// <summary>
    /// §14: a config string, not an IBenchmarkProvider — one benchmark exists today, and adding a
    /// second later is a new ticker value, not a new abstraction. Null skips the comparison
    /// entirely rather than failing the run when benchmark data is unavailable.
    /// </summary>
    public string? BenchmarkTicker { get; init; } = "NIFTY50TR";
}

public enum RebalanceFrequency
{
    Monthly = 0,
    Quarterly = 1,
    Annual = 2,
}

public enum WeightingMethod
{
    EqualWeight = 0,

    /// <summary>Not implemented in v1 — see <see cref="BacktestDefinition.WeightingMethod"/>.</summary>
    MarketCapWeight = 1,
}

public enum ExecutionPriceRule
{
    NextOpen = 0,
    NextClose = 1,
}
