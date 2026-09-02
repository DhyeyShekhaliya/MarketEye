using MarketEye.Domain.Screening;

namespace MarketEye.Domain.Entities;

/// <summary>
/// Persisted row of the controlled vocabulary (PLAN.md §5.2).
///
/// This is the compiler's whitelist: <see cref="ColumnName"/> is what CriteriaCompiler turns into
/// a SQL identifier, which is exactly why the table is system-owned and has no edit screen.
///
/// The qualitative half of the vocabulary — what "cheap" means — lives in
/// <see cref="StrategyConceptEntity"/>, which is user-editable and carries no column names.
/// Keeping a threshold a user can change apart from a string that becomes SQL is the whole point
/// of the split; see docs/adr/0007.
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
}

public enum MetricSource
{
    Indicator = 0,
    FundamentalRatio = 1,
    PriceBar = 2,
    Security = 3,
}
