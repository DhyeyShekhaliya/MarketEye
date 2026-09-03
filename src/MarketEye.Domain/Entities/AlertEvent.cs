namespace MarketEye.Domain.Entities;

/// <summary>
/// A recorded entry into or exit from one saved strategy's matched set (PLAN.md §10 Phase 4
/// "Alerts": "notify when a security enters/exits a saved strategy").
///
/// Written once, at alert-check time, by diffing a <see cref="ScreenRun"/>'s
/// <see cref="ScreenRun.MemberSecuritiesJson"/> against the immediately preceding run for the same
/// strategy -- never recomputed on read. A feed needs history ("entered Tuesday, exited Friday"),
/// not just the latest diff, and recomputing an N-way diff across every historical run on each page
/// view would be wasted work for something that only ever changes once a day.
/// </summary>
public class AlertEvent
{
    public long Id { get; set; }

    /// <summary>
    /// Cascades on delete, unlike <see cref="ScreenRun.SavedStrategyId"/>'s SetNull: an alert with
    /// no strategy left to point back to is meaningless clutter, not history worth keeping.
    /// </summary>
    public int SavedStrategyId { get; set; }
    public SavedStrategy? SavedStrategy { get; set; }

    public int SecurityId { get; set; }

    /// <summary>Denormalised so the feed never has to join Securities to render a row.</summary>
    public required string Ticker { get; set; }

    public AlertEventType EventType { get; set; }

    /// <summary>The run whose diff against its predecessor produced this event.</summary>
    public long ScreenRunId { get; set; }

    public DateTimeOffset DetectedAt { get; set; }

    /// <summary>The sealed snapshot date the triggering run used.</summary>
    public DateOnly AsOfDate { get; set; }
}

public enum AlertEventType
{
    Entered = 0,
    Exited = 1,
}
