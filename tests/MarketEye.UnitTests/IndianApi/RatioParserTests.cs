using FluentAssertions;
using MarketEye.Infrastructure.MarketData.IndianApi;
using Xunit;

namespace MarketEye.UnitTests.IndianApi;

/// <summary>
/// The provider embeds ratios in prose, so this parser sits directly upstream of every price
/// adjustment. ADR-0004: a misread ratio halves or doubles the whole historical series, and the
/// result looks smooth and plausible. These tests care as much about what it REFUSES to parse as
/// what it parses.
/// </summary>
public class RatioParserTests
{
    [Fact]
    public void Parses_the_real_Reliance_bonus_remark()
    {
        // Verbatim from the provider's response for RELIANCE, 2024-10-28.
        const string remarks = "Bonus issue in the ratio of 1:1 of Rs. 10/-.";

        CorporateActionRatioParser.BonusFactor(remarks).Should().Be(0.5m,
            "a 1:1 bonus doubles the share count, so prior prices halve");
    }

    [Theory]
    [InlineData("Bonus issue in the ratio of 1:2", 2.0 / 3.0)]   // 1 free per 2 held -> 2/3
    [InlineData("Bonus issue in the ratio of 3:1", 0.25)]        // 3 free per 1 held -> 1/4
    [InlineData("Bonus in the ratio of 2 : 5", 5.0 / 7.0)]
    public void Parses_bonus_ratios(string remarks, double expected)
    {
        CorporateActionRatioParser.BonusFactor(remarks)!.Value
            .Should().BeApproximately((decimal)expected, 0.0001m);
    }

    [Theory]
    [InlineData("Face value split from Rs. 10 to Rs. 5", 0.5)]
    [InlineData("Stock split from Rs10/- to Rs2/-", 0.2)]
    [InlineData("Split from Rs. 2 to Rs. 1", 0.5)]
    public void Parses_split_face_value_changes(string remarks, double expected)
    {
        CorporateActionRatioParser.SplitFactor(remarks)!.Value
            .Should().BeApproximately((decimal)expected, 0.0001m);
    }

    [Fact]
    public void A_split_remark_is_not_read_as_a_share_ratio()
    {
        // The inversion this class exists to prevent. Both action types carry colon-separated
        // numbers, and crossing them over is silent and catastrophic.
        const string split = "Face value split from Rs. 10 to Rs. 5";
        CorporateActionRatioParser.BonusFactor(split).Should().BeNull(
            "there is no 'ratio of A:B' here, so the bonus parser must decline");
    }

    [Fact]
    public void A_bonus_remark_is_not_read_as_a_face_value_change()
    {
        CorporateActionRatioParser.SplitFactor("Bonus issue in the ratio of 1:1 of Rs. 10/-.")
            .Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Bonus issue declared")]
    [InlineData("Bonus issue in the ratio of unknown")]
    [InlineData("Scheme of arrangement approved")]
    public void Unparseable_remarks_return_null_rather_than_a_guess(string? remarks)
    {
        // A missing adjustment leaves a visible discontinuity someone will notice. A guessed one
        // produces a smooth series that is quietly wrong.
        CorporateActionRatioParser.BonusFactor(remarks).Should().BeNull();
        CorporateActionRatioParser.SplitFactor(remarks).Should().BeNull();
        CorporateActionRatioParser.RightsTerms(remarks).Should().BeNull();
    }

    [Fact]
    public void A_zero_denominator_is_refused()
    {
        CorporateActionRatioParser.BonusFactor("Bonus issue in the ratio of 1:0").Should().BeNull();
        CorporateActionRatioParser.SplitFactor("split from Rs. 0 to Rs. 5").Should().BeNull();
    }

    [Fact]
    public void Rights_terms_are_extracted_but_the_factor_is_left_to_the_caller()
    {
        // Rights dilution needs the cum-rights market price, which is not in the remark. The
        // parser returns terms only; AdjustmentFactors.ForRights computes the factor.
        var terms = CorporateActionRatioParser.RightsTerms(
            "Rights issue in the ratio of 1:5 at Rs. 250 per share");

        terms.Should().NotBeNull();
        terms!.Offered.Should().Be(1m);
        terms.Held.Should().Be(5m);
        terms.SubscriptionPrice.Should().Be(250m);
    }

    [Fact]
    public void Rights_without_a_stated_price_still_yield_the_ratio()
    {
        var terms = CorporateActionRatioParser.RightsTerms("Rights issue in the ratio of 2:9");

        terms.Should().NotBeNull();
        terms!.Offered.Should().Be(2m);
        terms.Held.Should().Be(9m);
        terms.SubscriptionPrice.Should().BeNull("the caller must supply it or skip the adjustment");
    }

    [Fact]
    public void A_stray_colon_elsewhere_in_the_sentence_is_not_treated_as_the_ratio()
    {
        // Anchoring on the word "ratio" is what prevents this.
        CorporateActionRatioParser.BonusFactor("Board meeting at 10:30 to consider a bonus issue")
            .Should().BeNull();
    }
}
