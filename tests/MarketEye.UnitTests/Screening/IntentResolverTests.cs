using FluentAssertions;
using MarketEye.Domain.Screening;
using Xunit;

namespace MarketEye.UnitTests.Screening;

/// <summary>
/// The resolver is the boundary §5.1 describes: untrusted prose above, deterministic execution
/// below. These tests are about what it REFUSES as much as what it builds — an unknown concept
/// that quietly became a nearby one would put a threshold nobody vetted onto a user's screen.
/// </summary>
public class IntentResolverTests
{
    private static IntentResolver Resolver()
    {
        var metrics = SeededMetricVocabulary.Instance;
        return new IntentResolver(
            new TestStrategyVocabulary(), metrics, new ScreenCriteriaValidator(metrics));
    }

    private static ParsedIntent Intent(
        string[]? concepts = null, ExplicitFilter[]? filters = null) => new()
    {
        Concepts = concepts ?? [],
        ExplicitFilters = filters ?? [],
    };

    private static ExplicitFilter Filter(string field, ComparisonOperator op, decimal value) =>
        new() { Field = field, Operator = op, Value = value };

    private static List<Comparison> ComparisonsOf(IntentResolution r) =>
        r.Criteria!.Root.Comparisons().ToList();

    // --- The two worked examples from §5.1 --------------------------------------------------

    [Fact]
    public void Concepts_alone_resolve_to_the_vocabularys_thresholds()
    {
        // "cheap profitable small caps that aren't overbought"
        var result = Resolver().Resolve(
            Intent(["cheap", "profitable", "small_cap", "not_overbought"]));

        result.IsResolved.Should().BeTrue(
            because: string.Join("; ", result.Errors.Select(e => e.Message)));

        // Every number below came from the vocabulary. None was supplied by the caller, which is
        // the whole point of §5.1.
        ComparisonsOf(result).Should().BeEquivalentTo(new[]
        {
            new { Field = "PeRatio", Value = 25m },
            new { Field = "PbRatio", Value = 3m },
            new { Field = "ReturnOnEquity", Value = 0m },
            new { Field = "MarketCap", Value = 5_000m },
            new { Field = "Rsi14", Value = 70m },
        }, o => o.ExcludingMissingMembers());
    }

    [Fact]
    public void A_number_the_user_supplied_is_carried_through_untouched()
    {
        // "profitable small caps with P/E below 12"
        var result = Resolver().Resolve(Intent(
            ["profitable", "small_cap"],
            [Filter("PeRatio", ComparisonOperator.LessThan, 12m)]));

        result.IsResolved.Should().BeTrue();
        ComparisonsOf(result).Should().ContainSingle(c => c.Field == "PeRatio")
            .Which.Value.Should().Be(12m);
    }

    // --- Failing closed ----------------------------------------------------------------------

    [Fact]
    public void An_unknown_concept_is_rejected_and_never_substituted()
    {
        var result = Resolver().Resolve(Intent(["cheap", "meme_stocks"]));

        result.IsResolved.Should().BeFalse();
        result.Criteria.Should().BeNull("a partial parse must not run as if it were complete");
        result.Errors.Should().ContainSingle()
            .Which.Code.Should().Be(CriteriaErrorCode.UnknownStrategyConcept);
    }

    [Fact]
    public void A_disabled_concept_is_rejected_exactly_like_an_unknown_one()
    {
        // Falling back to the concept's old definition would let a user believe they had turned
        // it off while it kept screening.
        var result = Resolver().Resolve(Intent(["retired_idea"]));

        result.IsResolved.Should().BeFalse();
        result.Errors.Should().ContainSingle()
            .Which.Code.Should().Be(CriteriaErrorCode.UnknownStrategyConcept);
    }

    [Fact]
    public void An_explicit_filter_may_not_name_a_strategy_concept()
    {
        // "cheap < 12" is the shape of the model attaching its own number to a qualitative word,
        // which is the exact failure §5.1 exists to prevent.
        var result = Resolver().Resolve(Intent(
            filters: [Filter("cheap", ComparisonOperator.LessThan, 12m)]));

        result.IsResolved.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == CriteriaErrorCode.UnknownConcept);
    }

    [Fact]
    public void An_empty_intent_never_screens_the_whole_universe()
    {
        var result = Resolver().Resolve(Intent());

        result.IsResolved.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == CriteriaErrorCode.EmptyIntent);
    }

    [Fact]
    public void A_clarification_short_circuits_before_anything_resolves()
    {
        // §5.6: a low-confidence parse asks a question. Even with resolvable concepts present,
        // the question wins -- otherwise "did you mean X?" would silently run a screen.
        var result = Resolver().Resolve(new ParsedIntent
        {
            Concepts = ["cheap"],
            ExplicitFilters = [],
            Clarification = "Did you mean cheap by earnings or by book value?",
        });

        result.NeedsClarification.Should().BeTrue();
        result.IsResolved.Should().BeFalse();
        result.Concepts.Should().BeEmpty();
    }

    [Fact]
    public void An_out_of_range_user_number_is_rejected_by_the_validator()
    {
        // RSI is bounded 0..100. A request for RSI below 5000 is a data error or an injection
        // attempt, not a screen (§5.2).
        var result = Resolver().Resolve(Intent(
            filters: [Filter("Rsi14", ComparisonOperator.LessThan, 5000m)]));

        result.IsResolved.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == CriteriaErrorCode.ValueOutOfRange);
    }

    // --- The override rule --------------------------------------------------------------------

    [Fact]
    public void A_user_number_replaces_the_concepts_comparison_on_the_same_metric()
    {
        var result = Resolver().Resolve(Intent(
            ["cheap"],
            [Filter("PeRatio", ComparisonOperator.LessThan, 12m)]));

        result.IsResolved.Should().BeTrue();

        // Cheap is P/E < 25 AND P/B < 3. The user's P/E replaces the 25; the P/B survives.
        ComparisonsOf(result).Should().BeEquivalentTo(new[]
        {
            new { Field = "PbRatio", Value = 3m },
            new { Field = "PeRatio", Value = 12m },
        }, o => o.ExcludingMissingMembers());
    }

    [Fact]
    public void A_loosening_user_number_replaces_rather_than_intersects()
    {
        // The case that proves replacement is the right rule. ANDing would keep P/E < 25 and
        // silently ignore the user asking for 40 -- returning results they did not request while
        // the panel claimed otherwise.
        var result = Resolver().Resolve(Intent(
            ["cheap"],
            [Filter("PeRatio", ComparisonOperator.LessThan, 40m)]));

        result.IsResolved.Should().BeTrue();
        ComparisonsOf(result).Should().ContainSingle(c => c.Field == "PeRatio")
            .Which.Value.Should().Be(40m);
    }

    [Fact]
    public void An_override_is_reported_so_the_panel_can_never_show_a_stale_definition()
    {
        var result = Resolver().Resolve(Intent(
            ["cheap"],
            [Filter("PeRatio", ComparisonOperator.LessThan, 12m)]));

        var cheap = result.Concepts.Should().ContainSingle().Subject;
        cheap.OverriddenBy.Should().ContainSingle().Which.Should().Be("PeRatio");
        cheap.FullyOverridden.Should().BeFalse("P/B < 3 still contributes");
    }

    [Fact]
    public void A_concept_whose_every_part_is_overridden_is_flagged_not_hidden()
    {
        var result = Resolver().Resolve(Intent(
            ["cheap"],
            [
                Filter("PeRatio", ComparisonOperator.LessThan, 12m),
                Filter("PbRatio", ComparisonOperator.LessThan, 2m),
            ]));

        result.IsResolved.Should().BeTrue();
        var cheap = result.Concepts.Should().ContainSingle().Subject;
        cheap.FullyOverridden.Should().BeTrue(
            "the panel must say the concept contributed nothing rather than imply it applied");
    }

    // --- Housekeeping the model will trip over ------------------------------------------------

    [Fact]
    public void Aliases_resolve_to_the_same_concept_as_the_canonical_name()
    {
        var viaAlias = Resolver().Resolve(Intent(["undervalued"]));
        var viaName = Resolver().Resolve(Intent(["cheap"]));

        viaAlias.IsResolved.Should().BeTrue();
        ComparisonsOf(viaAlias).Should().BeEquivalentTo(ComparisonsOf(viaName));
    }

    [Theory]
    [InlineData("Small Cap")]
    [InlineData("small-cap")]
    [InlineData("SMALL_CAP")]
    public void Concept_names_resolve_regardless_of_the_models_spelling(string spelling)
    {
        // The model is prompted with normalised names but does not reliably echo them exactly.
        // Rejecting "Small Cap" would read to a user as the AI hallucinating a real concept.
        Resolver().Resolve(Intent([spelling])).IsResolved.Should().BeTrue();
    }

    [Fact]
    public void The_same_concept_named_twice_contributes_once()
    {
        // "cheap value stocks" maps two words to one concept. Duplicating its comparisons would
        // eat the §6 budget of 20 for no gain.
        var result = Resolver().Resolve(Intent(["cheap", "value"]));

        result.IsResolved.Should().BeTrue();
        result.Concepts.Should().ContainSingle();
        ComparisonsOf(result).Should().HaveCount(2);
    }

    [Fact]
    public void Every_bad_concept_is_reported_not_just_the_first()
    {
        // A user who misphrased two things should be told both, not sent round the loop twice.
        var result = Resolver().Resolve(Intent(["nonsense_one", "cheap", "nonsense_two"]));

        result.Errors.Should().HaveCount(2);
        result.Errors.Select(e => e.Path).Should().BeEquivalentTo(["concepts[0]", "concepts[2]"]);
    }

    [Fact]
    public void Resolved_criteria_are_a_flat_And_group_the_v1_compiler_accepts()
    {
        var result = Resolver().Resolve(Intent(["cheap", "profitable", "small_cap"]));

        var root = result.Criteria!.Root.Should().BeOfType<Group>().Subject;
        root.Op.Should().Be(GroupOperator.And);
        root.Children.Should().AllBeOfType<Comparison>();
        result.Criteria.Root.Depth().Should().Be(2, "§6 v1 compiles a single flat AND");
    }
}
