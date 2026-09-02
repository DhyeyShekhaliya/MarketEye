using FluentAssertions;
using MarketEye.Ai;
using MarketEye.UnitTests.Screening;
using Xunit;

namespace MarketEye.UnitTests.Ai;

/// <summary>
/// The prompt does not enforce §5.1 — the schema and the resolver do — but it is what makes the
/// model's choices good rather than merely legal. It is generated from the vocabulary so it cannot
/// drift from what the resolver will actually accept.
/// </summary>
public class SystemPromptTests
{
    private static string Prompt() =>
        SystemPrompt.Build(new SeededStrategyVocabulary(), SeededMetricVocabulary.Instance);

    [Fact]
    public void Every_enabled_concept_and_its_aliases_are_described()
    {
        var prompt = Prompt();
        var vocabulary = new SeededStrategyVocabulary();

        foreach (var concept in vocabulary.Enabled)
        {
            prompt.Should().Contain(concept.Name);
            foreach (var alias in concept.Aliases) prompt.Should().Contain(alias);
        }
    }

    [Fact]
    public void Metric_ranges_are_stated_so_the_model_does_not_propose_a_rejected_number()
    {
        // Without the range, a user asking for "RSI under 5000" produces a filter the validator
        // rejects -- a round trip that reads as a failure rather than as a bad request.
        Prompt().Should().Contain("Rsi14").And.Contain("0 to 100");
    }

    [Fact]
    public void The_concepts_not_thresholds_rule_is_stated_explicitly()
    {
        var prompt = Prompt();

        prompt.Should().Contain("explicit_filters");
        prompt.Should().Contain("stated that number themselves");
    }

    [Fact]
    public void The_clarification_route_is_offered_as_the_preferred_exit() =>
        // §5.6: a low-confidence parse must ask, not guess.
        Prompt().Should().Contain("clarification").And.Contain("better than guessing");

    [Fact]
    public void The_disclaimer_is_present() =>
        // A §12 non-negotiable: every system prompt carries it.
        Prompt().Should().Contain("educational purposes only");
}
