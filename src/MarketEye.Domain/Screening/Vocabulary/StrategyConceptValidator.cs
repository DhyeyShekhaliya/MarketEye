namespace MarketEye.Domain.Screening.Vocabulary;

/// <summary>
/// Guards the vocabulary write path (PLAN.md §5.2, §5.1).
///
/// This is the security boundary the two-table split exists to create. StrategyConcepts is the
/// only user-editable input that ends up shaping SQL, and it does so indirectly: a definition
/// names metrics, and the compiler resolves those to columns from its own sealed table. That
/// indirection only holds if every stored definition is one the compiler can already handle — so
/// a definition is validated BEFORE it is written, never on the way out.
///
/// Validating on read would be too late in the worst way: the concept would already be in the
/// table, and every screen naming it would fail at run time for a user who did nothing wrong.
/// </summary>
public sealed class StrategyConceptValidator(
    IStrategyConceptVocabulary strategies,
    ScreenCriteriaValidator criteriaValidator)
{
    public const int MaxNameLength = 64;
    public const int MaxDisplayNameLength = 128;
    public const int MaxDescriptionLength = 512;

    /// <summary>Fits AliasesCsv's nvarchar(512) with room for separators.</summary>
    public const int MaxAliases = 12;

    /// <summary>
    /// Validates a draft. <paramref name="replacingName"/> is the normalised name of the concept
    /// being edited, so an update does not collide with itself.
    /// </summary>
    public CriteriaValidationResult Validate(StrategyConceptDraft draft, string? replacingName = null)
    {
        var errors = new List<CriteriaValidationError>();

        var name = ConceptName.Normalise(draft.Name);
        if (name.Length == 0)
        {
            errors.Add(new(
                "name", CriteriaErrorCode.InvalidConceptName,
                "A concept name must contain at least one letter or digit."));
        }
        else if (name.Length > MaxNameLength)
        {
            errors.Add(new(
                "name", CriteriaErrorCode.InvalidConceptName,
                $"Concept name is longer than {MaxNameLength} characters once normalised."));
        }

        if (string.IsNullOrWhiteSpace(draft.DisplayName))
        {
            errors.Add(new(
                "displayName", CriteriaErrorCode.InvalidConceptName,
                "A concept needs a display name -- it is what the interpretation panel shows."));
        }
        else if (draft.DisplayName.Length > MaxDisplayNameLength)
        {
            errors.Add(new(
                "displayName", CriteriaErrorCode.InvalidConceptName,
                $"Display name is longer than {MaxDisplayNameLength} characters."));
        }

        if (draft.Description is { Length: > MaxDescriptionLength })
        {
            errors.Add(new(
                "description", CriteriaErrorCode.InvalidConceptName,
                $"Description is longer than {MaxDescriptionLength} characters."));
        }

        ValidateAliases(draft, name, errors);
        ValidateUniqueness(draft, name, replacingName, errors);
        ValidateDefinition(draft, errors);

        return errors.Count == 0
            ? CriteriaValidationResult.Ok()
            : CriteriaValidationResult.Failed(errors);
    }

    private static void ValidateAliases(
        StrategyConceptDraft draft, string name, List<CriteriaValidationError> errors)
    {
        if (draft.Aliases.Count > MaxAliases)
        {
            errors.Add(new(
                "aliases", CriteriaErrorCode.InvalidConceptName,
                $"A concept may carry at most {MaxAliases} aliases."));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < draft.Aliases.Count; i++)
        {
            var alias = ConceptName.Normalise(draft.Aliases[i]);

            if (alias.Length == 0)
            {
                errors.Add(new(
                    $"aliases[{i}]", CriteriaErrorCode.InvalidConceptName,
                    "An alias must contain at least one letter or digit."));
                continue;
            }

            if (alias == name)
            {
                // Harmless but always a mistake, and it silently eats one of the alias slots.
                errors.Add(new(
                    $"aliases[{i}]", CriteriaErrorCode.ConceptNameInUse,
                    $"'{alias}' is the concept's own name."));
                continue;
            }

            if (!seen.Add(alias))
            {
                errors.Add(new(
                    $"aliases[{i}]", CriteriaErrorCode.ConceptNameInUse,
                    $"'{alias}' is listed twice."));
            }
        }
    }

    private void ValidateUniqueness(
        StrategyConceptDraft draft, string name, string? replacingName,
        List<CriteriaValidationError> errors)
    {
        // The loader takes the first writer for a duplicated key, so a collision would make which
        // concept a user gets depend on row order -- a bug that appears only after a restart.
        var self = replacingName is null ? null : ConceptName.Normalise(replacingName);

        Check(name, "name");
        for (var i = 0; i < draft.Aliases.Count; i++)
        {
            Check(ConceptName.Normalise(draft.Aliases[i]), $"aliases[{i}]");
        }

        void Check(string key, string path)
        {
            if (key.Length == 0) return;

            var existing = strategies.Find(key);
            if (existing is null || existing.Name == self) return;

            errors.Add(new(
                path, CriteriaErrorCode.ConceptNameInUse,
                $"'{key}' already resolves to '{existing.DisplayName}'."));
        }
    }

    private void ValidateDefinition(
        StrategyConceptDraft draft, List<CriteriaValidationError> errors)
    {
        // §6: v1 compiles a single flat AND, and the resolver's override rule only reaches the top
        // level of one. Storing a nested definition would validate here and then behave wrongly
        // the first time a user supplied their own number for a metric buried inside it.
        if (draft.Definition is not Group { Op: GroupOperator.And } group)
        {
            errors.Add(new(
                "definition", CriteriaErrorCode.DefinitionShapeNotSupportedInV1,
                "A definition must be a single AND group in v1."));
            return;
        }

        if (group.Children.Any(c => c is not Comparison))
        {
            errors.Add(new(
                "definition", CriteriaErrorCode.DefinitionShapeNotSupportedInV1,
                "A definition must contain only comparisons in v1 -- no nested groups."));
            return;
        }

        // Validated as a screen in its own right, which is exactly how the resolver will splice
        // it: unknown metric, disallowed operator and out-of-range value all surface here.
        var asScreen = new ScreenCriteria
        {
            Universe = UniverseConstraint.All,
            Root = draft.Definition,
        };

        errors.AddRange(criteriaValidator.Validate(asScreen).Errors
            .Select(e => e with { Path = $"definition.{e.Path}" }));
    }
}
