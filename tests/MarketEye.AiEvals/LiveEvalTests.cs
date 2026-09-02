using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MarketEye.Ai;
using MarketEye.AiEvals.Vocabulary;
using MarketEye.Application.Ai;
using Xunit;

namespace MarketEye.AiEvals;

/// <summary>
/// The live half of §5.6's suite: calls the real provider for all 50 cases and asserts the ≥85%
/// gate §10's Phase 2 exit criterion names, scoring concepts and explicit filters separately.
///
/// Gated behind MARKETEYE_AI_EVALS=1, the same idiom DockerGate uses for
/// MARKETEYE_INTEGRATION -- opt-in, never part of a routine `dotnet test` or PR run, because it
/// spends real provider credits and several minutes of wall clock. Configuration comes from
/// role-named environment variables (AI_API_KEY, AI_ENDPOINT, AI_MODEL, ...), not vendor-named
/// ones, so switching provider is a secret change in the workflow, not a code change here.
/// </summary>
public class LiveEvalTests
{
    /// <summary>
    /// A simple sequential delay between calls, not a parallel fan-out -- this is a rate limit
    /// the provider imposes, not a throughput problem to engineer around. Configurable because the
    /// right value depends on the account's actual per-minute allowance, which changes.
    /// </summary>
    private static int DelayMs =>
        int.TryParse(Environment.GetEnvironmentVariable("MARKETEYE_AI_EVALS_DELAY_MS"), out var ms)
            ? ms : 1500;

    [Fact(Skip = LiveEvalGate.SkipReason, SkipUnless = nameof(LiveEvalGate.Enabled), SkipType = typeof(LiveEvalGate))]
    public async Task The_live_provider_meets_the_85_percent_gate()
    {
        var ct = TestContext.Current.CancellationToken;
        var parser = BuildParser();
        var cases = EvalCases.LoadAll();
        var scores = new List<Scoring.CaseScore>();

        foreach (var (testCase, index) in cases.Select((c, i) => (c, i)))
        {
            if (index > 0) await Task.Delay(DelayMs, ct);

            var (content, unavailableReason) = await parser.FetchRawContentAsync(testCase.Prompt, ct);

            if (content is null)
            {
                // Provider-unavailable is not a scoring miss -- it is a run that could not
                // complete, and inflating the denominator with it would understate a real outage
                // as a quality problem. Recorded as its own failure so the run still fails loudly.
                scores.Add(new Scoring.CaseScore(testCase.Id, false, false, $"provider unavailable: {unavailableReason}"));
                continue;
            }

            if (LiveEvalGate.Recording) await RecordAsync(testCase.Prompt, content, ct);

            // Scored through the SAME parser the offline tier replays recordings through, so a
            // live run and an offline replay of what it just recorded agree by construction.
            var parsed = IntentResponseParser.Parse(content);
            var intent = parsed.Outcome switch
            {
                ParseOutcome.Parsed p => p.Intent,
                _ => null,
            };

            scores.Add(intent is null
                ? new Scoring.CaseScore(testCase.Id, false, false, $"unparseable response: {parsed.Detail}")
                : Scoring.Score(testCase, intent));
        }

        var conceptRate = Scoring.PassRate(scores, s => s.ConceptsCorrect);
        var filterRate = Scoring.PassRate(scores, s => s.FiltersCorrect);

        var failureReport = string.Join("\n", scores
            .Where(s => !s.ConceptsCorrect || !s.FiltersCorrect)
            .Select(s => $"  [{s.CaseId}] {s.Detail}"));

        var report =
            $"concept-set match: {conceptRate:F1}% ({scores.Count(s => s.ConceptsCorrect)}/{scores.Count})\n" +
            $"explicit-filter match: {filterRate:F1}% ({scores.Count(s => s.FiltersCorrect)}/{scores.Count})\n" +
            (failureReport.Length > 0 ? $"failures:\n{failureReport}" : "no failures");

        conceptRate.Should().BeGreaterThanOrEqualTo(85.0, report);
        filterRate.Should().BeGreaterThanOrEqualTo(85.0, report);
    }

    /// <summary>
    /// Writes the model's ACTUAL raw reply -- not a reconstruction -- to both the source tree
    /// (what ships in the PR) and the build-output copy (so a replay later in this same process
    /// run sees it without needing a rebuild).
    /// </summary>
    private static async Task RecordAsync(string prompt, string rawContent, CancellationToken ct)
    {
        foreach (var path in new[] { EvalCases.SourceRecordingPath(prompt), EvalCases.RecordingPath(prompt) })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, rawContent, ct);
        }
    }

    private static NvidiaIntentParser BuildParser()
    {
        var apiKey = Environment.GetEnvironmentVariable("AI_API_KEY")
            ?? throw new InvalidOperationException(
                "MARKETEYE_AI_EVALS=1 but AI_API_KEY is not set. This is a misconfiguration, not " +
                "a skip -- a live-gated run with no key would otherwise silently score nothing.");

        var options = new AiOptions
        {
            ApiKey = apiKey,
            Endpoint = Environment.GetEnvironmentVariable("AI_ENDPOINT") ?? "https://integrate.api.nvidia.com/v1",
            Model = Environment.GetEnvironmentVariable("AI_MODEL") ?? "openai/gpt-oss-20b",
            MaxOutputTokens = 2000,
            TimeoutSeconds = 90,
            StructuredOutput = StructuredOutputMode.ResponseFormatJsonSchema,
        };

        var http = new HttpClient
        {
            BaseAddress = new Uri(options.Endpoint.EndsWith('/') ? options.Endpoint : options.Endpoint + "/"),
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        return new NvidiaIntentParser(
            http, Options.Create(options),
            SeedBackedStrategyVocabulary.Instance, SeedBackedMetricVocabulary.Instance,
            NullLogger<NvidiaIntentParser>.Instance);
    }
}
