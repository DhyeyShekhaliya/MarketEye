namespace MarketEye.Domain.Entities;

/// <summary>
/// A sealed, immutable view of the data as of a point in time (PLAN.md §4.5).
/// Ingestion writes, then seals. Screens and backtests resolve against a
/// <see cref="Id"/> and never read live tables — that is what makes results
/// reproducible and makes cache invalidation free.
/// </summary>
public class DataSnapshot
{
    public int Id { get; set; }

    /// <summary>The market date this snapshot represents.</summary>
    public DateOnly AsOfDate { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Null until ingestion completed successfully. Queries read sealed snapshots only,
    /// so a half-finished nightly job leaves something nothing reads (§4.5).
    /// </summary>
    public DateTimeOffset? SealedAt { get; set; }

    public required string ProviderVersion { get; set; }

    public long PriceRowCount { get; set; }
    public long FundamentalRowCount { get; set; }

    public bool IsSealed => SealedAt is not null;
}
