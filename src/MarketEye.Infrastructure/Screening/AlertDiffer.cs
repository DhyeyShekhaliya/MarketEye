using System.Text.Json;
using MarketEye.Application.Screening;
using MarketEye.Domain.Entities;
using MarketEye.Infrastructure.Persistence;

namespace MarketEye.Infrastructure.Screening;

/// <summary>
/// Reads a saved strategy's two most recent screen runs, hands their member sets to
/// <see cref="AlertSetDiffer"/>, and writes the resulting <see cref="AlertEvent"/> rows (PLAN.md
/// §10 Phase 4 "Alerts"). Infrastructure, not Application: it writes through
/// <see cref="MarketEyeDbContext"/>, so it belongs alongside <see cref="ScreeningEngine"/>, per
/// CLAUDE.md's "a DB-touching component lives in Infrastructure" invariant. The actual set-diff
/// math is pure and lives in <see cref="AlertSetDiffer"/> so it can be unit-tested without a
/// database.
/// </summary>
public static class AlertDiffer
{
    private sealed record MemberRow(int Id, string Ticker, string Name);

    /// <summary>
    /// Diffs <paramref name="newRun"/>'s matched set against <paramref name="previousRun"/>'s (null
    /// when this is the strategy's first-ever run with a member list) and writes an
    /// <see cref="AlertEvent"/> for every entry and exit. Returns the number of events written.
    ///
    /// A null <paramref name="previousRun"/> writes zero events by design: there is nothing to
    /// compare against, and treating every current member as a fresh "entry" would flood the feed
    /// with noise on day one for every strategy ever saved, rather than showing only genuine
    /// day-over-day change.
    /// </summary>
    public static async Task<int> DiffAndRecordAsync(
        MarketEyeDbContext db,
        int savedStrategyId,
        ScreenRun? previousRun,
        ScreenRun newRun,
        DateOnly asOfDate,
        CancellationToken ct)
    {
        if (previousRun is null) return 0;

        var previous = Deserialize(previousRun.MemberSecuritiesJson);
        var current = Deserialize(newRun.MemberSecuritiesJson);
        var diff = AlertSetDiffer.Diff(previous, current);

        if (diff.Entered.Count == 0 && diff.Exited.Count == 0) return 0;

        var now = DateTimeOffset.UtcNow;
        var events = diff.Entered
            .Select(m => BuildEvent(m, AlertEventType.Entered, savedStrategyId, newRun.Id, now, asOfDate))
            .Concat(diff.Exited.Select(
                m => BuildEvent(m, AlertEventType.Exited, savedStrategyId, newRun.Id, now, asOfDate)))
            .ToList();

        db.AlertEvents.AddRange(events);
        await db.SaveChangesAsync(ct);
        return events.Count;
    }

    private static AlertEvent BuildEvent(
        AlertSetDiffer.Member member, AlertEventType type, int savedStrategyId, long screenRunId,
        DateTimeOffset detectedAt, DateOnly asOfDate) => new()
    {
        SavedStrategyId = savedStrategyId,
        SecurityId = member.SecurityId,
        Ticker = member.Ticker,
        EventType = type,
        ScreenRunId = screenRunId,
        DetectedAt = detectedAt,
        AsOfDate = asOfDate,
    };

    private static List<AlertSetDiffer.Member> Deserialize(string? json) =>
        string.IsNullOrEmpty(json)
            ? []
            : (JsonSerializer.Deserialize<List<MemberRow>>(json) ?? [])
                .Select(r => new AlertSetDiffer.Member(r.Id, r.Ticker))
                .ToList();
}
