using FluentAssertions;
using MarketEye.Application.Backtesting;
using Xunit;

namespace MarketEye.UnitTests.Backtesting;

public class TradeListBuilderTests
{
    [Fact]
    public void A_new_target_position_from_zero_is_a_buy()
    {
        var current = new Dictionary<int, decimal>();
        var target = new Dictionary<int, decimal> { [1] = 0.5m };

        var trades = TradeListBuilder.Diff(current, target, portfolioValue: 1000m);

        trades.Should().ContainSingle();
        trades[0].SecurityId.Should().Be(1);
        trades[0].IsBuy.Should().BeTrue();
        trades[0].Notional.Should().Be(500m); // 0.5 * 1000
    }

    [Fact]
    public void Dropping_a_holding_to_zero_is_a_sell()
    {
        var current = new Dictionary<int, decimal> { [1] = 0.3m };
        var target = new Dictionary<int, decimal>();

        var trades = TradeListBuilder.Diff(current, target, portfolioValue: 1000m);

        trades.Should().ContainSingle();
        trades[0].IsBuy.Should().BeFalse();
        trades[0].Notional.Should().Be(300m);
    }

    [Fact]
    public void A_below_threshold_delta_generates_no_trade()
    {
        // §7's rebalance should not pay real costs to close a fractional, economically
        // meaningless gap between current and target weight.
        var current = new Dictionary<int, decimal> { [1] = 0.2000m };
        var target = new Dictionary<int, decimal> { [1] = 0.20005m };

        var trades = TradeListBuilder.Diff(current, target, portfolioValue: 1000m);

        trades.Should().BeEmpty();
    }

    [Fact]
    public void An_unchanged_holding_generates_no_trade()
    {
        var current = new Dictionary<int, decimal> { [1] = 0.5m, [2] = 0.5m };
        var target = new Dictionary<int, decimal> { [1] = 0.5m, [2] = 0.5m };

        TradeListBuilder.Diff(current, target, portfolioValue: 1000m).Should().BeEmpty();
    }
}
