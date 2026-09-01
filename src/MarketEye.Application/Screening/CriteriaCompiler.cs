using System.Text;
using MarketEye.Domain.Entities;
using MarketEye.Domain.Screening;
using MarketEye.Domain.Screening.Vocabulary;

namespace MarketEye.Application.Screening;

/// <summary>
/// Compiles a validated <see cref="ScreenCriteria"/> into parameterised SQL (PLAN.md §6).
///
/// Two properties make injection structurally impossible rather than defended against:
///
/// 1. Column names never come from the input. They are read from the MetricConcepts vocabulary,
///    which is a controlled table. The input supplies a *concept name*, which is matched against
///    that table or rejected.
/// 2. Every value becomes a parameter. Nothing user- or model-supplied is ever concatenated.
///
/// The compiler assumes it is given already-validated criteria and re-checks the concept lookup
/// anyway, because a compiler that trusts its caller is one refactor away from being the hole.
/// </summary>
public sealed class CriteriaCompiler(IMetricConceptVocabulary vocabulary)
{
    /// <summary>
    /// Compiles against a sealed snapshot. Both point-in-time conditions §4.1 requires are applied
    /// here rather than left to the caller: <c>AsOfDate</c> bounds the price and indicator series,
    /// and <c>ReportedDate &lt;= AsOfDate</c> bounds fundamentals by what the market actually knew.
    /// </summary>
    public CompiledScreen Compile(ScreenCriteria criteria, DataSnapshot snapshot)
    {
        if (snapshot.SealedAt is null)
        {
            // §4.5: queries read sealed snapshots, never live tables. Compiling against an open
            // snapshot would return results that change under the caller's feet.
            throw new InvalidOperationException(
                $"Snapshot {snapshot.Id} is not sealed. Screens resolve against sealed snapshots only (§4.5).");
        }

        var parameters = new Dictionary<string, object>
        {
            ["@asOfDate"] = snapshot.AsOfDate.ToDateTime(TimeOnly.MinValue),
        };

        var where = new StringBuilder();
        var index = 0;
        BuildPredicate(criteria.Root, where, parameters, ref index);

        var sql = new StringBuilder();
        sql.AppendLine("""
            SELECT s.Id, s.Ticker, s.Name, s.Exchange, s.Sector, s.Industry,
                   p.[Close], p.AdjClose, p.Date AS PriceDate
            FROM dbo.Securities s
            -- Latest bar at or before the snapshot date. Securities that stopped trading before
            -- then still resolve to their final bar, which is what §7 needs for a delisting exit.
            CROSS APPLY (
                SELECT TOP 1 pb.* FROM dbo.PriceBars pb
                WHERE pb.SecurityId = s.Id AND pb.Date <= @asOfDate
                ORDER BY pb.Date DESC
            ) p
            LEFT JOIN dbo.Indicators i
                ON i.SecurityId = s.Id AND i.Date = p.Date
            OUTER APPLY (
                SELECT TOP 1 fr.* FROM dbo.FundamentalRatios fr
                WHERE fr.SecurityId = s.Id AND fr.ReportedDate <= @asOfDate
                ORDER BY fr.ReportedDate DESC
            ) f
            WHERE 1 = 1
            """);

        if (criteria.Universe.Exchange is { } exchange)
        {
            sql.AppendLine("  AND s.Exchange = @exchange");
            parameters["@exchange"] = exchange;
        }
        if (criteria.Universe.Sector is { } sector)
        {
            sql.AppendLine("  AND s.Sector = @sector");
            parameters["@sector"] = sector;
        }

        // Deliberately NOT filtered on s.IsActive. A security delisted after the snapshot date was
        // tradeable then, and excluding it is survivorship bias (§7, §8.2). Delisting is handled
        // by the price join above -- a security with no bar at or before the date drops out.

        if (where.Length > 0)
        {
            sql.Append("  AND (").Append(where).AppendLine(")");
        }

        if (criteria.Sort is { } sort)
        {
            var concept = Resolve(sort.Field);
            var dir = sort.Direction == SortDirection.Descending ? "DESC" : "ASC";
            // The column comes from the vocabulary, never from the input, so it cannot carry SQL.
            sql.AppendLine($"ORDER BY {Qualify(concept)} {dir}");
        }
        else
        {
            sql.AppendLine("ORDER BY s.Ticker ASC");
        }

        var limit = criteria.Limit ?? 200;
        sql.AppendLine($"OFFSET 0 ROWS FETCH NEXT {limit} ROWS ONLY;");

        return new CompiledScreen
        {
            Sql = sql.ToString(),
            Parameters = parameters,
        };
    }

    private void BuildPredicate(
        FilterNode node, StringBuilder sql, Dictionary<string, object> parameters, ref int index)
    {
        switch (node)
        {
            case Group g:
                if (g.Op is not GroupOperator.And)
                {
                    // §6: v1 compiles AND only. The validator rejects OR/NOT first; this is the
                    // backstop for a caller that skipped validation.
                    throw new NotSupportedException(
                        $"Group operator '{g.Op}' does not compile in v1 (§6). Only AND is supported.");
                }

                var first = true;
                foreach (var child in g.Children)
                {
                    if (!first) sql.Append(" AND ");
                    sql.Append('(');
                    BuildPredicate(child, sql, parameters, ref index);
                    sql.Append(')');
                    first = false;
                }
                break;

            case Comparison c:
                var concept = Resolve(c.Field);
                var name = $"@p{index++}";
                parameters[name] = c.Value;
                sql.Append(Qualify(concept)).Append(' ').Append(Sql(c.Operator)).Append(' ').Append(name);
                break;
        }
    }

    private MetricConcept Resolve(string conceptName) =>
        vocabulary.Find(conceptName)
        ?? throw new InvalidOperationException(
            $"'{conceptName}' is not a known metric concept. The validator should have rejected " +
            "this before compilation (§5.1); reaching here means validation was skipped.");

    /// <summary>Maps a concept to its table alias. Aliases are fixed strings, not input.</summary>
    private static string Qualify(MetricConcept concept)
    {
        var alias = concept.Source switch
        {
            MetricSource.Indicator => "i",
            MetricSource.FundamentalRatio => "f",
            MetricSource.PriceBar => "p",
            MetricSource.Security => "s",
            _ => throw new NotSupportedException($"Unknown metric source '{concept.Source}'."),
        };
        return $"{alias}.[{concept.ColumnName}]";
    }

    private static string Sql(ComparisonOperator op) => op switch
    {
        ComparisonOperator.LessThan => "<",
        ComparisonOperator.LessThanOrEqual => "<=",
        ComparisonOperator.GreaterThan => ">",
        ComparisonOperator.GreaterThanOrEqual => ">=",
        ComparisonOperator.Equal => "=",
        ComparisonOperator.NotEqual => "<>",
        _ => throw new NotSupportedException($"Unknown operator '{op}'."),
    };
}
