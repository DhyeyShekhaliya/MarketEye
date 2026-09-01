using Microsoft.EntityFrameworkCore;
using MarketEye.Domain.Screening;
using MarketEye.Domain.Screening.Vocabulary;
using MarketEye.Infrastructure.Persistence;

namespace MarketEye.Infrastructure.Screening;

/// <summary>
/// Loads the controlled vocabulary from the MetricConcepts table (PLAN.md §5.2).
///
/// Cached for the lifetime of the instance: the vocabulary is ~20 rows that change when a human
/// edits a definition, and re-reading it per comparison would put a query inside the validator's
/// inner loop.
/// </summary>
public sealed class DbMetricConceptVocabulary : IMetricConceptVocabulary
{
    private readonly Dictionary<string, MetricConcept> _byName;

    private DbMetricConceptVocabulary(Dictionary<string, MetricConcept> byName) => _byName = byName;

    public static async Task<DbMetricConceptVocabulary> LoadAsync(
        MarketEyeDbContext db, CancellationToken ct)
    {
        var rows = await db.MetricConcepts.AsNoTracking().ToListAsync(ct);

        // Ordinal, matching the validator. Case-insensitive lookup here would accept concepts the
        // validator rejects, and the two must agree or validation stops meaning anything.
        var map = new Dictionary<string, MetricConcept>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            map[r.Name] = new MetricConcept
            {
                Name = r.Name,
                DisplayName = r.DisplayName,
                ColumnName = r.ColumnName,
                Source = r.Source,
                AllowedOperators = ParseOperators(r.AllowedOperatorsCsv),
                MinValue = r.MinValue,
                MaxValue = r.MaxValue,
                Unit = r.Unit,
            };
        }
        return new DbMetricConceptVocabulary(map);
    }

    private static IReadOnlyList<ComparisonOperator> ParseOperators(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
           .Select(s => Enum.Parse<ComparisonOperator>(s, ignoreCase: true))
           .ToList();

    public MetricConcept? Find(string conceptName) =>
        _byName.TryGetValue(conceptName, out var c) ? c : null;

    public IReadOnlyCollection<MetricConcept> All => _byName.Values;
}
