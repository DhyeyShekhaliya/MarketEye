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
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(ScreenCriteria criteria) =>
        JsonSerializer.Serialize(criteria, Options);

    public static ScreenCriteria Deserialize(string json) =>
        JsonSerializer.Deserialize<ScreenCriteria>(json, Options)
        ?? throw new JsonException("Criteria JSON deserialised to null.");
}
