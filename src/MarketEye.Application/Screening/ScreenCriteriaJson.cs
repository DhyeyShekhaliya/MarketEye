using System.Text.Json;
using System.Text.Json.Serialization;
using MarketEye.Domain.Screening;

namespace MarketEye.Application.Screening;

/// <summary>
/// JSON for <see cref="ScreenCriteria"/> (PLAN.md §6: "the JSON schema is a tree").
///
/// Round-tripping matters beyond storage: §4.5 promises that re-running a stored ScreenRun returns
/// identical results forever, and that promise is only as good as the serialisation.
/// </summary>
public static class ScreenCriteriaJson
{
    private static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = false };
        ApplyWireFormat(options);
        return options;
    }

    /// <summary>
    /// The one definition of how criteria cross a boundary — storage or HTTP.
    ///
    /// The API applies this to its own options so that a ScreenCriteria can be posted in exactly
    /// the shape it is persisted in. They diverged once: the API bound enums from integers only,
    /// so every request the Blazor UI sent was rejected with a 400 while stored criteria used
    /// names. Sharing the configuration is what stops that recurring.
    /// </summary>
    public static void ApplyWireFormat(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Converters.Add(new JsonStringEnumConverter());
    }

    public static string Serialize(ScreenCriteria criteria) =>
        JsonSerializer.Serialize(criteria, Options);

    public static ScreenCriteria Deserialize(string json) =>
        JsonSerializer.Deserialize<ScreenCriteria>(json, Options)
        ?? throw new JsonException("Criteria JSON deserialised to null.");

    /// <summary>
    /// Node-level round-trip, for StrategyConcepts.DefinitionJson (PLAN.md §5.2).
    ///
    /// Shares <see cref="Options"/> with the criteria round-trip on purpose: a concept definition
    /// is spliced directly into a ScreenCriteria tree by the resolver, so the two must serialise
    /// polymorphic nodes identically or a stored definition would deserialise into a shape the
    /// compiler has never seen.
    /// </summary>
    public static string SerializeNode(FilterNode node) =>
        JsonSerializer.Serialize(node, Options);

    public static FilterNode DeserializeNode(string json) =>
        JsonSerializer.Deserialize<FilterNode>(json, Options)
        ?? throw new JsonException("Filter node JSON deserialised to null.");
}
