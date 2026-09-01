using MarketEye.Domain.Screening;

namespace MarketEye.Domain.Entities;

/// <summary>
/// Persisted row of the controlled vocabulary (PLAN.md §5.2).
///
/// §5.2 calls the vocabulary a first-class feature, not a lookup table: it is what a user inspects
/// and edits when they disagree with what "cheap" means. Storing it makes those definitions
/// reviewable and versionable instead of buried in a prompt.
/// </summary>
public class MetricConceptEntity
{
    public int Id { get; set; }

    /// <summary>The name the AI emits and the validator matches, ordinally.</summary>
    public required string Name { get; set; }

    public required string DisplayName { get; set; }
    public string? Description { get; set; }

    /// <summary>Physical column. Never exposed to the model (§5.1).</summary>
    public required string ColumnName { get; set; }

    /// <summary>Which table the column lives on, so the compiler knows what to join.</summary>
    public MetricSource Source { get; set; }

    /// <summary>Comma-separated <see cref="ComparisonOperator"/> names.</summary>
    public required string AllowedOperatorsCsv { get; set; }

    public decimal MinValue { get; set; }
    public decimal MaxValue { get; set; }
    public string? Unit { get; set; }

    /// <summary>
    /// Default threshold for a qualitative term like "cheap". Nullable because most concepts are
    /// plain metrics with no implied threshold. §5.1: this number comes from here, never the model.
    /// </summary>
    public decimal? DefaultThreshold { get; set; }

    public ComparisonOperator? DefaultOperator { get; set; }
}

public enum MetricSource
{
    Indicator = 0,
    FundamentalRatio = 1,
    PriceBar = 2,
    Security = 3,
}
