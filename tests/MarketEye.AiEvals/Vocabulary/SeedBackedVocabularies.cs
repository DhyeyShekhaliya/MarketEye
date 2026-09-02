using MarketEye.Application.Screening;
using MarketEye.Domain.Screening;
using MarketEye.Domain.Screening.Vocabulary;
using MarketEye.Infrastructure.Persistence;

namespace MarketEye.AiEvals.Vocabulary;

/// <summary>
/// The REAL seeded vocabulary, built from the same generators production seeds from
/// (<see cref="MetricConceptSeed"/>, <see cref="StrategyConceptSeed"/>), without a database.
///
/// Both are pure static generators, so this suite needs no SQL Server -- what a case expects the
/// model to say is only meaningful if it is checked against the exact vocabulary a live deployment
/// actually seeds, not a hand-picked eval fixture that could quietly drift from it.
/// </summary>
internal sealed class SeedBackedMetricVocabulary : IMetricConceptVocabulary
{
    public static readonly SeedBackedMetricVocabulary Instance = new();

    private readonly Dictionary<string, MetricConcept> _byName;

    private SeedBackedMetricVocabulary()
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

internal sealed class SeedBackedStrategyVocabulary : IStrategyConceptVocabulary
{
    public static readonly SeedBackedStrategyVocabulary Instance = new();

    private readonly List<StrategyConcept> _all = [];
    private readonly Dictionary<string, StrategyConcept> _byKey = new(StringComparer.Ordinal);

    private SeedBackedStrategyVocabulary()
    {
        foreach (var row in StrategyConceptSeed.SeedRows())
        {
            var concept = new StrategyConcept
            {
                Name = row.Name,
                DisplayName = row.DisplayName,
                Description = row.Description,
                Aliases = row.AliasesCsv
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                Definition = ScreenCriteriaJson.DeserializeNode(row.DefinitionJson),
                IsEnabled = row.IsEnabled,
                IsSystem = row.IsSystem,
                OwnerUserId = row.OwnerUserId,
            };

            _all.Add(concept);
            if (!concept.IsEnabled) continue;

            _byKey[concept.Name] = concept;
            foreach (var alias in concept.Aliases) _byKey.TryAdd(alias, concept);
        }
    }

    public StrategyConcept? Find(string nameOrAlias) =>
        _byKey.TryGetValue(ConceptName.Normalise(nameOrAlias), out var c) ? c : null;

    public IReadOnlyCollection<StrategyConcept> Enabled => _all.Where(c => c.IsEnabled).ToList();
    public IReadOnlyCollection<StrategyConcept> All => _all;
    public string VersionToken => "eval-suite";
}
