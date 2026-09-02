namespace MarketEye.AiEvals;

/// <summary>
/// One prompt -> expected-intent pair (PLAN.md §5.6).
///
/// <see cref="ExpectedConcepts"/> and <see cref="ExpectedFilters"/> are scored by exact set
/// equality, never partial credit -- a case with one extra hallucinated concept fails that case
/// outright, which is what keeps the ≥85% gate meaningful rather than gameable.
/// </summary>
public sealed record EvalCase
{
    /// <summary>Human-readable id for failure output, e.g. "concept_only_01". Not the recording key.</summary>
    public required string Id { get; init; }

    public required string Prompt { get; init; }

    public string[] ExpectedConcepts { get; init; } = [];
    public ExpectedFilter[] ExpectedFilters { get; init; } = [];

    /// <summary>
    /// True means the model is expected to ask a clarifying question (§5.6) -- ExpectedConcepts
    /// and ExpectedFilters are ignored for such a case, and a real answer instead of a question
    /// counts as a miss on both axes.
    /// </summary>
    public bool ExpectClarification { get; init; }

    /// <summary>Optional context for a maintainer -- why this case is here, or what to watch for.</summary>
    public string? Notes { get; init; }
}

public sealed record ExpectedFilter(string Field, string Operator, decimal Value);
