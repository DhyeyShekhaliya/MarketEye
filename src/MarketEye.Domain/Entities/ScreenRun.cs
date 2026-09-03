namespace MarketEye.Domain.Entities;

/// <summary>
/// A recorded screen execution (PLAN.md §4.3).
///
/// <see cref="SnapshotId"/> is what makes it reproducible: re-running a stored ScreenRun against
/// the same sealed snapshot returns identical results, forever (§4.5).
/// </summary>
public class ScreenRun
{
    public long Id { get; set; }

    public int SnapshotId { get; set; }
    public DataSnapshot? Snapshot { get; set; }

    /// <summary>Serialised ScreenCriteria. Stored so the run can be replayed exactly.</summary>
    public required string CriteriaJson { get; set; }

    public DateTimeOffset RunAt { get; set; }
    public int ResultCount { get; set; }
    public int DurationMs { get; set; }

    /// <summary>
    /// Set when this run replayed a <see cref="SavedStrategy"/> (from its "Run" button or the
    /// nightly alert check) rather than an ad hoc /api/screen call. Null on delete rather than
    /// deleting the run: a run is evidence of what happened and outlives the strategy that
    /// produced it (PLAN.md §10 Phase 4 "Alerts").
    /// </summary>
    public int? SavedStrategyId { get; set; }
    public SavedStrategy? SavedStrategy { get; set; }

    /// <summary>
    /// JSON array of {Id, Ticker, Name} -- the matched security set, present only when
    /// <see cref="SavedStrategyId"/> is set. Alerts diff this run's set against the immediately
    /// preceding run for the same strategy to detect entries/exits (<see cref="AlertEvent"/>); an
    /// ad hoc screen has no strategy to diff against, so it is never populated there, avoiding the
    /// storage cost for a run nothing will ever read this field back from.
    /// </summary>
    public string? MemberSecuritiesJson { get; set; }

    /// <summary>
    /// True when this run was answered from ScreenResultCache (§5.5) rather than executed.
    /// ScreeningEngine writes a row per execution; without this flag a cache hit would skip the
    /// write entirely and quietly understate how often a screen actually runs. DurationMs is 0 on
    /// a cache hit -- there was no query to time.
    /// </summary>
    public bool FromCache { get; set; }
}
