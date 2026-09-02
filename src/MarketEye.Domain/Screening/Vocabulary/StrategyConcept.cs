namespace MarketEye.Domain.Screening.Vocabulary;

/// <summary>
/// One entry in the Strategy Vocabulary (PLAN.md §5.2) — the semantic layer the model is allowed
/// to name.
///
/// This is the table §5.1's rule actually rests on. When a user says "cheap", the thresholds come
/// from <see cref="Definition"/> — numbers a human chose, can see, and can edit — not from the
/// model's opinion. A concept the model returns that is not here is a hard validation failure,
/// never a fallback (§5.1).
///
/// Distinct from <see cref="MetricConcept"/> on purpose. A MetricConcept carries a physical column
/// name and is what the compiler turns into SQL, so it is system-owned and sealed. A
/// StrategyConcept carries only a <see cref="FilterNode"/> over metric *names*, so a user editing
/// what "cheap" means can never reach a SQL identifier. See docs/adr/0007.
/// </summary>
public sealed record StrategyConcept
{
    /// <summary>Normalised name the model emits and the resolver matches, e.g. "small_cap".</summary>
    public required string Name { get; init; }

    /// <summary>Human-facing label for the interpretation panel (§5.3), e.g. "Small cap".</summary>
    public required string DisplayName { get; init; }

    public string? Description { get; init; }

    /// <summary>
    /// Other normalised spellings that resolve here. Given to the model as vocabulary context so
    /// it can map "beaten down" to "oversold" rather than inventing a concept.
    /// </summary>
    public required IReadOnlyList<string> Aliases { get; init; }

    /// <summary>
    /// What the concept means, as a criteria fragment over metric names. Already deserialised, so
    /// the resolver is pure tree work and never parses JSON on the request path.
    ///
    /// Validated by ScreenCriteriaValidator on the way IN (when a user saves an edit), so a
    /// definition that reached this record is one the compiler can already handle.
    /// </summary>
    public required FilterNode Definition { get; init; }

    /// <summary>Disabled concepts are invisible to the model and rejected by the resolver.</summary>
    public required bool IsEnabled { get; init; }

    /// <summary>Seeded concepts. Editable, but not deletable — the vocabulary must never empty.</summary>
    public required bool IsSystem { get; init; }

    /// <summary>
    /// Null until authentication exists (PLAN.md §14 leaves it open). Modelled now so adding
    /// per-user vocabularies later is rows, not a schema change.
    /// </summary>
    public string? OwnerUserId { get; init; }
}
