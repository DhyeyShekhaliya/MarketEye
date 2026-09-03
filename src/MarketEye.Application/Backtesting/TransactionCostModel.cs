namespace MarketEye.Application.Backtesting;

/// <summary>
/// §7: costs are deducted on TRADED NOTIONAL, never on portfolio value. A monthly-rebalanced
/// screen can turn over 40%+ a year, and charging costs against the whole portfolio each time
/// would overstate drag on a strategy that only traded a small slice of it.
/// </summary>
public static class TransactionCostModel
{
    public static decimal Cost(decimal notionalTraded, int transactionCostBps, int slippageBps) =>
        notionalTraded * (transactionCostBps + slippageBps) / 10_000m;
}
