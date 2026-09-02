using System.Text.Json;
using System.Text.Json.Nodes;
using MarketEye.Application.Ai;
using MarketEye.Domain.Screening;

namespace MarketEye.Ai;

/// <summary>
/// Turns a model's raw JSON reply into a <see cref="ParseOutcome"/> (PLAN.md §5.1, §5.6).
///
/// Extracted out of <see cref="NvidiaIntentParser"/> as its own unit for one reason: the offline
/// half of the eval suite (§5.6, Phase 2 Step 9) replays RECORDED model responses through this
/// exact code, not a reimplementation of it. If the two ever drifted, the eval suite would be
/// scoring a parser that does not match what production runs -- passing in CI while production
/// behaved differently.
///
/// Deliberately defensive even though the schema should guarantee the shape (§5.1): a model that
/// silently ignored its structured-output contract must degrade to
/// <see cref="ParseOutcome.Unavailable"/>, never throw into a caller mid-request.
/// </summary>
public static class IntentResponseParser
{
    /// <summary><see cref="Detail"/> is set only when parsing failed, for the caller to log.</summary>
    public sealed record Result(ParseOutcome Outcome, string? Detail = null);

    public static Result Parse(string content)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(content);
        }
        catch (JsonException ex)
        {
            return new Result(
                new ParseOutcome.Unavailable("The language model did not return usable JSON."),
                ex.Message);
        }

        if (node is null)
        {
            return new Result(new ParseOutcome.Unavailable("The language model returned null."));
        }

        var clarification = node["clarification"]?.GetValue<string?>();
        if (!string.IsNullOrWhiteSpace(clarification))
        {
            return new Result(new ParseOutcome.Parsed(ParsedIntent.AskInstead(clarification)));
        }

        var concepts = node["concepts"]?.AsArray()
            .Select(v => v?.GetValue<string>())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .ToList() ?? [];

        var filters = new List<ExplicitFilter>();
        foreach (var item in node["explicit_filters"]?.AsArray() ?? [])
        {
            var field = item?["field"]?.GetValue<string>();
            var op = item?["operator"]?.GetValue<string>();
            var value = item?["value"];

            // A malformed filter is dropped rather than guessed at, and the resolver's validation
            // still sees whatever survives. Inventing a default operator here would be exactly the
            // "model picked the number" failure §5.1 exists to prevent.
            if (field is null || op is null || value is null) continue;
            if (!Enum.TryParse<ComparisonOperator>(op, ignoreCase: true, out var parsedOp)) continue;

            filters.Add(new ExplicitFilter
            {
                Field = field,
                Operator = parsedOp,
                Value = value.GetValue<decimal>(),
            });
        }

        return new Result(new ParseOutcome.Parsed(new ParsedIntent
        {
            Concepts = concepts,
            ExplicitFilters = filters,
        }));
    }
}
