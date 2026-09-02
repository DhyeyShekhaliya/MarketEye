using MarketEye.Domain.Screening;
using MarketEye.Domain.Screening.Vocabulary;
using MarketEye.Infrastructure.Persistence;

namespace MarketEye.UnitTests.Screening;

/// <summary>
/// The REAL seeded metric whitelist, in memory.
///
/// Distinct from <see cref="TestVocabulary"/> on purpose. TestVocabulary is a fixed three-concept
/// set so validator tests do not depend on seeded data. This one is the opposite: it exists so
/// tests of the *seed* check against what actually ships, catching a strategy concept that names
/// a metric nobody seeded — the failure that would otherwise appear as a broken screen in
/// production rather than a red test.
///
/// It reuses the same CSV parsing shape as DbMetricConceptVocabulary so the two cannot drift on
/// which operators a metric allows.
/// </summary>
internal sealed class SeededMetricVocabulary : IMetricConceptVocabulary
{
    public static readonly SeededMetricVocabulary Instance = new();

    private readonly Dictionary<string, MetricConcept> _byName;

    private SeededMetricVocabulary()
    {
        _byName = MetricConceptSeed.SeedRows().ToDictionary(
            r => r.Name,
            r => new MetricConcept
            {
                Name = r.Name,
                DisplayName = r.DisplayName,
                ColumnName = r.ColumnName,
                Source = r.Source,
                AllowedOperators = r.AllowedOperatorsCsv
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(Enum.Parse<ComparisonOperator>)
                    .ToList(),
                MinValue = r.MinValue,
                MaxValue = r.MaxValue,
                Unit = r.Unit,
            },
            StringComparer.Ordinal);
    }

    public MetricConcept? Find(string conceptName) =>
        _byName.TryGetValue(conceptName, out var c) ? c : null;

    public IReadOnlyCollection<MetricConcept> All => _byName.Values;
}
