using FluentAssertions;
using MarketEye.Domain.Screening;
using Xunit;

namespace MarketEye.UnitTests.Screening;

/// <summary>
/// The validator is the boundary between untrusted input and deterministic execution (§5.1).
/// These tests are written adversarially: the interesting cases are the ones where a lenient
/// validator would let something through.
/// </summary>
public class ScreenCriteriaValidatorTests
{
    private readonly ScreenCriteriaValidator _validator = new(new TestVocabulary());

    private static Comparison Cmp(string field, ComparisonOperator op, decimal value) =>
        new() { Field = field, Operator = op, Value = value };

    private static ScreenCriteria Criteria(FilterNode root, SortSpec? sort = null, int? limit = null) =>
        new() { Universe = UniverseConstraint.All, Root = root, Sort = sort, Limit = limit };

    private static Group And(params FilterNode[] children) =>
        new() { Op = GroupOperator.And, Children = children };

    [Fact]
    public void A_valid_flat_and_screen_passes()
    {
        var result = _validator.Validate(Criteria(And(
            Cmp("PeRatio", ComparisonOperator.LessThan, 15m),
            Cmp("Rsi14", ComparisonOperator.LessThan, 40m))));

        result.IsValid.Should().BeTrue(because: string.Join("; ", result.Errors.Select(e => e.Message)));
    }

    [Fact]
    public void An_unknown_concept_is_rejected_and_never_substituted()
    {
        // §5.1: this is the single most important behaviour in the validator. A model that
        // hallucinates "CheapnessScore" must fail the screen, not get quietly mapped to P/E.
        var result = _validator.Validate(Criteria(And(
            Cmp("CheapnessScore", ComparisonOperator.LessThan, 15m))));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle()
            .Which.Code.Should().Be(CriteriaErrorCode.UnknownConcept);
    }

    [Fact]
    public void A_column_name_is_not_a_valid_concept()
    {
        // The vocabulary maps PeRatio -> column "Pe". Accepting the column name would mean the
        // physical schema had leaked into the AI's vocabulary surface.
        var result = _validator.Validate(Criteria(And(
            Cmp("Pe", ComparisonOperator.LessThan, 15m))));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == CriteriaErrorCode.UnknownConcept);
    }

    [Fact]
    public void Concept_matching_is_case_sensitive()
    {
        // Ordinal matching keeps "peratio" from resolving. Loose matching is the first step
        // toward fuzzy matching, and fuzzy matching is how an unvetted concept gets through.
        _validator.Validate(Criteria(And(Cmp("peratio", ComparisonOperator.LessThan, 15m))))
            .IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(999999)]
    public void Values_outside_a_concepts_range_are_rejected(int value)
    {
        // RSI is bounded 0-100 by construction. A value outside it is a data error or an
        // injection attempt, not a screen.
        var result = _validator.Validate(Criteria(And(
            Cmp("Rsi14", ComparisonOperator.LessThan, value))));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == CriteriaErrorCode.ValueOutOfRange);
    }

    [Fact]
    public void Negative_net_income_is_allowed_because_loss_making_is_a_real_screen()
    {
        // Guards against a range check that assumes all financial metrics are positive.
        _validator.Validate(Criteria(And(
            Cmp("NetIncome", ComparisonOperator.LessThan, -5000m))))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void Equality_is_rejected_for_a_numeric_concept()
    {
        var result = _validator.Validate(Criteria(And(
            Cmp("PeRatio", ComparisonOperator.Equal, 15m))));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == CriteriaErrorCode.OperatorNotAllowedForField);
    }

    [Fact]
    public void Or_and_Not_are_representable_but_rejected_in_v1()
    {
        // §6: the type models all three operators so Phase 3+ is additive. v1 must refuse to
        // compile OR/NOT rather than silently treating them as AND.
        foreach (var op in new[] { GroupOperator.Or, GroupOperator.Not })
        {
            var root = new Group
            {
                Op = op,
                Children = [Cmp("PeRatio", ComparisonOperator.LessThan, 15m)],
            };

            var result = _validator.Validate(Criteria(root));
            result.IsValid.Should().BeFalse($"{op} does not compile in v1");
            result.Errors.Should().Contain(e => e.Code == CriteriaErrorCode.OperatorNotSupportedInV1);
        }
    }

    [Fact]
    public void A_tree_deeper_than_four_is_rejected()
    {
        FilterNode node = Cmp("PeRatio", ComparisonOperator.LessThan, 15m);
        for (var i = 0; i < 4; i++) node = And(node);   // depth 5 including the leaf

        var result = _validator.Validate(Criteria(node));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == CriteriaErrorCode.TreeTooDeep);
    }

    [Fact]
    public void A_tree_at_exactly_the_depth_limit_is_accepted()
    {
        // Boundary in the other direction: depth 4 must pass, or the limit is really 3.
        FilterNode node = Cmp("PeRatio", ComparisonOperator.LessThan, 15m);
        for (var i = 0; i < 3; i++) node = And(node);

        node.Depth().Should().Be(ScreenCriteriaValidator.MaxDepth);
        _validator.Validate(Criteria(node)).Errors
            .Should().NotContain(e => e.Code == CriteriaErrorCode.TreeTooDeep);
    }

    [Fact]
    public void More_than_twenty_comparisons_are_rejected_even_when_nested()
    {
        // The cap is on total comparisons, not per group -- otherwise it is trivially evaded
        // by splitting them across nested groups.
        var half = Enumerable.Range(0, 11)
            .Select(_ => (FilterNode)Cmp("PeRatio", ComparisonOperator.LessThan, 15m)).ToArray();

        var result = _validator.Validate(Criteria(And(And(half), And(half))));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == CriteriaErrorCode.TooManyComparisons);
    }

    [Fact]
    public void A_screen_with_no_comparisons_is_rejected()
    {
        // Would otherwise return the entire universe.
        var result = _validator.Validate(Criteria(And()));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == CriteriaErrorCode.NoComparisons);
    }

    [Fact]
    public void Every_error_in_the_tree_is_reported_not_just_the_first()
    {
        // §5.3 shows the user what was understood before running. Reporting one error at a time
        // turns that into a guessing game.
        var result = _validator.Validate(Criteria(And(
            Cmp("Nonsense", ComparisonOperator.LessThan, 1m),
            Cmp("Rsi14", ComparisonOperator.LessThan, 500m),
            Cmp("PeRatio", ComparisonOperator.Equal, 15m))));

        result.Errors.Should().HaveCount(3);
        result.Errors.Select(e => e.Code).Should().BeEquivalentTo(new[]
        {
            CriteriaErrorCode.UnknownConcept,
            CriteriaErrorCode.ValueOutOfRange,
            CriteriaErrorCode.OperatorNotAllowedForField,
        });
    }

    [Fact]
    public void Errors_carry_a_path_locating_the_offending_node()
    {
        var result = _validator.Validate(Criteria(And(
            Cmp("PeRatio", ComparisonOperator.LessThan, 15m),
            Cmp("Bogus", ComparisonOperator.LessThan, 1m))));

        result.Errors.Should().ContainSingle()
            .Which.Path.Should().Be("root.children[1].field");
    }

    [Fact]
    public void The_sort_field_is_validated_against_the_same_whitelist()
    {
        // An unvalidated sort field is the same injection surface as an unvalidated filter field.
        var result = _validator.Validate(Criteria(
            And(Cmp("PeRatio", ComparisonOperator.LessThan, 15m)),
            sort: new SortSpec { Field = "MarketCap; DROP TABLE Securities--", Direction = SortDirection.Descending }));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.Code == CriteriaErrorCode.UnknownConcept && e.Path == "sort.field");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(100_000)]
    public void Invalid_limits_are_rejected(int limit)
    {
        var result = _validator.Validate(Criteria(
            And(Cmp("PeRatio", ComparisonOperator.LessThan, 15m)), limit: limit));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == CriteriaErrorCode.InvalidLimit);
    }
}
