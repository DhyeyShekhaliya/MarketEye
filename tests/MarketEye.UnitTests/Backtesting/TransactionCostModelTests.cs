using FluentAssertions;
using MarketEye.Application.Backtesting;
using Xunit;

namespace MarketEye.UnitTests.Backtesting;

public class TransactionCostModelTests
{
    [Fact]
    public void Cost_is_charged_on_notional_at_the_combined_bps_rate()
    {
        // India-calibrated default (§7 revision 3): 23bps + 5bps = 28bps round figure.
        // 100,000 * 28 / 10,000 = 280.
        var cost = TransactionCostModel.Cost(notionalTraded: 100_000m, transactionCostBps: 23, slippageBps: 5);

        cost.Should().Be(280m);
    }

    [Fact]
    public void Zero_notional_costs_nothing()
    {
        TransactionCostModel.Cost(0m, 23, 5).Should().Be(0m);
    }

    [Fact]
    public void Zero_bps_costs_nothing_regardless_of_notional()
    {
        // The gross simulation in BacktestEngine relies on exactly this: zero cost inputs must
        // produce zero cost, so CagrGross is a genuinely cost-free curve, not an approximation.
        TransactionCostModel.Cost(1_000_000m, 0, 0).Should().Be(0m);
    }
}
