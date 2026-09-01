using FluentAssertions;
using MarketEye.Infrastructure.MarketData.IndianApi;
using Xunit;

namespace MarketEye.UnitTests.IndianApi;

/// <summary>
/// The provider supplies no reporting date, so §4.1's second condition rests entirely on this
/// estimate. These tests fix the direction of the error: later than reality, never earlier.
/// </summary>
public class ReportingLagTests
{
    [Fact]
    public void An_annual_period_is_reported_sixty_days_after_year_end()
    {
        // SEBI LODR: annual results within 60 days of the financial year end.
        ReportingLag.EstimateReportedDate(new DateOnly(2024, 3, 31), isAnnual: true)
            .Should().Be(new DateOnly(2024, 5, 30));
    }

    [Fact]
    public void A_quarterly_period_is_reported_forty_five_days_after_quarter_end()
    {
        ReportingLag.EstimateReportedDate(new DateOnly(2024, 6, 30), isAnnual: false)
            .Should().Be(new DateOnly(2024, 8, 14));
    }

    [Fact]
    public void The_estimate_is_always_after_the_period_end()
    {
        // The property that matters. If a reported date could equal or precede the period end, a
        // screen run on the last day of a fiscal year would "know" that year's results -- pure
        // lookahead, and it would look like genuine alpha.
        foreach (var isAnnual in new[] { true, false })
        {
            for (var month = 1; month <= 12; month++)
            {
                var periodEnd = new DateOnly(2024, month, 28);
                ReportingLag.EstimateReportedDate(periodEnd, isAnnual)
                    .Should().BeAfter(periodEnd);
            }
        }
    }

    [Fact]
    public void Annual_filings_are_assumed_slower_than_quarterly_ones()
    {
        var end = new DateOnly(2024, 3, 31);
        ReportingLag.EstimateReportedDate(end, isAnnual: true)
            .Should().BeAfter(ReportingLag.EstimateReportedDate(end, isAnnual: false),
                "audited annual results take longer to publish than a quarterly update");
    }

    [Theory]
    [InlineData("Annual", true)]
    [InlineData("annual", true)]
    [InlineData("Interim", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Statement_type_maps_to_the_right_deadline(string? type, bool expected)
    {
        // An unrecognised type falls to the SHORTER quarterly lag. That is the conservative choice
        // in the other direction: a shorter lag makes data available sooner, so it is the one case
        // where the estimate could run early -- but treating an unknown statement as annual would
        // hide a real filing for two months and silently drop it from screens.
        ReportingLag.IsAnnual(type).Should().Be(expected);
    }
}
