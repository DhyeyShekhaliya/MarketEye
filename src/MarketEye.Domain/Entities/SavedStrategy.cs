namespace MarketEye.Domain.Entities;

/// <summary>
/// A user's named, reproducible screen (PLAN.md §10: "core workflow, not polish").
///
/// Stores resolved criteria, not the prompt that produced them. A saved strategy therefore
/// reproduces exactly even if the model or the vocabulary later changes -- <see cref="OriginalPrompt"/>
/// is kept only for provenance display, and is never re-parsed.
/// </summary>
public class SavedStrategy
{
    public int Id { get; set; }

    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>The natural-language prompt that produced this, if any. Provenance only.</summary>
    public string? OriginalPrompt { get; set; }

    /// <summary>Serialised ScreenCriteria (ScreenCriteriaJson), the thing that actually re-runs.</summary>
    public required string CriteriaJson { get; set; }

    /// <summary>
    /// Null until authentication exists (§14). Part of the uniqueness key from day one, same as
    /// StrategyConceptEntity, so per-user strategies later are rows, not a migration.
    /// </summary>
    public string? OwnerUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
}
