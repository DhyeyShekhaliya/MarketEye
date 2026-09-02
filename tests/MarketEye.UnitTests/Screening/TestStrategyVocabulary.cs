using MarketEye.Domain.Screening;
using MarketEye.Domain.Screening.Vocabulary;

namespace MarketEye.UnitTests.Screening;

/// <summary>
/// A small fixed Strategy Vocabulary so resolver tests do not depend on seeded data — the same
/// reasoning as <see cref="TestVocabulary"/>. Includes one disabled concept, because "disabled
/// behaves exactly like absent" is a rule worth testing rather than assuming.
/// </summary>
internal sealed class TestStrategyVocabulary : IStrategyConceptVocabulary
{
    private readonly List<StrategyConcept> _all;
    private readonly Dictionary<string, StrategyConcept> _byKey = new(StringComparer.Ordinal);

    public TestStrategyVocabulary()
    {
        _all =
        [
            Make("cheap", "Cheap", ["value", "undervalued"], true,
                Cmp("PeRatio", ComparisonOperator.LessThan, 25m),
                Cmp("PbRatio", ComparisonOperator.LessThan, 3m)),

            Make("profitable", "Profitable", ["makes money"], true,
                Cmp("ReturnOnEquity", ComparisonOperator.GreaterThan, 0m)),

            // MarketCap's real range (SeededMetricVocabulary, matching MetricConceptSeed) is
            // 0..100,000,000: the field is denominated in crore, not raw rupees. 5000 here means
            // "5,000 crore", consistent with what production's small_cap actually means.
            Make("small_cap", "Small cap", ["smallcap"], true,
                Cmp("MarketCap", ComparisonOperator.LessThan, 5_000m)),

            Make("not_overbought", "Not overbought", [], true,
                Cmp("Rsi14", ComparisonOperator.LessThan, 70m)),

            Make("retired_idea", "Retired idea", ["old favourite"], false,
                Cmp("PeRatio", ComparisonOperator.LessThan, 5m)),
        ];

        foreach (var c in _all.Where(c => c.IsEnabled))
        {
            _byKey[c.Name] = c;
            foreach (var alias in c.Aliases) _byKey.TryAdd(alias, c);
        }
    }

    private static Comparison Cmp(string field, ComparisonOperator op, decimal value) =>
        new() { Field = field, Operator = op, Value = value };

    private static StrategyConcept Make(
        string name, string display, string[] aliases, bool enabled, params Comparison[] children) =>
        new()
        {
            Name = name,
            DisplayName = display,
            Aliases = aliases,
            IsEnabled = enabled,
            IsSystem = true,
            Definition = new Group { Op = GroupOperator.And, Children = children },
        };

    public StrategyConcept? Find(string nameOrAlias) =>
        _byKey.TryGetValue(ConceptName.Normalise(nameOrAlias), out var c) ? c : null;

    public IReadOnlyCollection<StrategyConcept> Enabled => _all.Where(c => c.IsEnabled).ToList();
    public IReadOnlyCollection<StrategyConcept> All => _all;
    public string VersionToken => "test";
}
