namespace MarketEye.Domain.Screening;

/// <summary>
/// A proposed create-or-update of a saved strategy, before it is trusted (PLAN.md §10).
///
/// Carries a <see cref="ScreenCriteria"/> rather than a JSON string for the same reason
/// StrategyConceptDraft carries a FilterNode: validation is pure tree work, and serialisation
/// happens exactly once, at the storage boundary.
/// </summary>
public sealed record SavedStrategyDraft
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? OriginalPrompt { get; init; }
    public required ScreenCriteria Criteria { get; init; }
}
