using FluentAssertions;
using MarketEye.Domain.Entities;
using MarketEye.Infrastructure.Screening;
using Xunit;

namespace MarketEye.BacktestTests.BiasGuards;

/// <summary>
/// PLAN.md §8.2: every guard must fail loudly. These tests assert the throw, not the filter —
/// a guard that silently corrects produces plausible results that are wrong, which is the exact
/// failure mode §8 exists to prevent.
/// </summary>
public class PointInTimeGuardTests
{
    private static Security Sec(int id, string ticker, bool active, string? delisted = null) => new()
    {
        Id = id, Ticker = ticker, ProviderSecurityId = $"ISIN{id:D6}",
        Name = ticker, Exchange = "NSE", IsActive = active,
        DelistedDate = delisted is null ? null : DateOnly.Parse(delisted),
    };

    private static PriceBar Bar(int securityId, string date, bool circuitLocked) => new()
    {
        SecurityId = securityId, Date = DateOnly.Parse(date),
        Open = 100, High = 100, Low = 100, Close = 100, AdjClose = 100, Volume = 1000,
        IsCircuitLocked = circuitLocked,
    };

    [Fact]
    public void Reading_an_unsealed_snapshot_throws()
    {
        var open = new DataSnapshot
        {
            Id = 7, AsOfDate = DateOnly.Parse("2024-06-28"),
            CreatedAt = DateTimeOffset.UtcNow, SealedAt = null, ProviderVersion = "t",
        };

        var act = () => PointInTimeGuard.RequireSealed(open);
        act.Should().Throw<LookaheadBiasException>().WithMessage("*not sealed*");
    }

    [Fact]
    public void Reading_past_the_as_of_date_throws()
    {
        var act = () => PointInTimeGuard.RequireNotAfterAsOf(
            DateOnly.Parse("2024-07-01"), DateOnly.Parse("2024-06-28"), "price bars");

        act.Should().Throw<LookaheadBiasException>().WithMessage("*did not exist yet*");
    }

    [Fact]
    public void Reading_on_the_as_of_date_itself_is_allowed()
    {
        var act = () => PointInTimeGuard.RequireNotAfterAsOf(
            DateOnly.Parse("2024-06-28"), DateOnly.Parse("2024-06-28"), "price bars");

        act.Should().NotThrow("the as-of date's own data was knowable that day");
    }

    [Fact]
    public void Fundamentals_reported_after_the_as_of_date_throw()
    {
        // §4.1's reporting-lag half: the fiscal period may have ended, but the market had not
        // been told yet.
        var act = () => PointInTimeGuard.RequireReportedBy(
            DateOnly.Parse("2024-05-02"), DateOnly.Parse("2024-04-15"), securityId: 42);

        act.Should().Throw<LookaheadBiasException>().WithMessage("*lookahead bias*");
    }

    [Fact]
    public void Executing_at_the_signal_dates_close_throws()
    {
        // §7: the screen used data as of T, so the fill belongs at T+1's open. Filling at T's
        // close uses information from the session the decision was made in.
        var t = DateOnly.Parse("2024-06-28");

        var act = () => PointInTimeGuard.RequireExecutionAfterSignal(t, t);
        act.Should().Throw<LookaheadBiasException>().WithMessage("*T+1*");
    }

    [Fact]
    public void Executing_the_next_day_is_allowed()
    {
        var act = () => PointInTimeGuard.RequireExecutionAfterSignal(
            DateOnly.Parse("2024-06-28"), DateOnly.Parse("2024-07-01"));

        act.Should().NotThrow();
    }

    [Fact]
    public void A_universe_that_drops_a_later_delisted_security_throws()
    {
        // The headline survivorship test. GAMMA delisted in June 2024; a screen run in January
        // 2024 must include it, because it was trading then.
        var asOf = DateOnly.Parse("2024-01-31");
        var all = new[]
        {
            Sec(1, "ALPHA", active: true),
            Sec(2, "BETA", active: true),
            Sec(3, "GAMMA", active: false, delisted: "2024-06-28"),
        };
        var survivorsOnly = all.Where(s => s.IsActive).ToList();

        var act = () => PointInTimeGuard.RequireDelistedIncluded(survivorsOnly, all, asOf);

        act.Should().Throw<LookaheadBiasException>()
            .WithMessage("*GAMMA*").And.Message.Should().Contain("survivorship bias");
    }

    [Fact]
    public void A_universe_including_the_delisted_security_passes()
    {
        var asOf = DateOnly.Parse("2024-01-31");
        var all = new[]
        {
            Sec(1, "ALPHA", active: true),
            Sec(3, "GAMMA", active: false, delisted: "2024-06-28"),
        };

        var act = () => PointInTimeGuard.RequireDelistedIncluded(all, all, asOf);
        act.Should().NotThrow();
    }

    [Fact]
    public void A_security_delisted_before_the_as_of_date_may_be_absent()
    {
        // The guard must not over-fire. Something that stopped trading in 2023 genuinely was not
        // in the 2024 universe, and demanding its presence would be as wrong as dropping it.
        var asOf = DateOnly.Parse("2024-01-31");
        var all = new[]
        {
            Sec(1, "ALPHA", active: true),
            Sec(4, "OLDCO", active: false, delisted: "2023-05-10"),
        };
        var universe = new[] { all[0] };

        var act = () => PointInTimeGuard.RequireDelistedIncluded(universe, all, asOf);
        act.Should().NotThrow("OLDCO had already delisted before the as-of date");
    }

    [Fact]
    public void Filling_a_circuit_locked_bar_throws()
    {
        // §7 revision 3: a locked stock cannot be filled at that price. FillExecutor is expected
        // to check IsCircuitLocked itself and never reach this call for a locked bar -- this guard
        // is the backstop that catches a future refactor that skips that check.
        var bar = Bar(1, "2024-06-28", circuitLocked: true);

        var act = () => PointInTimeGuard.RequireNotCircuitLocked(bar);

        act.Should().Throw<LookaheadBiasException>().WithMessage("*circuit-locked*");
    }

    [Fact]
    public void Filling_an_unlocked_bar_is_allowed()
    {
        var bar = Bar(1, "2024-06-28", circuitLocked: false);

        var act = () => PointInTimeGuard.RequireNotCircuitLocked(bar);

        act.Should().NotThrow();
    }
}
