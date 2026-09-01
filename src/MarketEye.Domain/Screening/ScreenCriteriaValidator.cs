using MarketEye.Domain.Screening.Vocabulary;

namespace MarketEye.Domain.Screening;

/// <summary>
/// The boundary between untrusted input and deterministic execution (PLAN.md §5.1, §6).
///
/// Everything upstream — a user, or a model reading prose — is untrusted. Everything downstream is
/// deterministic. This validator is the only thing standing between them, so it fails closed:
/// an unknown concept is an error, never a substitution or a best guess (§5.1).
///
/// It walks a tree even though v1 only compiles a flat AND, per §6.
/// </summary>
public sealed class ScreenCriteriaValidator(IMetricConceptVocabulary vocabulary)
{
    /// <summary>§6: max tree depth 4.</summary>
    public const int MaxDepth = 4;

    /// <summary>§6: max 20 comparisons.</summary>
    public const int MaxComparisons = 20;

    public const int MaxLimit = 1000;

    public CriteriaValidationResult Validate(ScreenCriteria criteria)
    {
        var errors = new List<CriteriaValidationError>();

        var depth = criteria.Root.Depth();
        if (depth > MaxDepth)
        {
            errors.Add(new(
                "root", CriteriaErrorCode.TreeTooDeep,
                $"Tree depth {depth} exceeds the maximum of {MaxDepth}."));
        }

        var comparisons = criteria.Root.Comparisons().ToList();
        if (comparisons.Count > MaxComparisons)
        {
            errors.Add(new(
                "root", CriteriaErrorCode.TooManyComparisons,
                $"{comparisons.Count} comparisons exceeds the maximum of {MaxComparisons}."));
        }

        if (comparisons.Count == 0)
        {
            // A screen with no filters returns the entire universe. That is almost never what was
            // meant, and §5.4 requires a failed parse to ask a question rather than guess.
            errors.Add(new(
                "root", CriteriaErrorCode.NoComparisons,
                "A screen must contain at least one comparison."));
        }

        Walk(criteria.Root, "root", errors);

        if (criteria.Sort is { } sort && vocabulary.Find(sort.Field) is null)
        {
            errors.Add(new(
                "sort.field", CriteriaErrorCode.UnknownConcept,
                $"'{sort.Field}' is not a known metric concept."));
        }

        if (criteria.Limit is { } limit && (limit <= 0 || limit > MaxLimit))
        {
            errors.Add(new(
                "limit", CriteriaErrorCode.InvalidLimit,
                $"Limit must be between 1 and {MaxLimit}."));
        }

        return errors.Count == 0
            ? CriteriaValidationResult.Ok()
            : CriteriaValidationResult.Failed(errors);
    }

    private void Walk(FilterNode node, string path, List<CriteriaValidationError> errors)
    {
        switch (node)
        {
            case Group g:
                if (g.Op is not GroupOperator.And)
                {
                    // Representable in the type, rejected by the validator. §6 defers OR/NOT to
                    // Phase 3+; this makes the boundary explicit instead of silently mis-compiling.
                    errors.Add(new(
                        path, CriteriaErrorCode.OperatorNotSupportedInV1,
                        $"Group operator '{g.Op}' is not supported in v1. Only AND compiles."));
                }

                if (g.Children.Count == 0)
                {
                    errors.Add(new(path, CriteriaErrorCode.EmptyGroup, "Group has no children."));
                }

                for (var i = 0; i < g.Children.Count; i++)
                {
                    Walk(g.Children[i], $"{path}.children[{i}]", errors);
                }
                break;

            case Comparison c:
                ValidateComparison(c, path, errors);
                break;
        }
    }

    private void ValidateComparison(Comparison c, string path, List<CriteriaValidationError> errors)
    {
        var concept = vocabulary.Find(c.Field);
        if (concept is null)
        {
            // §5.1: a concept not in the table is a hard failure, never a fallback. Guessing the
            // nearest match is how an unvetted threshold reaches a user's screen.
            errors.Add(new(
                $"{path}.field", CriteriaErrorCode.UnknownConcept,
                $"'{c.Field}' is not a known metric concept."));
            return;
        }

        if (!concept.AllowedOperators.Contains(c.Operator))
        {
            errors.Add(new(
                $"{path}.operator", CriteriaErrorCode.OperatorNotAllowedForField,
                $"Operator '{c.Operator}' is not allowed for '{concept.Name}'."));
        }

        if (c.Value < concept.MinValue || c.Value > concept.MaxValue)
        {
            errors.Add(new(
                $"{path}.value", CriteriaErrorCode.ValueOutOfRange,
                $"{c.Value} is outside the allowed range for '{concept.Name}' " +
                $"({concept.MinValue} to {concept.MaxValue})."));
        }
    }
}
