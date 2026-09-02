using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MarketEye.Application.Ai;
using MarketEye.Domain.Screening;
using MarketEye.Domain.Screening.Vocabulary;

namespace MarketEye.Ai;

/// <summary>
/// Calls an OpenAI-compatible chat endpoint and returns a <see cref="ParsedIntent"/> (§5.4).
///
/// A typed HttpClient rather than the OpenAI SDK, for one concrete reason: NVIDIA NIM models expose
/// schema-constrained output either through the standard `response_format` or through NVIDIA's
/// `nvext.guided_json` extension, and which one varies by model. `nvext` is a non-standard body
/// field, and bending the SDK's serialiser to emit it costs more than sending the request
/// ourselves. This also follows the shape the repo already uses twice — NseBhavcopyClient and
/// IndianApiClient, both typed clients with a standard resilience handler.
///
/// Supporting BOTH mechanisms is deliberate. Which one a model wants is then a setting
/// (<see cref="AiOptions.StructuredOutput"/>) rather than a fork in the code, so changing model or
/// provider does not mean rewriting the client.
///
/// Nothing here is trusted. The schema constrains the model, but the response is still parsed
/// defensively and handed to IntentResolver, which fails closed (§5.1).
/// </summary>
public sealed class NvidiaIntentParser(
    HttpClient http,
    IOptions<AiOptions> options,
    IStrategyConceptVocabulary strategies,
    IMetricConceptVocabulary metrics,
    ILogger<NvidiaIntentParser> logger) : IIntentParser
{
    private readonly AiOptions _options = options.Value;

    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web);

    public string Describe => $"{_options.Provider.ToString().ToLowerInvariant()}:{_options.Model}";

    public async Task<ParseOutcome> ParseAsync(string prompt, CancellationToken ct)
    {
        var (content, unavailableReason) = await FetchRawContentAsync(prompt, ct);
        return content is null
            ? new ParseOutcome.Unavailable(unavailableReason!)
            : Interpret(content);
    }

    /// <summary>
    /// Calls the model and returns its raw reply text, without interpreting it.
    ///
    /// Exposed publicly so the eval suite's live tier (§5.6, Phase 2 Step 9) can record the ACTUAL
    /// bytes a model returned, not a reconstruction built from an already-parsed ParsedIntent.
    /// Replaying real model output through the real parser is the entire point of the offline
    /// tier's regression coverage -- a synthetic recording would only ever test parsing of its own
    /// clean reconstruction, never a genuine shape the model actually produced.
    /// </summary>
    public async Task<(string? Content, string? UnavailableReason)> FetchRawContentAsync(
        string prompt, CancellationToken ct)
    {
        if (!_options.IsConfigured)
        {
            return (null, "No AI API key is configured.");
        }

        // Both the schema and the system prompt are rebuilt from the vocabulary on every call, so
        // an edit to a definition takes effect immediately rather than at the next restart (§5.2).
        var schema = IntentJsonSchema.Build(strategies, metrics);
        var system = SystemPrompt.Build(strategies, metrics);

        var body = BuildRequest(prompt, system, schema);

        try
        {
            using var response = await http.PostAsJsonAsync("chat/completions", body, ct);

            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning(
                    "Intent parse failed: {Status} {Detail}", (int)response.StatusCode, Truncate(detail));

                // A 429 here is the provider's own limit, distinct from our per-IP limiter and our
                // daily budget. It still degrades to "unavailable", never to a guessed screen.
                return (null, $"The language model returned {(int)response.StatusCode}.");
            }

            var payload = await response.Content.ReadFromJsonAsync<JsonNode>(ReadOptions, ct);
            var content = payload?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();

            return string.IsNullOrWhiteSpace(content)
                ? (null, "The language model returned an empty response.")
                : (content, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, "The language model timed out.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Could not reach the language model.");
            return (null, "Could not reach the language model.");
        }
    }

    private JsonObject BuildRequest(string prompt, string system, JsonObject schema)
    {
        var body = new JsonObject
        {
            ["model"] = _options.Model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = prompt }),
            ["max_tokens"] = _options.MaxOutputTokens,

            // Zero, not for "accuracy" but for reproducibility: §5.6's eval gate compares runs, and
            // a sampled parse would make the score depend on luck rather than on the prompt.
            ["temperature"] = 0,
        };

        switch (_options.StructuredOutput)
        {
            case StructuredOutputMode.NvextGuidedJson:
                // NVIDIA's extension. Grammar-constrained decoding, same schema.
                body["nvext"] = new JsonObject { ["guided_json"] = schema.DeepClone() };
                break;

            case StructuredOutputMode.ResponseFormatJsonSchema:
            default:
                body["response_format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject
                    {
                        ["name"] = IntentJsonSchema.SchemaName,
                        ["strict"] = true,
                        ["schema"] = schema.DeepClone(),
                    },
                };
                break;
        }

        return body;
    }

    /// <summary>
    /// Reads the model's JSON into a ParsedIntent via <see cref="IntentResponseParser"/> -- the
    /// same code the eval suite's offline tier replays recordings through, so the two can never
    /// silently diverge.
    /// </summary>
    private ParseOutcome Interpret(string content)
    {
        var result = IntentResponseParser.Parse(content);

        if (result.Detail is not null)
        {
            logger.LogWarning(
                "The model did not return JSON. This usually means the chosen model ignores " +
                "{Mode}; check AiOptions.StructuredOutput and the model's capabilities. {Message}",
                _options.StructuredOutput, result.Detail);
        }

        return result.Outcome;
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
