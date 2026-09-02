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
    /// True when this run was answered from ScreenResultCache (§5.5) rather than executed.
    /// ScreeningEngine writes a row per execution; without this flag a cache hit would skip the
    /// write entirely and quietly understate how often a screen actually runs. DurationMs is 0 on
    /// a cache hit -- there was no query to time.
    /// </summary>
    public bool FromCache { get; set; }
}
