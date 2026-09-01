namespace MarketEye.Domain.Entities;

/// <summary>
/// Per-day record of provider API calls consumed (PLAN.md §12: provider limits are a first-order
/// constraint, not an implementation detail).
///
/// indianapi.in's free tier allows 500 requests per day. Persisted rather than held in memory
/// because the process restarts — App Service F1 unloads after ~20 minutes idle — and an in-memory
/// counter would reset to zero on every cold start, silently allowing many times the quota.
/// </summary>
public class ApiCallBudget
{
    public int Id { get; set; }

    /// <summary>Provider key, e.g. "indianapi". One budget per provider per day.</summary>
    public required string Provider { get; set; }

    /// <summary>UTC date the quota applies to.</summary>
    public DateOnly Date { get; set; }

    public int CallsUsed { get; set; }

    /// <summary>Recorded so a limit change is visible in history rather than silently applied.</summary>
    public int DailyLimit { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
