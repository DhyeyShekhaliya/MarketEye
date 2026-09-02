using MarketEye.Application.Ai;
using MarketEye.Domain.Screening;
using MarketEye.Domain.Screening.Vocabulary;

namespace MarketEye.Ai;

/// <summary>
/// A deterministic keyword parser used when no model is available (PLAN.md §5.4).
///
/// Registered when no API key is configured, and also the fallback when the daily budget is spent
/// or the provider is unreachable. The point is that "no AI" degrades to a weaker parse and an
/// honest clarifying question — never to a broken app, and never to a guessed screen (§5.6). The
/// manual screener and the vocabulary keep working regardless, which is §2's claim that the model
/// can fail entirely and the system below it still works.
///
/// It matches concept names and aliases as whole words and does nothing clever. It deliberately
/// does NOT read numbers out of the prose: extracting "P/E below 12" reliably is the model's job,
/// and a regex that got it subtly wrong would put a threshold nobody chose onto a screen — exactly
/// what §5.1 forbids. Where the user clearly wanted a number, this asks instead.
/// </summary>
public sealed class StubIntentParser(IStrategyConceptVocabulary strategies) : IIntentParser
{
    private static readonly char[] Separators =
        [' ', '\t', '\n', '\r', ',', '.', ';', ':', '!', '?', '(', ')', '"', '\''];

    public string Describe => "stub:keyword-match";

    /// <summary>No model call happens here, so the daily AI budget must never be spent on it.</summary>
    public bool ConsumesBudget => false;

    public Task<ParseOutcome> ParseAsync(string prompt, CancellationToken ct)
    {
        var matched = Match(prompt);

        if (matched.Count == 0)
        {
            return Task.FromResult<ParseOutcome>(new ParseOutcome.Parsed(
                ParsedIntent.AskInstead(
                    "I could not match that to any strategy concept. Try naming one, such as " +
                    $"\"{Examples(3)}\" — or build the screen manually.")));
        }

        if (ContainsDigit(prompt))
        {
            // The user supplied a number and this parser cannot place it. Screening on the
            // concepts alone would quietly ignore the most specific thing they said.
            return Task.FromResult<ParseOutcome>(new ParseOutcome.Parsed(
                ParsedIntent.AskInstead(
                    "I matched some concepts but cannot read the number in that request without " +
                    "the language model configured. Add the filter manually, or drop the number.")));
        }

        return Task.FromResult<ParseOutcome>(new ParseOutcome.Parsed(new ParsedIntent
        {
            Concepts = matched,
            ExplicitFilters = [],
        }));
    }

    /// <summary>
    /// Longest-first so a multi-word alias wins over a single word inside it -- "not overbought"
    /// must not resolve as "overbought", which would invert the user's request.
    /// </summary>
    private List<string> Match(string prompt)
    {
        var normalised = ConceptName.Normalise(prompt);
        var padded = $"_{normalised}_";

        var keys = new List<(string Key, StrategyConcept Concept)>();
        foreach (var concept in strategies.Enabled)
        {
            keys.Add((concept.Name, concept));
            foreach (var alias in concept.Aliases) keys.Add((alias, concept));
        }

        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var consumed = padded;

        foreach (var (key, concept) in keys.OrderByDescending(k => k.Key.Length))
        {
            if (key.Length == 0) continue;

            // Underscore-delimited so "cheap" does not match inside "cheapest".
            var needle = $"_{key}_";
            if (!consumed.Contains(needle, StringComparison.Ordinal)) continue;

            // Blank out the match so a shorter overlapping key cannot also claim those words.
            consumed = consumed.Replace(needle, "_", StringComparison.Ordinal);

            if (seen.Add(concept.Name)) found.Add(concept.Name);
        }

        return found;
    }

    private static bool ContainsDigit(string prompt) => prompt.Any(char.IsDigit);

    private string Examples(int count) =>
        string.Join("\", \"", strategies.Enabled
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .Take(count)
            .Select(c => c.Name));
}
