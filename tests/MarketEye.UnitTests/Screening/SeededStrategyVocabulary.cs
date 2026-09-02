using MarketEye.Application.Screening;
using MarketEye.Domain.Screening.Vocabulary;
using MarketEye.Infrastructure.Persistence;

namespace MarketEye.UnitTests.Screening;

/// <summary>
/// The REAL seeded Strategy Vocabulary, in memory — the counterpart to
/// <see cref="SeededMetricVocabulary"/>.
///
/// Used where a test needs to assert against what actually ships rather than against a fixture:
/// the generated schema and system prompt are only meaningful if they describe the vocabulary a
/// user will really get.
///
/// It mirrors DbStrategyConceptVocabulary's lookup construction — normalised keys, aliases folded
/// in, disabled rows excluded — so the two cannot drift on what "findable" means.
/// </summary>
internal sealed class SeededStrategyVocabulary : IStrategyConceptVocabulary
{
    private readonly List<StrategyConcept> _all = [];
    private readonly Dictionary<string, StrategyConcept> _byKey = new(StringComparer.Ordinal);

    public SeededStrategyVocabulary()
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
    public string VersionToken => "seed";
}
