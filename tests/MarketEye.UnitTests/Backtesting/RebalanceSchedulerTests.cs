using FluentAssertions;
using MarketEye.Application.Backtesting;
using MarketEye.Domain.Backtesting;
using Xunit;

namespace MarketEye.UnitTests.Backtesting;

public class RebalanceSchedulerTests
{
    [Fact]
    public void Monthly_produces_one_date_per_calendar_month()
    {
        var dates = RebalanceScheduler.Dates(
            new DateOnly(2024, 1, 15), new DateOnly(2024, 4, 15), RebalanceFrequency.Monthly);

        dates.Should().Equal(
            new DateOnly(2024, 1, 15), new DateOnly(2024, 2, 15),
            new DateOnly(2024, 3, 15), new DateOnly(2024, 4, 15));
    }

    [Fact]
    public void Quarterly_steps_by_three_months()
    {
        var dates = RebalanceScheduler.Dates(
            new DateOnly(2024, 1, 1), new DateOnly(2024, 12, 31), RebalanceFrequency.Quarterly);

        dates.Should().Equal(
            new DateOnly(2024, 1, 1), new DateOnly(2024, 4, 1),
            new DateOnly(2024, 7, 1), new DateOnly(2024, 10, 1));
    }

    [Fact]
    public void Annual_steps_by_one_year()
    {
        var dates = RebalanceScheduler.Dates(
            new DateOnly(2021, 6, 1), new DateOnly(2024, 6, 1), RebalanceFrequency.Annual);

        dates.Should().Equal(
            new DateOnly(2021, 6, 1), new DateOnly(2022, 6, 1),
            new DateOnly(2023, 6, 1), new DateOnly(2024, 6, 1));
    }

    [Fact]
    public void A_window_shorter_than_one_period_still_returns_the_start_date()
    {
        var dates = RebalanceScheduler.Dates(
            new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 20), RebalanceFrequency.Monthly);

        dates.Should().Equal(new DateOnly(2024, 1, 1));
    }

    [Fact]
    public void Start_after_end_throws()
    {
        var act = () => RebalanceScheduler.Dates(
            new DateOnly(2024, 6, 1), new DateOnly(2024, 1, 1), RebalanceFrequency.Monthly);

        act.Should().Throw<ArgumentException>();
    }
}
