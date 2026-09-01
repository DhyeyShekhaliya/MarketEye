using FluentAssertions;
using MarketEye.Application.Screening;
using MarketEye.Domain.Entities;
using MarketEye.Domain.Screening;
using Xunit;

namespace MarketEye.UnitTests.Screening;

public class CriteriaCompilerTests
{
    private readonly CriteriaCompiler _compiler = new(new TestVocabulary());

    private static DataSnapshot Sealed(string asOf = "2024-06-28") => new()
    {
        Id = 1,
        AsOfDate = DateOnly.Parse(asOf),
        CreatedAt = DateTimeOffset.UtcNow,
        SealedAt = DateTimeOffset.UtcNow,
        ProviderVersion = "test/1",
    };

    private static ScreenCriteria Criteria(FilterNode root, SortSpec? sort = null, int? limit = null) =>
        new() { Universe = UniverseConstraint.All, Root = root, Sort = sort, Limit = limit };

    private static Group And(params FilterNode[] c) => new() { Op = GroupOperator.And, Children = c };

    private static Comparison Cmp(string f, ComparisonOperator o, decimal v) =>
        new() { Field = f, Operator = o, Value = v };

    [Fact]
    public void Values_are_parameterised_never_inlined()
    {
        // §6: the model never emits SQL, and no supplied value reaches the statement text. This is
        // the property that makes injection structurally impossible rather than filtered.
        var compiled = _compiler.Compile(
            Criteria(And(Cmp("PeRatio", ComparisonOperator.LessThan, 15m))), Sealed());

        compiled.Sql.Should().NotContain("15");
        compiled.Sql.Should().Contain("@p0");
        compiled.Parameters["@p0"].Should().Be(15m);
    }

    [Fact]
    public void Column_names_come_from_the_vocabulary_not_the_input()
    {
        // The concept is "PeRatio"; the column is "Pe". If the input string appeared in the SQL,
        // the vocabulary would not be doing its job.
        var compiled = _compiler.Compile(
            Criteria(And(Cmp("PeRatio", ComparisonOperator.LessThan, 15m))), Sealed());

        compiled.Sql.Should().Contain("f.[Pe]");
        compiled.Sql.Should().NotContain("PeRatio");
    }

    [Fact]
    public void Concepts_resolve_to_the_table_that_holds_them()
    {
        var compiled = _compiler.Compile(Criteria(And(
            Cmp("PeRatio", ComparisonOperator.LessThan, 15m),
            Cmp("Rsi14", ComparisonOperator.LessThan, 40m))), Sealed());

        compiled.Sql.Should().Contain("f.[Pe]");
        compiled.Sql.Should().Contain("i.[Rsi14]");
    }

    [Fact]
    public void The_query_does_not_filter_on_IsActive()
    {
        // §7 and §8.2: a security delisted AFTER the snapshot date was tradeable then. Filtering
        // it out here is survivorship bias introduced at the query layer, which is exactly where
        // it is hardest to notice.
        var compiled = _compiler.Compile(
            Criteria(And(Cmp("PeRatio", ComparisonOperator.LessThan, 15m))), Sealed());

        compiled.Sql.Should().NotContain("IsActive");
    }

    [Fact]
    public void Fundamentals_are_bounded_by_ReportedDate_not_by_period_end()
    {
        // §4.1's reporting-lag half. Bounding on FiscalPeriodEnd would let a screen see figures
        // that had not been published yet.
        var compiled = _compiler.Compile(
            Criteria(And(Cmp("PeRatio", ComparisonOperator.LessThan, 15m))), Sealed());

        compiled.Sql.Should().Contain("fr.ReportedDate <= @asOfDate");
        compiled.Sql.Should().NotContain("FiscalPeriodEnd <=");
    }

    [Fact]
    public void Prices_are_bounded_by_the_snapshot_date()
    {
        var compiled = _compiler.Compile(
            Criteria(And(Cmp("PeRatio", ComparisonOperator.LessThan, 15m))), Sealed("2024-03-15"));

        compiled.Sql.Should().Contain("pb.Date <= @asOfDate");
        compiled.Parameters["@asOfDate"].Should().Be(new DateTime(2024, 3, 15));
    }

    [Fact]
    public void Compiling_against_an_unsealed_snapshot_throws()
    {
        // §4.5: queries read sealed snapshots, never live tables.
        var open = new DataSnapshot
        {
            Id = 2, AsOfDate = DateOnly.Parse("2024-06-28"),
            CreatedAt = DateTimeOffset.UtcNow, SealedAt = null, ProviderVersion = "test/1",
        };

        var act = () => _compiler.Compile(
            Criteria(And(Cmp("PeRatio", ComparisonOperator.LessThan, 15m))), open);

        act.Should().Throw<InvalidOperationException>().WithMessage("*not sealed*");
    }

    [Fact]
    public void Or_does_not_compile_in_v1()
    {
        var root = new Group
        {
            Op = GroupOperator.Or,
            Children = [Cmp("PeRatio", ComparisonOperator.LessThan, 15m)],
        };

        var act = () => _compiler.Compile(Criteria(root), Sealed());
        act.Should().Throw<NotSupportedException>().WithMessage("*AND*");
    }

    [Fact]
    public void An_unknown_concept_throws_rather_than_emitting_anything()
    {
        // Backstop for a caller that skipped validation. A compiler that trusts its caller is one
        // refactor away from being the hole.
        var act = () => _compiler.Compile(
            Criteria(And(Cmp("Injected", ComparisonOperator.LessThan, 1m))), Sealed());

        act.Should().Throw<InvalidOperationException>().WithMessage("*not a known metric concept*");
    }

    [Fact]
    public void A_sort_field_carrying_sql_cannot_reach_the_statement()
    {
        var sort = new SortSpec
        {
            Field = "Pe; DROP TABLE Securities--",
            Direction = SortDirection.Descending,
        };

        var act = () => _compiler.Compile(
            Criteria(And(Cmp("PeRatio", ComparisonOperator.LessThan, 15m)), sort), Sealed());

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Multiple_comparisons_join_with_AND_and_get_distinct_parameters()
    {
        var compiled = _compiler.Compile(Criteria(And(
            Cmp("PeRatio", ComparisonOperator.LessThan, 15m),
            Cmp("Rsi14", ComparisonOperator.LessThan, 40m),
            Cmp("NetIncome", ComparisonOperator.GreaterThan, 0m))), Sealed());

        compiled.Sql.Should().Contain(" AND ");
        compiled.Parameters.Should().ContainKeys("@p0", "@p1", "@p2");
        compiled.Parameters["@p2"].Should().Be(0m);
    }

    [Fact]
    public void Universe_constraints_are_parameterised_too()
    {
        var criteria = new ScreenCriteria
        {
            Universe = new UniverseConstraint { Exchange = "NSE", Sector = "Technology" },
            Root = And(Cmp("PeRatio", ComparisonOperator.LessThan, 15m)),
        };

        var compiled = _compiler.Compile(criteria, Sealed());

        compiled.Parameters["@exchange"].Should().Be("NSE");
        compiled.Parameters["@sector"].Should().Be("Technology");
        compiled.Sql.Should().NotContain("'NSE'");
    }

    [Fact]
    public void A_limit_is_applied_and_defaults_when_absent()
    {
        _compiler.Compile(Criteria(And(Cmp("PeRatio", ComparisonOperator.LessThan, 15m)), limit: 50), Sealed())
            .Sql.Should().Contain("FETCH NEXT 50 ROWS ONLY");

        _compiler.Compile(Criteria(And(Cmp("PeRatio", ComparisonOperator.LessThan, 15m))), Sealed())
            .Sql.Should().Contain("FETCH NEXT 200 ROWS ONLY");
    }
}
