namespace MarketEye.Domain.Screening;

/// <summary>One reason a ScreenCriteria was rejected. Paths locate the offending node.</summary>
public sealed record CriteriaValidationError(string Path, CriteriaErrorCode Code, string Message);

public enum CriteriaErrorCode
{
    UnknownConcept = 0,
    OperatorNotAllowedForField = 1,
    ValueOutOfRange = 2,
    TreeTooDeep = 3,
    TooManyComparisons = 4,
    EmptyGroup = 5,
    /// <summary>OR / NOT are representable but not compilable in v1 (§6).</summary>
    OperatorNotSupportedInV1 = 6,
    NoComparisons = 7,
    InvalidLimit = 8,

    /// <summary>
    /// The model named a strategy concept that is not in the vocabulary, or is disabled. §5.1
    /// makes this a hard failure: guessing the nearest match is how an unvetted threshold reaches
    /// a user's screen. Distinct from <see cref="UnknownConcept"/>, which is about metric names,
    /// so the interpretation panel can say which vocabulary was missing the word.
    /// </summary>
    UnknownStrategyConcept = 9,

    /// <summary>
    /// A parse that named no concepts and supplied no numbers. §5.6 requires this to become a
    /// clarifying question rather than a screen over the entire universe.
    /// </summary>
    EmptyIntent = 10,

    /// <summary>A concept name or alias that is empty, too long, or normalises to nothing.</summary>
    InvalidConceptName = 11,

    /// <summary>A name or alias another concept already answers to.</summary>
    ConceptNameInUse = 12,

    /// <summary>
    /// A definition that is not a flat AND of comparisons. Representable, but not something the
    /// v1 compiler or the resolver's override rule can handle (§6).
    /// </summary>
    DefinitionShapeNotSupportedInV1 = 13,
}

public sealed record CriteriaValidationResult
{
    public required IReadOnlyList<CriteriaValidationError> Errors { get; init; }
    public bool IsValid => Errors.Count == 0;

    public static CriteriaValidationResult Ok() => new() { Errors = [] };
    public static CriteriaValidationResult Failed(IReadOnlyList<CriteriaValidationError> e) =>
        new() { Errors = e };
}
