using Microsoft.EntityFrameworkCore;
using MarketEye.Application.Screening;
using MarketEye.Domain.Entities;
using MarketEye.Domain.Screening;
using MarketEye.Domain.Screening.Vocabulary;
using MarketEye.Infrastructure.Persistence;

namespace MarketEye.Infrastructure.Vocabulary;

/// <summary>
/// Create, update and delete for the Strategy Vocabulary (PLAN.md §5.2).
///
/// Every write validates first. The compiler trusts that a stored definition names only real
/// metrics with in-range values, and this is the only place that guarantee is established.
/// </summary>
public sealed class StrategyConceptStore(
    MarketEyeDbContext db,
    StrategyConceptValidator validator)
{
    public sealed record WriteResult(
        StrategyConceptEntity? Concept, CriteriaValidationResult Validation, string? Conflict = null)
    {
        public bool Succeeded => Concept is not null;
    }

    public async Task<WriteResult> CreateAsync(StrategyConceptDraft draft, CancellationToken ct)
    {
        var validation = validator.Validate(draft);
        if (!validation.IsValid) return new(null, validation);

        var now = DateTimeOffset.UtcNow;
        var entity = new StrategyConceptEntity
        {
            Name = ConceptName.Normalise(draft.Name),
            DisplayName = draft.DisplayName,
            Description = draft.Description,
            AliasesCsv = Csv(draft.Aliases),
            DefinitionJson = ScreenCriteriaJson.SerializeNode(draft.Definition),
            IsEnabled = draft.IsEnabled,

            // User-created concepts are never system rows: IsSystem only marks what the seed owns,
            // and it is what makes a row undeletable. A user must be able to delete their own.
            IsSystem = false,
            OwnerUserId = null,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.StrategyConcepts.Add(entity);
        await db.SaveChangesAsync(ct);
        return new(entity, validation);
    }

    public async Task<WriteResult> UpdateAsync(
        string name, StrategyConceptDraft draft, CancellationToken ct)
    {
        var key = ConceptName.Normalise(name);
        var entity = await db.StrategyConcepts.FirstOrDefaultAsync(c => c.Name == key, ct);
        if (entity is null) return new(null, CriteriaValidationResult.Ok(), "not-found");

        var validation = validator.Validate(draft, replacingName: key);
        if (!validation.IsValid) return new(null, validation);

        entity.Name = ConceptName.Normalise(draft.Name);
        entity.DisplayName = draft.DisplayName;
        entity.Description = draft.Description;
        entity.AliasesCsv = Csv(draft.Aliases);
        entity.DefinitionJson = ScreenCriteriaJson.SerializeNode(draft.Definition);
        entity.IsEnabled = draft.IsEnabled;

        // Bumping this changes the vocabulary version token, which invalidates every cached parse
        // (§5.5). Editing what "cheap" means must not leave yesterday's meaning in the cache.
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return new(entity, validation);
    }

    public async Task<string?> DeleteAsync(string name, CancellationToken ct)
    {
        var key = ConceptName.Normalise(name);
        var entity = await db.StrategyConcepts.FirstOrDefaultAsync(c => c.Name == key, ct);
        if (entity is null) return "not-found";

        if (entity.IsSystem)
        {
            // Seeded concepts are editable but not deletable. §5.1 fails closed on an unknown
            // concept, so deleting the vocabulary out from under a saved strategy would break it
            // with no way back. Disabling expresses the same intent and is reversible.
            return "system";
        }

        db.StrategyConcepts.Remove(entity);
        await db.SaveChangesAsync(ct);
        return null;
    }

    private static string Csv(IReadOnlyList<string> aliases) =>
        string.Join(',', aliases
            .Select(ConceptName.Normalise)
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.Ordinal));
}
