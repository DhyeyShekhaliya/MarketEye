namespace MarketEye.Domain.Screening.Vocabulary;

/// <summary>
/// A proposed create-or-update of a strategy concept, before it is trusted (PLAN.md §5.2).
///
/// Carries a <see cref="FilterNode"/> rather than a JSON string so that validation is pure tree
/// work and serialisation happens exactly once, at the storage boundary.
/// </summary>
public sealed record StrategyConceptDraft
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public required IReadOnlyList<string> Aliases { get; init; }
    public required FilterNode Definition { get; init; }
    public bool IsEnabled { get; init; } = true;
}
