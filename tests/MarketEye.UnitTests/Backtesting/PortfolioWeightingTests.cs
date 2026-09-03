using FluentAssertions;
using MarketEye.Application.Backtesting;
using Xunit;

namespace MarketEye.UnitTests.Backtesting;

public class PortfolioWeightingTests
{
    [Fact]
    public void EqualWeight_splits_evenly_across_every_security()
    {
        var weights = PortfolioWeighting.EqualWeight([1, 2, 3, 4]);

        weights.Should().HaveCount(4);
        weights.Values.Should().AllSatisfy(w => w.Should().Be(0.25m));
        weights.Values.Sum().Should().Be(1m);
    }

    [Fact]
    public void EqualWeight_of_an_empty_universe_is_empty()
    {
        PortfolioWeighting.EqualWeight([]).Should().BeEmpty();
    }

    [Fact]
    public void MarketCapWeight_is_not_implemented_in_v1()
    {
        // Decided with the user (PLAN.md §14): modelled but deliberately unimplemented, mirroring
        // §6's OR/NOT precedent -- a rejected-but-representable option, not a silent fallback.
        var act = () => PortfolioWeighting.MarketCapWeight(new Dictionary<int, decimal> { [1] = 100m });

        act.Should().Throw<NotSupportedException>();
    }
}
