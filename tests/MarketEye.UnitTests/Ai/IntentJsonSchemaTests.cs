using System.Text.Json.Nodes;
using FluentAssertions;
using MarketEye.Ai;
using MarketEye.UnitTests.Screening;
using Xunit;

namespace MarketEye.UnitTests.Ai;

/// <summary>
/// The schema is what turns §5.1 from a rule the model is asked to follow into one it cannot
/// break: under schema-constrained decoding, a concept outside the enum is unemittable. These
/// tests pin the properties that guarantee holds — and the subset restrictions that keep the same
/// schema working across providers.
/// </summary>
public class IntentJsonSchemaTests
{
    private static JsonObject Schema() =>
        IntentJsonSchema.Build(new TestStrategyVocabulary(), SeededMetricVocabulary.Instance);

    private static string[] EnumOf(JsonNode? node) =>
        node!.AsArray().Select(v => v!.GetValue<string>()).ToArray();

    [Fact]
    public void Concepts_are_constrained_to_the_vocabularys_enabled_names()
    {
        var values = EnumOf(Schema()["properties"]!["concepts"]!["items"]!["enum"]);

        values.Should().Contain("cheap").And.Contain("small_cap");

        // The disabled concept must be absent. If the model could still name it, "turning a
        // concept off" would mean nothing until the resolver rejected it -- which reads to a user
        // as the AI hallucinating rather than as their own setting taking effect.
        values.Should().NotContain("retired_idea");
    }

    [Fact]
    public void Explicit_filter_fields_are_constrained_to_metric_names()
    {
        var values = EnumOf(
            Schema()["properties"]!["explicit_filters"]!["items"]!["properties"]!["field"]!["enum"]);

        values.Should().Contain("PeRatio").And.Contain("MarketCap");

        // A strategy concept must not be nameable here: "cheap < 12" is the model attaching its
        // own number to a qualitative word, which is exactly what §5.1 forbids.
        values.Should().NotContain("cheap");
    }

    [Fact]
    public void Only_the_operators_a_user_can_state_in_prose_are_offered()
    {
        var values = EnumOf(
            Schema()["properties"]!["explicit_filters"]!["items"]!["properties"]!["operator"]!["enum"]);

        values.Should().BeEquivalentTo(
            ["LessThan", "LessThanOrEqual", "GreaterThan", "GreaterThanOrEqual"]);

        // Nobody screens for "P/E exactly 15"; offering it only invites the model to use it.
        values.Should().NotContain("Equal").And.NotContain("NotEqual");
    }

    [Fact]
    public void Every_object_is_closed_and_every_property_required()
    {
        // Both are required by OpenAI strict mode, and harmless everywhere else -- which is what
        // lets one schema serve whichever mechanism a provider offers.
        var schema = Schema();
        schema["additionalProperties"]!.GetValue<bool>().Should().BeFalse();
        EnumOf(schema["required"]).Should()
            .BeEquivalentTo(["concepts", "explicit_filters", "clarification"]);

        var filter = schema["properties"]!["explicit_filters"]!["items"]!;
        filter["additionalProperties"]!.GetValue<bool>().Should().BeFalse();
        EnumOf(filter["required"]).Should().BeEquivalentTo(["field", "operator", "value"]);
    }

    [Fact]
    public void Clarification_is_optional_by_allowing_null_not_by_being_absent()
    {
        // Strict mode has no optional properties. Omitting it from "required" would be rejected;
        // a nullable type is how §5.6's escape hatch is expressed.
        var type = Schema()["properties"]!["clarification"]!["type"]!;

        EnumOf(type).Should().BeEquivalentTo(["string", "null"]);
    }

    [Fact]
    public void The_schema_uses_only_the_portable_subset()
    {
        // oneOf/anyOf/$ref/conditionals are accepted by some decoders and not others. Staying
        // inside the intersection is what makes changing provider a config change.
        var json = IntentJsonSchema.BuildJson(
            new TestStrategyVocabulary(), SeededMetricVocabulary.Instance);

        foreach (var unsupported in new[] { "oneOf", "anyOf", "allOf", "$ref", "if", "not" })
        {
            json.Should().NotContain($"\"{unsupported}\"");
        }
    }

    [Fact]
    public void The_schema_tracks_the_vocabulary_rather_than_a_hardcoded_list()
    {
        // Regenerated per request, so adding a concept changes what the model can say with no
        // prompt or schema to keep in sync by hand.
        var withTestVocab = EnumOf(Schema()["properties"]!["concepts"]!["items"]!["enum"]);
        var withRealSeed = EnumOf(IntentJsonSchema.Build(
            new SeededStrategyVocabulary(), SeededMetricVocabulary.Instance)
            ["properties"]!["concepts"]!["items"]!["enum"]);

        withRealSeed.Should().HaveCountGreaterThan(withTestVocab.Length);
        withRealSeed.Should().Contain("cash_generative", "it is in the seed but not the test set");
    }
}
