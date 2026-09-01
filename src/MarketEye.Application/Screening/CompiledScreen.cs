namespace MarketEye.Application.Screening;

/// <summary>
/// A parameterised query ready to execute. Values live in <see cref="Parameters"/>, never inlined
/// into <see cref="Sql"/> (PLAN.md §6).
/// </summary>
public sealed record CompiledScreen
{
    public required string Sql { get; init; }
    public required IReadOnlyDictionary<string, object> Parameters { get; init; }
}
