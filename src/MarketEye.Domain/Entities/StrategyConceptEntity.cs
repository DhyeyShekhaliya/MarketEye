namespace MarketEye.Domain.Entities;

/// <summary>
/// Persisted row of the Strategy Vocabulary (PLAN.md §5.2).
///
/// §5.2 calls the vocabulary a first-class feature, not a lookup table: it is what a user inspects
/// and edits when they disagree with what "cheap" means. Storing it makes those definitions
/// reviewable and versionable instead of buried in a prompt.
///
/// Separate from <see cref="MetricConceptEntity"/> deliberately (docs/adr/0007). MetricConcepts
/// carries physical column names that CriteriaCompiler turns into SQL identifiers, so it stays
/// system-owned. This table is user-editable and carries no column names at all — only a criteria
/// fragment naming metrics. That is what makes an edit screen safe to ship.
/// </summary>
public class StrategyConceptEntity
{
    public int Id { get; set; }

    /// <summary>
    /// Normalised name (see ConceptName) the model emits and the resolver matches, ordinally.
    /// </summary>
    public required string Name { get; set; }

    public required string DisplayName { get; set; }
    public string? Description { get; set; }

    /// <summary>Comma-separated normalised aliases. Empty string means none.</summary>
    public required string AliasesCsv { get; set; }

    /// <summary>
    /// A serialised <see cref="Screening.FilterNode"/> over metric names — never a column name,
    /// never SQL. Validated by ScreenCriteriaValidator before any write, so an unparseable or
    /// out-of-range definition cannot be stored (§5.1).
    /// </summary>
    public required string DefinitionJson { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>Seeded rows. Editable so a user can disagree with a default, but not deletable.</summary>
    public bool IsSystem { get; set; }

    /// <summary>
    /// Null until authentication exists (§14). Part of the uniqueness key from day one so
    /// per-user vocabularies later are rows, not a migration of the ownership model.
    /// </summary>
    public string? OwnerUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Drives the vocabulary version token, which invalidates the ParseCache (§5.5).</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
