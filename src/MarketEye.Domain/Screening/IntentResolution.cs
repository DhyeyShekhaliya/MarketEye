namespace MarketEye.Domain.Screening;

/// <summary>
/// The result of turning a <see cref="ParsedIntent"/> into criteria (PLAN.md §5.3, §5.4).
///
/// Carries everything the interpretation panel draws, not just the criteria: the panel has to show
/// which concepts were chosen and what each one resolved to, because that display IS the
/// architecture made visible — the model chose four words, the user owns what all four mean.
/// </summary>
public sealed record IntentResolution
{
    /// <summary>Null when resolution failed or a clarification is being asked for.</summary>
    public ScreenCriteria? Criteria { get; init; }

    public required IReadOnlyList<ResolvedConcept> Concepts { get; init; }
    public required IReadOnlyList<ResolvedFilter> ExplicitFilters { get; init; }
    public required IReadOnlyList<CriteriaValidationError> Errors { get; init; }

    /// <summary>A question to put back to the user instead of a screen (§5.6).</summary>
    public string? Clarification { get; init; }

    public bool IsResolved => Criteria is not null;
    public bool NeedsClarification => Clarification is not null;

    public static IntentResolution Ask(string question) => new()
    {
        Concepts = [], ExplicitFilters = [], Errors = [], Clarification = question,
    };

    public static IntentResolution Failed(
        IReadOnlyList<CriteriaValidationError> errors,
        IReadOnlyList<ResolvedConcept>? concepts = null,
        IReadOnlyList<ResolvedFilter>? filters = null) => new()
    {
        Concepts = concepts ?? [], ExplicitFilters = filters ?? [], Errors = errors,
    };
}

/// <summary>
/// One concept the model named, with what the vocabulary says it means. <see cref="Explanation"/>
/// is the rendered definition the panel shows next to the concept's display name.
/// </summary>
public sealed record ResolvedConcept
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public required FilterNode Definition { get; init; }
    public required string Explanation { get; init; }

    /// <summary>
    /// Metrics in this concept's definition that the user's own number replaced. Non-empty means
    /// the panel must say so — silently dropping half of what "cheap" means would make the
    /// displayed definition a lie about the screen that actually ran.
    /// </summary>
    public required IReadOnlyList<string> OverriddenBy { get; init; }

    /// <summary>True when the user's numbers replaced every part of this concept.</summary>
    public bool FullyOverridden { get; init; }
}

/// <summary>One filter whose number came from the user, ready for the panel.</summary>
public sealed record ResolvedFilter
{
    public required string Field { get; init; }
    public required string DisplayName { get; init; }
    public required Comparison Comparison { get; init; }
    public required string Explanation { get; init; }
}
