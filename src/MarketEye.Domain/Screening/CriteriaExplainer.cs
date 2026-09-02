using System.Globalization;
using MarketEye.Domain.Screening.Vocabulary;

namespace MarketEye.Domain.Screening;

/// <summary>
/// Renders a criteria fragment as the sentence a human checks (PLAN.md §5.3).
///
/// The interpretation panel is where a user confirms that "cheap" means what they think it means,
/// so this output is load-bearing rather than cosmetic: it is the only place the threshold is
/// visible before a screen runs. Reused by §7's assumptions panel in Phase 3.
/// </summary>
public sealed class CriteriaExplainer(IMetricConceptVocabulary metrics)
{
    /// <summary>One crore. Indian market caps are quoted in crore, not in raw rupees.</summary>
    private const decimal Crore = 10_000_000m;

    public string Explain(FilterNode node) => node switch
    {
        Comparison c => ExplainComparison(c),
        Group g => ExplainGroup(g),
        _ => node.ToString() ?? string.Empty,
    };

    private string ExplainGroup(Group g)
    {
        var joiner = g.Op switch
        {
            GroupOperator.And => " AND ",
            GroupOperator.Or => " OR ",
            _ => " AND ",
        };

        var parts = g.Children.Select(child =>
            // Parenthesise nested groups so "A AND B OR C" can never be read the wrong way. v1
            // only compiles AND, but the renderer walks whatever tree it is handed.
            child is Group ? $"({Explain(child)})" : Explain(child));

        var body = string.Join(joiner, parts);
        return g.Op == GroupOperator.Not ? $"NOT ({body})" : body;
    }

    private string ExplainComparison(Comparison c)
    {
        var concept = metrics.Find(c.Field);

        // An unknown metric renders as its raw name rather than throwing. This runs on the panel
        // that EXPLAINS a validation failure, so it must survive input the validator will reject.
        var label = concept?.DisplayName ?? c.Field;
        var value = FormatValue(c.Value, concept?.Unit);

        return $"{label} {Symbol(c.Operator)} {value}";
    }

    private static string FormatValue(decimal value, string? unit) => unit switch
    {
        "%" => $"{Trim(value)}%",

        // A per-share price (ClosePrice, Sma50, Sma200, Atr14). "Market cap < 50000000000" is
        // unreadable, and a user cannot check a number they cannot parse at a glance -- but a
        // share price realistically never reaches a crore, so this branch is a defensive
        // safeguard rather than something that fires in practice.
        "INR" when Math.Abs(value) >= Crore =>
            $"₹{Trim(value / Crore)} cr",
        "INR" => $"₹{Trim(value)}",

        // MarketCap. Already denominated in crore at the source (RatioCalculator.MarketCap's
        // doc-comment), so this renders directly -- dividing by Crore again would be the same
        // double-conversion bug the seed itself had before it was corrected.
        "INR_CR" => $"₹{Trim(value)} cr",

        null or "" => Trim(value),
        _ => $"{Trim(value)} {unit}",
    };

    /// <summary>Drops trailing zeros so 0.50 reads as 0.5 and 25.000 as 25.</summary>
    private static string Trim(decimal value)
    {
        var normalised = value == 0m ? 0m : value / 1.000000000000000000000000000000000m;
        return normalised.ToString("#,0.############", CultureInfo.InvariantCulture);
    }

    public static string Symbol(ComparisonOperator op) => op switch
    {
        ComparisonOperator.LessThan => "<",
        ComparisonOperator.LessThanOrEqual => "≤",
        ComparisonOperator.GreaterThan => ">",
        ComparisonOperator.GreaterThanOrEqual => "≥",
        ComparisonOperator.Equal => "=",
        ComparisonOperator.NotEqual => "≠",
        _ => "?",
    };
}
