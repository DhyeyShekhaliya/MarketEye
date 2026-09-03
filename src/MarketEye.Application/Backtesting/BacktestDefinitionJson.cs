using System.Text.Json;
using MarketEye.Application.Screening;
using MarketEye.Domain.Backtesting;

namespace MarketEye.Application.Backtesting;

/// <summary>
/// JSON for <see cref="BacktestDefinition"/>, mirroring <c>ScreenCriteriaJson</c>'s wire format —
/// it embeds a <see cref="ScreenCriteria"/> and shares the same enum-as-name / polymorphic
/// FilterNode requirements, so the two must agree or a stored definition's nested criteria would
/// deserialise into a shape the compiler has never seen.
/// </summary>
public static class BacktestDefinitionJson
{
    private static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = false };
        ScreenCriteriaJson.ApplyWireFormat(options);
        return options;
    }

    public static string Serialize(BacktestDefinition definition) =>
        JsonSerializer.Serialize(definition, Options);

    public static BacktestDefinition Deserialize(string json) =>
        JsonSerializer.Deserialize<BacktestDefinition>(json, Options)
        ?? throw new JsonException("Backtest definition JSON deserialised to null.");
}
