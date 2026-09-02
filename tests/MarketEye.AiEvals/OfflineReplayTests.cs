using FluentAssertions;
using MarketEye.Ai;
using MarketEye.AiEvals.Vocabulary;
using MarketEye.Application.Ai;
using MarketEye.Domain.Screening;
using Xunit;

namespace MarketEye.AiEvals;

/// <summary>
/// The offline half of §5.6's suite: replays FROZEN recordings through the real parsing,
/// resolution and validation code, on every PR, at zero cost and with no key (PLAN.md Step 9).
///
/// Every case here is deterministic -- a recording never changes on its own -- so unlike the live
/// tier's probabilistic ≥85% gate, each case gets its own pass/fail. A failure here means one of:
/// the recording no longer matches its own cases.json expectation (someone edited one without the
/// other), the vocabulary changed in a way that breaks a previously-resolvable response (a concept
/// renamed or a metric removed), or a genuine bug in IntentResponseParser/IntentResolver/
/// ScreenCriteriaValidator. None of those require a live model call to catch.
/// </summary>
public class OfflineReplayTests
{
    private static readonly IntentResolver Resolver = new(
        SeedBackedStrategyVocabulary.Instance,
        SeedBackedMetricVocabulary.Instance,
        new ScreenCriteriaValidator(SeedBackedMetricVocabulary.Instance));

    [Fact]
    public void The_suite_has_at_least_fifty_cases_with_no_duplicate_prompts()
    {
        // §10 Phase 2 asks for 50 cases specifically -- below that, the ≥85% gate the live tier
        // enforces is measuring too little to mean much. Duplicate prompts would double-count a
        // single behaviour and inflate the score without adding coverage.
        var cases = EvalCases.LoadAll();

        cases.Should().HaveCountGreaterThanOrEqualTo(50);
        cases.Select(c => c.Prompt).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Every_recorded_case_still_scores_and_resolves_correctly()
    {
        var cases = EvalCases.LoadAll();
        var failures = new List<string>();
        var missingRecordings = new List<string>();

        foreach (var testCase in cases)
        {
            var path = EvalCases.RecordingPath(testCase.Prompt);
            if (!File.Exists(path))
            {
                missingRecordings.Add(testCase.Id);
                continue;
            }

            var content = File.ReadAllText(path);
            var parsed = IntentResponseParser.Parse(content);

            if (parsed.Outcome is not ParseOutcome.Parsed { Intent: var intent })
            {
                failures.Add($"[{testCase.Id}] recording did not parse to an intent: {parsed.Detail}");
                continue;
            }

            var score = Scoring.Score(testCase, intent);
            if (!score.ConceptsCorrect || !score.FiltersCorrect)
            {
                failures.Add($"[{testCase.Id}] {score.Detail}");
                continue;
            }

            if (testCase.ExpectClarification) continue;

            // Proves the RECORDED response still resolves under the CURRENT vocabulary -- the
            // check a pure parser-level score cannot make. A concept the model correctly named at
            // recording time can still fail here if it was later renamed, disabled or removed.
            var resolution = Resolver.Resolve(intent);
            if (!resolution.IsResolved)
            {
                failures.Add(
                    $"[{testCase.Id}] recorded intent no longer resolves against the current " +
                    $"vocabulary: {string.Join("; ", resolution.Errors.Select(e => e.Message))}");
            }
        }

        if (missingRecordings.Count > 0)
        {
            failures.Add(
                $"{missingRecordings.Count} case(s) have no recording -- run with " +
                "MARKETEYE_AI_EVALS=1 MARKETEYE_AI_EVALS_RECORD=1 against a live key first: " +
                string.Join(", ", missingRecordings));
        }

        failures.Should().BeEmpty();
    }
}
