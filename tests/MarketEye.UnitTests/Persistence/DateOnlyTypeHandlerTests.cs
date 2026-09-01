using System.Data;
using FluentAssertions;
using MarketEye.Infrastructure.Persistence.TypeHandlers;
using Microsoft.Data.SqlClient;
using Xunit;

namespace MarketEye.UnitTests.Persistence;

/// <summary>
/// Regression tests for a bug that only appeared against real data: Dapper threw
/// InvalidCastException mapping SQL `date` to DateOnly, midway through a backfill, after the
/// bars had already been written. Cheap to assert, expensive to rediscover.
/// </summary>
public class DateOnlyTypeHandlerTests
{
    private readonly DateOnlyTypeHandler _handler = new();
    private readonly NullableDateOnlyTypeHandler _nullable = new();

    [Fact]
    public void Parses_a_DateTime_as_returned_by_SqlClient()
    {
        // SQL Server's `date` comes back as DateTime, which is exactly the case that failed.
        _handler.Parse(new DateTime(2024, 6, 28, 0, 0, 0)).Should().Be(new DateOnly(2024, 6, 28));
    }

    [Fact]
    public void Parses_a_DateTime_carrying_a_time_component()
    {
        _handler.Parse(new DateTime(2024, 6, 28, 15, 30, 0)).Should().Be(new DateOnly(2024, 6, 28));
    }

    [Fact]
    public void Parses_a_DateOnly_unchanged()
    {
        _handler.Parse(new DateOnly(2024, 6, 28)).Should().Be(new DateOnly(2024, 6, 28));
    }

    [Fact]
    public void Rejects_an_unconvertible_value_rather_than_guessing()
    {
        var act = () => _handler.Parse(42);
        act.Should().Throw<InvalidCastException>();
    }

    [Fact]
    public void Writes_a_date_typed_parameter()
    {
        var p = new SqlParameter();
        _handler.SetValue(p, new DateOnly(2024, 6, 28));

        p.DbType.Should().Be(DbType.Date);
        p.Value.Should().Be(new DateTime(2024, 6, 28));
    }

    [Fact]
    public void The_nullable_handler_maps_DBNull_to_null()
    {
        _nullable.Parse(DBNull.Value).Should().BeNull();
    }

    [Fact]
    public void The_nullable_handler_writes_DBNull_for_null()
    {
        var p = new SqlParameter();
        _nullable.SetValue(p, null);
        p.Value.Should().Be(DBNull.Value);
    }

    [Fact]
    public void Registration_is_idempotent()
    {
        // Dapper's handler table is global and re-adding the same type throws, so this is called
        // defensively from composition and must tolerate repeats.
        var act = () => { DapperTypeHandlers.Register(); DapperTypeHandlers.Register(); };
        act.Should().NotThrow();
    }
}
