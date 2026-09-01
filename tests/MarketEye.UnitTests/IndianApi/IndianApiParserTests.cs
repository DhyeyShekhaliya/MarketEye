using System.Text.Json;
using FluentAssertions;
using MarketEye.Domain.Entities;
using MarketEye.Infrastructure.MarketData.IndianApi;
using Xunit;

namespace MarketEye.UnitTests.IndianApi;

/// <summary>
/// Fixtures mirror the real indianapi.in response shape, including the collision that crashed the
/// first live run: Annual and Interim statements sharing a period-end date.
/// </summary>
public class IndianApiParserTests
{
    private static JsonDocument Doc(string json) => JsonDocument.Parse(json);

    private const string Financials = """
    {
      "financials": [
        { "Type": "Annual", "EndDate": "2026-03-31", "stockFinancialMap": {
            "INC": [ {"key":"TotalRevenue","value":"1075675.00"}, {"key":"NetIncome","value":"80775.00"} ],
            "BAL": [ {"key":"TotalDebt","value":"398000.00"}, {"key":"TotalEquity","value":"904030.00"} ] } },
        { "Type": "Interim", "EndDate": "2026-03-31", "stockFinancialMap": {
            "INC": [ {"key":"TotalRevenue","value":"270000.00"}, {"key":"NetIncome","value":"20000.00"} ],
            "BAL": [ {"key":"TotalDebt","value":"398000.00"}, {"key":"TotalEquity","value":"904030.00"} ] } },
        { "Type": "Annual", "EndDate": "2025-03-31", "stockFinancialMap": {
            "INC": [ {"key":"Revenue","value":"900000.00"} ],
            "BAL": [ {"key":"LongTermDebt","value":"270751.00"} ] } }
      ]
    }
    """;

    [Fact]
    public void Interim_statements_are_excluded_so_periods_cannot_collide()
    {
        // The live failure: an FY and a Q4 both ending 2026-03-31 map to the same key
        // (SecurityId, FiscalPeriodEnd) and EF refuses to track both.
        using var doc = Doc(Financials);
        var rows = IndianApiParser.ParseFundamentals(doc, securityId: 1);

        rows.Should().HaveCount(2);
        rows.Select(r => r.FiscalPeriodEnd).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Annual_figures_win_over_the_interim_for_the_same_period_end()
    {
        // Not just "one of them" -- it must be the annual one, or a screen would compare a
        // quarter's revenue against other companies' full years.
        using var doc = Doc(Financials);
        var rows = IndianApiParser.ParseFundamentals(doc, 1);

        var fy2026 = rows.Single(r => r.FiscalPeriodEnd == new DateOnly(2026, 3, 31));
        fy2026.Revenue.Should().Be(1075675.00m, "the annual figure, not the 270000 interim");
    }

    [Fact]
    public void Reported_dates_are_estimated_and_flagged()
    {
        using var doc = Doc(Financials);
        var rows = IndianApiParser.ParseFundamentals(doc, 1);

        foreach (var r in rows)
        {
            r.IsReportedDateEstimated.Should().BeTrue();
            r.ReportedDate.Should().BeAfter(r.FiscalPeriodEnd,
                "§4.1: results cannot be known before the period ends");
        }
    }

    [Fact]
    public void Alternative_metric_keys_are_accepted()
    {
        // The provider names the same figure differently across layouts: Revenue vs TotalRevenue,
        // TotalDebt vs LongTermDebt.
        using var doc = Doc(Financials);
        var rows = IndianApiParser.ParseFundamentals(doc, 1);

        var fy2025 = rows.Single(r => r.FiscalPeriodEnd == new DateOnly(2025, 3, 31));
        fy2025.Revenue.Should().Be(900000.00m);
        fy2025.TotalDebt.Should().Be(270751.00m);
    }

    [Fact]
    public void A_statement_with_no_usable_figures_is_skipped()
    {
        // Storing it would put an empty period into the temporal table, and a point-in-time read
        // would return blanks instead of the last real filing.
        using var doc = Doc("""
        { "financials": [ { "Type": "Annual", "EndDate": "2024-03-31",
            "stockFinancialMap": { "INC": [ {"key":"SomethingElse","value":"1"} ], "BAL": [] } } ] }
        """);

        IndianApiParser.ParseFundamentals(doc, 1).Should().BeEmpty();
    }

    [Fact]
    public void A_missing_financials_section_yields_nothing_rather_than_throwing()
    {
        using var doc = Doc("""{ "companyName": "X" }""");
        IndianApiParser.ParseFundamentals(doc, 1).Should().BeEmpty();
    }

    [Fact]
    public void Corporate_actions_parse_all_four_types()
    {
        using var doc = Doc("""
        { "stockCorporateActionData": {
            "bonus":    [ {"remarks":"Bonus issue in the ratio of 1:1 of Rs. 10/-.","xbDate":"2024-10-28"} ],
            "splits":   [ {"remarks":"Face value split from Rs. 10 to Rs. 5","exDate":"2023-06-15"} ],
            "rights":   [ {"remarks":"Rights issue in the ratio of 1:5 at Rs. 250","exDate":"2022-05-10"} ],
            "dividend": [ {"remarks":"Rs.6.0000 per share(60%)Final Dividend","xdDate":"2026-06-05","value":6} ] } }
        """);

        var actions = IndianApiParser.ParseCorporateActions(doc, 1);
        actions.Should().HaveCount(4);

        actions.Single(a => a.ActionType == CorporateActionType.Bonus)
            .AdjustmentFactor.Should().Be(0.5m);
        actions.Single(a => a.ActionType == CorporateActionType.Split)
            .AdjustmentFactor.Should().Be(0.5m);
        actions.Single(a => a.ActionType == CorporateActionType.Dividend)
            .DividendAmount.Should().Be(6m);

        // Rights carry no factor: dilution needs the cum-rights market price, which is not in the
        // response. PriceAdjuster skips a null factor rather than applying a wrong one.
        actions.Single(a => a.ActionType == CorporateActionType.Rights)
            .AdjustmentFactor.Should().BeNull();
    }

    [Fact]
    public void An_action_with_an_unparseable_ratio_is_kept_but_left_unadjusted()
    {
        // Keeping it preserves the audit trail in RawDescription; the null factor means
        // PriceAdjuster leaves a visible discontinuity rather than inventing a number.
        using var doc = Doc("""
        { "stockCorporateActionData": { "bonus": [ {"remarks":"Bonus issue declared","xbDate":"2024-10-28"} ] } }
        """);

        var action = IndianApiParser.ParseCorporateActions(doc, 1).Single();
        action.AdjustmentFactor.Should().BeNull();
        action.RawDescription.Should().Be("Bonus issue declared");
    }

    [Fact]
    public void An_action_with_no_usable_date_is_dropped()
    {
        using var doc = Doc("""
        { "stockCorporateActionData": { "bonus": [ {"remarks":"Bonus issue in the ratio of 1:1"} ] } }
        """);

        IndianApiParser.ParseCorporateActions(doc, 1).Should().BeEmpty();
    }
}
