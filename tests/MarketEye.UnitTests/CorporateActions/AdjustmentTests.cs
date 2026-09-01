using FluentAssertions;
using MarketEye.Application.CorporateActions;
using MarketEye.Domain.Entities;
using Xunit;

namespace MarketEye.UnitTests.CorporateActionMath;

/// <summary>
/// §4.4 and ADR-0004. The expected values here are computed by hand from the definitions, not
/// from the implementation — the whole risk with adjustment math is that a wrong factor produces
/// a plausible-looking series.
/// </summary>
public class AdjustmentTests
{
    [Fact]
    public void A_two_for_one_split_halves_prior_prices()
    {
        AdjustmentFactors.ForSplit(newShares: 2, oldShares: 1).Should().Be(0.5m);
    }

    [Fact]
    public void A_one_for_two_reverse_split_doubles_prior_prices()
    {
        AdjustmentFactors.ForSplit(newShares: 1, oldShares: 2).Should().Be(2m);
    }

    [Fact]
    public void A_one_to_one_bonus_has_the_same_effect_as_a_two_for_one_split()
    {
        // The trap ADR-0004 names: "1:1" bonus and "2-for-1" split are the same economics with
        // opposite numbers. Feeding a 1:1 bonus into the split formula gives 1.0 -- no adjustment
        // at all -- and leaves a 50% cliff in the price series that reads as a crash.
        var bonus = AdjustmentFactors.ForBonus(freeShares: 1, heldShares: 1);
        var split = AdjustmentFactors.ForSplit(newShares: 2, oldShares: 1);

        bonus.Should().Be(0.5m);
        bonus.Should().Be(split);
        AdjustmentFactors.ForSplit(newShares: 1, oldShares: 1).Should().Be(1m,
            "which is what you would wrongly get by treating the bonus ratio as a split ratio");
    }

    [Fact]
    public void A_one_to_two_bonus_gives_two_thirds()
    {
        // One free share per two held: 2 shares become 3, so price x 2/3.
        AdjustmentFactors.ForBonus(freeShares: 1, heldShares: 2)
            .Should().BeApproximately(0.6666667m, 0.0000001m);
    }

    [Fact]
    public void A_rights_issue_priced_at_market_causes_no_dilution()
    {
        // TERP equals the cum price, so the factor is exactly 1. This is the sanity check that
        // catches a rights formula with the terms transposed.
        AdjustmentFactors.ForRights(offered: 1, held: 2, subscriptionPrice: 100m, cumRightsPrice: 100m)
            .Should().Be(1m);
    }

    [Fact]
    public void A_discounted_rights_issue_dilutes()
    {
        // 1 new share per 2 held at 50, cum price 100.
        // TERP = (2*100 + 1*50)/3 = 250/3 = 83.333...  factor = 0.83333...
        AdjustmentFactors.ForRights(offered: 1, held: 2, subscriptionPrice: 50m, cumRightsPrice: 100m)
            .Should().BeApproximately(0.8333333m, 0.0000001m);
    }

    [Fact]
    public void A_rights_issue_is_not_a_split()
    {
        // Guards against the shortcut of treating a rights issue as a share-count change. A 1-for-2
        // rights at a discount dilutes by ~17%, nowhere near the 33% a split formula would apply.
        var rights = AdjustmentFactors.ForRights(1, 2, 50m, 100m);
        var wrongAsSplit = AdjustmentFactors.ForSplit(newShares: 3, oldShares: 2);

        rights.Should().NotBe(wrongAsSplit);
        rights.Should().BeGreaterThan(wrongAsSplit);
    }

    [Fact]
    public void A_dividend_factor_reflects_the_yield()
    {
        // 5 rupee dividend on a 100 rupee close: factor 0.95.
        AdjustmentFactors.ForDividend(dividendPerShare: 5m, cumDividendClose: 100m)
            .Should().Be(0.95m);
    }

    [Fact]
    public void A_dividend_at_or_above_the_share_price_is_rejected_as_a_data_error()
    {
        // A factor <= 0 would drive adjusted prices negative and corrupt every return derived
        // from them. Failing loudly beats silently poisoning the series.
        var act = () => AdjustmentFactors.ForDividend(dividendPerShare: 100m, cumDividendClose: 100m);
        act.Should().Throw<ArgumentException>();
    }

    private static PriceBar Bar(string date, decimal close) => new()
    {
        SecurityId = 1, Date = DateOnly.Parse(date),
        Open = close, High = close, Low = close, Close = close, AdjClose = close, Volume = 1000,
    };

    private static CorporateAction Action(string date, CorporateActionType type, decimal factor) => new()
    {
        SecurityId = 1, EffectiveDate = DateOnly.Parse(date), ActionType = type, AdjustmentFactor = factor,
    };

    [Fact]
    public void Adjusted_closes_are_continuous_across_a_split()
    {
        // A stock at 100 that splits 2-for-1 trades at 50 afterwards. Raw close shows a 50% drop;
        // the adjusted series must show none, because no value was lost.
        var bars = new[]
        {
            Bar("2024-01-01", 100m),
            Bar("2024-01-02", 100m),
            Bar("2024-01-03", 50m),   // ex-date
            Bar("2024-01-04", 50m),
        };
        var actions = new[] { Action("2024-01-03", CorporateActionType.Split, 0.5m) };

        var adj = PriceAdjuster.AdjustedCloses(bars, actions);

        adj[0].Should().Be(50m);
        adj[1].Should().Be(50m);
        adj[2].Should().Be(50m);
        adj[3].Should().Be(50m);

        // And the raw closes are untouched -- execution still uses what actually traded (§4.4).
        bars[0].Close.Should().Be(100m);
    }

    [Fact]
    public void Multiple_actions_compound()
    {
        // A 2-for-1 split then a 1:1 bonus: prices before both are scaled by 0.5 * 0.5 = 0.25.
        var bars = new[]
        {
            Bar("2024-01-01", 400m),
            Bar("2024-02-01", 200m),   // after split
            Bar("2024-03-01", 100m),   // after bonus
        };
        var actions = new[]
        {
            Action("2024-01-15", CorporateActionType.Split, 0.5m),
            Action("2024-02-15", CorporateActionType.Bonus, 0.5m),
        };

        var adj = PriceAdjuster.AdjustedCloses(bars, actions);

        adj[0].Should().Be(100m);   // 400 * 0.25
        adj[1].Should().Be(100m);   // 200 * 0.5
        adj[2].Should().Be(100m);
    }

    [Fact]
    public void An_action_on_the_ex_date_itself_does_not_adjust_that_bar()
    {
        // The ex-date bar already trades at the adjusted price. Applying the factor to it as well
        // would halve it twice -- an off-by-one that is invisible except as a one-day fake crash.
        var bars = new[] { Bar("2024-01-03", 50m) };
        var actions = new[] { Action("2024-01-03", CorporateActionType.Split, 0.5m) };

        PriceAdjuster.AdjustedCloses(bars, actions)[0].Should().Be(50m);
    }

    [Fact]
    public void Non_price_actions_are_ignored()
    {
        // A ticker change or merger does not rescale the price by itself.
        var bars = new[] { Bar("2024-01-01", 100m), Bar("2024-02-01", 100m) };
        var actions = new[]
        {
            new CorporateAction
            {
                SecurityId = 1, EffectiveDate = DateOnly.Parse("2024-01-15"),
                ActionType = CorporateActionType.TickerChange, NewTicker = "NEWCO",
            },
        };

        PriceAdjuster.AdjustedCloses(bars, actions).Should().AllBeEquivalentTo(100m);
    }

    [Fact]
    public void With_no_actions_adjusted_equals_raw()
    {
        var bars = new[] { Bar("2024-01-01", 100m), Bar("2024-02-01", 110m) };
        var adj = PriceAdjuster.AdjustedCloses(bars, []);

        adj[0].Should().Be(100m);
        adj[1].Should().Be(110m);
    }

    [Fact]
    public void Recomputing_is_idempotent()
    {
        // Adjustment is derived from scratch, never applied in place. Re-ingesting the same action
        // must not adjust twice -- ingestion is required to be idempotent (§10 Phase 1).
        var bars = new[] { Bar("2024-01-01", 100m), Bar("2024-01-03", 50m) };
        var actions = new[] { Action("2024-01-03", CorporateActionType.Split, 0.5m) };

        var first = PriceAdjuster.AdjustedCloses(bars, actions);
        var second = PriceAdjuster.AdjustedCloses(bars, actions);

        second.Should().BeEquivalentTo(first);
    }
}
