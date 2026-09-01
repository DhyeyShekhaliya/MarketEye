using MarketEye.Domain.Entities;

namespace MarketEye.Domain.Screening.Vocabulary;

/// <summary>
/// One entry in the controlled vocabulary (PLAN.md §5.2).
///
/// This table is what makes §5.1 enforceable. When a user says "cheap", the threshold comes from
/// here — a value a human chose, can see, and can edit — not from the model's opinion. A concept
/// the model returns that is not in this vocabulary is a hard validation failure, never a
/// fallback (§5.1).
/// </summary>
public sealed record MetricConcept
{
    /// <summary>Stable name the AI emits and the validator matches on, e.g. "PeRatio".</summary>
    public required string Name { get; init; }

    /// <summary>Human-facing label for the interpretation panel (§5.3).</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// The physical column this resolves to. Deliberately NOT part of the AI's vocabulary — the
    /// model names a concept and the compiler resolves the column, so no model output ever
    /// reaches SQL.
    /// </summary>
    public required string ColumnName { get; init; }

    /// <summary>Which table the column lives on, so the compiler knows which alias to qualify with.</summary>
    public required MetricSource Source { get; init; }

    /// <summary>Operators that make sense for this metric.</summary>
    public required IReadOnlyList<ComparisonOperator> AllowedOperators { get; init; }

    /// <summary>
    /// Sane bounds. A P/E of 10,000 is a data error or a prompt-injection attempt, not a screen.
    /// Rejecting out-of-range values here keeps nonsense out of the query planner.
    /// </summary>
    public required decimal MinValue { get; init; }
    public required decimal MaxValue { get; init; }

    public string? Unit { get; init; }
}
