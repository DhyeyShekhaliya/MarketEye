using System.Data;
using Dapper;

namespace MarketEye.Infrastructure.Persistence.TypeHandlers;

/// <summary>
/// Maps SQL <c>date</c> to <see cref="DateOnly"/> for Dapper.
///
/// EF Core converts this automatically; Dapper does not, and the failure is a runtime
/// InvalidCastException rather than a compile error. Since PLAN.md §3 deliberately splits the two
/// — EF for CRUD, Dapper for the hot path — every Dapper query touching a date column needs this
/// registered, or it throws the first time it runs against real data.
/// </summary>
public sealed class DateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly>
{
    public override DateOnly Parse(object value) => value switch
    {
        DateOnly d => d,
        DateTime dt => DateOnly.FromDateTime(dt),
        string s => DateOnly.Parse(s),
        _ => throw new InvalidCastException(
            $"Cannot convert {value?.GetType().Name ?? "null"} to DateOnly."),
    };

    public override void SetValue(IDbDataParameter parameter, DateOnly value)
    {
        parameter.DbType = DbType.Date;
        parameter.Value = value.ToDateTime(TimeOnly.MinValue);
    }
}

/// <summary>Nullable companion — Dapper resolves the two independently.</summary>
public sealed class NullableDateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly?>
{
    public override DateOnly? Parse(object value) => value switch
    {
        null or DBNull => null,
        DateOnly d => d,
        DateTime dt => DateOnly.FromDateTime(dt),
        string s => DateOnly.Parse(s),
        _ => throw new InvalidCastException(
            $"Cannot convert {value.GetType().Name} to DateOnly?."),
    };

    public override void SetValue(IDbDataParameter parameter, DateOnly? value)
    {
        parameter.DbType = DbType.Date;
        parameter.Value = value?.ToDateTime(TimeOnly.MinValue) ?? (object)DBNull.Value;
    }
}

public static class DapperTypeHandlers
{
    private static bool _registered;

    /// <summary>Idempotent: Dapper's handler table is global and re-adding throws.</summary>
    public static void Register()
    {
        if (_registered) return;
        SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
        SqlMapper.AddTypeHandler(new NullableDateOnlyTypeHandler());
        _registered = true;
    }
}
