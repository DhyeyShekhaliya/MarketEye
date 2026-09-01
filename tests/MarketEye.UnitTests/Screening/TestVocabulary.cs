using MarketEye.Domain.Entities;
using MarketEye.Domain.Screening;
using MarketEye.Domain.Screening.Vocabulary;

namespace MarketEye.UnitTests.Screening;

/// <summary>A small fixed vocabulary so validator tests do not depend on seeded data.</summary>
internal sealed class TestVocabulary : IMetricConceptVocabulary
{
    private static readonly ComparisonOperator[] Numeric =
    [
        ComparisonOperator.LessThan, ComparisonOperator.LessThanOrEqual,
        ComparisonOperator.GreaterThan, ComparisonOperator.GreaterThanOrEqual,
    ];

    private readonly Dictionary<string, MetricConcept> _byName = new(StringComparer.Ordinal)
    {
        ["PeRatio"] = new()
        {
            Name = "PeRatio", DisplayName = "P/E ratio", ColumnName = "Pe", Source = MetricSource.FundamentalRatio,
            AllowedOperators = Numeric, MinValue = 0m, MaxValue = 1000m,
        },
        ["Rsi14"] = new()
        {
            Name = "Rsi14", DisplayName = "RSI (14)", ColumnName = "Rsi14", Source = MetricSource.Indicator,
            AllowedOperators = Numeric, MinValue = 0m, MaxValue = 100m,
        },
        ["NetIncome"] = new()
        {
            Name = "NetIncome", DisplayName = "Net income", ColumnName = "NetIncome", Source = MetricSource.FundamentalRatio,
            AllowedOperators = Numeric, MinValue = -1_000_000_000m, MaxValue = 1_000_000_000m,
        },
    };

    public MetricConcept? Find(string conceptName) =>
        _byName.TryGetValue(conceptName, out var c) ? c : null;

    public IReadOnlyCollection<MetricConcept> All => _byName.Values;
}
