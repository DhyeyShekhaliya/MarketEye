using FluentAssertions;
using MarketEye.Domain.Screening.Vocabulary;
using Xunit;

namespace MarketEye.UnitTests.Screening;

/// <summary>
/// Normalisation is the contract between the schema handed to the model and the resolver that
/// checks what comes back. If they disagree about what "Small Cap" is, the schema advertises a
/// concept the resolver then rejects — which reads to a user as the AI hallucinating.
/// </summary>
public class ConceptNameTests
{
    [Theory]
    [InlineData("small_cap", "small_cap")]
    [InlineData("Small Cap", "small_cap")]
    [InlineData("small-cap", "small_cap")]
    [InlineData("SMALL   CAP", "small_cap")]
    [InlineData("Blue Chip", "blue_chip")]
    [InlineData("not overbought", "not_overbought")]
    public void Spellings_of_the_same_concept_normalise_together(string input, string expected) =>
        ConceptName.Normalise(input).Should().Be(expected);

    [Theory]
    [InlineData("  cheap  ", "cheap")]
    [InlineData("_cheap_", "cheap")]
    [InlineData("!!cheap!!", "cheap")]
    public void Leading_and_trailing_punctuation_never_becomes_an_underscore(
        string input, string expected) =>
        // "_cheap" would be stored as a key nothing can look up, because every caller's input
        // normalises without the prefix.
        ConceptName.Normalise(input).Should().Be(expected);

    [Fact]
    public void Normalising_twice_changes_nothing() =>
        // The seed stores normalised names and lookup normalises the caller's input, so the
        // function is applied an unpredictable number of times. It has to be idempotent.
        ConceptName.Normalise(ConceptName.Normalise("Cash Generative"))
            .Should().Be(ConceptName.Normalise("Cash Generative"));

    [Fact]
    public void An_empty_or_punctuation_only_name_normalises_to_empty() =>
        // Callers must treat empty as "no such concept" rather than storing it as a key.
        ConceptName.Normalise("???").Should().BeEmpty();
}
