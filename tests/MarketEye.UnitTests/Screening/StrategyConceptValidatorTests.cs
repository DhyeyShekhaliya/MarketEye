using FluentAssertions;
using MarketEye.Domain.Screening;
using MarketEye.Domain.Screening.Vocabulary;
using Xunit;

namespace MarketEye.UnitTests.Screening;

/// <summary>
/// The vocabulary write path is the one place a user's free-form input shapes what the compiler
/// later runs. It only stays safe because a definition is checked BEFORE storage: validating on
/// read would leave the bad row in the table, breaking every screen that names it for a user who
/// did nothing wrong.
/// </summary>
public class StrategyConceptValidatorTests
{
    private static StrategyConceptValidator Validator() =>
        new(new TestStrategyVocabulary(),
            new ScreenCriteriaValidator(SeededMetricVocabulary.Instance));

    private static Comparison Cmp(string field, decimal value) =>
        new() { Field = field, Operator = ComparisonOperator.LessThan, Value = value };

    private static StrategyConceptDraft Draft(
        string name = "my_idea",
        string[]? aliases = null,
        FilterNode? definition = null) => new()
    {
        Name = name,
        DisplayName = "My idea",
        Aliases = aliases ?? [],
        Definition = definition ?? new Group
        {
            Op = GroupOperator.And,
            Children = [Cmp("PeRatio", 20m)],
        },
    };

    [Fact]
    public void A_well_formed_concept_is_accepted() =>
        Validator().Validate(Draft()).IsValid.Should().BeTrue();

    [Fact]
    public void A_definition_naming_an_unknown_metric_is_rejected()
    {
        // The compiler resolves metric names to columns from its own sealed table. A definition
        // naming something absent would compile to nothing and fail at query time.
        var result = Validator().Validate(Draft(definition: new Group
        {
            Op = GroupOperator.And,
            Children = [Cmp("NoSuchMetric", 1m)],
        }));

        result.Errors.Should().Contain(e => e.Code == CriteriaErrorCode.UnknownConcept);
        result.Errors.Should().Contain(e => e.Path.StartsWith("definition."),
            "the panel needs the path to highlight the offending row");
    }

    [Fact]
    public void A_value_outside_the_metrics_range_is_rejected()
    {
        var result = Validator().Validate(Draft(definition: new Group
        {
            Op = GroupOperator.And,
            Children = [Cmp("Rsi14", 5000m)],
        }));

        result.Errors.Should().Contain(e => e.Code == CriteriaErrorCode.ValueOutOfRange);
    }

    [Fact]
    public void A_nested_definition_is_rejected_rather_than_stored_and_mishandled()
    {
        // §6 compiles a flat AND in v1, and the resolver's override rule only reaches the top
        // level of one. Storing this would validate and then behave wrongly the first time a user
        // supplied their own number for a metric buried inside.
        var result = Validator().Validate(Draft(definition: new Group
        {
            Op = GroupOperator.And,
            Children = [new Group { Op = GroupOperator.And, Children = [Cmp("PeRatio", 10m)] }],
        }));

        result.Errors.Should().Contain(
            e => e.Code == CriteriaErrorCode.DefinitionShapeNotSupportedInV1);
    }

    [Fact]
    public void An_Or_definition_is_rejected_in_v1()
    {
        var result = Validator().Validate(Draft(definition: new Group
        {
            Op = GroupOperator.Or,
            Children = [Cmp("PeRatio", 10m)],
        }));

        result.Errors.Should().Contain(
            e => e.Code == CriteriaErrorCode.DefinitionShapeNotSupportedInV1);
    }

    [Fact]
    public void A_name_another_concept_already_answers_to_is_rejected()
    {
        // The loader takes the first writer for a duplicated key, so a collision would make which
        // concept a user gets depend on row order -- a bug that only appears after a restart.
        var result = Validator().Validate(Draft(name: "cheap"));

        result.Errors.Should().Contain(e => e.Code == CriteriaErrorCode.ConceptNameInUse);
    }

    [Fact]
    public void An_alias_owned_by_another_concept_is_rejected()
    {
        var result = Validator().Validate(Draft(aliases: ["undervalued"]));

        result.Errors.Should().ContainSingle()
            .Which.Path.Should().Be("aliases[0]");
    }

    [Fact]
    public void Editing_a_concept_does_not_collide_with_itself()
    {
        // Without replacingName, every update of "cheap" would fail because "cheap" already
        // resolves to "cheap".
        var draft = Draft(name: "cheap", aliases: ["value"]);

        Validator().Validate(draft, replacingName: "cheap").IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_name_that_normalises_to_nothing_is_rejected() =>
        // It would be stored as an empty key that no lookup could ever reach.
        Validator().Validate(Draft(name: "???")).Errors
            .Should().Contain(e => e.Code == CriteriaErrorCode.InvalidConceptName);

    [Fact]
    public void An_alias_repeating_the_concepts_own_name_is_rejected() =>
        Validator().Validate(Draft(name: "my_idea", aliases: ["My Idea"])).Errors
            .Should().Contain(e => e.Code == CriteriaErrorCode.ConceptNameInUse);

    [Fact]
    public void An_empty_definition_is_rejected() =>
        // A concept meaning nothing would silently contribute nothing to every screen naming it.
        Validator().Validate(Draft(definition: new Group
        {
            Op = GroupOperator.And,
            Children = [],
        })).IsValid.Should().BeFalse();
}
