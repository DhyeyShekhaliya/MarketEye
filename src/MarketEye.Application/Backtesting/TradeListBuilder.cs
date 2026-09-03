namespace MarketEye.Application.Backtesting;

/// <summary>The diff between current and target portfolio weights (PLAN.md §7 step 5).</summary>
public sealed record Trade
{
    public required int SecurityId { get; init; }
    public required decimal CurrentWeight { get; init; }
    public required decimal TargetWeight { get; init; }

    /// <summary>Absolute traded notional. Costs are charged on this, never on portfolio value (§7).</summary>
    public required decimal Notional { get; init; }

    public decimal DeltaWeight => TargetWeight - CurrentWeight;
    public bool IsBuy => DeltaWeight > 0;
}

public static class TradeListBuilder
{
    /// <summary>
    /// Below this, a weight difference is noise rather than a real rebalance decision — trading to
    /// close a fractional gap would pay real costs to achieve nothing.
    /// </summary>
    private const decimal MinimumDeltaWeight = 0.0001m;

    public static IReadOnlyList<Trade> Diff(
        IReadOnlyDictionary<int, decimal> currentWeights,
        IReadOnlyDictionary<int, decimal> targetWeights,
        decimal portfolioValue)
    {
        var ids = currentWeights.Keys.Union(targetWeights.Keys);
        var trades = new List<Trade>();

        foreach (var id in ids)
        {
            var current = currentWeights.GetValueOrDefault(id);
            var target = targetWeights.GetValueOrDefault(id);
            var delta = target - current;
            if (Math.Abs(delta) < MinimumDeltaWeight) continue;

            trades.Add(new Trade
            {
                SecurityId = id,
                CurrentWeight = current,
                TargetWeight = target,
                Notional = Math.Abs(delta) * portfolioValue,
            });
        }
        return trades;
    }
}
