using FluentAssertions;
using MarketEye.Ai;
using MarketEye.Application.Ai;
using MarketEye.UnitTests.Screening;
using Xunit;

namespace MarketEye.UnitTests.Ai;

/// <summary>
/// The no-key path. §2 claims the model can be removed entirely and the system below still works;
/// this is the parser that has to make that true, and its job is to degrade to an honest question
/// rather than to a guessed screen (§5.6).
/// </summary>
public class StubIntentParserTests
{
    private static StubIntentParser Parser() => new(new SeededStrategyVocabulary());

    private static async Task<MarketEye.Domain.Screening.ParsedIntent> ParseAsync(string prompt)
    {
        var outcome = await Parser().ParseAsync(prompt, TestContext.Current.CancellationToken);
        return outcome.Should().BeOfType<ParseOutcome.Parsed>().Subject.Intent;
    }

    [Fact]
    public async Task Concept_names_in_the_prompt_are_matched()
    {
        var intent = await ParseAsync("cheap profitable small caps");

        intent.Clarification.Should().BeNull();
        intent.Concepts.Should().Contain(["cheap", "profitable", "small_cap"]);
    }

    [Fact]
    public async Task Aliases_are_matched_as_well_as_names()
    {
        var intent = await ParseAsync("undervalued blue chip companies");

        intent.Concepts.Should().Contain(["cheap", "large_cap"]);
    }

    [Fact]
    public async Task A_longer_alias_wins_over_a_shorter_one_inside_it()
    {
        // The case that matters most: matching "overbought" inside "not overbought" would invert
        // the user's request and screen for exactly what they excluded.
        var intent = await ParseAsync("stocks that are not overbought");

        intent.Concepts.Should().Contain("not_overbought");
        intent.Concepts.Should().NotContain("overbought");
    }

    [Fact]
    public async Task A_concept_name_inside_a_longer_word_is_not_matched()
    {
        // "cheapest" is not "cheap". Substring matching would fire on it.
        var intent = await ParseAsync("the cheapest way to do this");

        intent.Concepts.Should().NotContain("cheap");
        intent.Clarification.Should().NotBeNull();
    }

    [Fact]
    public async Task An_unmatched_prompt_asks_a_question_rather_than_screening()
    {
        // §5.6. Returning an empty concept list would resolve to "no comparisons" and, without
        // the guard, screen the entire universe.
        var intent = await ParseAsync("show me good stocks");

        intent.Clarification.Should().NotBeNull();
        intent.Concepts.Should().BeEmpty();
    }

    [Fact]
    public async Task A_prompt_carrying_a_number_asks_rather_than_dropping_it()
    {
        // The user's number is the most specific thing they said. This parser cannot place it, and
        // screening on the concepts alone would silently ignore it -- returning results that look
        // right and answer a different question.
        var intent = await ParseAsync("cheap stocks with P/E below 12");

        intent.Clarification.Should().NotBeNull();
        intent.ExplicitFilters.Should().BeEmpty();
    }

    [Fact]
    public async Task The_stub_never_invents_a_threshold()
    {
        // The whole point of §5.1, restated as a property of the fallback path: no code path here
        // can produce an explicit filter, because none of them parses a number.
        foreach (var prompt in new[]
                 {
                     "cheap", "oversold high quality", "large cap stable",
                     "profitable with ROE over 20", "anything good",
                 })
        {
            var intent = await ParseAsync(prompt);
            intent.ExplicitFilters.Should().BeEmpty($"'{prompt}' must not yield a made-up number");
        }
    }
}
