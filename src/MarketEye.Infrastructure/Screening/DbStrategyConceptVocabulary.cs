using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MarketEye.Application.Screening;
using MarketEye.Domain.Entities;
using MarketEye.Domain.Screening;
using MarketEye.Domain.Screening.Vocabulary;
using MarketEye.Infrastructure.Persistence;

namespace MarketEye.Infrastructure.Screening;

/// <summary>
/// Loads the Strategy Vocabulary from the StrategyConcepts table (PLAN.md §5.2).
///
/// Cached for the lifetime of the instance, like <see cref="DbMetricConceptVocabulary"/>: it is a
/// few dozen rows that change when a human edits a definition, and re-reading it per concept would
/// put a query inside the resolver's inner loop.
///
/// Definitions are deserialised here rather than in the resolver so that a corrupt row fails at
/// load — loudly, once, naming the concept — instead of midway through resolving a user's screen.
/// </summary>
public sealed class DbStrategyConceptVocabulary : IStrategyConceptVocabulary
{
    private readonly Dictionary<string, StrategyConcept> _byKey;
    private readonly List<StrategyConcept> _all;

    private DbStrategyConceptVocabulary(
        Dictionary<string, StrategyConcept> byKey, List<StrategyConcept> all, string versionToken)
    {
        _byKey = byKey;
        _all = all;
        VersionToken = versionToken;
    }

    public static async Task<DbStrategyConceptVocabulary> LoadAsync(
        MarketEyeDbContext db, CancellationToken ct)
    {
        var rows = await db.StrategyConcepts.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);

        var all = new List<StrategyConcept>(rows.Count);
        // Ordinal, on the already-normalised form. Case-insensitive lookup here would accept
        // concepts the schema handed to the model never advertised, and the two must agree.
        var byKey = new Dictionary<string, StrategyConcept>(StringComparer.Ordinal);

        foreach (var r in rows)
        {
            var concept = new StrategyConcept
            {
                Name = r.Name,
                DisplayName = r.DisplayName,
                Description = r.Description,
                Aliases = ParseAliases(r.AliasesCsv),
                Definition = Parse(r),
                IsEnabled = r.IsEnabled,
                IsSystem = r.IsSystem,
                OwnerUserId = r.OwnerUserId,
            };
            all.Add(concept);

            // Disabled concepts are loaded (the management screen lists them) but deliberately
            // absent from the lookup, so Find returns null for them exactly as for an unknown
            // name. §5.1: refuse, never substitute.
            if (!concept.IsEnabled) continue;

            byKey[concept.Name] = concept;
            foreach (var alias in concept.Aliases)
            {
                // First writer wins, and a unit test asserts no collisions exist in the seed.
                // Silently remapping an alias would make which concept a user gets depend on
                // row order.
                byKey.TryAdd(alias, concept);
            }
        }

        return new DbStrategyConceptVocabulary(byKey, all, ComputeVersion(rows));
    }

    private static FilterNode Parse(StrategyConceptEntity r)
    {
        try
        {
            return ScreenCriteriaJson.DeserializeNode(r.DefinitionJson);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Strategy concept '{r.Name}' has an unreadable definition. Every write path " +
                "validates before storing (§5.2), so this row was written by something that " +
                "bypassed it.", ex);
        }
    }

    private static IReadOnlyList<string> ParseAliases(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
           .Select(ConceptName.Normalise)
           .Where(a => a.Length > 0)
           .ToList();

    /// <summary>
    /// A hash over every row's identity and definition. Any edit, addition, removal or
    /// enable/disable changes it, which is what makes the ParseCache key self-invalidating (§5.5).
    /// Hashing the content rather than trusting max(UpdatedAt) means a restored backup or a direct
    /// SQL edit still invalidates.
    /// </summary>
    private static string ComputeVersion(List<StrategyConceptEntity> rows)
    {
        var sb = new StringBuilder();
        foreach (var r in rows)
        {
            sb.Append(r.Name).Append('|')
              .Append(r.IsEnabled ? '1' : '0').Append('|')
              .Append(r.DefinitionJson).Append('\n');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash, 0, 8).ToLower(CultureInfo.InvariantCulture);
    }

    public StrategyConcept? Find(string nameOrAlias) =>
        _byKey.TryGetValue(ConceptName.Normalise(nameOrAlias), out var c) ? c : null;

    public IReadOnlyCollection<StrategyConcept> Enabled =>
        _all.Where(c => c.IsEnabled).ToList();

    public IReadOnlyCollection<StrategyConcept> All => _all;

    public string VersionToken { get; }
}
