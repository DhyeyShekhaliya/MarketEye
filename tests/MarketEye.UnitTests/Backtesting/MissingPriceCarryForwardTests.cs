using FluentAssertions;
using MarketEye.Application.Backtesting;
using Xunit;

namespace MarketEye.UnitTests.Backtesting;

public class MissingPriceCarryForwardTests
{
    [Fact]
    public void A_bar_present_today_is_used_directly()
    {
        var decision = MissingPriceCarryForward.Resolve(todaysClose: 105m, lastKnownPrice: 100m, consecutiveMissingDays: 0);

        decision.Price.Should().Be(105m);
        decision.ForceExit.Should().BeFalse();
    }

    [Fact]
    public void A_missing_bar_within_the_window_carries_the_last_known_price()
    {
        var decision = MissingPriceCarryForward.Resolve(todaysClose: null, lastKnownPrice: 100m, consecutiveMissingDays: 2);

        decision.Price.Should().Be(100m);
        decision.ForceExit.Should().BeFalse();
    }

    [Fact]
    public void A_missing_bar_at_the_cap_forces_an_exit()
    {
        // §7 step 10: carry forward for up to 5 trading days, then force-exit. At exactly
        // MaxCarryForwardDays consecutive misses, the position must be closed rather than carried
        // a sixth time.
        var decision = MissingPriceCarryForward.Resolve(
            todaysClose: null, lastKnownPrice: 100m,
            consecutiveMissingDays: MissingPriceCarryForward.MaxCarryForwardDays);

        decision.ForceExit.Should().BeTrue();
        decision.Price.Should().BeNull();
    }

    [Fact]
    public void One_day_short_of_the_cap_still_carries_forward()
    {
        var decision = MissingPriceCarryForward.Resolve(
            todaysClose: null, lastKnownPrice: 100m,
            consecutiveMissingDays: MissingPriceCarryForward.MaxCarryForwardDays - 1);

        decision.ForceExit.Should().BeFalse();
        decision.Price.Should().Be(100m);
    }
}
