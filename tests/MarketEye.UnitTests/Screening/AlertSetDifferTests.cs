using FluentAssertions;
using MarketEye.Application.Screening;
using Xunit;

namespace MarketEye.UnitTests.Screening;

/// <summary>
/// Pure set-diff logic behind PLAN.md §10 Phase 4 "Alerts". <see cref="AlertDiffer"/>
/// (Infrastructure) adds the database read/write around this; everything worth asserting about
/// the diff itself is testable here without one.
/// </summary>
public class AlertSetDifferTests
{
    private static AlertSetDiffer.Member M(int id, string ticker) => new(id, ticker);

    [Fact]
    public void No_change_between_runs_raises_no_events()
    {
        var previous = new[] { M(1, "RELIANCE"), M(2, "TCS") };
        var current = new[] { M(1, "RELIANCE"), M(2, "TCS") };

        var diff = AlertSetDiffer.Diff(previous, current);

        diff.Entered.Should().BeEmpty();
        diff.Exited.Should().BeEmpty();
    }

    [Fact]
    public void A_new_member_is_reported_as_entered()
    {
        var previous = new[] { M(1, "RELIANCE") };
        var current = new[] { M(1, "RELIANCE"), M(2, "TCS") };

        var diff = AlertSetDiffer.Diff(previous, current);

        diff.Entered.Should().BeEquivalentTo([M(2, "TCS")]);
        diff.Exited.Should().BeEmpty();
    }

    [Fact]
    public void A_dropped_member_is_reported_as_exited()
    {
        var previous = new[] { M(1, "RELIANCE"), M(2, "TCS") };
        var current = new[] { M(1, "RELIANCE") };

        var diff = AlertSetDiffer.Diff(previous, current);

        diff.Entered.Should().BeEmpty();
        diff.Exited.Should().BeEquivalentTo([M(2, "TCS")]);
    }

    [Fact]
    public void Overlapping_entries_and_exits_are_both_reported_in_one_diff()
    {
        var previous = new[] { M(1, "RELIANCE"), M(2, "TCS") };
        var current = new[] { M(2, "TCS"), M(3, "INFY") };

        var diff = AlertSetDiffer.Diff(previous, current);

        diff.Entered.Should().BeEquivalentTo([M(3, "INFY")]);
        diff.Exited.Should().BeEquivalentTo([M(1, "RELIANCE")]);
    }

    [Fact]
    public void Full_turnover_reports_every_previous_member_exited_and_every_current_member_entered()
    {
        var previous = new[] { M(1, "RELIANCE"), M(2, "TCS") };
        var current = new[] { M(3, "INFY"), M(4, "HDFC") };

        var diff = AlertSetDiffer.Diff(previous, current);

        diff.Entered.Should().BeEquivalentTo([M(3, "INFY"), M(4, "HDFC")]);
        diff.Exited.Should().BeEquivalentTo([M(1, "RELIANCE"), M(2, "TCS")]);
    }

    [Fact]
    public void An_empty_previous_set_reports_every_current_member_as_entered()
    {
        // AlertDiffer itself never calls Diff at all on a strategy's first-ever run (there is no
        // previous ScreenRun to pass in) -- this asserts the pure function's own behavior in
        // isolation, distinct from that higher-level "skip the first run" policy.
        var current = new[] { M(1, "RELIANCE"), M(2, "TCS") };

        var diff = AlertSetDiffer.Diff([], current);

        diff.Entered.Should().BeEquivalentTo(current);
        diff.Exited.Should().BeEmpty();
    }
}
