namespace MarketEye.Domain.Screening.Vocabulary;

/// <summary>
/// Read access to the controlled vocabulary (§5.2). Backed by the MetricConcepts table in
/// production and by a fixed set in tests.
/// </summary>
public interface IMetricConceptVocabulary
{
    /// <summary>Returns null when the concept is unknown. Callers must fail, not substitute.</summary>
    MetricConcept? Find(string conceptName);

    IReadOnlyCollection<MetricConcept> All { get; }
}
