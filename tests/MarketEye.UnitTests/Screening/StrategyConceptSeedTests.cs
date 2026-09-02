using FluentAssertions;
using MarketEye.Application.Screening;
using MarketEye.Domain.Screening;
using MarketEye.Domain.Screening.Vocabulary;
using MarketEye.Infrastructure.Persistence;
using Xunit;

namespace MarketEye.UnitTests.Screening;

/// <summary>
/// The seeded Strategy Vocabulary is the layer §5.1's rule rests on: it is where every threshold
/// the model is NOT allowed to invent actually lives. A definition that names a metric which does
/// not exist, or sets a value outside that metric's range, breaks the screen at run time for a
/// user who did nothing wrong — so it is checked here, before it can ship.
/// </summary>
public class StrategyConceptSeedTests
{
    private static readonly IMetricConceptVocabulary Metrics = SeededMetricVocabulary.Instance;

    [Fact]
    public void Every_definition_round_trips_through_the_canonical_serialiser()
    {
        foreach (var row in StrategyConceptSeed.SeedRows())
        {
            var act = () => ScreenCriteriaJson.DeserializeNode(row.DefinitionJson);
            act.Should().NotThrow($"'{row.Name}' must be readable by the vocabulary loader");
        }
    }

    [Fact]
    public void Every_definition_names_only_seeded_metrics_and_passes_the_validator()
    {
        var validator = new ScreenCriteriaValidator(Metrics);

        foreach (var row in StrategyConceptSeed.SeedRows())
        {
            var definition = ScreenCriteriaJson.DeserializeNode(row.DefinitionJson);

            // Resolution splices the definition into a criteria tree, so validating it as one is
            // exactly the check that matters -- unknown metric, disallowed operator and
            // out-of-range value all surface here.
            var criteria = new ScreenCriteria
            {
                Universe = UniverseConstraint.All,
                Root = definition,
            };

            var result = validator.Validate(criteria);
            result.IsValid.Should().BeTrue(
                $"'{row.Name}' must be a valid screen on its own: " +
                string.Join("; ", result.Errors.Select(e => $"{e.Path}: {e.Message}")));
        }
    }

    [Fact]
    public void Every_definition_is_a_flat_And_group_that_v1_can_compile()
    {
        foreach (var row in StrategyConceptSeed.SeedRows())
        {
            var definition = ScreenCriteriaJson.DeserializeNode(row.DefinitionJson);

            // §6 compiles a single flat AND in v1. A concept shaped otherwise would validate in
            // isolation but blow the depth budget once several are ANDed together by the resolver.
            definition.Should().BeOfType<Group>($"'{row.Name}' must be a group");
            ((Group)definition).Op.Should().Be(GroupOperator.And);
            ((Group)definition).Children.Should().AllBeOfType<Comparison>(
                $"'{row.Name}' must contain only comparisons, so resolution stays depth-2");
        }
    }

    [Fact]
    public void No_name_or_alias_resolves_to_two_different_concepts()
    {
        var owner = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var row in StrategyConceptSeed.SeedRows())
        {
            var keys = new List<string> { row.Name };
            keys.AddRange(row.AliasesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries));

            foreach (var key in keys)
            {
                owner.TryGetValue(key, out var existing).Should().BeFalse(
                    $"'{key}' is claimed by both '{existing}' and '{row.Name}'. The loader takes " +
                    "the first writer, so a collision would make which concept a user gets depend " +
                    "on row order.");
                owner[key] = row.Name;
            }
        }
    }

    [Fact]
    public void Names_and_aliases_are_already_normalised()
    {
        foreach (var row in StrategyConceptSeed.SeedRows())
        {
            // Lookup normalises the caller's input and compares ordinally against what is stored.
            // A stored name that is not already normalised is simply unreachable.
            ConceptName.Normalise(row.Name).Should().Be(row.Name);

            foreach (var alias in row.AliasesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                ConceptName.Normalise(alias).Should().Be(alias);
            }
        }
    }

    [Fact]
    public void The_seed_is_large_enough_to_be_a_vocabulary()
    {
        // §10 Phase 2 asks for ~20 concepts. Below roughly this many, the model has too little
        // to map prose onto and falls back to clarifying questions for ordinary requests.
        StrategyConceptSeed.SeedRows().Should().HaveCountGreaterThanOrEqualTo(15);
    }
}
