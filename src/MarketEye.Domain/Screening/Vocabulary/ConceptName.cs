namespace MarketEye.Domain.Screening.Vocabulary;

/// <summary>
/// The single normalisation rule for strategy-concept names and aliases (PLAN.md §5.1).
///
/// Every lookup in the system — the seed, the vocabulary loader, the resolver, the JSON schema
/// handed to the model — must agree on what "Small Cap", "small-cap" and "small_cap" are, or a
/// concept the schema advertises becomes a concept the resolver rejects. Ordinal comparison on a
/// normalised form gives that: tolerant at the edge, exact underneath.
///
/// Deliberately NOT applied to <see cref="MetricConcept.Name"/>. Metric names are matched
/// ordinally and exactly (see DbMetricConceptVocabulary) because they are compiler input, not
/// prose the model reached for.
/// </summary>
public static class ConceptName
{
    /// <summary>
    /// Lower-cases and collapses every run of non-alphanumeric characters to a single underscore.
    /// "Small Cap" and "small-cap" both become "small_cap".
    /// </summary>
    public static string Normalise(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var chars = new char[raw.Length];
        var length = 0;
        var pendingSeparator = false;

        foreach (var c in raw)
        {
            if (char.IsLetterOrDigit(c))
            {
                // Only emit the separator once something follows it, so leading and trailing
                // punctuation cannot produce "_cheap" or "cheap_".
                if (pendingSeparator && length > 0) chars[length++] = '_';
                pendingSeparator = false;
                chars[length++] = char.ToLowerInvariant(c);
            }
            else
            {
                pendingSeparator = true;
            }
        }

        return new string(chars, 0, length);
    }
}
