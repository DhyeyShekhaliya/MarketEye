using System.Text.Json;
using System.Text.Json.Nodes;
using MarketEye.Domain.Screening;
using MarketEye.Domain.Screening.Vocabulary;

namespace MarketEye.Ai;

/// <summary>
/// Builds the model's output schema from the live vocabulary (PLAN.md §5.1).
///
/// This is the strongest guarantee in the phase, and it comes from one detail: the `concepts` array
/// is typed as an ENUM of the concept names that currently exist, and `explicit_filters[].field`
/// as an enum of metric names. Under schema-constrained decoding the model cannot emit a concept
/// that is not in the vocabulary — not "is unlikely to", cannot. IntentResolver still fails closed
/// behind it, because a schema is a property of one provider's decoder and the rule is a property
/// of the system.
///
/// The schema is regenerated per request from the vocabulary, so editing a definition or disabling
/// a concept changes what the model is even able to say, with no prompt to keep in sync.
///
/// Deliberately a conservative JSON Schema subset — objects, arrays, enum, string/number, every
/// property required, additionalProperties false. That is the intersection of OpenAI strict mode
/// and grammar-based guided decoding, so the same schema works whichever mechanism a provider
/// offers, and switching providers does not mean rewriting it.
/// </summary>
public static class IntentJsonSchema
{
    public const string SchemaName = "screen_intent";

    /// <summary>
    /// Only the operators a user can actually state in prose. Equal and NotEqual are omitted:
    /// nobody screens for "P/E exactly 15", and offering them invites the model to use one.
    /// </summary>
    private static readonly ComparisonOperator[] Operators =
    [
        ComparisonOperator.LessThan,
        ComparisonOperator.LessThanOrEqual,
        ComparisonOperator.GreaterThan,
        ComparisonOperator.GreaterThanOrEqual,
    ];

    public static JsonObject Build(
        IStrategyConceptVocabulary strategies, IMetricConceptVocabulary metrics)
    {
        var conceptNames = strategies.Enabled
            .Select(c => c.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        var metricNames = metrics.All
            .Select(m => m.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        return new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            // Strict decoding requires every property to be listed as required; optionality is
            // expressed by allowing null in the type, not by omitting the key.
            ["required"] = new JsonArray("concepts", "explicit_filters", "clarification"),
            ["properties"] = new JsonObject
            {
                ["concepts"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] =
                        "Names of strategy concepts that apply. Choose only from the list.",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = Enum(conceptNames),
                    },
                },

                ["explicit_filters"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] =
                        "Numeric filters ONLY where the user stated the number themselves. " +
                        "Never attach a number to a qualitative word.",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["required"] = new JsonArray("field", "operator", "value"),
                        ["properties"] = new JsonObject
                        {
                            ["field"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["enum"] = Enum(metricNames),
                            },
                            ["operator"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["enum"] = Enum(Operators.Select(o => o.ToString()).ToArray()),
                            },
                            ["value"] = new JsonObject { ["type"] = "number" },
                        },
                    },
                },

                ["clarification"] = new JsonObject
                {
                    // Null when the request mapped cleanly. §5.6 makes this the required exit for
                    // a vague request, so it is part of the schema rather than an error path.
                    ["type"] = new JsonArray("string", "null"),
                    ["description"] =
                        "A question to ask the user when the request is too vague or ambiguous " +
                        "to map onto the concepts above. Null otherwise.",
                },
            },
        };
    }

    public static string BuildJson(
        IStrategyConceptVocabulary strategies, IMetricConceptVocabulary metrics) =>
        Build(strategies, metrics).ToJsonString(new JsonSerializerOptions { WriteIndented = false });

    private static JsonArray Enum(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (var v in values) array.Add(v);
        return array;
    }
}
