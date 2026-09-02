using Microsoft.EntityFrameworkCore;
using MarketEye.Application.Screening;
using MarketEye.Domain.Entities;
using MarketEye.Domain.Screening;
using MarketEye.Domain.Screening.Vocabulary;

namespace MarketEye.Infrastructure.Persistence;

/// <summary>
/// Seeds the Strategy Vocabulary (PLAN.md §5.2) — what the qualitative words actually mean.
///
/// Every threshold below is a number a human chose and can edit in the UI. §5.1's rule depends on
/// that being true: when a user says "cheap", the number comes from this table, not from the model.
/// The model's entire contribution is picking which of these names apply.
///
/// Thresholds are calibrated for the Indian market (docs/adr/0004). A US-derived "cheap = P/E &lt; 15"
/// would screen out almost the whole NIFTY 50, which has traded in the low-to-mid 20s for years.
/// </summary>
public static class StrategyConceptSeed
{
    /// <summary>
    /// Inserts missing concepts and leaves existing rows alone.
    ///
    /// Deliberately NOT an upsert, unlike <see cref="MetricConceptSeed"/>. §5.2 makes these
    /// definitions user-editable, so overwriting on every start would silently revert a user's
    /// considered change to what "cheap" means — the exact opposite of the feature.
    /// </summary>
    public static async Task SeedAsync(MarketEyeDbContext db, CancellationToken ct)
    {
        var existing = await db.StrategyConcepts
            .Select(c => c.Name)
            .ToListAsync(ct);
        var have = existing.ToHashSet(StringComparer.Ordinal);

        foreach (var row in SeedRows())
        {
            if (have.Contains(row.Name)) continue;
            db.StrategyConcepts.Add(row);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The seed rows, buildable without a database so tests can assert that every definition
    /// parses, names only real metrics, and survives the validator before any of it ships.
    /// </summary>
    public static IReadOnlyList<StrategyConceptEntity> SeedRows()
    {
        var now = DateTimeOffset.UtcNow;
        return All().Select(c => new StrategyConceptEntity
        {
            Name = ConceptName.Normalise(c.Name),
            DisplayName = c.DisplayName,
            Description = c.Description,
            AliasesCsv = string.Join(',', c.Aliases.Select(ConceptName.Normalise)),
            DefinitionJson = ScreenCriteriaJson.SerializeNode(c.Definition),
            IsEnabled = true,
            IsSystem = true,
            OwnerUserId = null,
            CreatedAt = now,
            UpdatedAt = now,
        }).ToList();
    }

    private static FilterNode And(params FilterNode[] children) =>
        new Group { Op = GroupOperator.And, Children = children };

    private static Comparison Lt(string field, decimal value) =>
        new() { Field = field, Operator = ComparisonOperator.LessThan, Value = value };

    private static Comparison Gt(string field, decimal value) =>
        new() { Field = field, Operator = ComparisonOperator.GreaterThan, Value = value };

    private static Comparison Gte(string field, decimal value) =>
        new() { Field = field, Operator = ComparisonOperator.GreaterThanOrEqual, Value = value };

    // SEBI classifies large/mid/small by *rank* — top 100, next 150, the rest — which needs a
    // ranked query the flat indexable WHERE of §4.3 cannot express. These absolute rupee bands
    // approximate those cohorts: ~INR 20,000 crore and ~INR 5,000 crore. Recorded here rather than
    // implied, because "small cap" meaning a fixed number is an approximation a user may want to
    // change, and now they can.
    //
    // MarketCap's stored value is already in CRORE (RatioCalculator.MarketCap's doc-comment), so
    // "20,000 crore" is the literal value 20000, not 20000 followed by seven more zeros.
    private const decimal LargeCapFloor = 20_000m;   // INR 20,000 crore
    private const decimal SmallCapCeiling = 5_000m;  // INR  5,000 crore

    private static IEnumerable<(string Name, string DisplayName, string Description,
        string[] Aliases, FilterNode Definition)> All()
    {
        yield return ("cheap", "Cheap",
            "Trading at a low multiple of earnings and book value.",
            ["value", "undervalued", "low valuation", "bargain"],
            And(Lt("PeRatio", 25m), Lt("PbRatio", 3m)));

        yield return ("expensive", "Expensive",
            "Trading at a high multiple of earnings.",
            ["pricey", "overvalued", "rich", "high valuation"],
            And(Gt("PeRatio", 60m)));

        yield return ("profitable", "Profitable",
            // FundamentalRatios carries no net income column, and RatioCalculator refuses to
            // publish a P/E for a loss-maker rather than emitting a negative one. Positive ROE is
            // therefore the available profitability test, and it is the same question.
            "Earning a positive return on shareholders' equity.",
            ["makes money", "in profit", "earning"],
            And(Gt("ReturnOnEquity", 0m)));

        yield return ("high_quality", "High quality",
            "Strong returns on equity without leaning on leverage to get them.",
            ["quality", "well run"],
            And(Gt("ReturnOnEquity", 15m), Lt("DebtToEquity", 0.5m)));

        yield return ("low_debt", "Low debt",
            "Conservative balance sheet.",
            ["low leverage", "conservative balance sheet", "debt free", "unlevered"],
            And(Lt("DebtToEquity", 0.5m)));

        yield return ("high_debt", "High debt",
            "Carrying more than twice as much debt as equity.",
            ["leveraged", "indebted", "high leverage"],
            And(Gt("DebtToEquity", 2m)));

        yield return ("small_cap", "Small cap",
            "Market capitalisation below INR 5,000 crore.",
            ["small caps", "smallcap", "small companies"],
            And(Lt("MarketCap", SmallCapCeiling)));

        yield return ("mid_cap", "Mid cap",
            "Market capitalisation between INR 5,000 and 20,000 crore.",
            ["midcap", "mid caps"],
            And(Gte("MarketCap", SmallCapCeiling), Lt("MarketCap", LargeCapFloor)));

        yield return ("large_cap", "Large cap",
            "Market capitalisation above INR 20,000 crore.",
            ["largecap", "large caps", "blue chip", "blue chips"],
            And(Gte("MarketCap", LargeCapFloor)));

        yield return ("oversold", "Oversold",
            "RSI below the conventional 30 line.",
            ["beaten down", "sold off"],
            And(Lt("Rsi14", 30m)));

        yield return ("overbought", "Overbought",
            "RSI above the conventional 70 line.",
            ["overheated", "extended", "run up"],
            And(Gt("Rsi14", 70m)));

        yield return ("not_overbought", "Not overbought",
            "RSI below the conventional 70 line.",
            ["not overheated", "not extended"],
            And(Lt("Rsi14", 70m)));

        yield return ("volatile", "Volatile",
            "Annualised 30-day realised volatility above 40%.",
            ["high volatility", "choppy", "swingy"],
            And(Gt("Volatility30", 0.4m)));

        yield return ("stable", "Stable",
            "Annualised 30-day realised volatility below 20%.",
            ["low volatility", "steady", "calm"],
            And(Lt("Volatility30", 0.2m)));

        yield return ("high_margin", "High margin",
            "Gross margin above 40%.",
            ["high margins", "good margins"],
            And(Gt("GrossMargin", 40m)));

        yield return ("cash_generative", "Cash generative",
            "Free cash flow yield above 5%.",
            ["strong cash flow", "cash rich", "cash generating"],
            And(Gt("FcfYield", 5m)));

        yield return ("efficient", "Capital efficient",
            "Return on invested capital above 15%.",
            ["capital efficient", "high roic"],
            And(Gt("ReturnOnCapital", 15m)));

        yield return ("liquid", "Liquid",
            "More than 100,000 shares traded on the snapshot date. A tradeability floor, not a signal.",
            ["actively traded", "tradeable", "liquid enough"],
            And(Gt("Volume", 100_000m)));
    }
}
