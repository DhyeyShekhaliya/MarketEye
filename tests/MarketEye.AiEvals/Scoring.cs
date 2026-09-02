using MarketEye.Domain.Screening;
using MarketEye.Domain.Screening.Vocabulary;

namespace MarketEye.AiEvals;

/// <summary>
/// Scores one case against what the parser actually produced (PLAN.md §5.6).
///
/// Concept-set and explicit-filter-set are scored SEPARATELY, both by exact set equality. Folding
/// them into one pass/fail would hide which half of §5.1's rule is failing -- a model that
/// correctly picks concepts but keeps inventing filter values is a different, more dangerous bug
/// than one that misses aliases, and the two numbers need to stay distinguishable.
/// </summary>
public static class Scoring
{
    public sealed record CaseScore(
        string CaseId, bool ConceptsCorrect, bool FiltersCorrect, string? Detail);

    public static CaseScore Score(EvalCase testCase, ParsedIntent actual)
    {
        var gotClarification = !string.IsNullOrWhiteSpace(actual.Clarification);

        if (testCase.ExpectClarification)
        {
            // Both axes hinge on the same question here: did it correctly decline to guess?
            return gotClarification
                ? new(testCase.Id, true, true, null)
                : new(testCase.Id, false, false,
                    $"expected a clarification; got concepts=[{Join(actual.Concepts)}] " +
                    $"filters=[{Join(actual.ExplicitFilters.Select(DescribeFilter))}]");
        }

        if (gotClarification)
        {
            // A question when a real answer was expected is a miss on both axes: the model failed
            // to resolve something it should have been able to.
            return new(testCase.Id, false, false,
                $"unexpected clarification: \"{actual.Clarification}\"");
        }

        var expectedConcepts = testCase.ExpectedConcepts
            .Select(ConceptName.Normalise).ToHashSet(StringComparer.Ordinal);
        var actualConcepts = actual.Concepts
            .Select(ConceptName.Normalise).ToHashSet(StringComparer.Ordinal);
        var conceptsCorrect = expectedConcepts.SetEquals(actualConcepts);

        var expectedFilters = testCase.ExpectedFilters
            .Select(f => (f.Field, f.Operator, f.Value)).ToHashSet();
        var actualFilters = actual.ExplicitFilters
            .Select(f => (f.Field, Operator: f.Operator.ToString(), f.Value)).ToHashSet();
        var filtersCorrect = expectedFilters.SetEquals(actualFilters);

        string? detail = conceptsCorrect && filtersCorrect ? null :
            $"concepts: expected=[{Join(expectedConcepts)}] actual=[{Join(actualConcepts)}]; " +
            $"filters: expected=[{Join(expectedFilters.Select(Describe))}] " +
            $"actual=[{Join(actualFilters.Select(Describe))}]";

        return new(testCase.Id, conceptsCorrect, filtersCorrect, detail);
    }

    /// <summary>Aggregate pass rate for one axis across the whole suite, as a percentage.</summary>
    public static double PassRate(IReadOnlyList<CaseScore> scores, Func<CaseScore, bool> axis) =>
        scores.Count == 0 ? 0 : 100.0 * scores.Count(axis) / scores.Count;

    private static string Join(IEnumerable<string> values) => string.Join(", ", values);

    private static string Describe((string Field, string Operator, decimal Value) f) =>
        $"{f.Field} {f.Operator} {f.Value}";

    private static string DescribeFilter(ExplicitFilter f) =>
        $"{f.Field} {f.Operator} {f.Value}";
}
