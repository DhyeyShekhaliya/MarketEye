using FluentAssertions;
using MarketEye.Domain.Screening;
using Xunit;

namespace MarketEye.UnitTests.Screening;

/// <summary>
/// The interpretation panel (§5.3) is the only place a threshold is visible before a screen runs,
/// so this rendering is what a user actually checks. A market cap shown as "50000000000" is a
/// number nobody verifies at a glance, which makes confirm-before-run theatre.
/// </summary>
public class CriteriaExplainerTests
{
    private static readonly CriteriaExplainer Explainer = new(SeededMetricVocabulary.Instance);

    private static Comparison Cmp(string field, ComparisonOperator op, decimal value) =>
        new() { Field = field, Operator = op, Value = value };

    [Fact]
    public void A_comparison_reads_as_the_metrics_display_name() =>
        Explainer.Explain(Cmp("PeRatio", ComparisonOperator.LessThan, 25m))
            .Should().Be("P/E ratio < 25");

    [Fact]
    public void Market_cap_renders_in_crore_without_further_conversion() =>
        // MarketCap is already stored in crore (RatioCalculator.MarketCap's doc-comment: shares
        // outstanding arrives crore-scaled from the provider), so 5000 IS five thousand crore --
        // dividing again would be the double-conversion bug the seed itself had before it was
        // corrected against real ingested data (TCS's SharesOutstanding of 361.81 against a real
        // count of ~3.62 billion shares).
        Explainer.Explain(Cmp("MarketCap", ComparisonOperator.LessThan, 5_000m))
            .Should().Be("Market capitalisation < ₹5,000 cr");

    [Fact]
    public void A_per_share_price_would_convert_to_crore_only_far_above_any_real_price() =>
        // ClosePrice/Sma50/Sma200/Atr14 use "INR" (a real per-share rupee figure), not "INR_CR".
        // A share price never reaches a crore in practice, so this exercises the branch that
        // exists for correctness but is not expected to fire on real data.
        Explainer.Explain(Cmp("ClosePrice", ComparisonOperator.LessThan, 15_000_000m))
            .Should().Be("Close price < ₹1.5 cr");

    [Fact]
    public void Percentages_carry_their_sign() =>
        Explainer.Explain(Cmp("ReturnOnEquity", ComparisonOperator.GreaterThan, 15m))
            .Should().Be("Return on equity > 15%");

    [Fact]
    public void Trailing_zeros_are_dropped() =>
        Explainer.Explain(Cmp("DebtToEquity", ComparisonOperator.LessThan, 0.50m))
            .Should().Be("Debt to equity < 0.5");

    [Fact]
    public void An_And_group_reads_as_a_sentence() =>
        Explainer.Explain(new Group
        {
            Op = GroupOperator.And,
            Children =
            [
                Cmp("PeRatio", ComparisonOperator.LessThan, 25m),
                Cmp("PbRatio", ComparisonOperator.LessThan, 3m),
            ],
        }).Should().Be("P/E ratio < 25 AND P/B ratio < 3");

    [Fact]
    public void An_unknown_metric_renders_rather_than_throwing() =>
        // This runs on the panel that explains a validation FAILURE, so it must survive exactly
        // the input the validator is about to reject.
        Explainer.Explain(Cmp("NoSuchMetric", ComparisonOperator.LessThan, 1m))
            .Should().Be("NoSuchMetric < 1");
}
