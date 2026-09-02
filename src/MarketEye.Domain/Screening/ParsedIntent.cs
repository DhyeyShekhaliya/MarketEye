namespace MarketEye.Domain.Screening;

/// <summary>
/// What the model is allowed to say (PLAN.md §5.1).
///
/// This shape IS the rule. The model returns concept *names* and, separately, numeric filters
/// only where the user supplied the number themselves:
///
///     "cheap profitable small caps that aren't overbought"
///         Concepts = [cheap, profitable, small_cap, not_overbought], ExplicitFilters = []
///
///     "profitable small caps with P/E below 12"
///         Concepts = [profitable, small_cap], ExplicitFilters = [PeRatio lt 12]
///
/// There is deliberately nowhere in this type to put a threshold the model invented for a word.
/// "Cheap" carries no number here; the number comes from the Strategy Vocabulary, which a human
/// owns and can edit (§5.2).
/// </summary>
public sealed record ParsedIntent
{
    /// <summary>Strategy concept names or aliases. Resolved against the vocabulary, never guessed.</summary>
    public required IReadOnlyList<string> Concepts { get; init; }

    /// <summary>Numeric filters the user stated explicitly. Empty is the common case.</summary>
    public required IReadOnlyList<ExplicitFilter> ExplicitFilters { get; init; }

    public UniverseConstraint? Universe { get; init; }
    public SortSpec? Sort { get; init; }
    public int? Limit { get; init; }

    /// <summary>
    /// Set when the request was too vague or ambiguous to map. §5.6: a failed or low-confidence
    /// parse must degrade to a clarifying question, never to a guessed screen. When this is
    /// present the resolver returns the question and no criteria, whatever else was parsed.
    /// </summary>
    public string? Clarification { get; init; }

    public static ParsedIntent AskInstead(string question) => new()
    {
        Concepts = [],
        ExplicitFilters = [],
        Clarification = question,
    };
}

/// <summary>
/// A numeric filter the user supplied the number for. <see cref="Field"/> names a METRIC
/// (PeRatio, Rsi14), not a strategy concept — the model may say "P/E below 12" because the user
/// did, but it may never attach a number to "cheap".
/// </summary>
public sealed record ExplicitFilter
{
    public required string Field { get; init; }
    public required ComparisonOperator Operator { get; init; }
    public required decimal Value { get; init; }

    public Comparison ToComparison() =>
        new() { Field = Field, Operator = Operator, Value = Value };
}
