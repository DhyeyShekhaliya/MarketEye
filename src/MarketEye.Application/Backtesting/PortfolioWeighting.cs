namespace MarketEye.Application.Backtesting;

/// <summary>
/// Turns a target holdings list into portfolio weights (PLAN.md §7 step 4).
///
/// Only EqualWeight is implemented in v1 — a deliberate, documented deviation from §7's literal
/// "EqualWeight (v1) | MarketCapWeight" listing, decided with the user rather than silently
/// picked: MarketCapWeight is deferred to a later phase rather than built alongside it.
/// </summary>
public static class PortfolioWeighting
{
    public static IReadOnlyDictionary<int, decimal> EqualWeight(IReadOnlyList<int> securityIds)
    {
        if (securityIds.Count == 0) return new Dictionary<int, decimal>();

        var weight = 1m / securityIds.Count;
        return securityIds.ToDictionary(id => id, _ => weight);
    }

    /// <summary>
    /// Not implemented in v1. Kept as a named, throwing path (mirrors §6's OR/NOT precedent —
    /// model the option, reject it explicitly) rather than an absent case that silently falls
    /// through to EqualWeight.
    /// </summary>
    public static IReadOnlyDictionary<int, decimal> MarketCapWeight(
        IReadOnlyDictionary<int, decimal> marketCapsBySecurityId)
    {
        throw new NotSupportedException(
            "MarketCapWeight is not implemented in v1 (deferred by decision). " +
            "BacktestDefinition.WeightingMethod must be EqualWeight.");
    }
}
