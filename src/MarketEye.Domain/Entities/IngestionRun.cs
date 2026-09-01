namespace MarketEye.Domain.Entities;

/// <summary>History of ingestion attempts, including failures (PLAN.md §10, Phase 1).</summary>
public class IngestionRun
{
    public int Id { get; set; }
    public required string Source { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public IngestionStatus Status { get; set; }
    public long RecordsWritten { get; set; }

    /// <summary>Captured failure detail. Phase 1 requires failures be inspectable, not just counted.</summary>
    public string? Error { get; set; }

    public int? SnapshotId { get; set; }
    public DataSnapshot? Snapshot { get; set; }
}

public enum IngestionStatus
{
    Running = 0,
    Succeeded = 1,
    Failed = 2,
}
