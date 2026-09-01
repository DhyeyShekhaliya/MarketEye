using System.Text.Json.Serialization;

namespace MarketEye.Domain.Screening;

/// <summary>
/// A node in the criteria tree (PLAN.md §6).
///
/// The type is a tree from day one even though the v1 compiler only handles a single flat AND
/// group. §6 is explicit that this is deliberate: adding OR/NOT in Phase 3+ then becomes additive
/// rather than a rewrite of the type, the JSON schema, the validator and the UI at once.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Group), "group")]
[JsonDerivedType(typeof(Comparison), "comparison")]
public abstract record FilterNode
{
    /// <summary>Depth of the subtree rooted here, counting this node as 1.</summary>
    public abstract int Depth();

    /// <summary>Every comparison in this subtree, in tree order.</summary>
    public abstract IEnumerable<Comparison> Comparisons();
}

/// <summary>A boolean combination of child nodes.</summary>
public sealed record Group : FilterNode
{
    public required GroupOperator Op { get; init; }
    public required IReadOnlyList<FilterNode> Children { get; init; }

    public override int Depth() =>
        1 + (Children.Count == 0 ? 0 : Children.Max(c => c.Depth()));

    public override IEnumerable<Comparison> Comparisons() =>
        Children.SelectMany(c => c.Comparisons());
}

/// <summary>
/// §6 models all three operators but v1 compiles only <see cref="And"/>. The validator rejects the
/// other two rather than the type omitting them — a rejected-but-representable operator is a clear
/// "not yet", where an absent one is a rewrite waiting to happen.
/// </summary>
public enum GroupOperator
{
    And = 0,
    Or = 1,
    Not = 2,
}

/// <summary>A single field/operator/value test — the leaf of the tree.</summary>
public sealed record Comparison : FilterNode
{
    /// <summary>
    /// A concept name from the MetricConcepts vocabulary (§5.2), never a raw column name.
    /// Resolution to a column happens in the compiler, after validation.
    /// </summary>
    public required string Field { get; init; }

    public required ComparisonOperator Operator { get; init; }

    /// <summary>
    /// The threshold. §5.1: the AI may only populate this when the user stated the number
    /// themselves. Concept-derived thresholds come from MetricConcepts, not from the model.
    /// </summary>
    public required decimal Value { get; init; }

    public override int Depth() => 1;

    public override IEnumerable<Comparison> Comparisons() { yield return this; }
}

public enum ComparisonOperator
{
    LessThan = 0,
    LessThanOrEqual = 1,
    GreaterThan = 2,
    GreaterThanOrEqual = 3,
    Equal = 4,
    NotEqual = 5,
}
