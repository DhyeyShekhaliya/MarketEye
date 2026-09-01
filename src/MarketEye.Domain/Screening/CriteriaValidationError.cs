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
}

public sealed record CriteriaValidationResult
{
    public required IReadOnlyList<CriteriaValidationError> Errors { get; init; }
    public bool IsValid => Errors.Count == 0;

    public static CriteriaValidationResult Ok() => new() { Errors = [] };
    public static CriteriaValidationResult Failed(IReadOnlyList<CriteriaValidationError> e) =>
        new() { Errors = e };
}
