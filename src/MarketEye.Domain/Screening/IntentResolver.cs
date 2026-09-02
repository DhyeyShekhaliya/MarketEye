using MarketEye.Domain.Screening.Vocabulary;

namespace MarketEye.Domain.Screening;

/// <summary>
/// Turns what the model said into criteria the compiler can run (PLAN.md §5.1, §5.4).
///
/// This is the load-bearing half of the AI feature and it contains no AI. Everything upstream is
/// untrusted prose; everything downstream is deterministic. The resolver is where a concept name
/// becomes a threshold a human chose, and it fails closed at every step: an unknown concept is an
/// error, never the nearest match; a disabled concept is an error, never a silent fallback to its
/// old meaning.
///
/// The rule it enforces (§5.1): the model may infer concepts, it may not invent thresholds.
/// </summary>
public sealed class IntentResolver(
    IStrategyConceptVocabulary strategies,
    IMetricConceptVocabulary metrics,
    ScreenCriteriaValidator validator)
{
    private readonly CriteriaExplainer _explainer = new(metrics);

    public IntentResolution Resolve(ParsedIntent intent)
    {
        // §5.6: a low-confidence parse asks a question. It does this BEFORE resolution, so a
        // half-understood prompt cannot produce a screen just because some of its words happened
        // to be in the vocabulary.
        if (!string.IsNullOrWhiteSpace(intent.Clarification))
        {
            return IntentResolution.Ask(intent.Clarification);
        }

        var errors = new List<CriteriaValidationError>();

        var filters = ResolveFilters(intent, errors);
        var overridden = filters
            .Select(f => f.Field)
            .ToHashSet(StringComparer.Ordinal);

        var concepts = ResolveConcepts(intent, overridden, errors);

        if (intent.Concepts.Count == 0 && intent.ExplicitFilters.Count == 0)
        {
            // A screen with nothing in it returns the entire universe. §5.6 requires a question
            // rather than a guess, and the empty parse is the most common way to get here.
            errors.Add(new(
                "intent", CriteriaErrorCode.EmptyIntent,
                "The request named no concepts and supplied no numbers."));
        }

        if (errors.Count > 0)
        {
            return IntentResolution.Failed(errors, concepts, filters);
        }

        var children = new List<FilterNode>();
        foreach (var concept in concepts)
        {
            children.AddRange(SurvivingComparisons(concept.Definition, overridden));
        }
        children.AddRange(filters.Select(f => f.Comparison));

        var criteria = new ScreenCriteria
        {
            Universe = intent.Universe ?? UniverseConstraint.All,
            Root = new Group { Op = GroupOperator.And, Children = children },
            Sort = intent.Sort,
            Limit = intent.Limit,
        };

        // The validator runs last and unconditionally. Resolution built this tree from vocabulary
        // rows, so it should already be valid -- but "should already be valid" is exactly the
        // assumption that turns a boundary into a hole (§5.1).
        var validation = validator.Validate(criteria);
        if (!validation.IsValid)
        {
            return IntentResolution.Failed(validation.Errors, concepts, filters);
        }

        return new IntentResolution
        {
            Criteria = criteria,
            Concepts = concepts,
            ExplicitFilters = filters,
            Errors = [],
        };
    }

    private List<ResolvedFilter> ResolveFilters(
        ParsedIntent intent, List<CriteriaValidationError> errors)
    {
        var resolved = new List<ResolvedFilter>();

        for (var i = 0; i < intent.ExplicitFilters.Count; i++)
        {
            var filter = intent.ExplicitFilters[i];

            // An explicit filter must name a METRIC. Pointing one at a strategy concept
            // ("cheap < 12") is meaningless -- a concept is a set of comparisons, not a column --
            // and accepting it would let the model attach its own number to a qualitative word,
            // which is precisely what §5.1 forbids.
            var metric = metrics.Find(filter.Field);
            if (metric is null)
            {
                errors.Add(new(
                    $"explicitFilters[{i}].field", CriteriaErrorCode.UnknownConcept,
                    $"'{filter.Field}' is not a known metric. Explicit numbers may only be " +
                    "attached to metrics, never to a strategy concept."));
                continue;
            }

            var comparison = filter.ToComparison();
            resolved.Add(new ResolvedFilter
            {
                Field = metric.Name,
                DisplayName = metric.DisplayName,
                Comparison = comparison,
                Explanation = _explainer.Explain(comparison),
            });
        }

        return resolved;
    }

    private List<ResolvedConcept> ResolveConcepts(
        ParsedIntent intent, HashSet<string> overridden, List<CriteriaValidationError> errors)
    {
        var resolved = new List<ResolvedConcept>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < intent.Concepts.Count; i++)
        {
            var named = intent.Concepts[i];
            var concept = strategies.Find(named);

            if (concept is null)
            {
                // §5.1: hard failure, never a fallback. Find returns null for a disabled concept
                // too -- "we turned that off" and "that never existed" have the same right answer.
                errors.Add(new(
                    $"concepts[{i}]", CriteriaErrorCode.UnknownStrategyConcept,
                    $"'{named}' is not a known strategy concept."));
                continue;
            }

            // "cheap value stocks" can map both words to the same concept. Emitting it twice
            // would duplicate every comparison and eat the §6 budget of 20 for no gain.
            if (!seen.Add(concept.Name)) continue;

            var contributed = SurvivingComparisons(concept.Definition, overridden).ToList();
            var overriddenHere = concept.Definition.Comparisons()
                .Select(c => c.Field)
                .Where(overridden.Contains)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            resolved.Add(new ResolvedConcept
            {
                Name = concept.Name,
                DisplayName = concept.DisplayName,
                Description = concept.Description,
                Definition = concept.Definition,
                Explanation = _explainer.Explain(concept.Definition),
                OverriddenBy = overriddenHere,
                FullyOverridden = overriddenHere.Count > 0 && contributed.Count == 0,
            });
        }

        return resolved;
    }

    /// <summary>
    /// A concept's comparisons, minus any metric the user gave their own number for.
    ///
    /// The override rule (§5.1, §5.3): "cheap with P/E below 12" must screen at P/E &lt; 12, not at
    /// "P/E &lt; 25 AND P/E &lt; 12". Both are numerically equivalent here, but only because the user
    /// happened to tighten it; "cheap with P/E below 40" would silently keep the 25 and return
    /// nothing the user asked for. Replacing is the rule that behaves the same in both directions,
    /// and the panel reports it so the substitution is never invisible.
    ///
    /// Overrides reach only the top level of an AND group. v1 definitions are flat -- a unit test
    /// enforces it -- so there is no nested case to get wrong yet. When OR/NOT arrive in Phase 3
    /// this needs revisiting, because dropping a comparison out of an OR changes its meaning.
    /// </summary>
    private static IEnumerable<FilterNode> SurvivingComparisons(
        FilterNode definition, HashSet<string> overridden)
    {
        if (definition is Group { Op: GroupOperator.And } group)
        {
            foreach (var child in group.Children)
            {
                if (child is Comparison c && overridden.Contains(c.Field)) continue;
                yield return child;
            }
            yield break;
        }

        if (definition is Comparison single && overridden.Contains(single.Field)) yield break;
        yield return definition;
    }
}
