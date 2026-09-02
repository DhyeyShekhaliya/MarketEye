using System.Text.Json;
using FluentAssertions;
using MarketEye.Application.Screening;
using MarketEye.Domain.Screening;
using Xunit;

namespace MarketEye.UnitTests.Screening;

/// <summary>
/// Regression guard for a bug that reached a running deployment: the API bound enums from integers
/// only, so every screen the Blazor UI posted came back 400, while stored criteria used names. The
/// integration tests missed it because they build a ScreenCriteria in C# and call the engine
/// directly, never crossing the HTTP boundary — so nothing exercised the wire format.
///
/// These tests pin the format itself, which both the API and ScreenRun storage now share.
/// </summary>
public class CriteriaWireFormatTests
{
    private static ScreenCriteria Sample() => new()
    {
        Universe = new UniverseConstraint { Exchange = "NSE" },
        Root = new Group
        {
            Op = GroupOperator.And,
            Children = [new Comparison
            {
                Field = "Rsi14", Operator = ComparisonOperator.LessThan, Value = 40m,
            }],
        },
        Sort = new SortSpec { Field = "Volume", Direction = SortDirection.Descending },
        Limit = 5,
    };

    private static JsonSerializerOptions WebOptionsAsTheApiConfiguresThem()
    {
        // Exactly what Program.cs does to its ConfigureHttpJsonOptions.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ScreenCriteriaJson.ApplyWireFormat(options);
        return options;
    }

    [Fact]
    public void The_api_can_read_the_json_the_ui_sends()
    {
        // The literal payload shape Screen.razor posts: enums as names, nodes tagged by "kind".
        const string body = """
            {"universe":{"exchange":"NSE"},
             "root":{"kind":"group","op":"And","children":[
               {"kind":"comparison","field":"Rsi14","operator":"LessThan","value":40}]},
             "sort":{"field":"Volume","direction":"Descending"},
             "limit":5}
            """;

        var criteria = JsonSerializer.Deserialize<ScreenCriteria>(
            body, WebOptionsAsTheApiConfiguresThem());

        criteria.Should().NotBeNull();
        criteria!.Root.Comparisons().Should().ContainSingle()
            .Which.Operator.Should().Be(ComparisonOperator.LessThan);
        criteria.Sort!.Direction.Should().Be(SortDirection.Descending);
    }

    [Fact]
    public void Stored_criteria_deserialise_under_the_apis_options_and_vice_versa()
    {
        // §4.5 promises a stored ScreenRun replays identically. That promise spans both formats
        // once criteria can also arrive over HTTP, so the two must stay interchangeable.
        var stored = ScreenCriteriaJson.Serialize(Sample());

        var viaApi = JsonSerializer.Deserialize<ScreenCriteria>(
            stored, WebOptionsAsTheApiConfiguresThem());

        viaApi.Should().BeEquivalentTo(Sample());
    }

    [Fact]
    public void Enums_are_written_as_names_never_as_integers()
    {
        // An integer here would be a silent breaking change for anything already stored: adding
        // an operator in the middle of the enum would renumber every persisted ScreenRun.
        var json = ScreenCriteriaJson.Serialize(Sample());

        json.Should().Contain("\"LessThan\"").And.Contain("\"And\"").And.Contain("\"Descending\"");
        json.Should().NotContain("\"operator\":0");
    }
}
